using System.Globalization;
using PlayMap.Core.Bus;
using PlayMap.Core.Model;
using PlayMap.Core.Preflight;
using PlayMap.Core.Time;
using PlayMap.Core.Wire;
using PlayMap.Engine.Host;
using PlayMap.Engine.Show;
using PlayMap.Engine.Tests.Host;
using PlayMap.Net.Http;
using PlayMap.Net.Links;

namespace PlayMap.Engine.Tests.Show;

/// <summary>Preflight's breadth: mapping through the calibration ledger, the show clock, the links and the rest of the show's
/// safety. Each row fails on an injected fault, names a line that fixes it (a line of the catalogue), and passes once fixed.</summary>
public sealed class PreflightBreadthTests
{
    /// <summary>The network as a test sets it: the clock's role and uncertainty, the two links, and the token seen in clear.</summary>
    private sealed class Net : INetworkView, ITokenSightings
    {
        public SyncView SyncNow { get; set; } = new("solo", null, 0, 0, 0);
        public LinkView? PatternsNow { get; set; }
        public LinkView? StagewatchNow { get; set; }
        public TokenSighting? TokenSeenInClear { get; set; }

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
        Assert.Equal(Light.Green, solved.Light);
        Assert.StartsWith("A ", solved.Value, StringComparison.Ordinal);

        // A failed result made current over a good one: red, and the line that returns to the good one; it then passes.
        var good = rig.Engine.Store.Project.FindProjector(pj)!.CalibrationId!;
        await rig.Engine.Lane.RunAsync(() => rig.Engine.Store.Apply("a failed re-solve", p =>
        {
            var current = p.FindCalibration(good)!;
            // The failed re-solve moved the projector: the air stands as the bad result until FALLBACK puts the good one back.
            var moved = Pose.LookAt(new Core.Numerics.Vec3d(2, 1, -6), new Core.Numerics.Vec3d(0, 1.2, 0), Core.Numerics.Vec3d.UnitY);
            var failed = current with
            {
                Id = "cs0f0f0f0f",
                Score = current.Score with { Grade = "F", Value = 9, Words = "9.0 px: the re-solve failed" },
                Projector = current.Projector! with { Pose = moved },
            };
            return p with
            {
                Stage = p.Stage with
                {
                    Calibrations = p.Stage.Calibrations.Add(failed),
                    Projectors = p.Stage.Projectors.Update(pj, x => x with { Pose = moved, CalibrationId = failed.Id, FallbackId = good }),
                },
            };
        }));
        var failedRow = Assert.Single(await Check(new MappingCheck(), rig.Engine));
        Assert.Equal((Light.Red, $"CALIBRATION FALLBACK {pj}"), (failedRow.Light, failedRow.Fix));
        Parses(failedRow);
        await rig.Ok(failedRow.Fix!);
        Assert.Equal(Light.Green, Assert.Single(await Check(new MappingCheck(), rig.Engine)).Light);

        // A C grade is amber with a line to solve again (FALLBACK recorded the good result again as the current one).
        var standing = rig.Engine.Store.Project.FindProjector(pj)!.CalibrationId!;
        await rig.Engine.Lane.RunAsync(() => rig.Engine.Store.Apply("a fair result", p => p with
        {
            Stage = p.Stage with
            {
                Calibrations = p.Stage.Calibrations.Update(standing, c => c with { Score = c.Score with { Grade = "C", Value = 3 } }),
            },
        }));
        var fair = Assert.Single(await Check(new MappingCheck(), rig.Engine));
        Assert.Equal((Light.Amber, $"PROJECTOR SOLVE {pj}"), (fair.Light, fair.Fix));
    }

    [Fact]
    public async Task OneVerdictPerProjectorWhenTheLedgersOwnCheckRuns()
    {
        var wall = new FakeCheck("calibration");
        await using var rig = new ShowRig(services: s => s with { PreflightChecks = [wall] });
        await PinnedProjector(rig);
        Assert.Empty(await Check(new MappingCheck(), rig.Engine));
        Assert.Contains("calibration", PreflightReports.Steps.Single(st => st.Step == "mapping").Sections);
    }

