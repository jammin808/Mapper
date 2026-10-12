using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using System.Text;
using PlayMap.Core.Bus;
using PlayMap.Core.Time;
using PlayMap.Core.Wire;

namespace PlayMap.Net.Tests;

/// <summary>A node's endpoint as the tests play it: a STATE with a seq that rises on every published change, a
/// blackout verb that refuses a change that would change nothing, a clip seek, an accepted-not-applied scan, a
/// verb that faults, Patterns' blackout fact taken in silently, and every line it was handed kept with who sent it.</summary>
internal sealed class FakeEndpoint : IWireEndpoint, INodeActivity
{
    private readonly object _gate = new();
    private long _seq = 1;
    private long _projectRevision = 1;
    private string _projectName = "Show";

    public FakeEndpoint(string id = "a1b2c3d4", string name = "Stage left")
    {
        Id = id;
        Name = name;
    }

    public string Id { get; }

    public string Name { get; }

    public bool Blackout { get; private set; }

    public bool FactBlackout { get; private set; }

    public double PositionMs { get; set; } = 62000;

    public bool IsAnimating { get; set; }

    public bool IsLive { get; set; } = true;

    /// <summary>STATE itself faults (an engine bug the transports must survive).</summary>
    public bool ThrowOnState { get; set; }

    public ConcurrentQueue<(WireLine Line, SessionInfo Session)> Lines { get; } = new();

    public event Action<long>? StateChanged;

    public event Action<long>? ProjectChanged;

    public long Seq
    {
        get
        {
            lock (_gate)
                return _seq;
        }
    }

    public (string Json, long Revision) State()
    {
        if (ThrowOnState)
            throw new InvalidOperationException("the fake endpoint's STATE faults");
        lock (_gate)
        {
            var json = string.Create(CultureInfo.InvariantCulture,
                $"{{\"v\":1,\"seq\":{_seq},\"id\":\"{Id}\",\"t\":\"playmap\",\"name\":\"{Name}\",\"air\":{{\"blackout\":{(Blackout ? "true" : "false")}}},\"video\":{{\"file\":\"intro.mp4\",\"positionMs\":{PositionMs},\"lengthMs\":210000,\"playing\":true,\"paused\":false,\"rate\":1,\"seq\":7}}}}");
            return (json, _seq);
        }
    }

    public string Context() =>
        "{\"v\":1,\"node\":{\"t\":\"playmap\",\"id\":\"" + Id + "\",\"name\":\"" + Name + "\"},\"at\":1,\"light\":\"green\",\"headline\":\"fine\",\"items\":[],\"links\":[],\"recent\":[],\"commands\":[],\"peers\":[]}";

    public (string Json, long Revision) ProjectJson()
    {
        lock (_gate)
            return ("{\"name\":\"" + _projectName + "\"}", _projectRevision);
    }

    /// <summary>A published change: seq rises and listeners hear of it.</summary>
    public void Publish(Action? change = null)
    {
        long seq;
        lock (_gate)
        {
            change?.Invoke();
            seq = ++_seq;
        }
        StateChanged?.Invoke(seq);
    }

    /// <summary>A change the engine did not announce (the clip moved on between events).</summary>
    public void MoveSilently(double positionMs)
    {
        lock (_gate)
        {
            PositionMs = positionMs;
            _seq++;
        }
    }

    /// <summary>An event that moved nothing STATE carries.</summary>
    public void Touch() => StateChanged?.Invoke(Seq);

    public void RenameProject(string name)
    {
        long rev;
        lock (_gate)
        {
            _projectName = name;
            rev = ++_projectRevision;
        }
        ProjectChanged?.Invoke(rev);
    }

    public ValueTask<CommandReply> HandleAsync(WireLine line, SessionInfo session, CancellationToken ct)
    {
        Lines.Enqueue((line, session));
        switch (line.Verb)
        {
            case "BLACKOUT" when line.Word(1) is "0" or "1":
                FactBlackout = line.Word(1) == "1";
                return ValueTask.FromResult(CommandReply.NoReply("Patterns blackout fact"));
            case "BLACKOUT":
                var on = line.WordUpper(1) == "ON";
                if (Blackout == on)
                    return ValueTask.FromResult(CommandReply.Fail(on ? "already blacked out" : "not blacked out"));
                Publish(() => Blackout = on);
                return ValueTask.FromResult(CommandReply.Success(null, on ? "blackout on" : "blackout off"));
            case "VIDEO" when line.WordUpper(1) == "SEEK":
                return ValueTask.FromResult(CommandReply.Success(null, "seeked to " + line.Word(2)));
            case "SCAN" when line.WordUpper(1) == "START":
                return ValueTask.FromResult(CommandReply.Accepted("scan started"));
            case "PLAYLIST" when line.WordUpper(1) == "NEW":
                return ValueTask.FromResult(CommandReply.Success("pl_1", "made playlist " + line.Word(2)));
            case "PLAYLIST" when line.WordUpper(1) == "LIST":
                return ValueTask.FromResult(CommandReply.Success("[]"));
            case "BOOM":
                throw new InvalidOperationException("the fake endpoint faults on BOOM");
            case "LIVE" or "DECK" or "LOOK":
                return ValueTask.FromResult(CommandReply.NoReply("fact"));
            default:
                return ValueTask.FromResult(CommandReply.Fail("unknown word '" + line.Word(0) + "'"));
        }
    }
}

