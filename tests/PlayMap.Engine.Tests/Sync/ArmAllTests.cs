using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using PlayMap.Core.Bus;
using PlayMap.Core.Preflight;
using PlayMap.Core.Time;
using PlayMap.Core.Wire;
using PlayMap.Engine.Host;
using PlayMap.Engine.Sync;
using PlayMap.Engine.Tests.Host;

namespace PlayMap.Engine.Tests.Sync;

/// <summary>Show's group forms over the sync part's real fan-out (PLAN §6.1, sync × blind): a leader and two members, real nodes in
/// one process on one manual clock (the leader's 2 s ahead), finding each other over loopback. SHOW ARM ALL arms every member at
/// the leader's instant with one receipt each; a member that refuses says why in its receipt and an unreachable one reads silent;
/// SHOW DISARM ALL is the desk's only; PREFLIGHT ALL goes out once and lists one section per node, each member's from its own
/// report, which rides in its receipt and in no journal; a member's preflight never holds the group's air behind it; the leader's
/// PANIC reaches each member exactly once.</summary>
public sealed class ArmAllTests
{
    private const long Second = SyncRig.Second;
    private const long Ahead = 2 * Second;
    private const string StageId = SyncRig.FollowerId;
    private const string WingId = "5a1e7c3d";
    private const string GhostId = "77e0d1a2";

    [Fact]
    public async Task ArmAllArmsEveryMemberAtTheLeadersInstantAndEachReceiptSaysWhatThatMemberDid()
    {
        var clock = new ManualClock(1_000 * Second);
        await using var leader = await StartAsync("FOH", SyncRig.LeaderId, new OffsetClock(clock, Ahead));
        await using var stage = await StartAsync("Stage", StageId, clock);
        await using var wing = await StartAsync("Wing", WingId, clock);
        await GroupAsync(leader, stage, wing);
        await wing.OkAsync("SHOW SET DISARM-FROM DESK");
        var events = Listen(stage, wing);

        // ARM ALL: this node and each member, one receipt per member, every one applied at the leader's instant.
        var at = leader.Engine.LeaderNowNs;
        var armed = JsonDocument.Parse(await leader.OkAsync("SHOW ARM ALL doors")).RootElement;
        Assert.True(armed.GetProperty("armed").GetBoolean());
        Assert.Equal(2, armed.GetProperty("members").GetInt32());
        var nodes = armed.GetProperty("nodes").EnumerateArray().ToList();
        Assert.Equal([SyncRig.LeaderId, StageId, WingId], nodes.Select(n => n.GetProperty("node").GetString()));
        Assert.Equal(["FOH", "Stage", "Wing"], nodes.Select(n => n.GetProperty("name").GetString()));
        Assert.All(nodes, n => Assert.Equal(("applied", at), (n.GetProperty("stage").GetString(), n.GetProperty("appliedLeaderNs").GetInt64())));
        foreach (var (member, heard) in new[] { (stage, events[0]), (wing, events[1]) })
        {
            Assert.True(member.Engine.ShowNow.Armed);
            Assert.Equal(("FOH (sync leader)", "doors"), (member.Engine.ShowNow.By, member.Engine.ShowNow.Words));
            Assert.Equal(at, Assert.Single(heard, e => e.Kind == "show.armed").AtLeaderNs);
        }
        Assert.Single(Rows(leader), r => r.Text.StartsWith("SHOW ARM \"doors\" on Stage: applied", StringComparison.Ordinal));
        Assert.Single(Rows(leader), r => r.Text.StartsWith("SHOW ARM \"doors\" on Wing: applied", StringComparison.Ordinal));

        // The group's DISARM is the desk's: a remote client of the leader may not send it, and every node stays armed.
        await using (var remote = await leader.WireAsync())
        {
            Assert.Equal("ERR @r SHOW DISARM ALL disarms every node of the group: the desk's (an operator's) only", await remote.AskAsync("@r SHOW DISARM ALL"));
        }
        Assert.True(leader.Engine.ShowNow.Armed && stage.Engine.ShowNow.Armed && wing.Engine.ShowNow.Armed);

        // From the desk: a member that keeps disarming to its own desk refuses, and its receipt carries its words.
        var off = JsonDocument.Parse(await leader.OkAsync("SHOW DISARM ALL")).RootElement.GetProperty("nodes").EnumerateArray().ToList();
        Assert.Equal(["applied", "applied", "refused"], off.Select(n => n.GetProperty("stage").GetString()));
        Assert.Contains("DISARM-FROM DESK", off[2].GetProperty("words").GetString(), StringComparison.Ordinal);
        Assert.False(leader.Engine.ShowNow.Armed || stage.Engine.ShowNow.Armed);
        Assert.True(wing.Engine.ShowNow.Armed);
        Assert.Single(Rows(leader), r => r.Kind == "refused" && r.Text.StartsWith("SHOW DISARM on Wing: refused — ", StringComparison.Ordinal)
            && r.Text.Contains("DISARM-FROM DESK", StringComparison.Ordinal));

        // A member that subscribed and then never answers reads silent; the others answer as they are.
        await using var ghost = await leader.WireAsync();
        Assert.StartsWith("OK @g", await ghost.AskAsync("@g SUBSCRIBE ANCHORS " + GhostId), StringComparison.Ordinal);
        var fanout = leader.Engine.Services.Fanout!;
        Assert.True(await Eventually.TrueAsync(() => fanout.Members.Contains(GhostId)));
        var again = JsonDocument.Parse(await leader.OkAsync("SHOW ARM ALL")).RootElement;
        Assert.Equal(3, again.GetProperty("members").GetInt32());
        var receipts = again.GetProperty("nodes").EnumerateArray().ToDictionary(n => n.GetProperty("node").GetString()!, n => n.Clone());
        Assert.Equal(["applied", "applied", "refused", "silent"], new[] { SyncRig.LeaderId, StageId, WingId, GhostId }.Select(id => receipts[id].GetProperty("stage").GetString()));
        Assert.StartsWith("already armed by FOH (sync leader)", receipts[WingId].GetProperty("words").GetString(), StringComparison.Ordinal);
        Assert.StartsWith("no answer within", receipts[GhostId].GetProperty("words").GetString(), StringComparison.Ordinal);
        Assert.True(stage.Engine.ShowNow.Armed);
    }

