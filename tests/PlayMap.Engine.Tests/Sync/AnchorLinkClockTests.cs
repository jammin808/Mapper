using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using PlayMap.Core.Bus;
using PlayMap.Core.Time;
using PlayMap.Engine.Tests.Host;

namespace PlayMap.Engine.Tests.Sync;

/// <summary>A follower's link to its leader's anchor feed counts the leader's silence on the node's clock. A leader that says nothing
/// for longer than the stale mark of real time while that clock stands still leaves the feed live on its first connection; past the
/// stale mark on the clock the feed reads connecting on the same connection, and past the drop mark the link dials the leader again.</summary>
[Collection(SyncTimingsCollection.Name)]
public sealed class AnchorLinkClockTests
{
    private const string LeaderId = "5113e7a0";

    [Fact]
    public async Task AFollowersAnchorLinkCountsItsLeadersSilenceOnTheNodesClock()
    {
        await using var leader = new SilentWire();
        using var clockPort = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var clock = new ManualClock(1_000 * SyncRig.Second);
        await using var follower = await SyncRig.StartAsync("Stage", SyncRig.FollowerId, clock);
        void Hear() => follower.Network.Hello!.Peers.Hear(new ShowBusHello(ShowBusConstants.KindPlayMap, 1, 1, LeaderId, "Quiet leader", "elsewhere",
            new Dictionary<string, int> { ["wire"] = leader.Port, ["sync"] = ((IPEndPoint)clockPort.Client.LocalEndPoint!).Port },
            ShowBusHello.PlayMapCaps, 0, null, null), "127.0.0.1");
        Hear();
        await follower.OkAsync("SYNC FOLLOW show " + LeaderId);
        string Feed() => SyncRig.Sync(follower).GetProperty("feed").GetString() ?? "";
        Assert.True(await Eventually.TrueAsync(() => leader.Accepted == 1 && Feed() == "live"), $"{Feed()} after {leader.Accepted} connections");

        await Task.Delay(TimeSpan.FromMilliseconds(ShowBusConstants.StaleMs + 500));
        Assert.Equal(("live", 1), (Feed(), leader.Accepted));

        clock.AdvanceMs(ShowBusConstants.StaleMs + 500);
        Hear();
        Assert.True(await Eventually.TrueAsync(() => Feed() == "connecting"), Feed());
        Assert.Equal(1, leader.Accepted);

        clock.AdvanceMs(ShowBusConstants.GoneMs - ShowBusConstants.StaleMs);
        Hear();
        Assert.True(await Eventually.TrueAsync(() => leader.Accepted == 2, 30000), $"{Feed()} after {leader.Accepted} connections");
        Assert.Contains(leader.Heard, l => l.Contains("SUBSCRIBE ANCHORS " + SyncRig.FollowerId, StringComparison.Ordinal));
    }

    /// <summary>A leader's line wire that takes every connection, reads every line and says nothing.</summary>
    private sealed class SilentWire : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly ConcurrentBag<TcpClient> _clients = [];
        private readonly Task _accept;
        private int _accepted;

        public SilentWire()
        {
            _listener.Start();
            _accept = Task.Run(AcceptAsync, CancellationToken.None);
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public int Accepted => Volatile.Read(ref _accepted);

        public ConcurrentQueue<string> Heard { get; } = new();

        private async Task AcceptAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_stop.Token);
                }
                catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
                {
                    return;
                }
                _clients.Add(client);
                Interlocked.Increment(ref _accepted);
                _ = Task.Run(() => ReadAsync(client), CancellationToken.None);
            }
        }

        private async Task ReadAsync(TcpClient client)
        {
            try
            {
                using var reader = new StreamReader(client.GetStream());
                while (await reader.ReadLineAsync(_stop.Token) is { } line)
                    Heard.Enqueue(line);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException or OperationCanceledException)
            {
                // The follower let the connection go, or the test is over.
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            _listener.Stop();
            foreach (var c in _clients)
                c.Dispose();
            await _accept;
            _stop.Dispose();
        }
    }
}
