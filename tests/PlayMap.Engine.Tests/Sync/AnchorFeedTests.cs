using System.Collections.Concurrent;
using PlayMap.Core.Time;
using PlayMap.Engine.Tests.Host;

namespace PlayMap.Engine.Tests.Sync;

/// <summary>Anchors and the group's air reach the followers (the carry-over), on two real nodes in one process on one manual clock,
/// the leader's 7.3 s ahead: a leader's GO puts the follower on the same item and anchor; the leader's PANIC FADE 1000 lands on
/// both at the same instant (0.5 at +500 ms on each); its BLACKOUT blacks both at one leader instant; a dead leader leaves the
/// follower free-wheeling and switching its next item at the anchored instant; a restarted follower follows again, its
/// follow-blackout switch kept; HERE keeps a line on the leader.</summary>
public sealed class AnchorFeedTests
{
    private const long Second = SyncRig.Second;
    private const long Ahead = 7_300_000_000;
    private const double Tolerance = 1e-6;

    [Fact]
    public async Task AGoOnTheLeaderPutsTheFollowerOnTheSameItemAndAnchorAndTheGroupsAirLandsOnBothAtOneInstant()
    {
        var clock = new ManualClock(1_000 * Second);
        await using var leader = await SyncRig.StartAsync("FOH", SyncRig.LeaderId, new OffsetClock(clock, Ahead));
        await using var follower = await SyncRig.StartAsync("Stage", SyncRig.FollowerId, clock);
        await SyncRig.OpenShowAsync(leader);
        await SyncRig.OpenShowAsync(follower);
        await SyncRig.GroupAsync(leader, follower);
        Assert.Equal("leader", SyncRig.Sync(leader).GetProperty("role").GetString());
        Assert.Equal("show", SyncRig.Sync(leader).GetProperty("group").GetString());
        Assert.Equal("follower", SyncRig.Sync(follower).GetProperty("role").GetString());
        Assert.Equal(1, SyncRig.Sync(follower).GetProperty("steps").GetInt64());   // its own clock to the leader's: one step

        // GO on the leader: the follower adopts the same item and anchor through its own PLAYLIST ANCHOR.
        await leader.OkAsync("PLAYLIST GO pl_show");
        var anchor = leader.Engine.AnchorJson("pl_show");
        Assert.NotNull(anchor);
        Assert.True(await Eventually.TrueAsync(() => follower.Engine.AnchorJson("pl_show") == anchor), $"{follower.Engine.AnchorJson("pl_show")} != {anchor}");
        Assert.Equal(leader.Engine.Snapshot.Playback.Playlists["pl_show"].SwitchAtLeaderNs, follower.Engine.Snapshot.Playback.Playlists["pl_show"].SwitchAtLeaderNs);
        Assert.Contains("Sync test", follower.State().GetProperty("project").GetProperty("name").GetString(), StringComparison.Ordinal);
        Assert.True(await Eventually.TrueAsync(() => follower.Journal().Contains("Show: following A", StringComparison.Ordinal)), follower.Journal());

        // PANIC FADE 1000 on the leader: each node's level is 0.5 half way through, at the same leader instants.
        var events = new ConcurrentQueue<ShowEvent>();
        follower.Engine.EventRaised += events.Enqueue;
        var t0 = leader.Engine.LeaderNowNs;
        await leader.OkAsync("PANIC FADE 1000");
        var start = t0 + leader.Engine.Store.Project.Show.Rules.PanicLeadMs * 1_000_000L;
        Assert.True(await Eventually.TrueAsync(() => leader.Journal().Contains("PANIC FADE 1000 on Stage: applied", StringComparison.Ordinal)), leader.Journal());
        foreach (var node in new[] { leader, follower })
        {
            Assert.Equal(1.0, node.Engine.AirGain("op_1", start), Tolerance);
            Assert.Equal(0.5, node.Engine.AirGain("op_1", start + 500_000_000), Tolerance);
            Assert.Equal(0.0, node.Engine.AirGain("op_1", start + 1_000_000_000), Tolerance);
        }
        Assert.Equal(start, Assert.Single(events, e => e.Kind == "panic").AtLeaderNs);

        // BLACKOUT on the leader: the follower blacks out at the leader's instant (one frame for both).
        clock.AdvanceMs(2000);
        var t1 = leader.Engine.LeaderNowNs;
        await leader.OkAsync("BLACKOUT ON");
        Assert.True(await Eventually.TrueAsync(() => follower.State().GetProperty("air").GetProperty("blackout").GetBoolean()));
        Assert.Equal(t1, Assert.Single(events, e => e.Kind == "air.blackout").AtLeaderNs);
        Assert.True(await Eventually.TrueAsync(() => leader.Journal().Contains("BLACKOUT ON on Stage: applied", StringComparison.Ordinal)), leader.Journal());

        // HERE keeps a line on the leader; the master fades the follower too.
        await leader.OkAsync("PANIC CLEAR FADE 0 HERE");
        clock.AdvanceMs(100);
        var t2 = leader.Engine.LeaderNowNs;
        await leader.OkAsync("MASTER 50 FADE 1000");
        Assert.True(await Eventually.TrueAsync(() => leader.Journal().Contains("MASTER 50 FADE 1000 on Stage: applied", StringComparison.Ordinal)), leader.Journal());
        Assert.False(leader.Journal().Contains("PANIC CLEAR FADE 0 HERE on Stage", StringComparison.Ordinal));
        Assert.Equal(0.0, follower.Engine.AirGain("op_1", t2 + 2 * Second), Tolerance);   // still in panic there
        Assert.Equal(0.5, leader.Engine.AirGain("op_1", t2 + 2 * Second), Tolerance);
        Assert.Equal(leader.State().GetProperty("air").GetProperty("master").GetProperty("untilLeaderNs").GetInt64(),
            follower.State().GetProperty("air").GetProperty("master").GetProperty("untilLeaderNs").GetInt64());
    }