    [Fact]
    public async Task PreflightAllGoesOutOnceAndListsEachMembersOwnReportUnderItsName()
    {
        var clock = new ManualClock(1_000 * Second);
        await using var leader = await StartAsync("FOH", SyncRig.LeaderId, new OffsetClock(clock, Ahead));
        await using var stage = await StartAsync("Stage", StageId, clock);
        await using var wing = await StartAsync("Wing", WingId, clock);
        await GroupAsync(leader, stage, wing);
        await stage.OkAsync("SHOW SET MINGO 0");

        var done = new TaskCompletionSource<PreflightReport>(TaskCreationOptions.RunContinuationsAsynchronously);
        leader.Engine.PreflightDone += r => done.TrySetResult(r);
        var accepted = await leader.RunAsync("PREFLIGHT ALL");
        Assert.True(accepted.Ok && !accepted.Applied, accepted.Payload);
        var started = JsonDocument.Parse(accepted.Payload!).RootElement;
        Assert.Equal(2, started.GetProperty("members").GetInt32());
        var report = await done.Task.WaitAsync(TimeSpan.FromSeconds(60));
        Assert.Equal(started.GetProperty("id").GetString(), report.Id);

        // One section per node, each member's from its own report: rows only that node can write (its link to the leader), and
        // its fix moved into the note, since it runs on the member.
        Assert.Equal(["FOH", "Stage", "Wing"], report.Rows.Select(r => r.Node).Distinct());
        foreach (var name in new[] { "Stage", "Wing" })
            Assert.Equal(Light.Green, report.Rows.Single(r => r.Node == name && r.Id == "sync:feed").Light);
        Assert.Equal(Light.Green, report.Rows.Single(r => r.Node == "FOH" && r.Id == "sync:members").Light);
        var mingo = report.Rows.Single(r => r.Node == "Stage" && r.Id == "safety:mingo");
        Assert.Equal(Light.Amber, mingo.Light);
        Assert.Null(mingo.Fix);
        Assert.EndsWith("— on Stage: SHOW SET MINGO 300", mingo.Note, StringComparison.Ordinal);
        Assert.All(new[] { StageId, WingId }, id => Assert.Equal("answered", report.Rows.Single(r => r.Id == "fleet:" + id).Value));
        var sections = JsonDocument.Parse(await leader.OkAsync("PREFLIGHT REPORT " + report.Id)).RootElement.GetProperty("nodes");
        Assert.Equal(["FOH", "Stage", "Wing"], sections.EnumerateArray().Select(n => n.GetProperty("node").GetString()));

        // Sent once: each member ran one preflight for it, and the leader keeps one receipt row per member, none carrying a report.
        foreach (var (member, name) in new[] { (stage, "Stage"), (wing, "Wing") })
        {
            Assert.True(await Eventually.TrueAsync(() => Rows(member).Any(r => r.Kind == "preflight")), member.Journal());
            Assert.Single(Rows(member), r => r.Kind == "preflight");
            Assert.Equal("PREFLIGHT QUICK WAIT on " + name + ": applied", Assert.Single(Rows(leader), r => r.Text.StartsWith("PREFLIGHT QUICK WAIT on " + name, StringComparison.Ordinal)).Text);
        }

        // The report rides in the receipt only: no node's journal holds it.
        foreach (var node in new[] { leader, stage, wing })
            Assert.DoesNotContain(Rows(node), r => r.Text.Contains("\"rows\"", StringComparison.Ordinal) || r.Text.TrimStart().StartsWith('{'));
    }

