using System.Collections.Concurrent;
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

    /// <summary>What a playlist that moved across a step would be named by: each lane's item and clip anchor, the clock's steps and the
    /// last Δ, the count STATE gives and the journal.</summary>
    private static string Why(HostRig rig)
    {
        var lanes = string.Join("; ", rig.Engine.Snapshot.Playback.Playlists.Select(kv =>
            $"{kv.Key} index {kv.Value.Index} item {kv.Value.ItemId} {kv.Value.Clip.State} media {kv.Value.Clip.Anchor.MediaNs} leader {kv.Value.Clip.Anchor.LeaderNs} seq {kv.Value.Clip.Seq} switch {kv.Value.SwitchAtLeaderNs}"));
        var clock = rig.Engine.Services.Clock;
        return $"now {rig.Engine.LeaderNowNs}; {lanes}; clock steps {clock.Steps}, last {clock.LastStepNs}, STATE steps {SyncRig.Sync(rig).GetProperty("steps").GetInt64()}; journal: {rig.Journal()}";
    }

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
        Assert.True(Math.Abs(Position(node, "pl_local") - local) <= 1_000_000, $"{Position(node, "pl_local") - local} ns; {Why(node)}");
        Assert.True(Math.Abs(Position(node, "pl_show") - show) <= 1_000_000, $"{Position(node, "pl_show") - show} ns; {Why(node)}");
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
    public async Task AWaitingBlindCommitMovesWithTheStepSoItLandsNeitherEarlyNorLate()
    {
        var clock = new ManualClock(1_000 * Second);
        await using var leader = await SyncRig.StartAsync("FOH", SyncRig.LeaderId, new MovableClock(clock, Ahead));
        await using var node = await SyncRig.StartAsync("Stage", SyncRig.FollowerId, clock);
        await SyncRig.OpenShowAsync(leader);
        await SyncRig.OpenShowAsync(node);
        await node.OkAsync("BLIND ON");
        await node.OkAsync("SET cv_main background \"#FF0000\"");
        var lands = node.Engine.LeaderNowNs + 5 * Second;
        await node.OkAsync("BLIND COMMIT AT +5000");
        Assert.Equal(lands, node.Engine.Snapshot.Pending!.EffectiveLeaderNs);

        // Following a leader 3 h 12 min ahead steps the clock: the commit's instant moves with it, as every instant on the air does,
        // so the render draws it, and the lane lands it, five seconds after it was asked for — not at once.
        await SyncRig.GroupAsync(leader, node);
        Assert.Equal(1, SyncRig.Sync(node).GetProperty("steps").GetInt64());
        Assert.Equal(lands + Ahead, node.Engine.Snapshot.Pending!.EffectiveLeaderNs);
        Assert.Equal(lands + Ahead, node.State().GetProperty("show").GetProperty("blind").GetProperty("commit").GetProperty("effectiveLeaderNs").GetInt64());
        clock.AdvanceMs(4_999);
        await Ticked(node);
        Assert.Equal("#000000", Background(node));
        clock.AdvanceMs(1);
        Assert.True(await Eventually.TrueAsync(() => Background(node) == "#FF0000"), node.Journal());
        Assert.Contains("blind commit: 1 change landed (at) by Desk", node.Journal(), StringComparison.Ordinal);
    }

    private static string Background(HostRig rig) =>
        System.Text.Json.JsonDocument.Parse(rig.Engine.ProjectJson().Json).RootElement.GetProperty("canvases")[0].GetProperty("background").GetString()!;

    private static async Task Ticked(HostRig rig)
    {
        var now = rig.Engine.LeaderNowNs;
        Assert.True(await Eventually.TrueAsync(() => rig.Engine.ShowTickedNs >= now), "the lane did not tick");
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

    /// <summary>A leader's anchor and the group's record that its playlist is on the leader's time line are one action on the member's
    /// lane: the anchor's own journal row, written in that action, already finds the playlist adopted — so a step the lane takes at any
    /// point after the anchor leaves it where the leader put it.</summary>
    [Fact]
    public async Task ALeadersAnchorIsAdoptedInTheLaneActionThatAppliesIt()
    {
        var clock = new ManualClock(1_000 * Second);
        await using var leader = await SyncRig.StartAsync("FOH", SyncRig.LeaderId, new MovableClock(clock, 7 * Second));
        await using var node = await SyncRig.StartAsync("Stage", SyncRig.FollowerId, clock);
        await SyncRig.OpenShowAsync(leader);
        await SyncRig.OpenShowAsync(node);
        await SyncRig.GroupAsync(leader, node);
        var seen = new ConcurrentQueue<bool>();
        node.Engine.Journal.Written += row =>
        {
            if (row.Line?.StartsWith("PLAYLIST ANCHOR pl_show ", StringComparison.Ordinal) == true)
                seen.Enqueue(node.Engine.Services.GroupSync!.Adopted.Contains("pl_show"));
        };
        await leader.OkAsync("PLAYLIST GO pl_show");
        Assert.True(await Eventually.TrueAsync(() => !seen.IsEmpty), node.Journal());
        Assert.All(seen, Assert.True);
    }

    [Fact]
    public async Task AFollowerThatLeadsAgainStepsBackAndItsSettledPanicStaysDark()
    {
        var clock = new ManualClock(1_000 * Second);
        await using var leader = await SyncRig.StartAsync("FOH", SyncRig.LeaderId, new MovableClock(clock, 7 * Second));
        await using var node = await SyncRig.StartAsync("Stage", SyncRig.FollowerId, clock);
        await SyncRig.OpenShowAsync(leader);
        await SyncRig.OpenShowAsync(node);
        await SyncRig.GroupAsync(leader, node);
        await node.OkAsync("PANIC FADE 1000");
        clock.AdvanceMs(3_000);
        Assert.Equal(0.0, node.Engine.AirGain("op_1", node.Engine.LeaderNowNs), 1e-9);

        // Leading its own group again, the node goes back to its own clock, 7 s behind the leader's line: the panic's instants move
        // with the step, so the air stays dark (read on the old instants, its fade would not have begun yet).
        await node.OkAsync("SYNC LEAD stage");
        Assert.True(await Eventually.TrueAsync(() => SyncRig.Sync(node).GetProperty("steps").GetInt64() == 2), node.Journal());
        Assert.InRange(node.Engine.Services.Clock.LastStepNs, -7 * Second - 1_000_000, -7 * Second + 1_000_000);
        Assert.Contains("show clock stepped −7 s", node.Journal(), StringComparison.Ordinal);
        foreach (var later in new[] { 0L, 1, 5, 10 })
            Assert.Equal(0.0, node.Engine.AirGain("op_1", node.Engine.LeaderNowNs + later * Second), 1e-9);
    }

    /// <summary>STATE counts a step once it is taken in full, never ahead of its work: as the step's last word is written (its journal
    /// line, after every anchor and instant has moved), STATE, read afresh there, still counts the steps before it; once it counts
    /// the step, the line is in the journal.</summary>
    [Fact]
    public async Task AStepIsCountedOnceItIsTakenInFull()
    {
        var clock = new ManualClock(1_000 * Second);
        await using var leader = await SyncRig.StartAsync("FOH", SyncRig.LeaderId, new MovableClock(clock, 7 * Second));
        await using var node = await SyncRig.StartAsync("Stage", SyncRig.FollowerId, clock);
        await SyncRig.OpenShowAsync(leader);
        await SyncRig.OpenShowAsync(node);
        await node.OkAsync("PLAYLIST GO pl_local");
        var counted = new ConcurrentQueue<long>();
        node.Engine.Journal.Written += row =>
        {
            if (row.Kind != "sync" || !row.Text.Contains("show clock stepped", StringComparison.Ordinal))
                return;
            node.Engine.FactsChanged();
            counted.Enqueue(SyncRig.Sync(node).GetProperty("steps").GetInt64());
        };

        await SyncRig.GroupAsync(leader, node);
        Assert.Equal(1, SyncRig.Sync(node).GetProperty("steps").GetInt64());
        Assert.Equal([0L], counted.ToArray());
        Assert.Contains("show clock stepped +7 s; 1 playlist kept its place", node.Journal(), StringComparison.Ordinal);
    }
}
