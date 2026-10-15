using System.Text.Json;
using PlayMap.Core.Align;
using PlayMap.Core.Bus;
using PlayMap.Core.Model;
using PlayMap.Core.Preflight;
using PlayMap.Core.Wire;
using PlayMap.Engine.Align;
using PlayMap.Engine.Scanning;

namespace PlayMap.Engine.Tests.Align;

/// <summary>ALIGN RUN end to end on the engine: STATE.align and outputs[].measured by field through a run and an ACCEPT; a curved screen
/// fails in words and stages nothing; a second run while one runs is refused, and CANCEL ends it; the run through the scan display and
/// a camera seam (the simulator behind both) stages as the simulator's own path does; a project whose measured files are missing says
/// which in STATE, the context and preflight (red, with the line that returns to the fallback); INDEX lists each group with keys that
/// are catalogue lines.</summary>
public sealed class AlignRunTests
{
    private const string Layout = "flat-screen:tile2x1:o20";

    [Fact]
    public async Task StateAndTheOutputsMeasuredReadByFieldThroughARunAndAnAccept()
    {
        await using var rig = new AlignRig(AlignRig.Project(2, Layout));
        Assert.Equal(JsonValueKind.Null, rig.State().GetProperty("align").GetProperty("run").ValueKind);
        var group = await rig.NewGroup();
        var g = rig.State().GetProperty("align").GetProperty("groups")[0];
        Assert.Equal((group, "Wall", "auto", "cm_sim", "cv_wall", "inside", "grey"), (g.GetProperty("id").GetString(), g.GetProperty("name").GetString(),
            g.GetProperty("shape").GetString(), g.GetProperty("camera").GetString(), g.GetProperty("canvas").GetString(), g.GetProperty("fit").GetString(),
            g.GetProperty("light").GetString()));

        var run = await rig.RunToEnd(group);
        var state = rig.State();
        var r = state.GetProperty("align").GetProperty("run");
        Assert.Equal((run, group, "staged", "stage", 1.0), (r.GetProperty("id").GetString(), r.GetProperty("group").GetString(), r.GetProperty("state").GetString(),
            r.GetProperty("step").GetString(), r.GetProperty("progress").GetDouble()));
        Assert.Equal(2, r.GetProperty("outputs").GetArrayLength());
        Assert.Equal("green", r.GetProperty("outputs")[0].GetProperty("light").GetString());
        var score = r.GetProperty("score");
        Assert.Equal(("points", "A"), (score.GetProperty("unit").GetString(), score.GetProperty("grade").GetString()));
        g = state.GetProperty("align").GetProperty("groups")[0];
        Assert.Equal((run, "amber"), (g.GetProperty("staged").GetString(), g.GetProperty("light").GetString()));
        Assert.Equal(JsonValueKind.Null, state.GetProperty("outputs")[0].GetProperty("measured").ValueKind);
        var item = Context(rig).Single(i => i.GetProperty("id").GetString() == "align:" + group);
        Assert.Equal("amber", item.GetProperty("light").GetString());

        // The run's folder: run.json and each output's files, never written again.
        var folder = Path.Combine(rig.Folder.FullName, "align", run);
        Assert.True(File.Exists(Path.Combine(folder, "run.json")));
        Assert.True(File.Exists(Path.Combine(folder, "warp-op1.pfm")) && File.Exists(Path.Combine(folder, "blend-op2.pfm")));
        // run.json reads back as the run the runner holds: its score whole (what ACCEPT records in the ledger) and each output's result.
        var kept = AlignArchive.Load(rig.Folder.FullName, run)!;
        var held = rig.Runner.Finished(run)!;
        Assert.Equal("staged", kept.State);
        Assert.Equal((held.Score.Value, held.Score.Unit, held.Score.Grade, held.Score.RmsPx, held.Score.P95Px, held.Score.Confidence, held.Score.Source, held.Score.Words),
            (kept.Score.Value, kept.Score.Unit, kept.Score.Grade, kept.Score.RmsPx, kept.Score.P95Px, kept.Score.Confidence, kept.Score.Source, kept.Score.Words));
        Assert.True(held.Score.RmsPx > 0 && held.Score.Confidence > 0, "the score carries the seam and the coverage");
        Assert.Equal(held.Score.Terms.ToList(), kept.Score.Terms.ToList());
        Assert.Equal(held.Outputs.Select(o => (o.OutputId, o.Measured)), kept.Outputs.Select(o => (o.OutputId, o.Measured)));

        await rig.Ok($"ALIGN ACCEPT {group}");
        await rig.Engine.AlignFiles.Settled;
        state = rig.State();
        var m = state.GetProperty("outputs")[0].GetProperty("measured");
        Assert.Equal((run, "replace", "ok"), (m.GetProperty("run").GetString(), m.GetProperty("blend").GetString(), m.GetProperty("files").GetString()));
        Assert.InRange(m.GetProperty("residualPx").GetDouble(), 0, 0.3);
        Assert.InRange(m.GetProperty("gamma").GetDouble(), 2.1, 2.3);
        Assert.False(m.GetProperty("colour").GetBoolean());
        g = state.GetProperty("align").GetProperty("groups")[0];
        Assert.Equal((run, "", "A", "green"), (g.GetProperty("lastRun").GetString(), g.GetProperty("staged").GetString(), g.GetProperty("grade").GetString(),
            g.GetProperty("light").GetString()));
        Assert.Equal("accepted", state.GetProperty("align").GetProperty("run").GetProperty("state").GetString());
        Assert.True(rig.Engine.AlignMeasured.Loads.ContainsKey("op1"));
        Assert.Equal(33 * 19 * 3, rig.Engine.AlignMeasured.Loads["op1"].Grid!.Values.Length);

        // Every fix the run's score carries is a line the node carries out: the uniformity term's raises the group's own.
        var terms = rig.Runner.Finished(run)!.Score.Terms;
        foreach (var term in terms.Where(t => t.Fix.Length > 0))
        {
            Assert.True(WireGrammar.TryParse(term.Fix, out var fixLine, out _), term.Fix);
            Assert.NotNull(VerbCatalog.Match(fixLine));
        }
        await rig.Ok(terms.Single(t => t.Code == ScoreTermCodes.Uniformity).Fix);
        Assert.Equal(1, rig.Current.FindAlign(group)!.Options.Uniformity);
    }

