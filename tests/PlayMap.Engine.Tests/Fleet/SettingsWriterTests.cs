using PlayMap.Core.Time;
using PlayMap.Engine.Tests.Host;
using PlayMap.Engine.Tests.Sync;

namespace PlayMap.Engine.Tests.Fleet;

/// <summary>The node's settings file has one writer, the network host: a sync role (the sync part), an epoch (the fleet's promotion),
/// the Patterns link with its token (the host), a new pairing token and the follow-blackout switch (the sync part again), written one
/// after another by their own parts, each find every other's keys still in the file — and the running node says the same.</summary>
public sealed class SettingsWriterTests
{
    [Fact]
    public async Task ASyncRoleAnEpochTheLinkAndANewTokenSurviveEachOther()
    {
        const string deskToken = "AAAA-BBBB-CCCC";
        var clock = new ManualClock(1_000 * SyncRig.Second);
        var time = new ManualTime();
        await using var desk = new FakePatternsDesk(deskToken);
        await using var leader = await BackupRig.StartAsync("FOH", BackupRig.PrimaryId, clock, time);
        await using var node = await BackupRig.StartAsync("Stage", BackupRig.BackupId, clock, time);
        await SyncRig.OpenShowAsync(leader);
        await SyncRig.OpenShowAsync(node);
        await leader.OkAsync("SYNC LEAD show");
        await BackupRig.MeetAsync(leader, node);
        Assert.True(await Eventually.TrueAsync(() => node.Network.Hello!.Peers.Find(leader.Id)?.Hello.Port("sync") == leader.Network.SyncPort));

        // The sync part keeps the role it follows in.
        await node.OkAsync("SYNC FOLLOW show " + leader.Id);
        var followed = Kept(node).Sync;
        Assert.Equal(("follow", "show", leader.Id), (followed.Role, followed.Group, followed.Leader));

        // The host keeps the link and its token.
        await node.OkAsync($"LINK PATTERNS 127.0.0.1:{desk.Port} {deskToken}");
        Assert.True(await Eventually.TrueAsync(() => Kept(node).PatternsToken == deskToken));
        Assert.Equal("follow", Kept(node).Sync.Role);

        // The fleet keeps the epoch of a promotion, and the sync part then the role it leads in.
        await node.OkAsync("FLEET PROMOTE");
        Assert.True(await Eventually.TrueAsync(() => Kept(node) is { Fleet.Epoch: 1, Sync.Role: "lead" }), $"{Kept(node).Sync} {Kept(node).Fleet}");
        Assert.Equal(("show", $"127.0.0.1:{desk.Port}", deskToken), (Kept(node).Sync.Group, Kept(node).PatternsHost, Kept(node).PatternsToken));

        // A new pairing token is written over the file as it stands, not over the settings this run began with.
        var before = Kept(node).Token;
        await node.OkAsync("PAIRING NEW");
        var token = Kept(node);
        Assert.NotEqual(before, token.Token);
        Assert.Equal(("lead", "show", 1L, deskToken), (token.Sync.Role, token.Sync.Group, token.Fleet.Epoch, token.PatternsToken));

        // The follow switch (the sync part, beside the lane) keeps the token, the link and the epoch.
        await node.OkAsync("LINK PATTERNS FOLLOW-BLACKOUT OFF");
        Assert.True(await Eventually.TrueAsync(() => !Kept(node).Sync.FollowPatternsBlackout));
        var last = Kept(node);
        Assert.Equal((token.Token, "lead", 1L, deskToken, $"127.0.0.1:{desk.Port}"),
            (last.Token, last.Sync.Role, last.Fleet.Epoch, last.PatternsToken, last.PatternsHost));
        // What the node runs with is what it keeps: the epoch it announces and the token its wire compares against.
        Assert.Equal(1, BackupRig.Fleet(node).GetProperty("epoch").GetInt64());
        Assert.Equal(last.Token, node.Network.Settings.Token);
    }

    private static Core.Bus.NodeSettings Kept(HostRig rig) => rig.Home.LoadSettings("unused");
}
