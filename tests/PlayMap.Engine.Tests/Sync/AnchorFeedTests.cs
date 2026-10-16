using System.Collections.Concurrent;
using PlayMap.Core.Time;
using PlayMap.Engine.Tests.Host;
using Xunit.Abstractions;

namespace PlayMap.Engine.Tests.Sync;

/// <summary>Anchors and the group's air reach the followers (the carry-over), on two real nodes in one process on one manual clock,
/// the leader's 7.3 s ahead: a leader's GO puts the follower on the same item and anchor; the leader's PANIC FADE 1000 lands on
/// both at the same instant (0.5 at +500 ms on each); its BLACKOUT is stamped on both with one leader instant (on this standing clock
/// it also lands at once; on a network a switch lands when the line runs there); a dead leader leaves the
/// follower free-wheeling and switching its next item at the anchored instant; a restarted follower follows again, its
/// follow-blackout switch kept; HERE keeps a line on the leader.</summary>
public sealed class AnchorFeedTests(ITestOutputHelper output)
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
        var took = System.Diagnostics.Stopwatch.StartNew();
        await leader.OkAsync("PLAYLIST GO pl_show");
        var anchor = leader.Engine.AnchorJson("pl_show");
        Assert.NotNull(anchor);
        Assert.True(await Eventually.TrueAsync(() => follower.Engine.AnchorJson("pl_show") == anchor, stepMs: 1), $"{follower.Engine.AnchorJson("pl_show")} != {anchor}");
        // The design's bar is 200 ms on a show network; on this machine's loopback the measurement is recorded and only a 4x ceiling
        // is asserted (PLAN 5.2).
        output.WriteLine($"the follower adopted the leader's GO in {took.Elapsed.TotalMilliseconds:0.0} ms");
        Assert.True(took.ElapsedMilliseconds <= 800, $"adopted in {took.ElapsedMilliseconds} ms");
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
        Assert.Equal("red", SyncRig.GroupItem(follower).GetProperty("light").GetString());
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
    public async Task ARestartedFollowerFollowsItsLeaderAgainRejoinsTheShowWhereItIsAndKeepsItsSwitch()
    {
        var clock = new ManualClock(1_000 * Second);
        await using var leader = await SyncRig.StartAsync("FOH", SyncRig.LeaderId, new OffsetClock(clock, Ahead));
        await SyncRig.OpenShowAsync(leader);
        var first = await SyncRig.StartAsync("Stage", SyncRig.FollowerId, clock);
        await using (first)
        {
            await SyncRig.GroupAsync(leader, first);
            Assert.StartsWith("already following FOH", (await first.RunAsync("SYNC FOLLOW show " + leader.Id)).Payload, StringComparison.Ordinal);
            await first.OkAsync("LINK PATTERNS FOLLOW-BLACKOUT OFF");
            Assert.True(await Eventually.TrueAsync(() => first.Home.LoadSettings("unused").Sync.FollowPatternsBlackout == false));
            var kept = first.Home.LoadSettings("unused").Sync;
            Assert.Equal(("follow", "show", SyncRig.LeaderId, false), (kept.Role, kept.Group, kept.Leader, kept.FollowPatternsBlackout));
            Assert.Equal("lead", leader.Home.LoadSettings("unused").Sync.Role);
            await first.StopAsync();

            // While it is away the show moves on: a GO, a NEXT and a blackout on the leader.
            await leader.OkAsync("PLAYLIST GO pl_show");
            clock.AdvanceMs(1_000);
            await leader.OkAsync("PLAYLIST NEXT pl_show");
            await leader.OkAsync("BLACKOUT ON");

            await using var again = await SyncRig.StartAsync("Stage", SyncRig.FollowerId, clock, dir: first.Dir);
            await SyncRig.OpenShowAsync(again);
            Assert.False(again.State().GetProperty("air").GetProperty("followPatternsBlackout").GetBoolean());
            Assert.False(again.State().GetProperty("air").GetProperty("blackout").GetBoolean());
            // Until it hears its leader it waits, and says for whom.
            Assert.Equal("connecting", SyncRig.Sync(again).GetProperty("feed").GetString());
            Assert.Contains("waiting to hear 0badc0de", SyncRig.Sync(again).GetProperty("words").GetString(), StringComparison.Ordinal);
            HostRig.Introduce(leader, again);
            Assert.True(await Eventually.TrueAsync(() => SyncRig.Sync(again).GetProperty("role").GetString() == "follower"), again.Journal());
            Assert.Equal(SyncRig.LeaderId, SyncRig.Sync(again).GetProperty("leader").GetString());
            await SyncRig.CaughtUpAsync(leader, again);
            Assert.Contains("(kept from before the restart)", again.Journal(), StringComparison.Ordinal);

            // Its new subscription hands it the latest anchor and the latest blackout: it rejoins the show where the show is.
            Assert.True(await Eventually.TrueAsync(() => again.Engine.AnchorJson("pl_show") == leader.Engine.AnchorJson("pl_show")), again.Journal());
            Assert.Equal("it_b", again.Engine.Playback.Lane("pl_show")!.ItemId);
            Assert.True(await Eventually.TrueAsync(() => again.State().GetProperty("air").GetProperty("blackout").GetBoolean()), again.Journal());
        }
    }

    [Fact]
    public async Task APlaylistOfAnotherIdTakesTheLeadersAnchorOnlyWhileEachSideHasOneForTheGroup()
    {
        var clock = new ManualClock(1_000 * Second);
        await using var leader = await SyncRig.StartAsync("FOH", SyncRig.LeaderId, new OffsetClock(clock, Ahead));
        await using var follower = await SyncRig.StartAsync("Stage", SyncRig.FollowerId, clock);
        var show = SyncRig.Show();
        // The leader's project has two playlists in the group; the follower's, made apart, has one, under its own id.
        await SyncRig.OpenShowAsync(leader, show with { Playlists = [show.Playlists[0], show.Playlists[0] with { Id = "pl_side", Name = "Side" }, show.Playlists[1]] });
        await SyncRig.OpenShowAsync(follower, show with
        {
            Playlists = [show.Playlists[0] with { Id = "pl_mine" }, show.Playlists[1]],
            Outputs = [show.Outputs[0] with { Source = PlayMap.Core.Model.SourceRef.Playlist("pl_mine") }],
        });
        await SyncRig.GroupAsync(leader, follower);

        // One of the leader's playlists has moved so far: the follower's only group playlist takes its anchor.
        await leader.OkAsync("PLAYLIST GO pl_show");
        var switchAt = leader.Engine.Snapshot.Playback.Playlists["pl_show"].SwitchAtLeaderNs;
        Assert.True(await Eventually.TrueAsync(() => follower.Engine.Snapshot.Playback.Playlists["pl_mine"].SwitchAtLeaderNs == switchAt), follower.Journal());

        // A second one moves: which of the follower's playlists would take it is not clear, so none does, and the journal says so once.
        clock.AdvanceMs(500);
        await leader.OkAsync("PLAYLIST GO pl_side");
        await leader.OkAsync("PLAYLIST NEXT pl_side");
        Assert.True(await Eventually.TrueAsync(() => follower.Journal().Contains("FOH (0badc0de)'s pl_side for show is not taken here", StringComparison.Ordinal)),
            follower.Journal());
        await leader.OkAsync("PLAYLIST NEXT pl_show");
        var next = leader.Engine.AnchorJson("pl_show");
        Assert.True(await Eventually.TrueAsync(() => follower.Journal().Contains("FOH (0badc0de)'s pl_show for show is not taken here", StringComparison.Ordinal)),
            follower.Journal());
        Assert.Equal(switchAt, follower.Engine.Snapshot.Playback.Playlists["pl_mine"].SwitchAtLeaderNs);
        Assert.NotEqual(next, follower.Engine.AnchorJson("pl_mine"));
        Assert.Single(follower.Journal().Split('\n'), l => l.Contains("pl_side for show is not taken here", StringComparison.Ordinal));
    }

    private static async Task Ticked(HostRig rig)
    {
        var now = rig.Engine.LeaderNowNs;
        Assert.True(await Eventually.TrueAsync(() => rig.Engine.ShowTickedNs >= now), "the lane did not tick");
    }
}
