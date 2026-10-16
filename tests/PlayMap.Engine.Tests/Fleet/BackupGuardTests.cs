using PlayMap.Core.Time;
using PlayMap.Core.Wire;
using PlayMap.Engine.Tests.Host;
using PlayMap.Engine.Tests.Sync;

namespace PlayMap.Engine.Tests.Fleet;

/// <summary>What keeps a hot-backup pair to one node with the show, real nodes on loopback: a hand-back is taken only by a backup whose
/// mirror is identical.</summary>
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

    /// <summary>A line as the node's own mirror link hands it on (the primary's session, transport "fleet").</summary>
    private static async Task<CommandReply> Mirror(HostRig rig, string text)
    {
        Assert.True(WireGrammar.TryParse(text, out var line, out var error), error?.Reason);
        var session = new SessionInfo("fleet:" + BackupRig.PrimaryId, "fleet", "127.0.0.1", false, "FOH-A (mirror)", true);
        return await rig.Engine.HandleAsync(line, session, CancellationToken.None);
    }
}
