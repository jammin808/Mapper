using System.Collections.Concurrent;
using PlayMap.Core.Model;
using PlayMap.Core.Time;
using PlayMap.Engine.Presentation;
using PlayMap.Engine.Sync;
using PlayMap.Engine.Tests.Host;
using PlayMap.Engine.Tests.Presentation;
using PlayMap.Net.Sync;

namespace PlayMap.Engine.Tests.Sync;

/// <summary>The group's air latency (SYNC LATENCY): the leader carries it to every member on the feed (<c>ANCHOR &lt;group&gt; @latency
/// {"ms":…}</c>) — AUTO the largest output latency of its own outputs and every member's report, else the stated one — and each node
/// writes it through to its outputs (<see cref="IPresentBackend.SetGroupAirLatency"/>); STATE's <c>sync.groupLatencyMs</c> and the
/// context's group item say it on both. A quiet member keeps its place in AUTO, one that leaves drops out, and a lead waits for its
/// members' first reports before AUTO is carried. A follower's latency is its leader's (refused with the way there), a value set
/// already, a latency that is not a finite 0–500 ms is refused, a stated value below a display the group reports says that node
/// lights late, and a record that is not one whole is never taken.</summary>
public sealed class SyncLatencyTests
{
    private const long Second = SyncRig.Second;

    private static double Latency(HostRig rig) => SyncRig.Sync(rig).GetProperty("groupLatencyMs").GetDouble();

    private static string GroupWords(HostRig rig) => SyncRig.GroupItem(rig).GetProperty("state").GetString()!;

    /// <summary>A render host that draws nothing and keeps every group latency the node writes through to its outputs.</summary>
    private sealed class LatencyOutputs : IRenderHost, IPresentBackend
    {
        private readonly ConcurrentQueue<double> _group = new();

        /// <summary>The last group latency written through (null before any).</summary>
        public double? Last => _group.IsEmpty ? null : _group.Last();

        public string RendererName => "latency (test)";

        public IReadOnlyList<DisplayInfo> Displays() => [];

        public IReadOnlyList<OutputHealth> Health() => [];

        public Task<long> ShowAsync(string outputId, ReadOnlyMemory<byte> gray8, int width, int height, CancellationToken ct) =>
            Task.FromException<long>(new NotSupportedException("this test's render host shows no scan frames"));

        public void Release()
        {
            // Nothing was taken from the show.
        }

