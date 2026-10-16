using System.Globalization;
using System.Net;
using PlayMap.Core.Sync;
using PlayMap.Core.Time;
using PlayMap.Core.Wire;
using PlayMap.Engine.Tests.Host;
using PlayMap.Net.Sync;

namespace PlayMap.Engine.Tests.Sync;

/// <summary>Vblank reports between two real nodes over loopback (synthetic vblanks, one manual clock): the leader's STATE lists both
/// nodes' outputs — its own and the member's, named from its subscription — with the member's injected phase within 1 µs, a pair
/// against the reference and the verdict; a member at 59.94 makes the group a mixed rate, amber. Each step waits for the reports'
/// arrival, not for wall time.</summary>
public sealed class SyncBoardTests
{
    private const long Second = SyncRig.Second;
    private const long Ahead = 7_300_000_000;
    private const long Base = 1_000 * Second;

    [Theory]
    [InlineData(60.0, 3_210, "free-running", "green")]
    [InlineData(59.94, -2_500, "mixed rate", "amber")]
    public async Task TheLeadersStateListsBothNodesOutputsWithTheMembersPhase(double memberHz, long phaseNs, string verdict, string light)
    {
        var clock = new ManualClock(Base);
        // The leader's output has a vblank at the leader instant Base + Ahead; the member's (on its own clock) phaseNs after it.
        var leaderV = new FakeVblanks(FakeVblanks.At("op_1", 60, Base + Ahead));
        var memberV = new FakeVblanks(FakeVblanks.At("op_1", memberHz, Base + phaseNs));
        await using var leader = await SyncRig.StartAsync("FOH", SyncRig.LeaderId, new OffsetClock(clock, Ahead), SyncRig.Fast(leaderV));
        await using var member = await SyncRig.StartAsync("Stage", SyncRig.FollowerId, clock, SyncRig.Fast(memberV));
        await SyncRig.OpenShowAsync(leader);
        await SyncRig.OpenShowAsync(member);
        await SyncRig.GroupAsync(leader, member);

        Assert.True(await Eventually.TrueAsync(() => SyncRig.Sync(leader).GetProperty("outputs").GetArrayLength() == 2), leader.Journal());
        var sync = SyncRig.Sync(leader);
        var rows = sync.GetProperty("outputs").EnumerateArray().ToList();
        Assert.Equal([SyncRig.LeaderId, SyncRig.FollowerId], rows.Select(r => r.GetProperty("node").GetString()));
        Assert.All(rows, r => Assert.Equal("op_1", r.GetProperty("output").GetString()));
        Assert.All(rows, r => Assert.True(r.GetProperty("heard").GetBoolean()));
        var theirs = rows[1];
        Assert.Equal(memberHz, theirs.GetProperty("refreshHz").GetDouble(), 1e-6);
        Assert.Equal(phaseNs / 1000.0, theirs.GetProperty("phaseUs").GetDouble(), 1e-3);
        Assert.Equal(0, theirs.GetProperty("uncertaintyUs").GetDouble());   // no round trip on a standing clock
        Assert.Equal("gl", theirs.GetProperty("path").GetString());
        var pair = sync.GetProperty("pairs").EnumerateArray().Single();
        Assert.Equal(phaseNs / 1000.0, pair.GetProperty("phaseUs").GetDouble(), 1e-3);
        Assert.Equal(verdict, sync.GetProperty("verdict").GetString());
        Assert.Equal(light, SyncRig.GroupItem(leader).GetProperty("light").GetString());
        Assert.Equal(memberHz != 60, pair.GetProperty("mixedRate").GetBoolean());
        Assert.Equal((SyncRig.LeaderId, SyncRig.FollowerId), (pair.GetProperty("aNode").GetString(), pair.GetProperty("bNode").GetString()));
        Assert.StartsWith("leading show with 1 member; 2 nodes, 2 outputs", sync.GetProperty("words").GetString(), StringComparison.Ordinal);

        // The member shows its own outputs; the verdict is the leader's.
        var mine = SyncRig.Sync(member);
        Assert.Equal(SyncRig.FollowerId, mine.GetProperty("outputs")[0].GetProperty("node").GetString());
        Assert.Equal("unknown", mine.GetProperty("verdict").GetString());

        // A node that asks for the time but never joined the feed is not heard: only the group's members are.
        var refused = leader.Engine.Services.GroupSync!.RefusedReports;
        var port = new IPEndPoint(IPAddress.Loopback, leader.Network.SyncPort);
        await using var asker = new ClockSyncClient(clock, TimeSpan.FromMilliseconds(20));
        await asker.FollowAsync(port);
        Assert.True(await Eventually.TrueAsync(() => asker.Snapshot.Samples > 0));
        using var stranger = new VblankReporter(port);
        Assert.True(stranger.Send(new VblankReport(LockState.FreeMeasured, 0, 1, 0x1a2b3c4d, VblankReport.HashId("op_9"), 1, 0, 16_666_666_667, 0, 0, 0, 0, 0, 0)));
        Assert.True(await Eventually.TrueAsync(() => leader.Engine.Services.GroupSync!.RefusedReports > refused));
        Assert.Equal(2, SyncRig.Sync(leader).GetProperty("outputs").GetArrayLength());
    }

