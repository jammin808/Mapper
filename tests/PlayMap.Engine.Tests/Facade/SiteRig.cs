using System.Text.Json;
using PlayMap.Core.Model;
using PlayMap.Core.Numerics;
using PlayMap.Core.Time;
using PlayMap.Core.Wire;

namespace PlayMap.Engine.Tests.Facade;

/// <summary>An engine on a hand-moved clock with a project folder of its own (deleted at the end), and the facade's helpers: verbs as
/// the desk sends them, STATE, and waiting for an import to land.</summary>
internal sealed class SiteRig : IAsyncDisposable
{
    public static readonly SessionInfo Desk = new("t1", "desk", "127.0.0.1", true, "Test desk", true);

    public readonly ManualClock Clock = new(5_000_000_000);
    public readonly EngineCore Engine;
    public readonly string Folder;

    public SiteRig(Project? project = null)
    {
        Folder = Path.Combine(Path.GetTempPath(), "playmap-facade-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(Folder);
        Engine = new EngineCore(EngineServices.ForTests(Clock) with { ProjectFolder = Folder }, project);
    }

    public string File(string name) => Path.Combine(Folder, name);

    public async Task<CommandReply> Run(string line, SessionInfo? session = null)
    {
        Assert.True(WireGrammar.TryParse(line, out var parsed, out var error), error?.Reason);
        return await Engine.HandleAsync(parsed, session ?? Desk, CancellationToken.None);
    }

    public async Task<string> Ok(string line, SessionInfo? session = null)
    {
        var r = await Run(line, session);
        Assert.True(r.Ok, $"{line} → {r.Payload}");
        return r.Payload ?? "";
    }

    public async Task<string> Refused(string line, SessionInfo? session = null)
    {
        var r = await Run(line, session);
        Assert.False(r.Ok, $"{line} was taken: {r.Payload}");
        return r.Payload ?? "";
    }

    public static JsonElement Json(string payload)
    {
        using var doc = JsonDocument.Parse(payload);
        return doc.RootElement.Clone();
    }

    public JsonElement State() => Json(Engine.State().Json);

    /// <summary>Starts an import and waits for it to be applied (or to fail): the job's final view.</summary>
    public async Task<SurveyJobView> Import(string args)
    {
        var accepted = Json(await Ok("SURVEY IMPORT " + args));
        Assert.Equal("accepted", accepted.GetProperty("stage").GetString());
        await Engine.WhenSurveyed.WaitAsync(TimeSpan.FromMinutes(5));
        return Engine.SurveyJob!;
    }

    public async ValueTask DisposeAsync()
    {
        await Engine.DisposeAsync();
        try
        {
            Directory.Delete(Folder, recursive: true);
        }
        catch (IOException)
        {
            // A file still held by the OS is left for the temp cleaner; the test's result does not depend on it.
        }
    }

    /// <summary>A projector at a pose, its pins made from world points by the true optics (pixel noise σ, Pcg32 seeded).</summary>
    public static Projector ProjectorWithPins(string id, Intrinsics k, Pose pose, IReadOnlyList<Vec3d> world, double sigmaPx = 0, ulong seed = 1,
        SolveLocks locks = SolveLocks.Shift | SolveLocks.Distortion)
    {
        var rng = new Pcg32(seed, 0x77);
        var pins = world.Select((w, i) =>
        {
            var px = Core.Geometry.CameraModel.Project(w, k, LensDistortion.None, pose)!.Value;
            return new CalibrationPin($"pn{i:00000000}", w, px + new Vec2d(rng.NextGaussian() * sigmaPx, rng.NextGaussian() * sigmaPx), true);
        }).ToList();
        return new Projector(id, id, k, LensDistortion.None, pose, 30_000, "#FFFFFF", false, [.. pins], locks, 0);
    }
}
