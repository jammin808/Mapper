using PlayMap.Core.Time;
using PlayMap.Core.Wire;
using PlayMap.Engine.Fleet;
using PlayMap.Engine.Tests.Host;
using PlayMap.Engine.Tests.Sync;

namespace PlayMap.Engine.Tests.Fleet;

/// <summary>What keeps a hot-backup pair to one node with the show, real nodes on loopback: a hand-back is taken only by a backup whose
/// mirror is identical; a node that has begun to stop takes no show over; a primary that could not hear a takeover (its fleet monitor
/// off) takes no backup; a second backup does not take a show another node has already taken from its primary.</summary>
public sealed class BackupGuardTests
{
    private const long Second = SyncRig.Second;

    [Fact]
    public async Task AHandBackIsTakenOnlyByABackupWhoseMirrorIsIdentical()
    {
        var clock = new ManualClock(1_000 * Second);
        var time = new ManualTime();
        var primary = await BackupRig.StartAsync("FOH-A", BackupRig.PrimaryId, new OffsetClock(clock, 2 * Second), time);
        await using var backup = await BackupRig.StartAsync("FOH-B", BackupRig.BackupId, clock, time);
        try
        {
            await SyncRig.OpenShowAsync(primary);
            await primary.OkAsync("SYNC LEAD show");
            await BackupRig.MeetAsync(primary, backup);
            await BackupRig.BackUpAsync(primary, backup);

            // The primary goes: the backup's mirror is lost (the last show and air hold).
            await primary.StopAsync();
            Assert.True(await Eventually.TrueAsync(() => BackupRig.MirrorState(backup) == "lost"), BackupRig.Fleet(backup).ToString());
            // A hand-back that reaches it now is refused: the show it holds is not known to be its primary's.
            var handback = await Mirror(backup, $"MIRROR HANDBACK {{\"from\":\"{BackupRig.PrimaryId}\",\"epoch\":0}}");
            Assert.False(handback.Ok);
            Assert.Contains("is not identical", handback.Payload, StringComparison.Ordinal);
            Assert.Equal("backup", BackupRig.Role(backup));
            Assert.Contains("is not identical", backup.Journal(), StringComparison.Ordinal);
            Assert.Equal(("backup", 0L), (backup.Home.LoadSettings("x").Fleet.Role, backup.Home.LoadSettings("x").Fleet.Epoch));
        }
        finally
        {
            await primary.DisposeAsync();
        }
    }

    [Fact]
    public async Task AStoppingNodeTakesNoShowOver()
    {
        var clock = new ManualClock(1_000 * Second);
        var time = new ManualTime();
        await using var primary = await BackupRig.StartAsync("FOH-A", BackupRig.PrimaryId, new OffsetClock(clock, 2 * Second), time);
        await using var backup = await BackupRig.StartAsync("FOH-B", BackupRig.BackupId, clock, time);
        await SyncRig.OpenShowAsync(primary);
        await primary.OkAsync("SYNC LEAD show");
        await BackupRig.MeetAsync(primary, backup);
        await BackupRig.BackUpAsync(primary, backup);

        // Even by the node's own line (a hand-back runs the takeover so): a node that stops would take the show, switch the projectors
        // to itself and go.
        backup.Engine.BeginStop();
        var takeover = await Own(backup, "FAILOVER");
        Assert.False(takeover.Ok);
        Assert.Equal(BackupNode.StoppingTakesNothing, takeover.Payload);
        Assert.Equal("backup", backup.Engine.Services.Backup!.Role);
        Assert.Equal(("backup", 0L), (backup.Home.LoadSettings("x").Fleet.Role, backup.Home.LoadSettings("x").Fleet.Epoch));
        Assert.Equal("primary", BackupRig.Role(primary));
    }