    private sealed class FakeCheck(string section) : IPreflightCheck
    {
        public string Section => section;

        public PreflightDepth MinDepth => PreflightDepth.Quick;

        public TimeSpan Budget => TimeSpan.FromSeconds(1);

        public Task<IReadOnlyList<PreflightRow>> RunAsync(PreflightContext context, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<PreflightRow>>([]);
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

    private const string SpeaksForPatterns = "this key speaks for patterns 9f8e7d6c — make a key for playmap 0badc0de";

    [Fact]
    public async Task AStagewatchThatHoldsThisNodesRefusedKeyIsRedWithItsWordsAndNoLine()
    {
        var net = new Net { StagewatchNow = new LinkView("held", "10.0.0.6:9924", false, StagewatchLink.KeyRefused + "unknown or revoked API key") };
        await using var rig = new ShowRig(services: s => s with { Network = net });
        var row = (await Check(new LinksCheck(), rig.Engine)).Single(r => r.Id == "links:stagewatch");
        // The fix is on Stagewatch (a node key for this node, or the key let speak for it): no line here carries it.
        Assert.Equal((Light.Red, "held", (string?)null), (row.Light, row.Value, row.Fix));
        Assert.Equal("Stagewatch at 10.0.0.6:9924 refused this node's key — unknown or revoked API key — dialled again when its hold ends", row.Note);
        var item = ContextBuilder.Build(rig.Engine).Items.Single(i => i.Id == "link:stagewatch");
        Assert.Equal((Light.Red, "held, key refused — unknown or revoked API key"), (item.Light, item.State));

        // A refusal that names no key (the viewing code, asked of a node with none) is said as the far end's.
        net.StagewatchNow = new LinkView("held", "10.0.0.6:9924", false, "viewing code asked — Enter the viewing code");
        row = (await Check(new LinksCheck(), rig.Engine)).Single(r => r.Id == "links:stagewatch");
        Assert.Equal((Light.Red, "held", (string?)null), (row.Light, row.Value, row.Fix));
        Assert.Equal("Stagewatch at 10.0.0.6:9924 refused this node: viewing code asked — Enter the viewing code — dialled again when its hold ends", row.Note);

        // Live, its key refused: heard but not paired, amber with Stagewatch's words, in preflight and the context alike.
        net.StagewatchNow = new LinkView("live", "10.0.0.6:9924", false, StagewatchLink.KeyRefused + SpeaksForPatterns);
        row = (await Check(new LinksCheck(), rig.Engine)).Single(r => r.Id == "links:stagewatch");
        Assert.Equal((Light.Amber, "not paired", (string?)null), (row.Light, row.Value, row.Fix));
        Assert.Equal($"heard at 10.0.0.6:9924 but not paired — key refused — {SpeaksForPatterns}", row.Note);
        Assert.Equal(Light.Amber, ContextBuilder.Build(rig.Engine).Items.Single(i => i.Id == "link:stagewatch").Light);

        // Paired: green in both.
        net.StagewatchNow = new LinkView("live", "10.0.0.6:9924", true, "following Stagewatch at 10.0.0.6:9924 over TLS");
        Assert.Equal(Light.Green, (await Check(new LinksCheck(), rig.Engine)).Single(r => r.Id == "links:stagewatch").Light);
        Assert.Equal(Light.Green, ContextBuilder.Build(rig.Engine).Items.Single(i => i.Id == "link:stagewatch").Light);
    }

    [Fact]
    public async Task ANodesStagewatchLinkIsHeldWhileItsRefusedKeysHoldRunsAndThenSaysWhatItsDialMeets()
    {
        // A Stagewatch on this machine that refuses the key: the link reads its frames, unpaired, with Stagewatch's words. Once the
        // connection goes, the link is held, red in preflight with no line. Then Stagewatch itself goes: past the hold, on the node's
        // own clock, the link is not heard, red with the line that links it again, in what its dial met.
        var clock = new ManualClock(1_000 * 1_000_000_000L);
        var sw = await ScreensStagewatch.StartAsync();
        try
        {
            sw.Verdict = "{\"type\":\"auth\",\"ok\":false,\"error\":\"unknown or revoked API key\",\"retry\":60}";
            await using var rig = await HostRig.StartAsync(mono: clock);
            var address = "127.0.0.1:" + sw.Port.ToString(CultureInfo.InvariantCulture);
            await rig.OkAsync($"LINK STAGEWATCH {address} swk_refused0123456789");
            Assert.True(await Eventually.TrueAsync(() => rig.Journal().Contains($"network: following Stagewatch at {address} on a node link", StringComparison.Ordinal)),
                rig.Journal());
            Assert.True(await Eventually.TrueAsync(() => rig.Network.Stagewatch() is { State: "live", Paired: false } v
                && v.Detail.StartsWith(StagewatchLink.KeyRefused, StringComparison.Ordinal)), rig.LogTail());
            Assert.Equal(StagewatchLink.KeyRefused + "unknown or revoked API key", rig.Network.Stagewatch()!.Detail);
            Assert.Equal("live", Link(rig).GetProperty("state").GetString());
            Assert.False(Link(rig).GetProperty("paired").GetBoolean());

            sw.Drop();
            Assert.True(await Eventually.TrueAsync(() => rig.Network.Stagewatch()?.State == "held"), rig.LogTail());
            var row = (await Check(new LinksCheck(), rig.Engine)).Single(r => r.Id == "links:stagewatch");
            Assert.Equal((Light.Red, "held", (string?)null), (row.Light, row.Value, row.Fix));
            Assert.Equal($"Stagewatch at {address} refused this node's key — unknown or revoked API key — dialled again when its hold ends", row.Note);
            Assert.Equal("held", Link(rig).GetProperty("state").GetString());
            Assert.Equal(1, sw.Screens);

            await sw.DisposeAsync();
            clock.Advance((long)StagewatchLink.RefusedHold.TotalMilliseconds * 1_000_000 + 1);
            Assert.True(await Eventually.TrueAsync(() => rig.Network.Stagewatch() is { State: "down" or "connecting" } v
                && v.Detail.StartsWith($"Stagewatch at {address}: ", StringComparison.Ordinal), 20_000), rig.LogTail());
            row = (await Check(new LinksCheck(), rig.Engine)).Single(r => r.Id == "links:stagewatch");
            Assert.Equal((Light.Red, $"LINK STAGEWATCH {address}"), (row.Light, row.Fix));
            Assert.StartsWith($"Stagewatch at {address} is not heard (", row.Note, StringComparison.Ordinal);
            Assert.NotEqual("held", Link(rig).GetProperty("state").GetString());
            Assert.Equal(1, sw.Screens);
        }
        finally
        {
            await sw.DisposeAsync();
        }

        static System.Text.Json.JsonElement Link(HostRig rig) => rig.State().GetProperty("links").GetProperty("stagewatch");
    }

    [Fact]
    public async Task ANodeWithNoStagewatchKeyIsPairedOnceTheLinkIsLive()
    {
        await using var sw = await ScreensStagewatch.StartAsync();
        await using var rig = await HostRig.StartAsync();
        sw.Verdict = "{\"type\":\"auth\",\"ok\":true,\"scope\":\"none\",\"node\":{\"t\":\"playmap\",\"id\":\"" + rig.Id
            + "\",\"verified\":\"none\"},\"words\":\"no key — the id is only claimed\"}";
        var address = "127.0.0.1:" + sw.Port.ToString(CultureInfo.InvariantCulture);
        await rig.OkAsync($"LINK STAGEWATCH {address}");
        // No key set asks no proof: live is paired, green in preflight.
        Assert.True(await Eventually.TrueAsync(() => rig.Network.Stagewatch() is { State: "live", Paired: true }), rig.LogTail());
        Assert.Equal("", Assert.Single(sw.Authorizations));
        Assert.Equal(Light.Green, (await Check(new LinksCheck(), rig.Engine)).Single(r => r.Id == "links:stagewatch").Light);
    }

    [Fact]
    public async Task ATokenSeenInClearIsAmberWithTheLineThatMakesANewOne()
    {
        var net = new Net { PatternsNow = new LinkView("live", "10.0.0.5:9697", true, "no clip on air") };
        await using var rig = new ShowRig(services: s => s with { Network = net });
        Assert.DoesNotContain(await Check(new LinksCheck(), rig.Engine), r => r.Id == "links:token");

        net.TokenSeenInClear = new TokenSighting(new ClearSighting("10.0.0.9", ClearSighting.Wire), 1_700_000_000_000);
        var rows = await Check(new LinksCheck(), rig.Engine);
        var token = rows.Single(r => r.Id == "links:token");
        Assert.Equal((Light.Amber, "seen in clear", "PAIRING NEW"), (token.Light, token.Value, token.Fix));
        Assert.Contains("came in clear from 10.0.0.9 (plain AUTH on the line wire)", token.Note, StringComparison.Ordinal);
        Parses(token);
        // The links' own rows are as they were: a token seen in clear refuses nothing.
        Assert.Equal(Light.Green, rows.Single(r => r.Id == "links:patterns").Light);

        net.TokenSeenInClear = null;
        Assert.DoesNotContain(await Check(new LinksCheck(), rig.Engine), r => r.Id == "links:token");
    }

    [Fact]
    public async Task ANodesTokenSeenInClearIsJournalledOncePerAddressAndCarrierAndGoesWithANewToken()
    {
        await using var rig = await HostRig.StartAsync();
        var rows = new System.Collections.Concurrent.ConcurrentQueue<JournalRow>();
        rig.Engine.Journal.Written += rows.Enqueue;
        Assert.DoesNotContain(await Check(new LinksCheck(), rig.Engine), r => r.Id == "links:token");

        // A client that sends the token with every request writes one row per address and carrier; the mark keeps the latest.
        rig.Network.OnTokenSeenInClear(new ClearSighting("10.0.0.9", ClearSighting.Http));
        rig.Network.OnTokenSeenInClear(new ClearSighting("10.0.0.9", ClearSighting.Http));
        rig.Network.OnTokenSeenInClear(new ClearSighting("10.0.0.9", ClearSighting.Wire));
        Assert.Equal(2, rows.Count(r => r.Text.StartsWith("this node's pairing token came in clear from 10.0.0.9", StringComparison.Ordinal)));
        Assert.All(rows.Where(r => r.Text.Contains("came in clear", StringComparison.Ordinal)), r => Assert.Equal(("network", false), (r.Kind, r.Ok)));
        Assert.DoesNotContain(rows, r => r.Text.Contains(HostRig.Token, StringComparison.Ordinal) || r.Text.Contains(Pairing.Normalise(HostRig.Token), StringComparison.Ordinal));
        var token = (await Check(new LinksCheck(), rig.Engine)).Single(r => r.Id == "links:token");
        Assert.Equal((Light.Amber, "PAIRING NEW"), (token.Light, token.Fix));
        Assert.Contains("(plain AUTH on the line wire)", token.Note, StringComparison.Ordinal);

        // A new token is not the one seen: the mark goes, and the next sighting is the new token's own row.
        await rig.OkAsync(token.Fix!);
        Assert.DoesNotContain(await Check(new LinksCheck(), rig.Engine), r => r.Id == "links:token");
        rig.Network.OnTokenSeenInClear(new ClearSighting("10.0.0.9", ClearSighting.Http));
        Assert.Equal(3, rows.Count(r => r.Text.StartsWith("this node's pairing token came in clear", StringComparison.Ordinal)));
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
