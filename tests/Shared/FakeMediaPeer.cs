using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace PlayMap.Tests;

/// <summary>
/// A peer's web port reduced to the two routes a media pull asks, over plain HTTP/1.1 on loopback, for the faults a real node does not
/// make on purpose: its list of files (<c>GET /api/media</c>: the one file, listed <c>listed</c> times) and the file by its hash
/// (<c>GET /api/media/&lt;hash&gt;</c>: one byte range, answered 206). With <c>stallAfter</c> each answer for the file falls silent after
/// that many bytes of its body, the connection held open; with <c>answersList</c> false the list is asked for and never answered.
/// </summary>
internal sealed class FakeMediaPeer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly string _hash;
    private readonly byte[] _file;
    private readonly long? _stallAfter;
    private readonly bool _answersList;
    private readonly int _listed;
    private readonly Lock _gate = new();
    private readonly List<Task> _connections = [];
    private readonly Task _accepting;
    private int _listAsks;
    private int _fileAsks;

    public FakeMediaPeer(string hash, byte[] file, long? stallAfter = null, bool answersList = true, int listed = 1)
    {
        _hash = hash;
        _file = file;
        _stallAfter = stallAfter;
        _answersList = answersList;
        _listed = listed;
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _accepting = AcceptAsync();
    }

    public int Port { get; }

    /// <summary>Requests for the list of files heard.</summary>
    public int ListAsks => Volatile.Read(ref _listAsks);

    /// <summary>Requests for the file heard (one per try at a chunk).</summary>
    public int FileAsks => Volatile.Read(ref _fileAsks);

    private async Task AcceptAsync()
    {
        try
        {
            while (true)
            {
                var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                lock (_gate)
                    _connections.Add(ServeAsync(client));
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
        {
            // The peer stopped: no more connections.
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using var owned = client;
        var stream = client.GetStream();
        var ct = _stop.Token;
        try
        {
            while (await ReadHeadAsync(stream, ct) is { } head)
            {
                var lines = head.Split("\r\n");
                var target = lines[0].Split(' ') is { Length: > 1 } parts ? parts[1] : "";
                if (target == "/api/media")
                {
                    Interlocked.Increment(ref _listAsks);
                    if (!_answersList)
                    {
                        await HoldAsync(stream, ct);
                        return;
                    }
                    var row = string.Create(CultureInfo.InvariantCulture, $"{{\"hash\":\"{_hash}\",\"size\":{_file.Length},\"name\":\"clip.bin\"}}");
                    var body = Encoding.UTF8.GetBytes("[" + string.Join(",", Enumerable.Repeat(row, _listed)) + "]");
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(string.Create(CultureInfo.InvariantCulture,
                        $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\n\r\n")), ct);
                    await stream.WriteAsync(body, ct);
                }
                else if (target == "/api/media/" + _hash)
                {
                    Interlocked.Increment(ref _fileAsks);
                    var (from, to) = RangeOf(lines) ?? (0, _file.Length - 1);
                    var length = to - from + 1;
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(string.Create(CultureInfo.InvariantCulture,
                        $"HTTP/1.1 206 Partial Content\r\nContent-Type: application/octet-stream\r\nContent-Range: bytes {from}-{to}/{_file.Length}\r\nContent-Length: {length}\r\n\r\n")), ct);
                    var send = _stallAfter is { } some ? Math.Min(some, length) : length;
                    await stream.WriteAsync(_file.AsMemory((int)from, (int)send), ct);
                    if (send < length)
                    {
                        await HoldAsync(stream, ct);
                        return;
                    }
                }
                else
                {
                    await stream.WriteAsync("HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\n\r\n"u8.ToArray(), ct);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
        {
            // The client went away or the peer stopped: this connection is done.
        }
    }

    /// <summary>One request's head (a pull's requests carry no body), or null once the client has closed the connection.</summary>
    private static async Task<string?> ReadHeadAsync(NetworkStream stream, CancellationToken ct)
    {
        var head = new List<byte>(512);
        var one = new byte[1];
        while (head.Count < 4 || head[^4] != '\r' || head[^3] != '\n' || head[^2] != '\r' || head[^1] != '\n')
        {
            if (await stream.ReadAsync(one, ct) == 0)
                return null;
            head.Add(one[0]);
        }
        return Encoding.ASCII.GetString([.. head]);
    }

    /// <summary>Holds the connection open, sending nothing, until the client closes it or the peer stops.</summary>
    private static async Task HoldAsync(NetworkStream stream, CancellationToken ct)
    {
        var sink = new byte[1024];
        while (await stream.ReadAsync(sink, ct) > 0)
        {
            // Whatever the client sends now is not read as a request: this answer is never finished.
        }
    }

    private static (long From, long To)? RangeOf(string[] lines)
    {
        foreach (var line in lines)
        {
            if (!line.StartsWith("Range: bytes=", StringComparison.OrdinalIgnoreCase))
                continue;
            var spec = line["Range: bytes=".Length..].Split('-');
            return (long.Parse(spec[0], CultureInfo.InvariantCulture), long.Parse(spec[1], CultureInfo.InvariantCulture));
        }
        return null;
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        await _accepting;
        Task[] open;
        lock (_gate)
            open = [.. _connections];
        await Task.WhenAll(open);
        _listener.Dispose();
        _stop.Dispose();
    }
}
