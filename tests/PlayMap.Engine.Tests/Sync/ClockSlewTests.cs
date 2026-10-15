using PlayMap.Core.Time;
using PlayMap.Engine.Tests.Host;

namespace PlayMap.Engine.Tests.Sync;

/// <summary>Following never makes a playlist jump (the carry-over): a node playing a playlist in no group (and a group playlist
/// before its first anchor) follows a leader 3 h 12 min ahead — its clock steps once and every such anchor moves by the same Δ
/// on the lane, so the media position is where it was; a later correction under a millisecond is slewed (no step, never
/// backwards, never faster than 1 + 200 ppm + drift). The arithmetic of the slew itself is NetworkClockTests' (Core).</summary>
public sealed class ClockSlewTests
{
    private const long Second = SyncRig.Second;
    private const long Ahead = (3 * 3600 + 12 * 60) * Second;

    private static long Position(HostRig rig, string playlist) =>
        rig.Engine.Snapshot.Playback.Playlists[playlist].Clip.PositionAt(rig.Engine.LeaderNowNs);

    [Fact]
    public async Task FollowingALeaderThreeHoursTwelveMinutesAheadKeepsEveryPlaylistInPlace()
    {
        var clock = new ManualClock(1_000 * Second);
        var leaderClock = new MovableClock(clock, Ahead);
        await using var leader = await SyncRig.StartAsync("FOH", SyncRig.LeaderId, leaderClock);
        await using var node = await SyncRig.StartAsync("Stage", SyncRig.FollowerId, clock);
        await SyncRig.OpenShowAsync(leader);
        await SyncRig.OpenShowAsync(node);
        await node.OkAsync("PLAYLIST GO pl_local");
        await node.OkAsync("PLAYLIST GO pl_show");
        clock.AdvanceMs(2_500);
        var local = Position(node, "pl_local");
        var show = Position(node, "pl_show");
        Assert.Equal(2_500_000_000, local);

        await SyncRig.GroupAsync(leader, node);
        Assert.Equal(1, SyncRig.Sync(node).GetProperty("steps").GetInt64());
        Assert.Equal(Ahead, node.Engine.Services.Clock.LastStepNs);
        Assert.True(Math.Abs(Position(node, "pl_local") - local) <= 1_000_000, $"{Position(node, "pl_local") - local} ns");
        Assert.True(Math.Abs(Position(node, "pl_show") - show) <= 1_000_000, $"{Position(node, "pl_show") - show} ns");
        Assert.True(await Eventually.TrueAsync(() => node.Journal().Contains("followed 0badc0de: show clock stepped +3 h 12 min; 2 playlists kept their place",
            StringComparison.Ordinal)), node.Journal());
        clock.AdvanceMs(1_000);
        Assert.Equal(local + 1_000_000_000, Position(node, "pl_local"));

        // The leader's clock moves 0.6 ms: the follower slews to it — no second step, its time never runs back.
        var before = node.Engine.LeaderNowNs;
        leaderClock.OffsetNs = Ahead + 600_000;
        Assert.True(await Eventually.TrueAsync(() => SyncRig.Sync(node).GetProperty("slewing").GetBoolean()), node.Journal());
        Assert.Equal(1, SyncRig.Sync(node).GetProperty("steps").GetInt64());
        Assert.True(node.Engine.LeaderNowNs >= before);
        var last = node.Engine.LeaderNowNs;
        for (var i = 0; i < 50; i++)
        {
            clock.AdvanceMs(100);
            var now = node.Engine.LeaderNowNs;
            Assert.True(now >= last, "leader time ran backwards");
            // The estimate carries no drift here (its samples all sit at one local instant), so the slew alone may speed it up.
            Assert.True(now - last <= 100_000_000 * (1 + 200e-6) + 2, $"leader time ran {now - last} ns in 100 ms");
            last = now;
        }
        Assert.True(await Eventually.TrueAsync(() => Math.Abs(leader.Engine.LeaderNowNs - node.Engine.LeaderNowNs) < 50_000),
            $"{leader.Engine.LeaderNowNs - node.Engine.LeaderNowNs} ns apart");
        Assert.Equal(1, SyncRig.Sync(node).GetProperty("steps").GetInt64());
        Assert.Equal(200, node.Engine.Services.Clock.SlewPpm, 1e-9);
    }

    [Fact]
    public async Task AStepAfterAdoptionLeavesTheLeadersAnchorAndMovesTheRest()
    {
        var clock = new ManualClock(1_000 * Second);
        var leaderClock = new MovableClock(clock, 7 * Second);
        await using var leader = await SyncRig.StartAsync("FOH", SyncRig.LeaderId, leaderClock);
        await using var node = await SyncRig.StartAsync("Stage", SyncRig.FollowerId, clock);
        await SyncRig.OpenShowAsync(leader);
        await SyncRig.OpenShowAsync(node);
        await SyncRig.GroupAsync(leader, node);
        await node.OkAsync("PLAYLIST GO pl_local");
        await node.OkAsync("MASTER 50 FADE 4000");
        await leader.OkAsync("PLAYLIST GO pl_show");
        Assert.True(await Eventually.TrueAsync(() => node.Engine.AnchorJson("pl_show") == leader.Engine.AnchorJson("pl_show")));
        clock.AdvanceMs(1_000);
        var local = Position(node, "pl_local");
        var master = node.Engine.AirGain("op_1", node.Engine.LeaderNowNs);
        Assert.Equal(0.875, master, 1e-6);   // a quarter of the way from 1 to 0.5

        // The leader's clock jumps 5 ms (its machine stepped): the member steps with it.
        leaderClock.OffsetNs = 7 * Second + 5_000_000;
        Assert.True(await Eventually.TrueAsync(() => SyncRig.Sync(node).GetProperty("steps").GetInt64() == 2), node.Journal());
        Assert.Equal(5_000_000, node.Engine.Services.Clock.LastStepNs);
        Assert.Equal(leader.Engine.AnchorJson("pl_show"), node.Engine.AnchorJson("pl_show"));   // already on the leader's line
        Assert.Equal(local, Position(node, "pl_local"));                                         // moved with the step
        Assert.Equal(master, node.Engine.AirGain("op_1", node.Engine.LeaderNowNs), 1e-6);         // the master's fade too
    }
}