    [Fact]
    public async Task AFollowerWhoseLeaderDiesFreeWheelsAndSwitchesItsNextItemAtTheAnchoredInstant()
    {
        var clock = new ManualClock(1_000 * Second);
        await using var follower = await SyncRig.StartAsync("Stage", SyncRig.FollowerId, clock);
        long switchAt;
        await using (var leader = await SyncRig.StartAsync("FOH", SyncRig.LeaderId, new OffsetClock(clock, Ahead)))
        {
            await SyncRig.OpenShowAsync(leader);
            await SyncRig.OpenShowAsync(follower);
            await SyncRig.GroupAsync(leader, follower);
            await leader.OkAsync("PLAYLIST GO pl_show");
            switchAt = leader.Engine.Snapshot.Playback.Playlists["pl_show"].SwitchAtLeaderNs;
            Assert.True(await Eventually.TrueAsync(() => follower.Engine.Snapshot.Playback.Playlists["pl_show"].SwitchAtLeaderNs == switchAt));
        }

        // The leader is gone: past the timeout the follower free-wheels on its last estimate, and says so.
        clock.AdvanceMs(4_000);
        Assert.True(await Eventually.TrueAsync(() => follower.Journal().Contains("went quiet: free-wheeling", StringComparison.Ordinal)), follower.Journal());
        clock.AdvanceMs(1_500);
        Assert.Equal(1.5, SyncRig.Sync(follower).GetProperty("freewheelS").GetDouble());
        Assert.Equal("red", SyncRig.Sync(follower).GetProperty("light").GetString());
        Assert.Contains("free-wheeling: FOH (0badc0de) is not heard", SyncRig.Sync(follower).GetProperty("words").GetString(), StringComparison.Ordinal);

        // Its own lane switches to B at the anchored instant, not a tick before.
        string? Item() => follower.Engine.Snapshot.Playback.Playlists["pl_show"].At(follower.Engine.LeaderNowNs).ItemId;
        clock.Advance(switchAt - follower.Engine.LeaderNowNs - 1_000_000);
        Assert.Equal("it_a", Item());
        await Ticked(follower);
        Assert.Equal("it_a", follower.Engine.Snapshot.Playback.Playlists["pl_show"].ItemId);
        clock.Advance(1_000_000);
        Assert.Equal("it_b", Item());
        await Ticked(follower);
        Assert.True(await Eventually.TrueAsync(() => follower.Engine.Snapshot.Playback.Playlists["pl_show"].ItemId == "it_b"));
    }

    [Fact]
    public async Task ARestartedFollowerFollowsItsLeaderAgainAndKeepsItsFollowBlackoutSwitch()
    {
        var clock = new ManualClock(1_000 * Second);
        await using var leader = await SyncRig.StartAsync("FOH", SyncRig.LeaderId, new OffsetClock(clock, Ahead));
        var first = await SyncRig.StartAsync("Stage", SyncRig.FollowerId, clock);
        await using (first)
        {
            await SyncRig.GroupAsync(leader, first);
            Assert.StartsWith("already following FOH", (await first.RunAsync("SYNC FOLLOW show " + leader.Id)).Payload, StringComparison.Ordinal);
            await first.OkAsync("LINK PATTERNS FOLLOW-BLACKOUT OFF");
            var kept = first.Home.LoadSettings("unused").Sync;
            Assert.Equal(("follow", "show", SyncRig.LeaderId, false), (kept.Role, kept.Group, kept.Leader, kept.FollowPatternsBlackout));
            Assert.Equal("lead", leader.Home.LoadSettings("unused").Sync.Role);
            await first.StopAsync();

            await using var again = await SyncRig.StartAsync("Stage", SyncRig.FollowerId, clock, dir: first.Dir);
            Assert.False(again.State().GetProperty("air").GetProperty("followPatternsBlackout").GetBoolean());
            // Until it hears its leader it waits, and says for whom.
            Assert.Equal("connecting", SyncRig.Sync(again).GetProperty("feed").GetString());
            HostRig.Introduce(leader, again);
            Assert.True(await Eventually.TrueAsync(() => SyncRig.Sync(again).GetProperty("role").GetString() == "follower"), again.Journal());
            Assert.Equal(SyncRig.LeaderId, SyncRig.Sync(again).GetProperty("leader").GetString());
            await SyncRig.CaughtUpAsync(leader, again);
            Assert.Contains("(kept from before the restart)", again.Journal(), StringComparison.Ordinal);
        }
    }

    private static async Task Ticked(HostRig rig)
    {
        var now = rig.Engine.LeaderNowNs;
        Assert.True(await Eventually.TrueAsync(() => rig.Engine.ShowTickedNs >= now), "the lane did not tick");
    }
}