    [Fact]
    public async Task APrimaryThatCouldNotHearATakeoverTakesNoBackup()
    {
        var clock = new ManualClock(1_000 * Second);
        var time = new ManualTime();
        await using var primary = await HostRig.StartAsync("FOH-A", network: true, mono: new OffsetClock(clock, 2 * Second), parts: BackupRig.Parts(time),
            settings: s => s with { NodeId = BackupRig.PrimaryId, TrustLoopback = false, Fleet = s.Fleet with { Monitor = false } });
        await using var backup = await BackupRig.StartAsync("FOH-B", BackupRig.BackupId, clock, time);
        await SyncRig.OpenShowAsync(primary);
        await primary.OkAsync("SYNC LEAD show");
        HostRig.Introduce(primary, backup);
        Assert.True(await Eventually.TrueAsync(() => backup.Network.Hello!.Peers.Find(primary.Id)?.Hello.Port("http") > 0));

        // Its fleet monitor is off: it would never hear its backup take the show, nor fence itself — two nodes would serve one show.
        await backup.OkAsync($"CREDENTIAL SET node.{primary.Id} {HostRig.Token}");
        var joined = await backup.RunAsync("FLEET BACKUP " + primary.Id);
        Assert.False(joined.Ok);
        Assert.Contains("fleet monitor is off", joined.Payload, StringComparison.Ordinal);
        Assert.Equal("solo", BackupRig.Role(backup));
        Assert.Equal("solo", BackupRig.Role(primary));
    }

    [Fact]
    public async Task ASecondBackupDoesNotTakeAShowAnotherNodeTookFromItsPrimary()
    {
        var clock = new ManualClock(1_000 * Second);
        var time = new ManualTime();
        await using var primary = await BackupRig.StartAsync("FOH-A", BackupRig.PrimaryId, new OffsetClock(clock, 2 * Second), time);
        await using var first = await BackupRig.StartAsync("FOH-B", BackupRig.BackupId, clock, time);
        await using var second = await BackupRig.StartAsync("FOH-C", BackupRig.ThirdId, clock, time);
        await SyncRig.OpenShowAsync(primary);
        await primary.OkAsync("SYNC LEAD show");
        await BackupRig.MeetAsync(primary, first, second);
        await BackupRig.BackUpAsync(primary, first);
        await BackupRig.BackUpAsync(primary, second);

        var takeover = await first.RunAsync("FAILOVER");
        Assert.True(takeover.Ok, takeover.Payload);
        Assert.True(await Eventually.TrueAsync(() => BackupRig.Role(primary) == "fenced"), BackupRig.Fleet(primary).ToString());
        // The second backup hears that FOH-B took FOH-A's show: its own FAILOVER would make two primaries of one show.
        Assert.True(await Eventually.TrueAsync(() => second.Engine.Services.FleetPeers!.Peers()
            .Any(p => p.Id == first.Id && p.Health?.Facts is { Role: "primary" } f && f.Of == primary.Id)));
        var again = await second.RunAsync("FAILOVER");
        Assert.False(again.Ok);
        Assert.Contains("FOH-B took FOH-A's show", again.Payload, StringComparison.Ordinal);
        Assert.Contains("FLEET BACKUP FOH-B", again.Payload, StringComparison.Ordinal);
        Assert.Equal("backup", BackupRig.Role(second));
        Assert.Equal("primary", BackupRig.Role(first));
    }

    private static async Task<CommandReply> Own(HostRig rig, string text)
    {
        Assert.True(WireGrammar.TryParse(text, out var line, out var error), error?.Reason);
        return await rig.Engine.HandleAsync(line, EngineCore.SystemSession, CancellationToken.None);
    }

    /// <summary>A line as the node's own mirror link hands it on (the primary's session, transport "fleet").</summary>
    private static async Task<CommandReply> Mirror(HostRig rig, string text)
    {
        Assert.True(WireGrammar.TryParse(text, out var line, out var error), error?.Reason);
        var session = new SessionInfo("fleet:" + BackupRig.PrimaryId, "fleet", "127.0.0.1", false, "FOH-A (mirror)", true);
        return await rig.Engine.HandleAsync(line, session, CancellationToken.None);
    }
}