/// <summary>Node settings for a test: a token, loopback trust as the test needs, ephemeral ports.</summary>
internal static class TestSettings
{
    public const string Token = "K7QM-3XWD-P9RA";

    public static NodeSettings Make(bool trustLoopback = false, string token = Token, params string[] trusted) => new(
        "a1b2c3d4", "Stage left", "127.0.0.1", 0, 0, 0, token, trustLoopback, [.. trusted], false, true, "", "", "", "", false);

    public static ShowWallClock Wall() => ShowWallClock.System();
}

/// <summary>A line client for the tests: sends lines, reads lines with a timeout, and can skip pushes.</summary>
internal sealed class LineClient : IAsyncDisposable
{
    private readonly TcpClient _tcp = new();
    private NetworkStream? _stream;
    private StreamReader? _reader;
    private Task<string?>? _pending;

    public List<string> Pushes { get; } = [];

    public static async Task<LineClient> ConnectAsync(int port)
    {
        var c = new LineClient();
        await c._tcp.ConnectAsync("127.0.0.1", port);
        c._tcp.NoDelay = true;
        c._stream = c._tcp.GetStream();
        c._reader = new StreamReader(c._stream, new UTF8Encoding(false));
        return c;
    }

    public async Task SendAsync(string line)
    {
        var bytes = Encoding.UTF8.GetBytes(line + "\n");
        await _stream!.WriteAsync(bytes);
    }

    public async Task SendRawAsync(byte[] bytes) => await _stream!.WriteAsync(bytes);

    /// <summary>The next line, or null when the server closed; throws on timeout.</summary>
    public async Task<string?> ReadAsync(int timeoutMs = 5000)
    {
        _pending ??= _reader!.ReadLineAsync();
        var done = await Task.WhenAny(_pending, Task.Delay(timeoutMs));
        if (done != _pending)
            throw new TimeoutException("no line within " + timeoutMs + " ms");
        var line = await _pending;
        _pending = null;
        return line;
    }

    /// <summary>The next line, or null when nothing arrives within the wait (the line stays pending for later).</summary>
    public async Task<string?> TryReadAsync(int waitMs)
    {
        _pending ??= _reader!.ReadLineAsync();
        var done = await Task.WhenAny(_pending, Task.Delay(waitMs));
        if (done != _pending)
            return null;
        var line = await _pending;
        _pending = null;
        return line;
    }

    /// <summary>The next line that is not a push (STATE/CONTEXT/PROJECT pushes are kept in <see cref="Pushes"/>).</summary>
    public async Task<string?> ReplyAsync(int timeoutMs = 5000)
    {
        var sw = Stopwatch.StartNew();
        while (true)
        {
            var left = Math.Max(1, timeoutMs - (int)sw.ElapsedMilliseconds);
            var line = await ReadAsync(left);
            if (line is not null && (line.StartsWith("STATE ", StringComparison.Ordinal) || line.StartsWith("CONTEXT ", StringComparison.Ordinal) || line.StartsWith("PROJECT ", StringComparison.Ordinal)))
            {
                Pushes.Add(line);
                continue;
            }
            return line;
        }
    }

    public async Task<string?> AskAsync(string line, int timeoutMs = 5000)
    {
        await SendAsync(line);
        return await ReplyAsync(timeoutMs);
    }

    public async ValueTask DisposeAsync()
    {
        _reader?.Dispose();
        if (_stream is not null)
            await _stream.DisposeAsync();
        _tcp.Dispose();
    }
}

/// <summary>Polling with a deadline: the tests wait for a fact instead of sleeping a fixed time.</summary>
internal static class Eventually
{
    public static async Task<bool> TrueAsync(Func<bool> condition, int timeoutMs = 5000, int stepMs = 10)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (condition())
                return true;
            await Task.Delay(stepMs);
        }
        return condition();
    }
}
