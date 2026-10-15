using System.Collections.Immutable;
using System.Text.Json;
using PlayMap.Core.Geometry;
using PlayMap.Core.Model;
using PlayMap.Core.Numerics;
using PlayMap.Core.Show;
using PlayMap.Core.Time;
using PlayMap.Core.Wire;

namespace PlayMap.Engine.Tests.Wall;

/// <summary>Two displays a node sees, and no window opened on them.</summary>
internal sealed class TwoDisplays : IOutputBackend
{
    public IReadOnlyList<DisplayInfo> Displays() =>
    [
        new("DISPLAY1", "Desk monitor", 0, 0, 2560, 1440, 60, true),
        new("DISPLAY2", "Epson L1505U", 2560, 0, 1920, 1200, 60, false),
    ];

    public IReadOnlyList<OutputHealth> Health() => [];

    public string RendererName => "test";
}

/// <summary>A show gate that is armed: every line that changes the show is refused with the show's words.</summary>
internal sealed class ArmedGate : IShowGate
{
    public const string Words = "the show is armed — mapping is locked (SHOW DISARM, or blind-edit)";

    public bool Armed => true;

    public string ArmedBy => "Test desk";

    public string? BlindOwner => null;

    public int MinGoMs => 0;

    public ShowRole RoleOf(SessionInfo session) => ShowRole.Operator;

    public ShowDecision Decide(VerbSpec spec, WireLine line, SessionInfo session) =>
        spec.Mutates ? new(ShowVerdict.Refuse, Words) : new(ShowVerdict.Allow, null);

    public IReadOnlyCollection<string>? Allowed(SessionInfo session) => [];
}

/// <summary>An engine on a hand-moved clock with two displays, and the wall's helpers: verbs as a peer sends them, STATE, and a
/// stage of projectors with pins made from known poses.</summary>
internal sealed class WallRig : IAsyncDisposable
{
    public static readonly SessionInfo Desk = new("t1", "desk", "127.0.0.1", true, "Test desk", true);

    public readonly ManualClock Clock = new(5_000_000_000);
    public readonly EngineCore Engine;
    public readonly List<ShowEvent> Events = [];

    public WallRig(Project? project = null, IShowGate? gate = null)
    {
        var services = EngineServices.ForTests(Clock) with { Outputs = new TwoDisplays() };
        if (gate is not null)
            services = services with { ShowGate = gate };
        Engine = new EngineCore(services, project);
        Engine.EventRaised += e =>
        {
            lock (Events)
                Events.Add(e);
        };
    }

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
        Assert.False(r.Ok, $"{line} was not refused: {r.Payload}");
        return r.Payload ?? "";
    }

    public static JsonElement Json(string payload) => JsonDocument.Parse(payload).RootElement.Clone();

    public JsonElement State() => JsonDocument.Parse(Engine.State().Json).RootElement.Clone();

    public Project Project => Engine.Store.Project;

    /// <summary>Waits (up to 10 s) for the lane's own 10 ms tick to make a condition true.</summary>
    public static async Task Until(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"timed out waiting for {what}");
            await Task.Delay(10);
        }
    }

    public ValueTask DisposeAsync() => Engine.DisposeAsync();

    // ---- a stage of projectors with pins ---------------------------------------------------------------------------------

    public const int W = 1920;
    public const int H = 1080;

    /// <summary>Where projector n (0-based) really stands: three side by side, looking at a wall 5 m away.</summary>
    public static Pose TruePose(int n) => Pose.LookAt(new Vec3d(-2 + 2 * n, 1.6, 0), new Vec3d(-2 + 2 * n, 1.2, 5), Vec3d.UnitY);

    public static Intrinsics TrueOptics => Intrinsics.FromThrowRatio(W, H, 1.5, 0, 0.1);

    /// <summary>Pins seen through a device: pixels spread over the raster, world points at depths 3–7 m on their rays (off one plane),
    /// with pixel noise σ.</summary>
    public static ImmutableArray<CalibrationPin> PinsFor(Intrinsics k, Pose pose, int count, ulong seed, double noisePx = 0)
    {
        var rng = new Pcg32(seed);
        var pins = ImmutableArray.CreateBuilder<CalibrationPin>(count);
        for (var i = 0; i < count; i++)
        {
            var pixel = new Vec2d(rng.Range(0.05, 0.95) * k.Width, rng.Range(0.05, 0.95) * k.Height);
            var depth = rng.Range(3.0, 7.0);
            var dir = CameraModel.PixelRayCamera(pixel, k, LensDistortion.None);
            var world = pose.CameraToWorldPoint(dir * (depth / dir.Z));
            var seen = noisePx > 0 ? pixel + new Vec2d(rng.NextGaussian() * noisePx, rng.NextGaussian() * noisePx) : pixel;
            pins.Add(new CalibrationPin($"pn_{seed:x4}{i:x2}", world, seen, true));
        }
        return pins.MoveToImmutable();
    }

    /// <summary>A project with <paramref name="count"/> projectors standing near their true poses (a few cm and a degree off, a
    /// datasheet throw) with pins made from the true poses, each shown by an enabled projector-mode output.</summary>
    public static Project ProjectorsWithPins(int count = 3, double noisePx = 0.15)
    {
        var p = Project.CreateDefault("Wall test");
        var projectors = ImmutableArray.CreateBuilder<Projector>();
        var outputs = ImmutableArray.CreateBuilder<Output>();
        for (var n = 0; n < count; n++)
        {
            var truth = TruePose(n);
            var guess = new Pose(truth.Position + new Vec3d(0.05, -0.03, 0.04), truth.Rotation + new Vec3d(0.01, -0.01, 0.005));
            var id = $"pj_test{n + 1}";
            projectors.Add(new Projector(id, $"Projector {n + 1}", Intrinsics.FromThrowRatio(W, H, 1.5, 0, 0.1), LensDistortion.None, guess, 0, "#FF8800",
                false, PinsFor(TrueOptics, truth, 14, (ulong)(100 + n), noisePx), SolveLocks.Shift | SolveLocks.Distortion, 0));
            outputs.Add(new Output($"op_test{n + 1}", $"Output {n + 1}", OutputKind.Virtual, "", W, H, 60, OutputMode.Projector, id, SourceRef.None,
                new RectD(0, 0, W, H), OutputWarp.None, BlendSettings.Default, ColorTrim.Neutral, [], new RectD(n * W, 0, W, H), 0, true));
        }
        return p with { Outputs = outputs.ToImmutable(), Stage = p.Stage with { Projectors = projectors.ToImmutable() } };
    }
}
