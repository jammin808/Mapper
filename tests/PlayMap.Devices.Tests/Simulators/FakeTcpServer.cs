using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace PlayMap.Devices.Tests.Simulators;

/// <summary>
/// A projector on loopback: a TCP listener on a free port that serves each connection with the protocol's own
/// grammar. Keeps every line it was sent (for asserts on the wire) and can go dark (accept and drop at once, as a
/// dead or busy projector does) and come back.
/// </summary>
public abstract class FakeTcpServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _accept;
    private int _connections;

    protected FakeTcpServer()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        // The loops observe _stop themselves; Task.Run is not given it, so a server disposed before its loop starts
        // still has a loop to await.
        _accept = Task.Run(AcceptLoopAsync, CancellationToken.None);
    }

    public int Port { get; }

    /// <summary>Every command line the server received, decoded (digests and handshakes are kept apart).</summary>
    public ConcurrentQueue<string> Received { get; } = new();

    public int Connections => Volatile.Read(ref _connections);

    /// <summary>When true, every connection is closed as soon as it is accepted.</summary>
    public volatile bool Dark;

    protected abstract Task ServeAsync(Stream stream, CancellationToken ct);

    private async Task AcceptLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stop.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException)
            {
                return;
            }
            Interlocked.Increment(ref _connections);
            _ = Task.Run(async () =>
            {
                using (client)
                {
                    if (Dark)
                        return;
                    try
                    {
                        await ServeAsync(client.GetStream(), _stop.Token);
                    }
                    catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException or SocketException)
                    {
                        // The client hung up or the test ended: a simulator connection simply ends.
                    }
                }
            }, CancellationToken.None);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        await _accept;
        _stop.Dispose();
        GC.SuppressFinalize(this);
    }

    // ---- helpers for the grammars ----

    protected static Task WriteAsync(Stream s, string ascii, CancellationToken ct) =>
        s.WriteAsync(Encoding.Latin1.GetBytes(ascii), ct).AsTask();

    protected static Task WriteAsync(Stream s, byte[] bytes, CancellationToken ct) => s.WriteAsync(bytes, ct).AsTask();

    /// <summary>Reads up to (not including) a terminator; null when the client hung up.</summary>
    protected static async Task<string?> ReadUntilAsync(Stream s, byte terminator, CancellationToken ct)
    {
        var bytes = new List<byte>();
        var one = new byte[1];
        while (true)
        {
            var n = await s.ReadAsync(one, ct);
            if (n == 0)
                return null;
            if (one[0] == terminator)
                return Encoding.Latin1.GetString(bytes.ToArray());
            bytes.Add(one[0]);
        }
    }

    protected static async Task<byte[]?> ReadExactlyAsync(Stream s, int count, CancellationToken ct)
    {
        var buffer = new byte[count];
        var got = 0;
        while (got < count)
        {
            var n = await s.ReadAsync(buffer.AsMemory(got), ct);
            if (n == 0)
                return null;
            got += n;
        }
        return buffer;
    }
}
