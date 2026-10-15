using System.Collections.Concurrent;
using PlayMap.Core.Time;
using PlayMap.Engine.Tests.Host;

namespace PlayMap.Engine.Tests.Sync;

/// <summary>The group's air through <see cref="IFleetFanout"/>: SHOW ARM ALL on the leader arms its member with a receipt; an air line
/// for a later leader instant is accepted at once and applied at that instant (not a tick before); a client that subscribes to
/// the feed without naming its node reads the lines but is no member (never waited for); a member's own lines are not fanned
/// out again.</summary>
public sealed class GroupAirTests
{
    private const long Second = SyncRig.Second;
    private const long Ahead = 2 * Second;

    [Fact]
    public async Task ShowArmAllArmsTheMemberAndALaterInstantIsAcceptedThenApplied()
    {
        var clock = new ManualClock(1_000 * Second);
        var options = SyncRig.Fast() with { ReceiptWait = TimeSpan.FromMilliseconds(500) };
        await using var leader = await SyncRig.StartAsync("FOH", SyncRig.LeaderId, new OffsetClock(clock, Ahead), options);
        await using var member = await SyncRig.StartAsync("Stage", SyncRig.FollowerId, clock);
        await SyncRig.OpenShowAsync(leader);
        await SyncRig.OpenShowAsync(member);
        await SyncRig.GroupAsync(leader, member);
        var fanout = leader.Engine.Services.Fanout!;
        Assert.Equal([SyncRig.FollowerId], fanout.Members);
        Assert.Empty(member.Engine.Services.Fanout!.Members);   // a member leads no group

        // A reader of the feed that names no node is not a member.
        await using var reader = await leader.WireAsync();
        Assert.StartsWith("OK @a", await reader.AskAsync("@a SUBSCRIBE ANCHORS"), StringComparison.Ordinal);
        Assert.Equal([SyncRig.FollowerId], fanout.Members);
        Assert.StartsWith("ERR @b SUBSCRIBE ANCHORS", await reader.AskAsync("@b SUBSCRIBE ANCHORS noéde"), StringComparison.Ordinal);

        await leader.OkAsync("SHOW ARM ALL doors");
        Assert.True(await Eventually.TrueAsync(() => member.State().GetProperty("show").GetProperty("armed").GetBoolean()));
        Assert.True(await Eventually.TrueAsync(() => leader.Journal().Contains("SHOW ARM \"doors\" on Stage: applied", StringComparison.Ordinal)), leader.Journal());

        // A line for a second from now: accepted at once, applied at its instant.
        var events = new ConcurrentQueue<ShowEvent>();
        member.Engine.EventRaised += events.Enqueue;
        var at = leader.Engine.LeaderNowNs + Second;
        var receipt = Assert.Single(await fanout.SendAsync("BLACKOUT ON", at, CancellationToken.None));
        Assert.Equal(("Stage", "accepted", at), (receipt.Name, receipt.Stage, receipt.AppliedLeaderNs));
        Assert.False(member.State().GetProperty("air").GetProperty("blackout").GetBoolean());
        clock.Advance(Second - 1_000_000);
        await Ticked(member);
        Assert.False(member.State().GetProperty("air").GetProperty("blackout").GetBoolean());
        clock.Advance(1_000_000);
        Assert.True(await Eventually.TrueAsync(() => member.State().GetProperty("air").GetProperty("blackout").GetBoolean()));
        Assert.Equal(at, Assert.Single(events, e => e.Kind == "air.blackout").AtLeaderNs);
        Assert.False(leader.State().GetProperty("air").GetProperty("blackout").GetBoolean());   // sent, not run, on the leader
        // The member's receipt when it ran the line reaches the leader after its send stopped waiting.
        Assert.True(await Eventually.TrueAsync(() => leader.Journal().Contains("BLACKOUT ON on Stage: applied at its instant", StringComparison.Ordinal)), leader.Journal());

        // Lines that follow each other closely reach the member in the order they ran: a panic and its clear, a GO and a NEXT.
        await leader.OkAsync("PANIC FADE 0");
        await leader.OkAsync("PANIC CLEAR FADE 0");
        await leader.OkAsync("PLAYLIST GO pl_show");
        clock.AdvanceMs(500);   // past the armed show's double-GO protection
        await leader.OkAsync("PLAYLIST NEXT pl_show");
        Assert.True(await Eventually.TrueAsync(() => leader.Journal().Contains("PANIC CLEAR FADE 0 on Stage: applied", StringComparison.Ordinal)), leader.Journal());
        Assert.True(await Eventually.TrueAsync(() => member.Engine.AnchorJson("pl_show") == leader.Engine.AnchorJson("pl_show")));
        Assert.Equal("it_b", member.Engine.Playback.Lane("pl_show")!.ItemId);
        Assert.Equal(System.Text.Json.JsonValueKind.Null, member.State().GetProperty("air").GetProperty("panic").ValueKind);

        // A line no member runs from its leader is refused there with words.
        var refused = Assert.Single(await fanout.SendAsync("PROJECT NEW Gone", leader.Engine.LeaderNowNs, CancellationToken.None));
        Assert.Equal("refused", refused.Stage);
        Assert.Contains("is not an air line a member runs from its leader", refused.Words, StringComparison.Ordinal);
        Assert.Equal("Sync test", member.State().GetProperty("project").GetProperty("name").GetString());

        // A line that reaches the member after its instant runs as if at that instant: the fade starts where the leader's did, and
        // the receipt says when it really took effect.
        var late = leader.Engine.LeaderNowNs - 40_000_000;
        var lead = member.Engine.Store.Project.Show.Rules.PanicLeadMs * 1_000_000L;
        var ran = Assert.Single(await fanout.SendAsync("PANIC FADE 1000", late, CancellationToken.None));
        Assert.Equal(("applied", member.Engine.LeaderNowNs), (ran.Stage, ran.AppliedLeaderNs));
        Assert.Equal(late + lead, events.Last(e => e.Kind == "panic").AtLeaderNs);
        Assert.Equal(0.5, member.Engine.AirGain("op_1", late + lead + 500_000_000), 1e-6);

        // Receipts come only from a member's own subscription, for a line it was sent; anything else is refused with words.
        await using var other = await leader.WireAsync();
        Assert.StartsWith("ERR @c no air line 999 waits for a receipt from this session", await other.AskAsync("@c SYNC RECEIPT 999 APPLIED 0"), StringComparison.Ordinal);
        Assert.StartsWith("ERR @d SYNC RECEIPT <seq>", await other.AskAsync("@d SYNC RECEIPT soon APPLIED 0"), StringComparison.Ordinal);
        Assert.StartsWith("ERR @e SYNC RECEIPT <seq>", await other.AskAsync("@e SYNC RECEIPT 1 MAYBE 0"), StringComparison.Ordinal);

        // A node is named only by itself: an unpaired session from elsewhere cannot claim a member's id.
        var sync = leader.Engine.Services.GroupSync!;
        var stranger = new PlayMap.Core.Wire.SessionInfo("x1", "wire", "10.0.0.66", false, "Someone", false);
        var claim = sync.OpenAnchors([SyncRig.FollowerId, "op_1"], stranger, new NullSink());
        Assert.StartsWith("SUBSCRIBE ANCHORS 9f8e7d6c: no PlayMap node 9f8e7d6c is heard from 10.0.0.66", claim.Refused, StringComparison.Ordinal);
        Assert.Equal([SyncRig.FollowerId], fanout.Members);
        var readOnly = sync.OpenAnchors([], stranger, new NullSink());
        Assert.Null(readOnly.Refused);
        readOnly.Sub!.Dispose();
    }

    private sealed class NullSink : PlayMap.Net.IPushSink
    {
        public void Latest(string key, string line)
        {
            // A reader that keeps nothing.
        }

        public bool InOrder(string line) => true;
    }

    private static async Task Ticked(HostRig rig)
    {
        var now = rig.Engine.LeaderNowNs;
        Assert.True(await Eventually.TrueAsync(() => rig.Engine.ShowTickedNs >= now), "the lane did not tick");
    }
}
