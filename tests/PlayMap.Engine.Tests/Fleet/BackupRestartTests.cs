using PlayMap.Core.Fleet;
using PlayMap.Core.Media;
using PlayMap.Core.Time;
using PlayMap.Engine.Tests.Host;
using PlayMap.Engine.Tests.Sync;
using PlayMap.Engine.Tests.VideoIo;

namespace PlayMap.Engine.Tests.Fleet;

/// <summary>A hot-backup pair across restarts, real nodes on loopback each with an LED sender and an NDI sender the test reads: a backup
/// keeps the epoch its primary holds, so a takeover after the backup restarted still commits above it; an old primary that restarts
/// while its backup has the show puts nothing out beside its outputs (LED data, sends, sound) until it has heard that backup, then fences
/// itself without ever having sent; a primary whose backups are not heard goes out once its own clock has waited the time it gives them.</summary>
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

    [Fact]
    public async Task AnOldPrimaryThatRestartsSendsNothingUntilItHearsItsBackupThenFencesItself()
    {
        var clock = new ManualClock(1_000 * Second);
        var time = new ManualTime();
        var ledA = new RecordingLed();
        var ledB = new RecordingLed();
        using var ndiA = new FakeVideoProvider(LiveProtocol.Ndi, clock);
        using var ndiB = new FakeVideoProvider(LiveProtocol.Ndi, clock);
        var primary = await StartAsync("FOH-A", BackupRig.PrimaryId, new OffsetClock(clock, 2 * Second), time, ledA, ndiA);
        await using var backup = await StartAsync("FOH-B", BackupRig.BackupId, clock, time, ledB, ndiB);
        try
        {
            await SyncRig.OpenShowAsync(primary);
            await primary.OkAsync("SYNC LEAD show");
            await BackupRig.MeetAsync(primary, backup);
            await BackupRig.BackUpAsync(primary, backup);
            Assert.True(ledA.Master);
            Assert.False(ledB.Master);

            // The primary goes and its backup takes the show.
            await primary.StopAsync();
            var takeover = await backup.RunAsync("FAILOVER");
            Assert.True(takeover.Ok, takeover.Payload);
            Assert.True(ledB.Master);

            // The old primary comes back as it left, a primary with a backup: until it hears that backup, nothing goes out beside its
            // outputs — no LED data, no send, no sound (the backup may have the show, as it has).
            var ledA2 = new RecordingLed();
            using var ndiA2 = new FakeVideoProvider(LiveProtocol.Ndi, clock);
            await using var again = await StartAsync("FOH-A", BackupRig.PrimaryId, new OffsetClock(clock, 2 * Second), time, ledA2, ndiA2, primary.Dir);
            Assert.NotEqual(NetworkAir.Live, again.Engine.NetworkAir);
            Assert.False(ledA2.Master);
            Assert.Equal(0f, again.Engine.AirAudioGain(again.Engine.LeaderNowNs));

            // It hears its backup lead the show under a higher epoch, and fences itself — having sent nothing at all.
            await BackupRig.MeetAsync(again, backup);
            Assert.True(await Eventually.TrueAsync(() => BackupRig.Role(again) == "fenced"), BackupRig.Fleet(again) + "\n" + again.Journal());
            Assert.Equal(1, BackupRig.Fleet(again).GetProperty("epoch").GetInt64());
            Assert.Equal(NetworkAir.Held, again.Engine.NetworkAir);
            Assert.DoesNotContain(ledA2.Calls, c => c.On);
            Assert.Empty(ndiA2.Sinks);
            Assert.Equal(0f, again.Engine.Services.OutputGains!.GainOf("op_1", again.Engine.LeaderNowNs));
            Assert.True(ledB.Master);
            Assert.Equal("primary", BackupRig.Role(backup));
        }
        finally
        {
            await primary.DisposeAsync();
        }
    }

    [Fact]
    public async Task APrimaryWhoseBackupIsNotHeardGoesOutOnceItsClockHasWaitedForIt()
    {
        var clock = new ManualClock(1_000 * Second);
        var led = new RecordingLed();
        using var ndi = new FakeVideoProvider(LiveProtocol.Ndi, clock);
        await using var node = await HostRig.StartAsync("FOH-A", network: true, mono: clock, parts: NetworkEdges.With(BackupRig.Parts(new ManualTime()), led, ndi),
            settings: s => s with
            {
                NodeId = BackupRig.PrimaryId,
                TrustLoopback = false,
                Led = s.Led with { Output = true },
                Fleet = s.Fleet with { Role = FleetRoles.Primary, Backups = [BackupRig.BackupId] },
            });
        Assert.Equal(NetworkAir.Silent, node.Engine.NetworkAir);
        Assert.Contains("listens for its hot backup", node.Engine.NetworkAirWords, StringComparison.Ordinal);
        Assert.False(led.Master);

        // Its backup is not heard: once its own clock has given it the time (six seconds, when the board calls a machine gone), the
        // node's LED data, sends and sound go out.
        clock.Advance(6 * Second + 1);
        Assert.True(await Eventually.TrueAsync(() => node.Engine.NetworkAir == NetworkAir.Live), node.Journal());
        Assert.True(await Eventually.TrueAsync(() => led.Master), node.Journal());
        Assert.Equal(1f, node.Engine.AirAudioGain(node.Engine.LeaderNowNs));
        Assert.Contains("did not hear its hot backup", node.Journal(), StringComparison.Ordinal);
        Assert.Equal("primary", BackupRig.Role(node));
    }

    /// <summary>A backup that hears its primary hold an epoch above the one it keeps takes that epoch, for this run though its settings
    /// file refuses the write, and says so: STATE holds the epoch, and has been pushed, by the time that row is written.</summary>
    [Fact]
    public async Task ABackupWhoseSettingsCannotKeepItsPrimarysEpochSaysSo()
    {
        var clock = new ManualClock(1_000 * Second);
        var time = new ManualTime();
        await using var primary = await HostRig.StartAsync("FOH-A", network: true, mono: new OffsetClock(clock, 2 * Second), parts: BackupRig.Parts(time),
            settings: s => s with { NodeId = BackupRig.PrimaryId, TrustLoopback = false, Fleet = s.Fleet with { Epoch = 5 } });
        // The backup starts as it was kept: a hot backup of FOH-A, under an epoch below the one FOH-A holds now.
        await using var backup = await HostRig.StartAsync("FOH-B", network: true, mono: clock, parts: BackupRig.Parts(time),
            settings: s => s with
            {
                NodeId = BackupRig.BackupId,
                TrustLoopback = false,
                Fleet = s.Fleet with { Role = FleetRoles.Backup, Of = BackupRig.PrimaryId, Epoch = 2 },
            });
        backup.Home.WriteFile = (_, _) => throw new IOException("disk full");
        var epoch = new RowWatch(backup, "fault", "the epoch 5 FOH-A holds", () => backup.Engine.Services.Backup!.Epoch == 5);

        await BackupRig.MeetAsync(primary, backup);
        var (row, state, pushedBefore) = await epoch.SeenAsync(TimeSpan.FromSeconds(15));

        Assert.Equal("the epoch 5 FOH-A holds — not kept for the next start (the node's settings could not be written: disk full): after a "
            + "restart a takeover by FOH-B could commit one below it, which would not fence FOH-A", row.Text);
        Assert.False(row.Ok);
        var fleet = state.GetProperty("fleet");
        Assert.Equal(("backup", 5L), (fleet.GetProperty("role").GetString(), fleet.GetProperty("epoch").GetInt64()));
        Assert.True(pushedBefore, "no push of STATE was raised between the epoch's move and the row");
        Assert.Equal(2, backup.Home.LoadSettings("x").Fleet.Epoch);
    }

    private static Task<HostRig> StartAsync(string name, string id, IMonoClock mono, ManualTime time, RecordingLed led, FakeVideoProvider ndi, DirectoryInfo? dir = null) =>
        HostRig.StartAsync(name, network: true, mono: mono, parts: NetworkEdges.With(BackupRig.Parts(time), led, ndi),
            settings: s => s with { NodeId = id, TrustLoopback = false, Led = s.Led with { Output = true } }, dir: dir);
}
