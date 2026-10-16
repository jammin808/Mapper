using PlayMap.Core.Time;
using PlayMap.Engine.Tests.Host;
using PlayMap.Engine.Tests.Sync;

namespace PlayMap.Engine.Tests.Fleet;

/// <summary>A hot-backup pair across restarts, real nodes on loopback: a backup keeps the epoch its primary holds, so a takeover after
/// the backup restarted still commits above it.</summary>
public sealed class BackupRestartTests
{
    private const long Second = SyncRig.Second;

    [Fact]
    public async Task ABackupThatRestartsTakesTheShowOverAboveItsPrimarysEpoch()
    {
        var clock = new ManualClock(1_000 * Second);
        var time = new ManualTime();
        var primary = await HostRig.StartAsync("FOH-A", network: true, mono: new OffsetClock(clock, 2 * Second), parts: BackupRig.Parts(time),
            settings: s => s with { NodeId = BackupRig.PrimaryId, TrustLoopback = false, Fleet = s.Fleet with { Epoch = 5 } });
        var backup = await BackupRig.StartAsync("FOH-B", BackupRig.BackupId, clock, time);
        try
        {
            await SyncRig.OpenShowAsync(primary);
            await primary.OkAsync("SYNC LEAD show");
            await BackupRig.MeetAsync(primary, backup);
            await BackupRig.BackUpAsync(primary, backup);
            // The backup keeps the epoch its primary holds, for a takeover after a restart.
            Assert.True(await Eventually.TrueAsync(() => backup.Home.LoadSettings("x").Fleet.Epoch == 5), BackupRig.Fleet(backup).ToString());

            // The primary goes; the backup restarts (its role kept) and hears it no more.
            await primary.StopAsync();
            await backup.StopAsync();
            await using var again = await BackupRig.StartAsync("FOH-B", BackupRig.BackupId, clock, time, backup.Dir);
            Assert.Equal(("backup", BackupRig.PrimaryId), (BackupRig.Role(again), BackupRig.Fleet(again).GetProperty("of").GetString()));
            var takeover = await again.RunAsync("FAILOVER");
            Assert.True(takeover.Ok, takeover.Payload);
            Assert.Equal(("primary", 6L), (BackupRig.Role(again), BackupRig.Fleet(again).GetProperty("epoch").GetInt64()));
            Assert.Equal(6, again.Home.LoadSettings("x").Fleet.Epoch);
        }
        finally
        {
            await backup.DisposeAsync();
            await primary.DisposeAsync();
        }
    }
}
