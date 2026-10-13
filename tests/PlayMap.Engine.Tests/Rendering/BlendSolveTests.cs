using System.Text.Json;
using PlayMap.Core.Model;
using PlayMap.Core.Numerics;
using PlayMap.Core.Time;
using PlayMap.Core.Wire;
using PlayMap.Engine.Rendering;

namespace PlayMap.Engine.Tests.Rendering;

/// <summary>BLEND SOLVE through the router, with no GPU: two projectors on a wall share its middle, so each
/// lands wholly on the stage, about half of each is shared and nothing is in shadow; the verb answers at once and
/// the solve runs on a worker, one at a time, its progress and outcome in STATE.solve and the journal; the maps go
/// out on the engine's event once the lane applies them, or are dropped when the stage moved meanwhile; an unmoved
/// stage is refused; and outputs that cannot take part say why.</summary>
public sealed class BlendSolveTests
{
    private static readonly SessionInfo Paired = new("t1", "wire", "10.0.0.9", false, "Test", true);

    /// <summary>A 6 × 3 m wall (a primitive plane) facing +Z, two projectors 4 m back and 2.4 m apart, aimed
    /// 0.9 m either side of the middle: each throws a 4 m wide picture, so they share the middle 2.2 m.</summary>
    internal static Project TwoProjectorsOnAWall(bool auto3D = true)
    {
        var wall = new MeshAsset("ms_wall", "Wall", MeshOrigin.Primitive, "", "", "plane",
            ParamSet.Empty.With("width", 6.0).With("height", 3.0).With("segments", 12), 0, 0);
        var surface = new Surface("sf_wall", "Wall", "ms_wall", Transform3.Identity, [], SurfaceContent.Empty, 1, [], true, false, "#ffffff", []);
        Projector At(string id, string name, double x, double aim) => new(id, name, Intrinsics.FromThrowRatio(1920, 1080, 1.0), LensDistortion.None,
            Pose.LookAt(new Vec3d(x, 0, 4), new Vec3d(aim, 0, 0), Vec3d.UnitY), 0, "#ffffff", false, [], SolveLocks.None, 0);
        Output For(string id, string name, string projector) => new(id, name, OutputKind.Virtual, "", 1920, 1080, 60, OutputMode.Projector, projector,
            SourceRef.None, new RectD(0, 0, 1920, 1080), OutputWarp.None, BlendSettings.Default with { Auto3D = auto3D }, ColorTrim.Neutral, [],
            new RectD(0, 0, 1920, 1080), 0, true);
        var stage = Stage.Empty with
        {
            Surfaces = [surface],
            Meshes = [wall],
            Projectors = [At("pj_left", "Left", -1.2, -0.9), At("pj_right", "Right", 1.2, 0.9)],
        };
        return Project.CreateDefault("Wall") with { Stage = stage, Outputs = [For("op_left", "Left", "pj_left"), For("op_right", "Right", "pj_right")] };
    }

    private static async Task<CommandReply> Run(EngineCore engine, string line)
    {
        Assert.True(WireGrammar.TryParse(line, out var parsed, out var error), error?.Reason);
        return await engine.HandleAsync(parsed, Paired, CancellationToken.None);
    }

    /// <summary>BLEND SOLVE's receipt: accepted, with the solve's id.</summary>
    private static string Accepted(CommandReply reply)
    {
        Assert.True(reply.Ok, reply.Payload);
        Assert.False(reply.Applied);
        var receipt = JsonDocument.Parse(reply.Payload!).RootElement;
        Assert.Equal("accepted", receipt.GetProperty("stage").GetString());
        return receipt.GetProperty("solve").GetString()!;
    }

    private static JsonElement SolveRow(EngineCore engine) => JsonDocument.Parse(engine.State().Json).RootElement.GetProperty("solve").Clone();