    /// <summary>A member's outputs read by the ids and in the order its subscription named them — its latest, when it holds two —
    /// across a lead (which forgets the reports and keeps the names) and the end of either subscription, whichever order they come
    /// in; once it holds none they read by hash.</summary>
    [Fact]
    public async Task AMembersOutputsReadAsItsSubscriptionNamedThemAcrossALeadAndAnEnd()
    {
        const string Node = "1a2b3c4d";
        var clock = new ManualClock(Base);
        await using var leader = await SyncRig.StartAsync("FOH", SyncRig.LeaderId, clock);
        await SyncRig.OpenShowAsync(leader);
        await leader.OkAsync("SYNC LEAD show");
        var sync = leader.Engine.Services.GroupSync!;
        uint seq = 0;
        List<string> Rows()
        {
            seq++;
            foreach (var output in new[] { "op_a", "op_b" })
            {
                Assert.True(sync.Board.Offer(new VblankReport(LockState.FreeMeasured, VblankReport.FlagSettled, VblankReport.PathGl, VblankReport.NodeNumber(Node),
                    VblankReport.HashId(output), seq, 0, 16_666_666_667, leader.Engine.LeaderNowNs, 600, 0, 0, 16_000, 0), clock.NowNs));
            }
            return [.. sync.Board.Picture(leader.Engine.LeaderNowNs, clock.NowNs).Outputs.Where(o => o.Node == Node).Select(o => o.Output)];
        }
        var first = sync.OpenAnchors([Node, "op_b", "op_a"], new SessionInfo("s1", "wire", "10.0.0.21", false, "Stage", true), new NullSink());
        var second = sync.OpenAnchors([Node, "op_a", "op_b"], new SessionInfo("s2", "wire", "10.0.0.22", false, "Stage", true), new NullSink());
        Assert.Equal(["op_a", "op_b"], Rows());

        // A lead starts the board again: the reports go, the names and order stay.
        await leader.OkAsync("SYNC LEAD other");
        Assert.DoesNotContain(sync.Board.Picture(leader.Engine.LeaderNowNs, clock.NowNs).Outputs, o => o.Node == Node);
        Assert.Equal(["op_a", "op_b"], Rows());

        // The latest subscription ends: the member's other one names its outputs again.
        second.Sub!.Dispose();
        Assert.Equal(["op_b", "op_a"], Rows());
        first.Sub!.Dispose();
        string[] hashed = [.. new[] { "op_a", "op_b" }.Select(VblankReport.HashId).Order().Select(h => "#" + h.ToString("x8", CultureInfo.InvariantCulture))];
        Assert.Equal(hashed, Rows());
    }

    private sealed class NullSink : PlayMap.Net.IPushSink
    {
        public void Latest(string key, string line)
        {
            // A reader that keeps nothing.
        }

        public bool InOrder(string line) => true;
    }
}