    [Fact]
    public async Task AMembersPreflightNeverHoldsTheGroupsAirAndItsReportOutlastsAnAirLinesDeadline()
    {
        var clock = new ManualClock(1_000 * Second);
        var air = TimeSpan.FromMilliseconds(500);
        // The leader's deadlines run on this test's clock: an answer is never raced against them, and they pass when the test says.
        var time = new Fleet.ManualTime();
        var held = new HeldCheck();
        await using var leader = await StartAsync("FOH", SyncRig.LeaderId, new OffsetClock(clock, Ahead), SyncRig.Fast() with { ReceiptWait = air, Time = time });
        await using var stage = await StartAsync("Stage", StageId, clock, check: held);
        await using var wing = await StartAsync("Wing", WingId, clock);
        await GroupAsync(leader, stage, wing);

        var done = new TaskCompletionSource<PreflightReport>(TaskCreationOptions.RunContinuationsAsynchronously);
        leader.Engine.PreflightDone += r => done.TrySetResult(r);
        await leader.OkAsync("PREFLIGHT ALL");
        await held.Started.Task.WaitAsync(TimeSpan.FromSeconds(30));

        // Stage's preflight is still checking: the leader's PANIC lands there all the same, at the leader's instant.
        var events = Listen(stage);
        var at = leader.Engine.LeaderNowNs + leader.Engine.Store.Project.Show.Rules.PanicLeadMs * 1_000_000L;
        await leader.OkAsync("PANIC FADE 1000");
        Assert.True(await Eventually.TrueAsync(() => events[0].Any(e => e.Kind == "panic")), stage.Journal());
        Assert.Equal(at, Assert.Single(events[0], e => e.Kind == "panic").AtLeaderNs);
        Assert.False(done.Task.IsCompleted);   // the group's preflight still waits for Stage's report

        // A member whose checks take longer than an air line's deadline still answers with its report.
        // The group's preflight waits on its report deadline on the test's clock; an air line's deadline passes there, the report's not.
        Assert.True(await Eventually.TrueAsync(() => time.Waiting > 0), "the preflight never waited on its report deadline");
        time.Advance(air * 2);
        held.Release();
        var report = await done.Task.WaitAsync(TimeSpan.FromSeconds(60));
        var stageRow = report.Rows.Single(r => r.Id == "fleet:" + StageId);
        Assert.Equal("answered", stageRow.Value);
        Assert.Contains(report.Rows, r => r.Node == "Stage" && r.Section == HeldCheck.Name);
        Assert.Equal("answered", report.Rows.Single(r => r.Id == "fleet:" + WingId).Value);
    }