    [Fact]
    public async Task ACurvedScreenFailsInWordsAndStagesNothing()
    {
        await using var rig = new AlignRig(AlignRig.Project(3, "curved-screen:tile3x1"));
        var group = await rig.NewGroup();
        var run = await rig.RunToEnd(group);
        var r = rig.State().GetProperty("align").GetProperty("run");
        Assert.Equal("failed", r.GetProperty("state").GetString());
        Assert.Contains("not flat", r.GetProperty("words").GetString(), StringComparison.Ordinal);
        Assert.Contains("curved screen is next round", r.GetProperty("words").GetString(), StringComparison.Ordinal);
        Assert.Contains("failed", await rig.Refused($"ALIGN ACCEPT {run}"), StringComparison.Ordinal);
        Assert.Equal("failed", AlignArchive.Load(rig.Folder.FullName, run)!.State);
        Assert.Equal("red", rig.State().GetProperty("align").GetProperty("groups")[0].GetProperty("light").GetString());
        Assert.All(rig.Current.Outputs, o => Assert.Null(o.Measured));
    }

    [Fact]
    public async Task OneRunAtATimeAndCancelEndsIt()
    {
        await using var rig = new AlignRig(AlignRig.Project(3, "flat-screen:tile3x1"));
        var group = await rig.NewGroup();
        await rig.Ok($"ALIGN RUN {group}");
        Assert.Contains("is running", await rig.Refused($"ALIGN RUN {group}"), StringComparison.Ordinal);
        Assert.Contains("next round", await rig.Refused($"ALIGN RUN {group} STEPS COLOR"), StringComparison.Ordinal);
        await rig.Ok("ALIGN CANCEL");
        await rig.Runner.Running!;
        Assert.Equal("cancelled", rig.State().GetProperty("align").GetProperty("run").GetProperty("state").GetString());
        Assert.Contains("no alignment is running", await rig.Refused("ALIGN CANCEL"), StringComparison.Ordinal);
        Assert.Contains("FIT CORNERS needs", await rig.Refused($"ALIGN RUN {group} FIT CORNERS"), StringComparison.Ordinal);
        Assert.Contains("aligns these outputs", await rig.Refused("ALIGN NEW Again cm_sim ALL"), StringComparison.Ordinal);
        Assert.Contains("curved screens are next round", await rig.Refused("ALIGN NEW Dome cm_sim op1 SHAPE CYLINDER"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARunThroughTheDisplayAndACameraStagesLikeTheSimulatorsOwn()
    {
        var sim = AlignRig.Truth(2, Layout);
        var seam = new SimDisplayCamera(sim);
        var options = new AlignRunnerOptions { OpenCamera = _ => seam, LatencyFlashes = 1, FlashHold = TimeSpan.FromMilliseconds(1), FallbackLatencyMs = 0, MaxUnsettledFrames = 1 };
        await using var rig = new AlignRig(DeviceCamera(AlignRig.Project(2, Layout)), options, seam);
        var group = await rig.NewGroup();
        var run = await rig.RunToEnd(group);
        var r = rig.State().GetProperty("align").GetProperty("run");
        Assert.True(r.GetProperty("state").GetString() == "staged", r.GetProperty("words").GetString());
        var plan = AlignSequence.PlanFor(AlignRig.W, AlignRig.H, 2.2).Count;
        Assert.All(seam.Shown, n => Assert.True(n >= plan, $"{n} frames shown, the plan has {plan}"));
        Assert.True(seam.Released > 0, "the outputs were handed back to the show");
        await rig.Ok($"ALIGN ACCEPT {run}");
        Assert.NotNull(rig.Current.FindOutput("op2")!.Measured);
    }

    [Fact]
    public async Task ACameraThatFaultsEndsTheRunFailedInWordsAndTheNextRunStarts()
    {
        var seam = new SimDisplayCamera(AlignRig.Truth(2, Layout));
        var options = new AlignRunnerOptions { OpenCamera = _ => new FaultingCamera(), LatencyFlashes = 0, FallbackLatencyMs = 0, MaxUnsettledFrames = 1 };
        await using var rig = new AlignRig(DeviceCamera(AlignRig.Project(2, Layout)), options, seam);
        var group = await rig.NewGroup();
        await rig.RunToEnd(group);
        var r = rig.State().GetProperty("align").GetProperty("run");
        Assert.Equal("failed", r.GetProperty("state").GetString());
        Assert.Contains(FaultingCamera.Words, r.GetProperty("words").GetString(), StringComparison.Ordinal);
        Assert.Equal(1, seam.Released);

        // The runner is free again: the next run is accepted and runs.
        var next = AlignRig.Json(await rig.Ok($"ALIGN RUN {group}"));
        Assert.Equal("accepted", next.GetProperty("stage").GetString());
        await rig.Runner.Running!;
        Assert.Equal((next.GetProperty("run").GetString(), "failed"), (rig.Runner.View!.Id, rig.Runner.View.State));
    }

    [Fact]
    public async Task ASimulatedRunLeavesTheDisplayAlone()
    {
        var seam = new SimDisplayCamera(AlignRig.Truth(2, Layout));
        await using var rig = new AlignRig(AlignRig.Project(2, Layout), display: seam);
        var group = await rig.NewGroup();
        await rig.RunToEnd(group);
        Assert.Equal("staged", rig.Runner.View!.State);
        Assert.Equal(0, seam.Released);
        Assert.All(seam.Shown, n => Assert.Equal(0, n));
    }

    [Fact]
    public async Task AProjectOpenedFromItsFileReadsItsMeasuredFilesBesideIt()
    {
        await using var saved = new AlignRig(AlignRig.Project(2, Layout));
        var group = await saved.NewGroup();
        await saved.RunToEnd(group);
        await saved.Ok($"ALIGN ACCEPT {group}");
        var path = Path.Combine(saved.Folder.FullName, "show.playmap");
        await saved.Ok($"PROJECT SAVE {WireGrammar.Quote(path)}");

        // A node whose own folder holds no align files opens the show from its file, as a node does when it starts.
        await using var node = new AlignRig(AlignRig.Project(2, Layout));
        await node.Ok($"PROJECT OPEN {WireGrammar.Quote(path)}");
        await node.Engine.Lane.RunAsync(() => { });
        await node.Engine.AlignFiles.Settled;
        Assert.Empty(node.Engine.AlignMeasured.Faults);
        Assert.True(node.Engine.AlignMeasured.Loads.ContainsKey("op1") && node.Engine.AlignMeasured.Loads.ContainsKey("op2"));
        var m = node.State().GetProperty("outputs")[0].GetProperty("measured");
        Assert.Equal((saved.Current.FindOutput("op1")!.Measured!.RunId, "ok", "green"),
            (m.GetProperty("run").GetString(), m.GetProperty("files").GetString(), m.GetProperty("light").GetString()));
    }

    [Fact]
    public async Task MissingFilesAreSaidInStateTheContextAndPreflight()
    {
        Project accepted;
        await using (var rig = new AlignRig(AlignRig.Project(2, Layout)))
        {
            var group = await rig.NewGroup();
            await rig.RunToEnd(group);
            await rig.Ok($"ALIGN ACCEPT {group}");
            await rig.Engine.AlignFiles.Settled;
            var rows = await Preflight(rig);
            Assert.All(rows, row => Assert.Equal((Light.Green, "ok"), (row.Light, row.Value)));
            Assert.Equal(2, rows.Count);
            accepted = rig.Current;
        }

        // The same project on a node whose folder lacks the align files (a backup the files never reached).
        await using var bare = new AlignRig(accepted);
        await bare.Engine.AlignFiles.Settled;
        var state = bare.State();
        var files = state.GetProperty("outputs")[0].GetProperty("measured").GetProperty("files").GetString()!;
        Assert.Contains("warp-op1.pfm missing", files, StringComparison.Ordinal);
        Assert.Contains("runs without its measured correction", files, StringComparison.Ordinal);
        Assert.False(bare.Engine.AlignMeasured.Loads.ContainsKey("op1"));
        var group2 = accepted.Stage.Aligns[0].Id;
        Assert.Equal("red", state.GetProperty("align").GetProperty("groups")[0].GetProperty("light").GetString());
        Assert.Equal("red", Context(bare).Single(i => i.GetProperty("id").GetString() == "align:" + group2).GetProperty("light").GetString());
        var row = (await Preflight(bare))[0];
        Assert.Equal((Light.Red, "missing", $"ALIGN REVERT {group2}"), (row.Light, row.Value, row.Fix));
        Assert.True(WireGrammar.TryParse(row.Fix!, out var line, out _));
        Assert.NotNull(VerbCatalog.Match(line));
    }

    [Fact]
    public async Task IndexListsEachGroupWithKeysThatAreCatalogueLines()
    {
        await using var rig = new AlignRig(AlignRig.Project(2, Layout));
        var group = await rig.NewGroup();
        var index = rig.Engine.Routes.IndexSections.Single(s => s.Section == "align");
        foreach (var key in index.Hint.Keys)
        {
            Assert.True(WireGrammar.TryParse(key.DownLine.Replace("$(id)", group, StringComparison.Ordinal), out var line, out _), key.DownLine);
            Assert.NotNull(VerbCatalog.Match(line));
        }
        var rows = Rows(rig, index);
        Assert.Equal((group, "align", "Wall", "none", "grey"), (rows[0].GetProperty("id").GetString(), rows[0].GetProperty("kind").GetString(),
            rows[0].GetProperty("label").GetString(), rows[0].GetProperty("state").GetString(), rows[0].GetProperty("light").GetString()));
        await rig.RunToEnd(group);
        Assert.Equal("staged", Rows(rig, index)[0].GetProperty("state").GetString());
        await rig.Ok($"ALIGN ACCEPT {group}");
        Assert.StartsWith("A ", Rows(rig, index)[0].GetProperty("state").GetString(), StringComparison.Ordinal);
    }

    /// <summary>The project with its camera a capture device: a run goes through the display and the camera seam.</summary>
    private static Project DeviceCamera(Project project) =>
        project with { Stage = project.Stage with { Cameras = [project.Stage.Cameras[0] with { SourceKind = CameraSourceKind.Device, Source = "/dev/video9" }] } };

    private static List<JsonElement> Rows(AlignRig rig, IIndexContributor index)
    {
        var json = EngineCore.JsonOut(w => index.WriteRows(rig.Current, rig.Engine.Snapshot, w));
        return [.. JsonDocument.Parse(json).RootElement.Clone().EnumerateArray()];
    }

    private static List<JsonElement> Context(AlignRig rig) =>
        [.. JsonDocument.Parse(rig.Engine.Context()).RootElement.Clone().GetProperty("items").EnumerateArray()];

    private static async Task<IReadOnlyList<PreflightRow>> Preflight(AlignRig rig) =>
        await new AlignPreflight().RunAsync(new PreflightContext(rig.Engine, PreflightDepth.Quick, false), CancellationToken.None);
}

/// <summary>A display and a camera with the simulator behind both: what the display shows is what the camera sees, a frame after each
/// present (a 50 fps camera's clock), so the run's real path — latency, presents, settled frames — runs without hardware.</summary>
internal sealed class SimDisplayCamera(MultiProjectorSim sim) : IScanDisplay, IScanCamera
{
    private readonly Lock _gate = new();
    private readonly byte[]?[] _frames = new byte[]?[sim.Projectors.Count];
    private readonly int[] _shown = new int[sim.Projectors.Count];
    private long _now = 1_000_000_000;
    private long _generation;

    public IReadOnlyList<int> Shown
    {
        get
        {
            lock (_gate)
                return [.. _shown];
        }
    }

    public int Released { get; private set; }

    public string Problem => "";

    public Task<long> ShowAsync(string outputId, ReadOnlyMemory<byte> gray8, int width, int height, CancellationToken ct)
    {
        var j = int.Parse(outputId.AsSpan(2), System.Globalization.CultureInfo.InvariantCulture) - 1;
        lock (_gate)
        {
            _frames[j] = gray8.Span.IndexOfAnyExcept((byte)0) < 0 ? null : gray8.ToArray();
            _shown[j]++;
            _now += 1_000_000;
            return Task.FromResult(_now);
        }
    }

    public void Release()
    {
        lock (_gate)
        {
            Array.Clear(_frames);
            Released++;
        }
    }

    public Task<bool> OpenAsync(CancellationToken ct) => Task.FromResult(true);

    public Task<CameraFrame?> NextAsync(long afterGeneration, TimeSpan timeout, CancellationToken ct)
    {
        byte[]?[] frames;
        long arrival;
        long generation;
        lock (_gate)
        {
            frames = [.. _frames];
            _now += 20_000_000;
            arrival = _now;
            generation = ++_generation;
        }
        var picture = sim.Capture(frames);
        return Task.FromResult<CameraFrame?>(new CameraFrame(picture, sim.CameraWidth, sim.CameraHeight, arrival, generation));
    }

    public void Dispose()
    {
        // The simulator holds nothing to let go of.
    }
}

/// <summary>A camera that opens and then fails its first picture with an exception of its own.</summary>
internal sealed class FaultingCamera : IScanCamera
{
    public const string Words = "the camera's driver stalled";

    public string Problem => "";

    public Task<bool> OpenAsync(CancellationToken ct) => Task.FromResult(true);

    public Task<CameraFrame?> NextAsync(long afterGeneration, TimeSpan timeout, CancellationToken ct) =>
        Task.FromException<CameraFrame?>(new TimeoutException(Words));

    public void Dispose()
    {
        // Nothing was opened.
    }
}
