using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using PlayMap.Engine.Fleet;
using PlayMap.Engine.Host;
using PlayMap.Net.Fleet;
using PlayMap.Net.Tls;

namespace PlayMap.Engine.Tests.Fleet;

/// <summary>A backup's pull of one file from its primary: a pull the primary fails before the node stops ends with the words for its
/// journal row; one the primary fails as the node's stop is taken ends as the stop, so the run of pulls behind it ends with the node
/// rather than as a task that faulted.</summary>
public sealed class BackupPullTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task APullThePrimaryFailsAsTheStopIsTakenEndsAsTheStop()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var dir = Directory.CreateTempSubdirectory("playmap-backup-pull-");
        try
        {
            await using var store = new MediaBackend(new NodeHome(Path.Combine(dir.FullName, "node")), NullLogger.Instance);
            // A primary on this machine (plain HTTP may go to loopback); a file whose size the show does not say is asked for first.
            using var puller = new MirrorPuller(new MirrorSource("0badc0de", "FOH-A", "127.0.0.1", port, "", ""), new PinStore(), () => 1_760_000_000_000);
            var hash = "sha256:" + new string('a', 64);

            // Before the stop: the primary takes the ask and resets it, and the pull ends with the words for its row.
            using (var running = new CancellationTokenSource())
            {
                var pull = BackupNode.PullOneAsync(puller, "FOH-A", store, hash, 0, "clip.mov", running.Token);
                using var primary = await TakeRequestAsync(listener);
                Reset(primary);
                var (ok, words) = await pull.WaitAsync(Patience);
                Assert.False(ok);
                Assert.StartsWith("clip.mov could not be pulled from FOH-A: ", words, StringComparison.Ordinal);
            }

            // As the stop is taken: the primary resets the ask in a callback on the stop registered after the client linked the
            // request's token to it, so it runs first when the stop is taken (a token's callbacks run newest first), and the pull ends
            // before the stop reaches the request: the order in which a refusal Windows gives late (about 2 s for a primary that went
            // away) can meet a stop taken with CancelAsync, whose callbacks run after it returns.
            using var stop = new CancellationTokenSource();
            var late = BackupNode.PullOneAsync(puller, "FOH-A", store, hash, 0, "clip.mov", stop.Token);
            using var held = await TakeRequestAsync(listener);
            using var ended = new ManualResetEventSlim();
            _ = late.ContinueWith(_ => ended.Set(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            var failedAfterTheStop = false;
            var endedBeforeTheRequestsCancel = false;
            using (stop.Token.Register(() =>
            {
                failedAfterTheStop = stop.IsCancellationRequested;
                Reset(held);
                endedBeforeTheRequestsCancel = ended.Wait(Patience);
            }))
            {
                await stop.CancelAsync();
            }

            Assert.True(failedAfterTheStop && endedBeforeTheRequestsCancel,
                $"the primary failed the pull after the stop: {failedAfterTheStop}, and the pull ended before the stop reached its request: {endedBeforeTheRequestsCancel}");
            var thrown = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => late);
            Assert.Equal(stop.Token, thrown.CancellationToken);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    /// <summary>The next pull's connection, once its request is in (the client has linked the request's token by then).</summary>
    private static async Task<Socket> TakeRequestAsync(TcpListener listener)
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
                Assert.True(n > 0, "the pull closed before its request was in: " + request);
                request.Append(Encoding.ASCII.GetString(buffer, 0, n));
            }
            Assert.StartsWith("HEAD /", request.ToString(), StringComparison.Ordinal);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>The primary goes: the connection is reset, not closed in order.</summary>
    private static void Reset(Socket socket)
    {
        socket.LingerState = new LingerOption(true, 0);
        socket.Close();
    }
}
