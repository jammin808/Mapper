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

    [Fact]
    public async Task AFollowSwitchTheFileCannotKeepIsSaidAfterTheRowOfTheLineThatSetIt()
    {
        // The switch is written beside the lane and its file refuses the write: the fault row that says so follows the row of the
        // LINK PATTERNS that set it, however late that row is written.
        await using var node = await SyncRig.StartAsync("Stage", SyncRig.FollowerId, new ManualClock(1_000 * SyncRig.Second));
        node.Home.WriteFile = (_, _) => throw new IOException("disk full");
        var fault = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        node.Engine.Journal.Written += r =>
        {
            if (r.Kind == "fault" && r.Text.StartsWith("the follow-blackout switch", StringComparison.Ordinal))
                fault.TrySetResult();
        };
        node.Engine.RowHeldForTests = (line, held) => !held && line.Raw.StartsWith("LINK PATTERNS FOLLOW-BLACKOUT", StringComparison.Ordinal)
            ? fault.Task.WaitAsync(TimeSpan.FromSeconds(30))
            : Task.CompletedTask;
        var before = node.Engine.Journal.Recent(200).Count;

        await node.OkAsync("LINK PATTERNS FOLLOW-BLACKOUT OFF");
        await fault.Task.WaitAsync(TimeSpan.FromSeconds(30));

        var rows = node.Engine.Journal.Recent(200).Skip(before)
            .Where(r => r.Line?.StartsWith("LINK PATTERNS", StringComparison.Ordinal) == true || r.Text.StartsWith("the follow-blackout switch", StringComparison.Ordinal)).ToList();
        Assert.True(rows.Count == 2, node.Journal());
        Assert.Equal(["verb", "fault"], rows.Select(r => r.Kind));
        Assert.StartsWith("LINK PATTERNS FOLLOW-BLACKOUT ", rows[0].Line, StringComparison.Ordinal);
        Assert.Equal("the follow-blackout switch — not kept for the next start (the node's settings could not be written: disk full)", rows[1].Text);
        Assert.True(Kept(node).Sync.FollowPatternsBlackout);
    }

    private static Core.Bus.NodeSettings Kept(HostRig rig) => rig.Home.LoadSettings("unused");
}
