using System.Text.Json;
using PlayMap.Core.Time;
using PlayMap.Engine.Tests.Host;

namespace PlayMap.Engine.Tests.Sync;

/// <summary>A member's blind draft on a playlist of the group (sync × blind): the group's GO reaches a member as the leader's anchor,
/// so a commit waiting there for a GO lands on the GO's own instant, and one waiting for the playlist's next switch lands on the
/// leader's early GO into the next item, as either does on the leader — never left waiting for a GO the member's own desk will not
/// give, nor slid to the end of the item the GO brought.</summary>
public sealed class MemberCommitTests
{
    private const long Second = SyncRig.Second;
    private const long Ms = 1_000_000;

    [Fact]
    public async Task AMembersCommitWaitingForAGoLandsWithTheLeadersGoAndANextCommitWithItsEarlyNext()
    {
        var clock = new ManualClock(1_000 * Second);
        await using var leader = await SyncRig.StartAsync("FOH", SyncRig.LeaderId, new OffsetClock(clock, 2 * Second));
        await using var member = await SyncRig.StartAsync("Stage", SyncRig.FollowerId, clock);
        await SyncRig.OpenShowAsync(leader);
        await SyncRig.OpenShowAsync(member);
        await SyncRig.GroupAsync(leader, member);

        // ON GO: the leader's GO, 200 ms ahead, is the member's GO too.
        await member.OkAsync("BLIND ON");
        await member.OkAsync("SET cv_main background \"#FF0000\"");
        await member.OkAsync("BLIND COMMIT ON GO pl_show");
        var go = leader.Engine.LeaderNowNs + 200 * Ms;
        await leader.OkAsync("PLAYLIST GO pl_show AT +200");
        await AdoptedAsync(leader, member);
        Assert.Equal(go, Commit(member).GetProperty("effectiveLeaderNs").GetInt64());
        Assert.Equal(go, member.Engine.Snapshot.Pending!.EffectiveLeaderNs);
        clock.AdvanceMs(199);
        await Ticked(member);
        Assert.Equal("#000000", Background(member));
        clock.AdvanceMs(1);
        Assert.True(await Eventually.TrueAsync(() => Background(member) == "#FF0000"), member.Journal());
        // The landing's row is written once the commit has landed, on the lane's tick that landed it: it is read once that tick is over.
        await Ticked(member);
        Assert.Contains("blind commit: 1 change landed (on go) by Desk", member.Journal(), StringComparison.Ordinal);

        // NEXT: the item would switch at its end, ten seconds on; the leader's NEXT, 300 ms ahead, switches it early, and the
        // member's draft lands with that switch.
        await member.OkAsync("BLIND ON");
        await member.OkAsync("SET cv_main background \"#00FF00\"");
        var commit = JsonDocument.Parse(await member.OkAsync("BLIND COMMIT NEXT pl_show")).RootElement;
        Assert.Equal(go + 10 * Second, commit.GetProperty("effectiveLeaderNs").GetInt64());
        var next = leader.Engine.LeaderNowNs + 300 * Ms;
        await leader.OkAsync("PLAYLIST NEXT pl_show AT +300");
        await AdoptedAsync(leader, member);
        Assert.Equal(next, Commit(member).GetProperty("effectiveLeaderNs").GetInt64());
        clock.AdvanceMs(299);
        await Ticked(member);
        Assert.Equal("#FF0000", Background(member));
        clock.AdvanceMs(1);
        Assert.True(await Eventually.TrueAsync(() => Background(member) == "#00FF00"), member.Journal());
        Assert.Equal("it_b", member.Engine.Playback.Lane("pl_show")!.At(next).ItemId);
    }

    /// <summary>The member holds the leader's anchor of the group's playlist.</summary>
    private static async Task AdoptedAsync(HostRig leader, HostRig member) =>
        Assert.True(await Eventually.TrueAsync(() => member.Engine.AnchorJson("pl_show") == leader.Engine.AnchorJson("pl_show")), member.Journal());

    private static JsonElement Commit(HostRig rig) => rig.State().GetProperty("show").GetProperty("blind").GetProperty("commit");

    private static string Background(HostRig rig) =>
        JsonDocument.Parse(rig.Engine.ProjectJson().Json).RootElement.GetProperty("canvases")[0].GetProperty("background").GetString()!;

    private static async Task Ticked(HostRig rig)
    {
        var now = rig.Engine.LeaderNowNs;
        Assert.True(await Eventually.TrueAsync(() => rig.Engine.ShowTickedNs >= now), "the lane did not tick");
    }
}