        public void Start(EngineCore engine)
        {
            // Nothing to draw.
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public IReadOnlyList<OutputPresentView> Present() => [];

        public MachinePresentView Machine() => new("gl", "test", "offscreen", false, "", 0, 0, "core330", "glx") { AirLatencyMs = Last ?? 0 };

        public Task<IReadOnlyList<OutputPresentView>> ResetAsync(IReadOnlyCollection<string>? outputIds, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<OutputPresentView>>([]);

        public void SetGroupAirLatency(double ms) => _group.Enqueue(ms);
    }

    [Fact]
    public async Task TheLeaderCarriesTheGroupsAirLatencyToEveryMembersOutputsAndSyncLatencySetsIt()
    {
        var clock = new ManualClock(1_000 * Second);
        var leaderV = new FakeVblanks(FakeVblanks.At("op_1", 60, clock.NowNs + 3 * Second) with { AirLatencyUs = 17_000 });
        var memberV = new FakeVblanks(FakeVblanks.At("op_1", 60, clock.NowNs) with { AirLatencyUs = 50_000 });
        var leaderOut = new LatencyOutputs();
        var memberOut = new LatencyOutputs();
        await using var leader = await SyncRig.StartAsync("FOH", SyncRig.LeaderId, new OffsetClock(clock, 3 * Second), SyncRig.Fast(leaderV), render: _ => leaderOut);
        await using var follower = await SyncRig.StartAsync("Stage", SyncRig.FollowerId, clock, SyncRig.Fast(memberV), render: _ => memberOut);
        await SyncRig.OpenShowAsync(leader);
        await SyncRig.OpenShowAsync(follower);
        await SyncRig.GroupAsync(leader, follower);

        // AUTO: the member's slower display (50 ms) is the group's, written through to the outputs of both nodes and said in STATE.
        Assert.True(await Eventually.TrueAsync(() => leaderOut.Last == 50 && memberOut.Last == 50),
            $"leader {leaderOut.Last}, member {memberOut.Last}\n{follower.Journal()}");
        Assert.Equal((50.0, 50.0), (Latency(leader), Latency(follower)));
        Assert.Equal(50, follower.Engine.Services.GroupSync!.GroupLatencyMs);
        Assert.Equal(50, follower.State().GetProperty("machine").GetProperty("present").GetProperty("airLatencyMs").GetDouble());
        Assert.EndsWith("; the group's air latency 50 ms", GroupWords(leader), StringComparison.Ordinal);
        Assert.EndsWith("; the group's air latency 50 ms", GroupWords(follower), StringComparison.Ordinal);
        Assert.Contains(follower.Engine.Journal.Recent(50), r => r.Text.StartsWith("the group's air latency is 50 ms (FOH", StringComparison.Ordinal));

        // Stated: kept in the leader's project, carried to the member's outputs.
        var set = await leader.RunAsync("SYNC LATENCY 80");
        Assert.True(set.Ok, set.Payload);
        Assert.Equal(80, leader.Engine.Store.Project.Settings.SyncLatencyMs);
        Assert.True(await Eventually.TrueAsync(() => leaderOut.Last == 80 && memberOut.Last == 80), $"leader {leaderOut.Last}, member {memberOut.Last}");
        Assert.Equal((80.0, 80.0), (Latency(leader), Latency(follower)));

        // What is set already, a member's own word and a latency that is not 0 to 500 ms are refused with the reason; nothing changes.
        var revision = leader.Engine.Store.Current.Revision;
        var again = await leader.RunAsync("SYNC LATENCY 80");
        Assert.Equal((false, "the group's air latency is 80 ms already"), (again.Ok, again.Payload));
        var member = await follower.RunAsync("SYNC LATENCY 30");
        Assert.False(member.Ok);
        Assert.Equal($"this node follows {SyncRig.LeaderId} in show: the group's air latency is its leader's — SYNC LATENCY there", member.Payload);
        Assert.Equal(-1, follower.Engine.Store.Project.Settings.SyncLatencyMs);
        foreach (var bad in new[] { "501", "-2", "NaN", "Infinity", "-Infinity" })
            Assert.False((await leader.RunAsync("SYNC LATENCY " + bad)).Ok, bad);
        Assert.Equal(revision, leader.Engine.Store.Current.Revision);

        // Below a display the group reports: set, and said — that node stays at its own and lights late.
        var low = await leader.RunAsync("SYNC LATENCY 30");
        Assert.True(low.Ok, low.Payload);
        Assert.EndsWith($" — below the 50 ms display latency {SyncRig.FollowerId} reports: that node's outputs stay at their own and light later than the rest",
            low.Effect, StringComparison.Ordinal);

        // Back to AUTO: the largest reported again.
        Assert.True((await leader.RunAsync("SYNC LATENCY AUTO")).Ok);
        Assert.Equal(-1, leader.Engine.Store.Project.Settings.SyncLatencyMs);
        Assert.True(await Eventually.TrueAsync(() => leaderOut.Last == 50 && memberOut.Last == 50), $"leader {leaderOut.Last}, member {memberOut.Last}");

        // A member that goes quiet (no report heard for longer than the board's five seconds) keeps its place: nothing moves.
        memberV.Set();
        clock.Advance(6 * Second);
        Assert.True(await Eventually.TrueAsync(() => !SyncRig.Sync(leader).GetProperty("outputs").EnumerateArray()
            .Any(o => o.GetProperty("node").GetString() == SyncRig.FollowerId && o.GetProperty("heard").GetBoolean())), SyncRig.Sync(leader).ToString());
        var sync = leader.Engine.Services.GroupSync!;
        var turns = sync.Watches;
        Assert.True(await Eventually.TrueAsync(() => sync.Watches >= turns + 3));
        Assert.Equal((50.0, 50.0), (leaderOut.Last!.Value, Latency(leader)));

        // A member that leaves drops out: the leader's own display is the group's.
        await follower.StopAsync();
        Assert.True(await Eventually.TrueAsync(() => leaderOut.Last == 17), $"leader {leaderOut.Last}");
        Assert.Equal(17, Latency(leader));
    }

    [Fact]
    public void ALatencyRecordReadsBackAsItselfAndAnythingElseIsRefused()
    {
        foreach (var ms in new[] { 0, 16.667, 33, 500 })
        {
            Assert.True(SyncNode.TryReadLatency(SyncNode.LatencyJson(ms), out var back));
            Assert.Equal(ms, back);
        }
        Assert.Equal("{\"ms\":16.667}", SyncNode.LatencyJson(16.66666));
        foreach (var bad in new[] { "", "[]", "{}", "{\"ms\":\"33\"}", "{\"ms\":-1}", "{\"ms\":500.5}", "{\"ms\":null}", "{\"latency\":33}", "{\"ms\":33" })
            Assert.False(SyncNode.TryReadLatency(bad, out _), bad);
    }

    [Fact]
    public void TheLeadersLatencyIsTheStatedOneElseTheLargestReportedOnceEveryMemberHasReported()
    {
        static AnchorMember Member(string node, params string[] outputs) => new("s-" + node, node, node, "127.0.0.1", [.. outputs]);
        var a = Member("aaaa0001", "op_1", "op_2");
        var b = Member("bbbb0002", "op_1");
        var headless = Member("cccc0003");
        SyncNode.ReportedLatency[] reported = [new("aaaa0001", "op_1", 33_000), new("aaaa0001", "op_2", 50_000)];

        // Stated wins whatever is reported; 0 is a stated value.
        Assert.Equal(80, SyncNode.GroupLatencyOf(80, 17, [a, b], reported));
        Assert.Equal(0, SyncNode.GroupLatencyOf(0, 17, [a, b], reported));
        Assert.Equal(SyncNode.MaxLatencyMs, SyncNode.GroupLatencyOf(900, 17, [a], reported));
        // AUTO waits for every member that names outputs (b has not reported yet); a member with none never holds it up.
        Assert.Null(SyncNode.GroupLatencyOf(-1, 17, [a, b, headless], reported));
        Assert.Equal(50, SyncNode.GroupLatencyOf(-1, 17, [a, headless], reported));
        // Once the lead's wait is over, a member that never reports is carried without.
        Assert.Equal(50, SyncNode.GroupLatencyOf(-1, 17, [a, b], reported, waitForMembers: false));
        // Its own outputs count; a report for an output the member no longer names does not.
        Assert.Equal(60, SyncNode.GroupLatencyOf(-1, 60, [a], reported));
        Assert.Equal(33, SyncNode.GroupLatencyOf(-1, 17, [Member("aaaa0001", "op_1")], reported));
        Assert.Equal(17, SyncNode.GroupLatencyOf(-1, 17, [], reported));
    }

    [Fact]
    public async Task TheGroupsLatencyIsWrittenThroughToThisNodesOutputs()
    {
        var outputs = new ScriptedOutputs();
        await using var rig = PresentRig.Start(PresentRig.With(PresentRig.Screen("op_a")), outputs, new RoleNetwork("follower", SyncRig.LeaderId));
        rig.Engine.SetGroupAirLatency(50);
        rig.Engine.SetGroupAirLatency(0);
        Assert.Equal([50, 0], outputs.GroupLatencies);
    }
}