    [Fact]
    public async Task TwoOverlappingProjectorsOnAPlaneGiveSensibleFractions()
    {
        await using var engine = new EngineCore(EngineServices.ForTests(new ManualClock(5_000_000_000)), TwoProjectorsOnAWall());
        BlendSolveResult? published = null;
        engine.BlendSolved += r => published = r;

        var reply = await Run(engine, "BLEND SOLVE");
        var id = Accepted(reply);
        Assert.Equal("solving the blend for 2 outputs", reply.Effect);
        var end = await engine.WhenSolved.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(("done", id), (end!.State, end.Id));
        var row = SolveRow(engine);
        Assert.Equal("blend", row.GetProperty("kind").GetString());
        Assert.Equal("done", row.GetProperty("state").GetString());
        Assert.Equal(1, row.GetProperty("progress").GetDouble());
        var outputs = row.GetProperty("outputs").EnumerateArray().ToList();
        Assert.Equal(2, outputs.Count);
        foreach (var o in outputs)
        {
            var covered = o.GetProperty("covered").GetDouble();
            var overlap = o.GetProperty("overlap").GetDouble();
            Assert.True(covered > 0.98, $"{o.GetProperty("id")} lands {covered:P0} on the wall");
            // 2.2 m of each 4 m picture is shared: 55 %.
            Assert.InRange(overlap, 0.45, 0.65);
            Assert.Equal(0, o.GetProperty("shadow").GetDouble());
            Assert.Equal(2, o.GetProperty("maxOverlap").GetInt32());
            Assert.Single(o.GetProperty("overlapsWith").EnumerateArray());
            Assert.Equal(480, o.GetProperty("mapWidth").GetInt32());
            Assert.False(string.IsNullOrWhiteSpace(o.GetProperty("words").GetString()));
        }
        Assert.Empty(row.GetProperty("problems").EnumerateArray());
        Assert.Contains("2 projectors blended in 3D", row.GetProperty("words").GetString(), StringComparison.Ordinal);
        Assert.Contains(engine.Journal.Recent(10), r => r.Ok && r.Text.StartsWith("blend solved for 2 outputs", StringComparison.Ordinal));

        Assert.NotNull(published);
        Assert.Same(published, engine.LastBlendSolve);
        Assert.True(published.Inputs.Equals(BlendInputs.Of(engine.Store.Project)));
        var left = published.Solution.For("op_left")!.Map;
        // Far left of the left projector only it lights the wall (full weight); its right edge is shared and fades.
        Assert.Equal(1f, left.WeightAt(20, left.Height / 2), 2);
        Assert.True(left.WeightAt(left.Width - 4, left.Height / 2) < 0.2f);
    }

    [Fact]
    public async Task AnUnmovedStageIsRefusedAndAMovedOneSolvesAgain()
    {
        await using var engine = new EngineCore(EngineServices.ForTests(new ManualClock(5_000_000_000)), TwoProjectorsOnAWall());
        Accepted(await Run(engine, "BLEND SOLVE ALL"));
        Assert.Equal("done", (await engine.WhenSolved.WaitAsync(TimeSpan.FromSeconds(30)))!.State);
        var again = await Run(engine, "BLEND SOLVE");
        Assert.False(again.Ok);
        Assert.StartsWith("the blend is already solved for this stage", again.Payload, StringComparison.Ordinal);

        // A projector changed is a new stage: the solve runs again; naming one output checks it takes part.
        var set = await Run(engine, "SET pj_right lumens 6000");
        Assert.True(set.Ok, set.Payload);
        var one = await Run(engine, "BLEND SOLVE Right");
        Accepted(one);
        Assert.Contains("Right among them", one.Effect, StringComparison.Ordinal);
        Assert.Equal("done", (await engine.WhenSolved.WaitAsync(TimeSpan.FromSeconds(30)))!.State);
        Assert.True(engine.LastBlendSolve!.Inputs.Equals(BlendInputs.Of(engine.Store.Project)));
    }

