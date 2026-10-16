using PlayMap.Core.Time;
using PlayMap.Core.Wire;
using PlayMap.Engine.Fleet;
using PlayMap.Engine.Tests.Host;
using PlayMap.Engine.Tests.Sync;

namespace PlayMap.Engine.Tests.Fleet;

/// <summary>What keeps a hot-backup pair to one node with the show, real nodes on loopback: a hand-back is taken only by a backup whose
/// mirror is identical; a node that has begun to stop takes no show over.</summary>
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
