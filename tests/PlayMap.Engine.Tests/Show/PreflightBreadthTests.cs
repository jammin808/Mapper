using System.Globalization;
using PlayMap.Core.Bus;
using PlayMap.Core.Model;
using PlayMap.Core.Preflight;
using PlayMap.Core.Wire;
using PlayMap.Engine.Show;

namespace PlayMap.Engine.Tests.Show;

/// <summary>Preflight's breadth: mapping through the calibration ledger, the show clock, the links and the rest of the show's
/// safety. Each row fails on an injected fault, names a line that fixes it (a line of the catalogue), and passes once fixed.</summary>
public sealed class PreflightBreadthTests
{
    /// <summary>The network as a test sets it: the clock's role and uncertainty, and the two links.</summary>
    private sealed class Net : INetworkView
    {
        public SyncView SyncNow { get; set; } = new("solo", null, 0, 0, 0);
        public LinkView? PatternsNow { get; set; }
        public LinkView? StagewatchNow { get; set; }

        public IReadOnlyList<PeerView> Peers() => [];
        public LinkView? Patterns() => PatternsNow;
        public LinkView? Stagewatch() => StagewatchNow;
        public string? StagewatchTimerJson() => null;
        public SyncView Sync() => SyncNow;
    }

    private static async Task<IReadOnlyList<PreflightRow>> Check(IPreflightCheck check, EngineCore engine) =>
        await check.RunAsync(new PreflightContext(engine, PreflightDepth.Quick, engine.ShowNow.Armed), CancellationToken.None);

    private static void Parses(PreflightRow row)
    {
        Assert.NotNull(row.Fix);
        Assert.True(WireGrammar.TryParse(row.Fix, out var line, out var error), error?.Reason);
        Assert.NotNull(VerbCatalog.Match(line!));
    }

    /// <summary>A projector on an output, with ten pins that solve (on a box and a wall, from a known lens and pose).</summary>
    private static async Task<string> PinnedProjector(ShowRig rig)
    {
        var truthK = Intrinsics.FromThrowRatio(1920, 1080, 1.3, 0, 0.4);
        var truthPose = Pose.LookAt(new Core.Numerics.Vec3d(0.4, 1.6, -6), new Core.Numerics.Vec3d(0, 1.2, 0), Core.Numerics.Vec3d.UnitY);
        var pj = await rig.Ok("ADD projector {\"name\":\"Left\"}");
        await rig.Ok($"SET op_1 mode \"projector\"");
        await rig.Ok($"SET op_1 projectorId \"{pj}\"");
        (double, double, double)[] world =
        [
            (0, 0, 0), (1, 0, 0), (1, 1, 0), (0, 1, 0), (-1.5, 0, 1), (-1.5, 2.5, 1), (1.8, 2.4, 1), (1.8, 0.2, 1), (0.5, 0.5, -0.5), (0.2, 1.8, 0.6),
        ];
        foreach (var (x, y, z) in world)
        {
            var px = Core.Geometry.CameraModel.Project(new Core.Numerics.Vec3d(x, y, z), truthK, LensDistortion.None, truthPose)!.Value;
            await rig.Ok(string.Create(CultureInfo.InvariantCulture,
                $"ADD pin {pj} {{\"world\":{{\"x\":{x},\"y\":{y},\"z\":{z}}},\"pixel\":{{\"x\":{px.X},\"y\":{px.Y}}}}}"));
        }
        return pj;
    }

    [Fact]
    public async Task AProjectorWithNoSolvedCalibrationIsRedUntilItIsSolved()
    {
        await using var rig = new ShowRig();
        var none = Assert.Single(await Check(new MappingCheck(), rig.Engine));
        Assert.Equal(("mapping:none", Light.Grey), (none.Id, none.Light));

        var pj = await PinnedProjector(rig);
        var unsolved = Assert.Single(await Check(new MappingCheck(), rig.Engine));
        Assert.Equal(("mapping:" + pj, Light.Red, "not solved"), (unsolved.Id, unsolved.Light, unsolved.Value));
        Assert.Equal($"PROJECTOR SOLVE {pj}", unsolved.Fix);
        Parses(unsolved);

        await rig.Ok(unsolved.Fix!);
        var solved = Assert.Single(await Check(new MappingCheck(), rig.Engine));
        Assert.NotEqual(Light.Red, solved.Light);
        Assert.StartsWith(rig.Engine.CalibrationLedgerService.ScoreOf(rig.Engine.Store.Project, pj).Grade + " ", solved.Value, StringComparison.Ordinal);
        Assert.Contains(solved.Light, new[] { Light.Green, Light.Amber });
        if (solved.Light == Light.Amber)
            Parses(solved);
    }

    [Fact]
    public async Task TheClockIsJudgedByTheUncertaintyOfTheLeadersTime()
    {
        var net = new Net();
        await using var rig = new ShowRig(services: s => s with { Network = net });
        Assert.Equal((Light.Grey, "solo"), Row(await Check(new ClockCheck(), rig.Engine)));

        // A playlist that follows a group this node does not lead or follow.
        await rig.Ok("SET pl_show syncGroup \"stage\"");
        var orphan = Assert.Single(await Check(new ClockCheck(), rig.Engine));
        Assert.Equal((Light.Amber, "SYNC LEAD stage"), (orphan.Light, orphan.Fix));
        Parses(orphan);

        net.SyncNow = new SyncView("follower", "0badc0de", 6.5, 13, 2);
        var far = Assert.Single(await Check(new ClockCheck(), rig.Engine));
        Assert.Equal((Light.Red, "± 6.50 ms", "SYNC FOLLOW stage 0badc0de"), (far.Light, far.Value, far.Fix));
        Parses(far);
        net.SyncNow = new SyncView("follower", "0badc0de", 2, 4, 2);
        Assert.Equal(Light.Amber, Assert.Single(await Check(new ClockCheck(), rig.Engine)).Light);
        net.SyncNow = new SyncView("follower", "0badc0de", 0.4, 0.8, 2);
        Assert.Equal((Light.Green, "± 0.40 ms"), Row(await Check(new ClockCheck(), rig.Engine)));
        net.SyncNow = new SyncView("leader", "0badc0de", 0, 0, 0);
        Assert.Equal((Light.Green, "leads"), Row(await Check(new ClockCheck(), rig.Engine)));
    }