    [Fact]
    public async Task ThePanicOnTheLeaderReachesEachMemberExactlyOnce()
    {
        var clock = new ManualClock(1_000 * Second);
        await using var leader = await StartAsync("FOH", SyncRig.LeaderId, new OffsetClock(clock, Ahead));
        await using var stage = await StartAsync("Stage", StageId, clock);
        await using var wing = await StartAsync("Wing", WingId, clock);
        await GroupAsync(leader, stage, wing);
        var events = Listen(stage, wing);

        var at = leader.Engine.LeaderNowNs + leader.Engine.Store.Project.Show.Rules.PanicLeadMs * 1_000_000L;
        await leader.OkAsync("PANIC FADE 1000");
        // A later line on the same feed: once each member has taken it, a second send of the panic would have reached it first.
        await leader.OkAsync("MASTER 80");
        foreach (var name in new[] { "Stage", "Wing" })
        {
            Assert.True(await Eventually.TrueAsync(() => Rows(leader).Any(r => r.Text.StartsWith("MASTER 80 on " + name + ":", StringComparison.Ordinal))), leader.Journal());
            Assert.Single(Rows(leader), r => r.Text.StartsWith("PANIC FADE 1000 on " + name + ":", StringComparison.Ordinal));
            Assert.StartsWith("PANIC FADE 1000 on " + name + ": applied", Rows(leader).Single(r => r.Text.StartsWith("PANIC FADE 1000 on " + name + ":", StringComparison.Ordinal)).Text, StringComparison.Ordinal);
        }
        foreach (var (member, heard) in new[] { (stage, events[0]), (wing, events[1]) })
        {
            Assert.Equal(at, Assert.Single(heard, e => e.Kind == "panic").AtLeaderNs);
            Assert.Single(Rows(member), r => r.Line == "PANIC FADE 1000");
            Assert.DoesNotContain(Rows(member), r => r.Text.Contains("already in this panic", StringComparison.Ordinal));
            Assert.Equal(0.5 * 0.8, member.Engine.AirGain("op_1", at + 500_000_000), 1e-6);   // half way through the panic, under the master
        }
    }

    [Fact]
    public async Task AMemberLandsTheLeadersPanicAtTheLeadersLeadAndItsOwnAtItsOwn()
    {
        var clock = new ManualClock(1_000 * Second);
        await using var leader = await StartAsync("FOH", SyncRig.LeaderId, new OffsetClock(clock, Ahead));
        await using var stage = await StartAsync("Stage", StageId, clock);
        await GroupAsync(leader, stage);
        await leader.OkAsync("SHOW SET PANIC-LEAD 200");
        await stage.OkAsync("SHOW SET PANIC-LEAD 40");
        var events = Listen(leader, stage);
        var now = leader.Engine.LeaderNowNs;
        const long Ms = 1_000_000;

        // The leader's lead is the group's: each member fades on the leader's frames, whatever lead its own show keeps.
        await leader.OkAsync("PANIC FADE 1000");
        Assert.True(await Eventually.TrueAsync(() => events[1].Any(e => e.Kind == "panic")), stage.Journal());
        Assert.Equal(now + 200 * Ms, Assert.Single(events[0], e => e.Kind == "panic").AtLeaderNs);
        Assert.Equal(now + 200 * Ms, Assert.Single(events[1], e => e.Kind == "panic").AtLeaderNs);

        // A panic at an instant of its own lands that far ahead of it, the leader's lead still the group's.
        await leader.OkAsync("PANIC CLEAR FADE 0");
        Assert.True(await Eventually.TrueAsync(() => events[1].Any(e => e.Kind == "panic.clear")), stage.Journal());
        await leader.OkAsync("PANIC FADE 1000 AT +500");
        Assert.True(await Eventually.TrueAsync(() => events[1].Count(e => e.Kind == "panic") == 2), stage.Journal());
        Assert.Equal([now + 200 * Ms, now + 700 * Ms], events[0].Where(e => e.Kind == "panic").Select(e => e.AtLeaderNs));
        Assert.Equal([now + 200 * Ms, now + 700 * Ms], events[1].Where(e => e.Kind == "panic").Select(e => e.AtLeaderNs));

        // The member's own panic, from its own desk, keeps its own lead.
        await leader.OkAsync("PANIC CLEAR FADE 0");
        Assert.True(await Eventually.TrueAsync(() => events[1].Count(e => e.Kind == "panic.clear") == 2), stage.Journal());
        await stage.OkAsync("PANIC FADE 1000");
        Assert.True(await Eventually.TrueAsync(() => events[1].Count(e => e.Kind == "panic") == 3), stage.Journal());
        Assert.Equal(now + 40 * Ms, events[1].Last(e => e.Kind == "panic").AtLeaderNs);
    }

