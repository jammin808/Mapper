using System.Net;
using System.Net.Sockets;
using PlayMap.Core.Bus;
using PlayMap.Core.Fleet;
using PlayMap.Core.Time;
using PlayMap.Engine.Tests.Host;
using PlayMap.Engine.Tests.Sync;
using PlayMap.Net.Fleet;

namespace PlayMap.Engine.Tests.Fleet;

/// <summary>The meeting the hot-backup tests start from: each node hears the others' hellos at the web port each serves and their
/// health, however a fleet monitor's first subscription to a peer ended — refused, or made to another machine under the peer's id — since
/// the monitor dials again once its backoff has run out on the fleet's clock, which these nodes' tests move only by hand.</summary>
public sealed class BackupRigTests
{
    private const long Second = SyncRig.Second;

    [Fact]
    public async Task NodesMeetThoughOneFirstDialledAnotherMachineUnderItsPeersIdThatServesNothing()
    {
        var clock = new ManualClock(1_000 * Second);
        var time = new ManualTime();
        await using var primary = await BackupRig.StartAsync("FOH-A", BackupRig.PrimaryId, new OffsetClock(clock, 2 * Second), time);
        await using var backup = await BackupRig.StartAsync("FOH-B", BackupRig.BackupId, clock, time);
        var monitor = Assert.IsType<FleetMonitor>(primary.Engine.Services.FleetPeers);

        // Before the meeting a hello under the backup's id reaches the primary from a machine whose web port serves nothing (another
        // test's node that has stopped): the primary's monitor dials it, is refused, and waits on the fleet's clock to dial again.
        var closed = ClosedPort();
        var stray = new ShowBusHello(ShowBusConstants.KindPlayMap, 1, 1, BackupRig.BackupId, "FOH-B", "elsewhere",
            new Dictionary<string, int>(StringComparer.Ordinal) { ["http"] = closed, ["ws"] = closed }, [], 0, "f0f0f0f0", null);
        using (var udp = new UdpClient(AddressFamily.InterNetwork))
            await udp.SendAsync(stray.ToJson(), new IPEndPoint(IPAddress.Loopback, primary.Network.HelloPort));
        Assert.True(await Eventually.TrueAsync(() => primary.Network.Hello!.Peers.Find(BackupRig.BackupId)?.Hello.Port("http") == closed
            && monitor.Redialling == 1), $"the primary's monitor never dialled the stray hello: {monitor.WordsOf(BackupRig.BackupId)}");

        await BackupRig.MeetAsync(primary, backup);
        Assert.Equal(backup.Network.HttpPort, primary.Network.Hello!.Peers.Find(BackupRig.BackupId)!.Hello.Port("http"));
        AssertHeard(monitor, BackupRig.BackupId);
    }

    [Fact]
    public async Task NodesMeetThoughOnesFirstSubscriptionToItsPeerEndedUnheard()
    {
        var clock = new ManualClock(1_000 * Second);
        var time = new ManualTime();
        var refusing = (RefusesFirst?)null;
        await using var primary = await BackupRig.StartAsync("FOH-A", BackupRig.PrimaryId, new OffsetClock(clock, 2 * Second), time,
            feeds: pins => refusing = new RefusesFirst(new HealthFeedClient(pins.Current, time)));
        await using var backup = await BackupRig.StartAsync("FOH-B", BackupRig.BackupId, clock, time);
        var monitor = Assert.IsType<FleetMonitor>(primary.Engine.Services.FleetPeers);

        // The primary's first subscription to its backup ends with no record, as a refused or cut dial ends: its monitor waits on the
        // fleet's clock to dial again.
        await BackupRig.MeetAsync(primary, backup);
        Assert.Equal(1, refusing!.Refused);
        Assert.True(refusing.Dialled >= 1, "the monitor never dialled the backup again");
        AssertHeard(monitor, BackupRig.BackupId);
    }

    /// <summary>The monitor holds the peer's own health, live, from a subscription that stands.</summary>
    private static void AssertHeard(FleetMonitor monitor, string id)
    {
        var heard = Assert.Single(monitor.Peers(), p => p.Id == id);
        Assert.Equal(id, heard.Health!.Facts.Node);
        Assert.Equal(FleetHeard.Live, heard.Heard);
        Assert.StartsWith("subscribed", monitor.WordsOf(id), StringComparison.Ordinal);
    }

    /// <summary>A loopback port nothing listens on: taken from the system and let go at once.</summary>
    private static int ClosedPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    /// <summary>A monitor's transport whose first subscription ends at once with no record; every later one is the real dial's.</summary>
    private sealed class RefusesFirst(IHealthFeeds real) : IHealthFeeds
    {
        private int _refused;
        private int _dialled;

        public int Refused => Volatile.Read(ref _refused);

        public int Dialled => Volatile.Read(ref _dialled);

        public Task<FeedEnd> RunAsync(FleetDial dial, Action<HealthRecord> onRecord, Action<string> onPin, CancellationToken ct)
        {
            if (Interlocked.CompareExchange(ref _refused, 1, 0) == 0)
                return Task.FromResult(new FeedEnd($"{dial.Name}'s health at {dial.Host}:{dial.Port} did not answer: refused by the test", false, false));
            Interlocked.Increment(ref _dialled);
            return real.RunAsync(dial, onRecord, onPin, ct);
        }
    }
}
