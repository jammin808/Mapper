using System.Collections.Immutable;
using System.Text.Json;
using PlayMap.Core.Align;
using PlayMap.Core.Model;
using PlayMap.Core.Numerics;
using PlayMap.Core.Show;
using PlayMap.Core.Time;
using PlayMap.Core.Wire;
using PlayMap.Engine.Align;

namespace PlayMap.Engine.Tests.Align;

/// <summary>A show gate a test arms and disarms: armed, every line that changes the show is refused with the show's words.</summary>
internal sealed class SwitchGate : IShowGate
{
    public const string Words = "the show is armed — mapping is locked (SHOW DISARM, or blind-edit)";

    public volatile bool IsArmed;

    public bool Armed => IsArmed;

    public string ArmedBy => IsArmed ? "Test desk" : "";

    public string? BlindOwner => null;

    public int MinGoMs => 0;

    public ShowRole RoleOf(SessionInfo session) => ShowRole.Operator;

    public ShowDecision Decide(VerbSpec spec, WireLine line, SessionInfo session) =>
        IsArmed && spec.Mutates ? new(ShowVerdict.Refuse, Words) : new(ShowVerdict.Allow, null);

    public IReadOnlyCollection<string>? Allowed(SessionInfo session) => IsArmed ? [] : null;
}

/// <summary>An engine with a project of canvas outputs and a simulated camera, its files in a folder of its own, and the helpers the
/// align tests share: verbs as the desk sends them, a run started and waited for, STATE, and the truth the simulator keeps.</summary>
internal sealed class AlignRig : IAsyncDisposable
{
    public const int W = 320;
    public const int H = 180;
    public const int CameraW = 800;
    public const int CameraH = 450;

    public static readonly SessionInfo Desk = new("t1", "desk", "127.0.0.1", true, "Test desk", true);

    public readonly ManualClock Clock = new(5_000_000_000);
    public readonly SwitchGate Gate = new();
    public readonly EngineCore Engine;
    public readonly DirectoryInfo Folder;
    private readonly AlignRunner _runner;

    public AlignRig(Project project, AlignRunnerOptions? options = null, IScanDisplay? display = null, Func<EngineServices, EngineServices>? configure = null)
    {
        Folder = Directory.CreateTempSubdirectory("playmap-align-");
        _runner = new AlignRunner(display, (options ?? new AlignRunnerOptions()) with { UnixMs = () => 1_760_000_000_000 });
        var services = EngineServices.ForTests(Clock) with { ProjectFolder = Folder.FullName, ShowGate = Gate, Align = _runner };
        services = configure?.Invoke(services) ?? services;
        Engine = new EngineCore(services, project);
    }

    public AlignRunner Runner => _runner;

    /// <summary>A project of <paramref name="count"/> canvas outputs (the canvas split between them) and a simulated camera whose
    /// source names the simulator's layout ("cm_sim").</summary>
    public static Project Project(int count, string source)
    {
        var canvas = new Canvas("cv_wall", "Wall", W * count, H, "#000000", []);
        var outputs = Enumerable.Range(0, count).Select(j => new Output($"op{j + 1}", $"P{j + 1}", OutputKind.Virtual, "", W, H, 60, OutputMode.Canvas, null,
            SourceRef.Canvas(canvas.Id), new RectD(j * W, 0, W, H), OutputWarp.None, BlendSettings.Default with { Auto3D = false }, ColorTrim.Neutral, [],
            new RectD(j * W, 0, W, H), 0, true)).ToImmutableArray();
        var camera = new CameraRig("cm_sim", "Truck camera", CameraSourceKind.Simulated, source, new Intrinsics(600, 600, CameraW / 2.0, CameraH / 2.0, CameraW, CameraH),
            LensDistortion.None, Pose.Identity, false);
        var p = Core.Model.Project.CreateDefault("Align test");
        return p with { Canvases = [canvas], Outputs = outputs, Stage = p.Stage with { Cameras = [camera] } };
    }

    /// <summary>The simulator the runner builds for the same layout (deterministic: the truth for the tests).</summary>
    public static MultiProjectorSim Truth(int count, string source) =>
        MultiProjectorSim.FromSource(source, [.. Enumerable.Repeat((W, H), count)], CameraW, CameraH);

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

    public Project Current => Engine.Store.Project;

    /// <summary>ALIGN NEW over every output through the simulated camera; answers the group's id.</summary>
    public async Task<string> NewGroup(string outputs = "ALL") =>
        Json(await Ok($"ALIGN NEW Wall cm_sim {outputs}")).GetProperty("id").GetString()!;

    /// <summary>ALIGN RUN and the run to its end; answers the run's id.</summary>
    public async Task<string> RunToEnd(string group, string extra = "")
    {
        var run = Json(await Ok($"ALIGN RUN {group} {extra}".Trim())).GetProperty("run").GetString()!;
        await _runner.Running!;
        return run;
    }

    /// <summary>The measured files the project names, read back as the engine reads them.</summary>
    public WarpGrid Grid(string outputId)
    {
        var o = Current.FindOutput(outputId)!;
        var path = AlignArchive.Resolve(Folder.FullName, o.Measured!.Warp!.File)!;
        return WarpGrid.FromMap(Pfm.Read(File.ReadAllBytes(path)), o.Width, o.Height);
    }

    /// <summary>For canvas uv: every output that shows it, with the screen point it lights and that output's pixels per metre there.</summary>
    public List<(int Output, Vec2d Screen, double PxPerM)> Landings(MultiProjectorSim sim, IReadOnlyList<(WarpGrid Grid, CanvasInverse Inverse)> warps, Vec2d uv)
    {
        var list = new List<(int, Vec2d, double)>();
        for (var j = 0; j < warps.Count; j++)
        {
            if (warps[j].Inverse.At(uv) is not { } start || warps[j].Grid.Invert(uv, start) is not { } q || sim.ProjectorToScreen(j, q) is not { } world)
                continue;
            list.Add((j, sim.OnScreen(world), sim.PixelsPerMetre(j, q)));
        }
        return list;
    }

    public List<(WarpGrid Grid, CanvasInverse Inverse)> Warps(int count) =>
        [.. Enumerable.Range(1, count).Select(j => Grid($"op{j}")).Select(g => (g, CanvasInverse.Build(g, 1024, 1024)))];

    public static double Quantile(List<double> values, double q)
    {
        values.Sort();
        return values[Math.Min(values.Count - 1, (int)(q * values.Count))];
    }

    public async ValueTask DisposeAsync()
    {
        await _runner.DisposeAsync();
        await Engine.DisposeAsync();
        Folder.Delete(recursive: true);
    }
}