    [Fact]
    public void AReceiptCarriesTheMembersReportOnlyForALineThatAsksForItAndOnlyWhileItFitsOneLine()
    {
        static WireLine Line(string text)
        {
            Assert.True(WireGrammar.TryParse(text, out var line, out var error), error?.Reason);
            return line;
        }
        const string Report = "{\"id\":\"pf1\",\"rows\":[]}";
        Assert.Equal(Report, SyncNode.ReceiptWords(Line("PREFLIGHT QUICK WAIT"), CommandReply.Success(Report, "preflight pf1 by FOH")));
        Assert.Equal("preflight pf1 started by FOH", SyncNode.ReceiptWords(Line("PREFLIGHT"), new CommandReply(true, "{\"id\":\"pf1\"}", "preflight pf1 started by FOH", Applied: false)));
        Assert.Equal("armed by FOH", SyncNode.ReceiptWords(Line("SHOW ARM"), CommandReply.Success("{\"armed\":true}", "armed by FOH")));
        Assert.Equal("not armed", SyncNode.ReceiptWords(Line("SHOW DISARM"), CommandReply.Fail("not armed")));
        var huge = "{\"rows\":\"" + new string('x', WireGrammar.MaxLineBytes) + "\"}";
        Assert.StartsWith("its report is longer than one line", SyncNode.ReceiptWords(Line("PREFLIGHT QUICK WAIT"), CommandReply.Success(huge, "preflight pf2 by FOH")), StringComparison.Ordinal);
    }

    /// <summary>A node of the group: the sync part paced fast, and an extra preflight check when asked.</summary>
    private static Task<HostRig> StartAsync(string name, string id, IMonoClock mono, SyncNodeOptions? options = null, IPreflightCheck? check = null) =>
        HostRig.StartAsync(name, network: true, mono: mono,
            parts: new EngineParts { Extensions = check is null ? [SyncExtension.With(options ?? SyncRig.Fast())] : [SyncExtension.With(options ?? SyncRig.Fast()), _ => new CheckPart(check)] },
            settings: s => s with { NodeId = id });

    /// <summary>The leader leads "show" and each member, once it hears the leader, follows it; every node has the test show open.</summary>
    private static async Task GroupAsync(HostRig leader, params HostRig[] members)
    {
        await SyncRig.OpenShowAsync(leader);
        foreach (var m in members)
            await SyncRig.OpenShowAsync(m);
        leader.Network.Hello!.Targets = [.. members.Select(m => new IPEndPoint(IPAddress.Loopback, m.Network.HelloPort))];
        await leader.OkAsync("SYNC LEAD show");
        foreach (var m in members)
        {
            m.Network.Hello!.Targets = [new IPEndPoint(IPAddress.Loopback, leader.Network.HelloPort)];
            Assert.True(await Eventually.TrueAsync(() => m.Network.Hello!.Peers.Find(leader.Id)?.Hello.Port("sync") == leader.Network.SyncPort));
            await m.OkAsync("SYNC FOLLOW show " + leader.Id);
            await SyncRig.CaughtUpAsync(leader, m);
        }
        Assert.Equal(members.Select(m => m.Id), leader.Engine.Services.Fanout!.Members);
    }

    private static ConcurrentQueue<ShowEvent>[] Listen(params HostRig[] nodes) =>
        [.. nodes.Select(n =>
        {
            var q = new ConcurrentQueue<ShowEvent>();
            n.Engine.EventRaised += q.Enqueue;
            return q;
        })];

    private static IReadOnlyList<JournalRow> Rows(HostRig rig) => rig.Engine.Journal.Recent(500);

    /// <summary>A member's own check that runs until the test lets it end (its budget is far beyond the test's).</summary>
    private sealed class HeldCheck : IPreflightCheck
    {
        public const string Name = "held";

        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Section => Name;

        public PreflightDepth MinDepth => PreflightDepth.Quick;

        public TimeSpan Budget => TimeSpan.FromSeconds(60);

        public void Release() => _release.TrySetResult();

        public async Task<IReadOnlyList<PreflightRow>> RunAsync(PreflightContext context, CancellationToken ct)
        {
            Started.TrySetResult();
            await _release.Task.WaitAsync(ct);
            return [new PreflightRow("held", context.Engine.Services.Settings.Name, Name, "Held", Light.Green, "done", "checked once released", null)];
        }
    }

    /// <summary>Adds one preflight check to a node's services.</summary>
    private sealed class CheckPart(IPreflightCheck check) : IEngineExtension
    {
        public string Name => "check";

        public EngineServices Configure(EngineServices services) => services with { PreflightChecks = [.. services.PreflightChecks, check] };

        public Task StartAsync(EngineCore engine, CancellationToken ct) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