    [Fact]
    public async Task ASolveLeavesTheLaneFreeRunsOneAtATimeAndShowsItsProgress()
    {
        await using var engine = new EngineCore(EngineServices.ForTests(new ManualClock(5_000_000_000)), TwoProjectorsOnAWall());
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.BlendSolver = async (inputs, progress, ct) =>
        {
            progress.Report(0.5);
            await release.Task.WaitAsync(ct);
            return inputs.Solve(engine.Meshes, progress, ct);
        };

        var id = Accepted(await Run(engine, "BLEND SOLVE"));
        // The lane answers other verbs while the solve is held open on its worker.
        Assert.True((await Run(engine, "BLACKOUT ON")).Ok);
        Assert.True(await OpsRig.Eventually(() => SolveRow(engine).GetProperty("progress").GetDouble() == 0.5));
        var row = SolveRow(engine);
        Assert.Equal((id, "running"), (row.GetProperty("id").GetString(), row.GetProperty("state").GetString()));
        var second = await Run(engine, "BLEND SOLVE");
        Assert.False(second.Ok);
        Assert.Contains("already running", second.Payload, StringComparison.Ordinal);
        Assert.Null(engine.LastBlendSolve);

        release.SetResult();
        Assert.Equal("done", (await engine.WhenSolved.WaitAsync(TimeSpan.FromSeconds(30)))!.State);
        Assert.NotNull(engine.LastBlendSolve);
    }

    [Fact]
    public async Task AResultForAStageThatMovedMeanwhileIsDroppedAndSaysSo()
    {
        await using var engine = new EngineCore(EngineServices.ForTests(new ManualClock(5_000_000_000)), TwoProjectorsOnAWall());
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var published = 0;
        engine.BlendSolved += _ => published++;
        engine.BlendSolver = async (inputs, progress, ct) =>
        {
            await release.Task.WaitAsync(ct);
            return inputs.Solve(engine.Meshes, progress, ct);
        };

        Accepted(await Run(engine, "BLEND SOLVE"));
        Assert.True((await Run(engine, "SET pj_right lumens 6000")).Ok);
        release.SetResult();
        var end = await engine.WhenSolved.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal("stale", end!.State);
        Assert.Contains("the result was dropped", end.Words, StringComparison.Ordinal);
        Assert.Equal("stale", SolveRow(engine).GetProperty("state").GetString());
        Assert.Null(engine.LastBlendSolve);
        Assert.Equal(0, published);
        Assert.Contains(engine.Journal.Recent(10), r => !r.Ok && r.Text.Contains("the stage changed while the blend was solved", StringComparison.Ordinal));

        // The stage as it is now solves.
        engine.BlendSolver = null;
        Accepted(await Run(engine, "BLEND SOLVE"));
        Assert.Equal("done", (await engine.WhenSolved.WaitAsync(TimeSpan.FromSeconds(30)))!.State);
        Assert.Equal(1, published);
    }

    [Fact]
    public async Task OutputsThatCannotTakePartSayWhy()
    {
        await using var engine = new EngineCore(EngineServices.ForTests(new ManualClock(5_000_000_000)), TwoProjectorsOnAWall(auto3D: false));
        var none = await Run(engine, "BLEND SOLVE");
        Assert.False(none.Ok);
        Assert.StartsWith("no output blends automatically", none.Payload, StringComparison.Ordinal);
        var manual = await Run(engine, "BLEND SOLVE op_left");
        Assert.False(manual.Ok);
        Assert.Contains("blends by hand", manual.Payload, StringComparison.Ordinal);
        Assert.False((await Run(engine, "BLEND SOLVE op_nowhere")).Ok);
        Assert.Null(engine.LastBlendSolve);
    }

    [Fact]
    public void BlendInputsMoveWithTheStageAndNotWithATrim()
    {
        var project = TwoProjectorsOnAWall();
        var a = BlendInputs.Of(project);
        var trimmed = project with { Outputs = project.Outputs.Update("op_left", o => o with { Color = ColorTrim.Neutral with { Brightness = 0.9 } }) };
        Assert.True(a.Equals(BlendInputs.Of(trimmed)));
        var moved = project.WithStage(s => s with { Projectors = s.Projectors.Update("pj_left", p => p with { Lumens = 5000 }) });
        Assert.False(a.Equals(BlendInputs.Of(moved)));
        var curve = project with { Outputs = project.Outputs.Update("op_left", o => o with { Blend = o.Blend with { Curve = BlendCurve.Linear } }) };
        Assert.False(a.Equals(BlendInputs.Of(curve)));
        Assert.Equal(["op_left", "op_right"], a.Outputs.Select(o => o.Id).ToArray());
    }
}
