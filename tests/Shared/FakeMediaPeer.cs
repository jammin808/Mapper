using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace PlayMap.Tests;

/// <summary>
/// A peer's web port reduced to the two routes a media pull asks, over plain HTTP/1.1 on loopback, for the faults a real node does not
/// make on purpose: its list of files (<c>GET /api/media</c>: each file, listed <c>listed</c> times) and a file by its hash
/// (<c>GET &lt;mediaPath&gt;&lt;hash&gt;</c>, <c>/api/media/</c> unless another path is given: one byte range, answered 206). With
/// <c>stallAfter</c> each answer for a file falls silent after that many bytes of its body, the connection held open, and with
/// <c>stallFrom</c> so does an answer for a range starting at or past that offset, before any byte of its body; with <c>endAfter</c>
/// each answer ends after that many bytes of its body (the connection closed in order, every byte sent before it delivered); with
/// <c>pauseAfter</c> it sends nothing for <c>pauseFor</c> of real time after that many bytes and then the rest; with
/// <c>answerAfter</c> each answer's head waits that long first; with <c>holdsAnswers</c> each answer waits until the test lets one go
/// (<see cref="Answer"/>), <see cref="Holding"/> saying how many wait; with <c>answersList</c> false the list is asked for and never
/// answered; <c>listStatus</c> and <c>fileStatus</c> other than 200 and 206 answer the list or the file with that status and nothing
/// else.
/// </summary>
internal sealed class FakeMediaPeer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly IReadOnlyList<(string Hash, byte[] File)> _files;
    private readonly long? _stallAfter;
    private readonly long? _stallFrom;
    private readonly long? _endAfter;
    private readonly string _mediaPath;
    private readonly long? _pauseAfter;
    private readonly TimeSpan _pauseFor;
    private readonly TimeSpan _answerAfter;
    private readonly bool _holdsAnswers;
    private readonly SemaphoreSlim _answer = new(0);
    private readonly bool _answersList;
    private readonly int _listed;
    private readonly int _listStatus;
    private readonly int _fileStatus;
    private readonly Lock _gate = new();
    private readonly List<Task> _connections = [];
    private readonly Task _accepting;
    private int _listAsks;
    private int _fileAsks;
    private int _holding;

    public FakeMediaPeer(string hash, byte[] file, long? stallAfter = null, bool answersList = true, int listed = 1, int listStatus = 200, int fileStatus = 206,
        TimeSpan answerAfter = default, long? pauseAfter = null, TimeSpan pauseFor = default, long? stallFrom = null, long? endAfter = null,
        string mediaPath = "/api/media/", bool holdsAnswers = false)
        : this([(hash, file)], stallAfter, answersList, listed, listStatus, fileStatus, answerAfter, pauseAfter, pauseFor, stallFrom, endAfter, mediaPath, holdsAnswers)
    {
    }

    public FakeMediaPeer(IReadOnlyList<(string Hash, byte[] File)> files, long? stallAfter = null, bool answersList = true, int listed = 1, int listStatus = 200,
        int fileStatus = 206, TimeSpan answerAfter = default, long? pauseAfter = null, TimeSpan pauseFor = default, long? stallFrom = null, long? endAfter = null,
        string mediaPath = "/api/media/", bool holdsAnswers = false)
    {
        _files = files;
        _stallAfter = stallAfter;
        _stallFrom = stallFrom;
        _endAfter = endAfter;
        _mediaPath = mediaPath;
        _answersList = answersList;
        _listed = listed;
        _listStatus = listStatus;
        _fileStatus = fileStatus;
        _answerAfter = answerAfter;
        _holdsAnswers = holdsAnswers;
        _pauseAfter = pauseAfter;
        _pauseFor = pauseFor;
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _accepting = AcceptAsync();
    }

    public int Port { get; }

    /// <summary>Requests for the list of files heard.</summary>
    public int ListAsks => Volatile.Read(ref _listAsks);

    /// <summary>Requests for a file heard (one per try at a chunk).</summary>
    public int FileAsks => Volatile.Read(ref _fileAsks);

    /// <summary>Answers held now, waiting for <see cref="Answer"/> (with <c>holdsAnswers</c>).</summary>
    public int Holding => Volatile.Read(ref _holding);

    /// <summary>Lets one held answer go.</summary>
    public void Answer() => _answer.Release();

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
                if (_holdsAnswers)
                {
                    Interlocked.Increment(ref _holding);
                    try
                    {
                        await _answer.WaitAsync(ct);
                    }
                    finally
                    {
                        Interlocked.Decrement(ref _holding);
                    }
                }
                if (_answerAfter > TimeSpan.Zero)
                    await Task.Delay(_answerAfter, ct);
                var file = _files.FirstOrDefault(f => target == _mediaPath + f.Hash).File;
                if (target == "/api/media")
                {
                    Interlocked.Increment(ref _listAsks);
                    if (!_answersList)
                    {
                        await HoldAsync(stream, ct);
                        return;
                    }
                    if (_listStatus != 200)
                    {
                        await StatusAsync(stream, _listStatus, ct);
                        continue;
                    }
                    var rows = _files.SelectMany(f => Enumerable.Repeat(
                        string.Create(CultureInfo.InvariantCulture, $"{{\"hash\":\"{f.Hash}\",\"size\":{f.File.Length},\"name\":\"clip.bin\"}}"), _listed));
                    var body = Encoding.UTF8.GetBytes("[" + string.Join(",", rows) + "]");
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(string.Create(CultureInfo.InvariantCulture,
                        $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\n\r\n")), ct);
                    await stream.WriteAsync(body, ct);
                }
                else if (file is not null)
                {
                    Interlocked.Increment(ref _fileAsks);
                    if (_fileStatus != 206)
                    {
                        await StatusAsync(stream, _fileStatus, ct);
                        continue;
                    }
                    var (from, to) = RangeOf(lines) ?? (0, file.Length - 1);
                    var length = to - from + 1;
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(string.Create(CultureInfo.InvariantCulture,
                        $"HTTP/1.1 206 Partial Content\r\nContent-Type: application/octet-stream\r\nContent-Range: bytes {from}-{to}/{file.Length}\r\nContent-Length: {length}\r\n\r\n")), ct);
                    var send = _stallFrom is { } at && from >= at ? 0 : _stallAfter is { } some ? Math.Min(some, length) : length;
                    if (_endAfter is { } end)
                        send = Math.Min(send, end);
                    var first = _pauseAfter is { } pause ? Math.Min(pause, send) : send;
                    await stream.WriteAsync(file.AsMemory((int)from, (int)first), ct);
                    if (first < send)
                    {
                        await Task.Delay(_pauseFor, ct);
                        await stream.WriteAsync(file.AsMemory((int)(from + first), (int)(send - first)), ct);
                    }
                    if (send < length && _endAfter is not null)
                    {
                        // Ended in order: what was sent arrives, then the end, and the connection closes once the client has let it go.
                        client.Client.Shutdown(SocketShutdown.Send);
                        await HoldAsync(stream, ct);
                        return;
                    }
                    if (send < length)
                    {
                        await HoldAsync(stream, ct);
                        return;
                    }
                }
                else
                {
                    await StatusAsync(stream, 404, ct);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
        {
            // The client went away or the peer stopped: this connection is done.
        }
    }

    private static Task StatusAsync(NetworkStream stream, int status, CancellationToken ct) =>
        stream.WriteAsync(Encoding.ASCII.GetBytes(string.Create(CultureInfo.InvariantCulture, $"HTTP/1.1 {status} Refused\r\nContent-Length: 0\r\n\r\n")), ct).AsTask();

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
        _answer.Dispose();
        _stop.Dispose();
    }
}
