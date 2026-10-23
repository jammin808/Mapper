using System.Net;
using System.Net.Sockets;
using System.Text;
using PlayMap.Net.Fleet;
using PlayMap.Net.Tls;

namespace PlayMap.Net.Tests.Fleet;

/// <summary>A peer's health dial: a dial the peer fails before the monitor stops ends with words that say so; one the peer fails as
/// the monitor's stop is taken ends as the stop, never as a fault of the feed.</summary>
public sealed class HealthFeedsTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task ADialThePeerFailsAsTheStopIsTakenEndsAsTheStop()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var client = new HealthFeedClient(new PinStore(), TimeProvider.System);
        var dial = new FleetDial("0badc0de", "FOH-B", "127.0.0.1", port, null);

        // Before the stop: the peer takes the dial's upgrade and resets it, and the feed's end says so.
        using (var running = new CancellationTokenSource())
        {
            var run = client.RunAsync(dial, _ => { }, _ => { }, running.Token);
            using var peer = await TakeUpgradeAsync(listener);
            Reset(peer);
            var end = await run.WaitAsync(Patience);
            Assert.StartsWith($"FOH-B's health at 127.0.0.1:{port} did not answer: ", end.Words, StringComparison.Ordinal);
            Assert.False(end.Heard || end.Refused);
        }

        // As the stop is taken: the peer resets the dial in a callback on the stop registered after the dial linked its own token to
        // it, so it runs first when the stop is taken (a token's callbacks run newest first), and the dial ends before the stop reaches
        // its own token: the order in which a dial refused late (on Windows about 2 s for a peer that went away) can meet a stop taken
        // with CancelAsync, whose callbacks run after it returns.
        using var stop = new CancellationTokenSource();
        var late = client.RunAsync(dial, _ => { }, _ => { }, stop.Token);
        using var held = await TakeUpgradeAsync(listener);
        using var ended = new ManualResetEventSlim();
        _ = late.ContinueWith(_ => ended.Set(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        var failedAfterTheStop = false;
        var endedBeforeTheDialsCancel = false;
        using (stop.Token.Register(() =>
        {
            failedAfterTheStop = stop.IsCancellationRequested;
            Reset(held);
            endedBeforeTheDialsCancel = ended.Wait(Patience);
        }))
        {
            await stop.CancelAsync();
        }

        Assert.True(failedAfterTheStop && endedBeforeTheDialsCancel,
            $"the peer failed the dial after the stop: {failedAfterTheStop}, and the dial ended before the stop reached its token: {endedBeforeTheDialsCancel}");
        var thrown = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => late);
        Assert.Equal(stop.Token, thrown.CancellationToken);
    }

    /// <summary>The next dial's connection, once its upgrade request is in (the dial has linked its token by then).</summary>
    private static async Task<Socket> TakeUpgradeAsync(TcpListener listener)
    {
        using var patience = new CancellationTokenSource(Patience);
        var socket = await listener.AcceptSocketAsync(patience.Token);
        try
        {
            var buffer = new byte[4096];
            var request = new StringBuilder();
            while (!request.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
            {
                var n = await socket.ReceiveAsync(buffer, SocketFlags.None, patience.Token);
                Assert.True(n > 0, "the dial closed before its upgrade request was in: " + request);
                request.Append(Encoding.ASCII.GetString(buffer, 0, n));
            }
            Assert.StartsWith("GET /health HTTP/1.1", request.ToString(), StringComparison.Ordinal);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>The peer goes: the connection is reset, not closed in order.</summary>
    private static void Reset(Socket socket)
    {
        socket.LingerState = new LingerOption(true, 0);
        socket.Close();
    }
}