    private static (Light, string) Row(IReadOnlyList<PreflightRow> rows) => (Assert.Single(rows).Light, rows[0].Value);

    [Fact]
    public async Task ALinkedPeerNotHeardIsRedWithTheLineThatLinksItAgain()
    {
        var net = new Net();
        await using var rig = new ShowRig(services: s => s with { Network = net });
        var rows = await Check(new LinksCheck(), rig.Engine);
        Assert.All(rows, r => Assert.Equal((Light.Grey, "not linked"), (r.Light, r.Value)));

        net.PatternsNow = new LinkView("down", "10.0.0.5:7000", false, "");
        net.StagewatchNow = new LinkView("stale", "10.0.0.6", false, "");
        rows = await Check(new LinksCheck(), rig.Engine);
        var patterns = rows.Single(r => r.Id == "links:patterns");
        var stagewatch = rows.Single(r => r.Id == "links:stagewatch");
        Assert.Equal((Light.Red, "LINK PATTERNS 10.0.0.5:7000"), (patterns.Light, patterns.Fix));
        Assert.Equal((Light.Red, "LINK STAGEWATCH 10.0.0.6"), (stagewatch.Light, stagewatch.Fix));
        Parses(patterns);
        Parses(stagewatch);

        // Heard but not paired: amber, and no line (its token is a secret).
        net.PatternsNow = new LinkView("live", "10.0.0.5:7000", false, "wrong token — LINK PATTERNS with the desk's pairing token");
        patterns = (await Check(new LinksCheck(), rig.Engine)).Single(r => r.Id == "links:patterns");
        Assert.Equal((Light.Amber, (string?)null), (patterns.Light, patterns.Fix));

        net.PatternsNow = new LinkView("live", "10.0.0.5:7000", true, "no clip on air");
        net.StagewatchNow = new LinkView("live", "10.0.0.6", true, "");
        Assert.All(await Check(new LinksCheck(), rig.Engine), r => Assert.Equal(Light.Green, r.Light));
    }

    [Fact]
    public async Task TheShowsSafetyNamesWhatWouldGoWrongInAPanicOrAGo()
    {
        await using var rig = new ShowRig();
        await rig.Ok("SHOW SET MINGO 0");
        var mingo = (await Check(new SafetyCheck(), rig.Engine)).Single(r => r.Id == "safety:mingo");
        Assert.Equal((Light.Amber, "SHOW SET MINGO 300"), (mingo.Light, mingo.Fix));
        Parses(mingo);
        await rig.Ok(mingo.Fix!);
        Assert.DoesNotContain(await Check(new SafetyCheck(), rig.Engine), r => r.Id == "safety:mingo");

        // A panic in force is red until it is cleared.
        await rig.Ok("PANIC HARD");
        var panic = (await Check(new SafetyCheck(), rig.Engine)).Single(r => r.Id == "safety:in-panic");
        Assert.Equal((Light.Red, "PANIC CLEAR"), (panic.Light, panic.Fix));
        Parses(panic);
        await rig.Ok("PANIC CLEAR");
        Assert.DoesNotContain(await Check(new SafetyCheck(), rig.Engine), r => r.Id == "safety:in-panic");

        // A plan for an output the show no longer has, and a draft left open, are amber.
        await rig.Ok("PANIC SET op_1 BLACK FADE 500");
        await rig.Ok("ADD output {\"name\":\"Spare\"}");
        var spare = rig.Engine.Store.Project.Outputs[^1].Id;
        await rig.Ok($"PANIC SET {spare} HARD");
        await rig.Ok($"REMOVE {spare}");
        Assert.Equal(Light.Amber, (await Check(new SafetyCheck(), rig.Engine)).Single(r => r.Id == "safety:plan:" + spare).Light);
        await rig.Ok("BLIND ON");
        await rig.Ok("SET cv_main background \"#FF0000\"");
        var blind = (await Check(new SafetyCheck(), rig.Engine)).Single(r => r.Id == "safety:blind");
        Assert.Equal((Light.Amber, "1 staged"), (blind.Light, blind.Value));
        await rig.Ok("BLIND DISCARD");
        Assert.DoesNotContain(await Check(new SafetyCheck(), rig.Engine), r => r.Id == "safety:blind");
    }

    [Fact]
    public async Task TheNewSectionsAreInTheNodesPreflightAndInShowReady()
    {
        await using var rig = new ShowRig();
        var sections = rig.Engine.PreflightChecks.Select(c => c.Section).ToHashSet();
        Assert.Superset(new HashSet<string> { "mapping", "clock", "links", "safety" }, sections);
        var steps = PreflightReports.Steps.ToDictionary(s => s.Step, s => s.Sections);
        Assert.Contains("mapping", steps["mapping"]);
        Assert.Contains("clock", steps["sync and backup"]);
        Assert.Contains("links", steps["sync and backup"]);
    }
}
