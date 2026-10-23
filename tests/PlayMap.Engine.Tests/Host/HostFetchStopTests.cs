using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using PlayMap.Core.Bus;
using PlayMap.Engine.Host;

namespace PlayMap.Engine.Tests.Host;

/// <summary>A node's MEDIA FETCH from a peer: a fetch the peer fails before the node stops ends with the file's row and its words;
/// one the peer fails as the node's stop is taken ends as the stop, so the fetch ends with the node rather than as a network task
/// that faulted.</summary>
public sealed class HostFetchStopTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task AFetchThePeerFailsAsTheStopIsTakenEndsAsTheStop()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        await using var rig = await HostRig.StartAsync("Stage right", network: true);
        var hex = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("a clip the node does not hold")));
        var id = await rig.OkAsync("ADD media {\"name\":\"clip\",\"kind\":\"video\",\"location\":\"/elsewhere/clip.bin\",\"contentHash\":\"sha256:" + hex + "\",\"sizeBytes\":1024}");
        var item = rig.Engine.Store.Project.Media.Single(m => m.Id == id);
        var heard = rig.Network.Hello!.Peers;
        heard.Hear(new ShowBusHello(ShowBusConstants.KindPlayMap, 1, 1, "5113e7a0", "Gone peer", "elsewhere",
            new Dictionary<string, int> { ["http"] = port }, ShowBusHello.PlayMapCaps, 0, null, null), "127.0.0.1");
        var peer = heard.Find("5113e7a0");
        Assert.NotNull(peer);

        // Before the stop: the peer takes the ask for its media list and resets it, and the fetch ends with the file's row.
        using (var running = new CancellationTokenSource())
        {
            var fetch = rig.Network.PullAllAsync([(item, hex)], [peer], new FetchJournal(rig.Engine.Journal, NullLogger.Instance, "the fetch of clip"), running.Token);
            using var asked = await TakeRequestAsync(listener);
            Reset(asked);
            await fetch.WaitAsync(Patience);
            Assert.Contains("refused: clip could not be fetched: Gone peer's media list could not be read: ", rig.Journal(), StringComparison.Ordinal);
        }

        // As the stop is taken: the peer resets the ask in a callback on the stop registered after the client linked the request's
        // token to it, so it runs first when the stop is taken (a token's callbacks run newest first), and the fetch ends before the
        // stop reaches the request: the order in which a refusal Windows gives late (about 2 s for a peer that went away) can meet a
        // stop taken with CancelAsync, whose callbacks run after it returns.
        using var stop = new CancellationTokenSource();
        var late = rig.Network.PullAllAsync([(item, hex)], [peer], new FetchJournal(rig.Engine.Journal, NullLogger.Instance, "the fetch of clip"), stop.Token);
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
            $"the peer failed the fetch after the stop: {failedAfterTheStop}, and the fetch ended before the stop reached its request: {endedBeforeTheRequestsCancel}");
        var thrown = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => late);
        Assert.Equal(stop.Token, thrown.CancellationToken);
    }

    /// <summary>The next ask's connection, once its request is in (the client has linked the request's token by then).</summary>
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
                Assert.True(n > 0, "the fetch closed before its request was in: " + request);
                request.Append(Encoding.ASCII.GetString(buffer, 0, n));
            }
            Assert.StartsWith("GET /api/media HTTP/1.1", request.ToString(), StringComparison.Ordinal);
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
