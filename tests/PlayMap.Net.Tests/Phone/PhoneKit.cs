using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using PlayMap.Core.Phone;
using PlayMap.Net.Http;
using PlayMap.Net.Phone;

namespace PlayMap.Net.Tests.Phone;

/// <summary>A clock a test moves by hand, its timestamps on the same ticks.</summary>
internal sealed class HandClock(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;

    public override long GetTimestamp() => Now.UtcTicks;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
}

/// <summary>A log a test reads back.</summary>
internal sealed class ListLog : Microsoft.Extensions.Logging.ILogger
{
    private readonly List<string> _lines = [];

    public IReadOnlyList<string> Lines
    {
        get
        {
            lock (_lines)
                return [.. _lines];
        }
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

    public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        lock (_lines)
            _lines.Add(formatter(state, exception));
    }
}

/// <summary>A node's web port with the phone hub's module on loopback, its keys in a folder of its own.</summary>
internal sealed class PhoneNode : IAsyncDisposable
{
    private PhoneNode(string folder, PhoneHub hub, HttpHost host, HandClock clock)
    {
        Folder = folder;
        Hub = hub;
        Host = host;
        Clock = clock;
    }

    public string Folder { get; }

    public static ListLog Log { get; } = new();

    public PhoneHub Hub { get; }

    public HttpHost Host { get; }

    public HandClock Clock { get; }

    public int Port => Host.Port;

    public Uri Base => new("http://127.0.0.1:" + Port);

    public string KeysPath => Path.Combine(Folder, "phones.json");

    public static async Task<PhoneNode> StartAsync(Func<PhoneHubOptions, PhoneHubOptions>? tune = null)
    {
        var folder = Directory.CreateTempSubdirectory("playmap-phone-").FullName;
        var clock = new HandClock(new DateTimeOffset(2026, 10, 15, 20, 0, 0, TimeSpan.Zero));
        var options = new PhoneHubOptions { KeysPath = Path.Combine(folder, "phones.json"), Time = clock, NodeId = "a1b2c3d4", NodeName = "Stage left", Log = Log };
        var hub = new PhoneHub(tune?.Invoke(options) ?? options);
        var host = await HttpHostTests.StartAsync(new FakeEndpoint(), TestSettings.Make(trustLoopback: false), new HttpHostOptions { Modules = [hub.Module] });
        return new PhoneNode(folder, hub, host, clock);
    }

    public PhoneInvite Invite(string name = "Pixel 8", TimeSpan? life = null) =>
        Hub.Invite(name, life ?? TimeSpan.FromMinutes(10), "desk d1", new Uri("https://192.168.1.44:" + Port));

    public static string TicketOf(PhoneInvite invite) => new Uri(invite.Url).Fragment["#t=".Length..];

    public async ValueTask DisposeAsync()
    {
        await Host.DisposeAsync();
        await Hub.DisposeAsync();
        try
        {
            Directory.Delete(Folder, recursive: true);
        }
        catch (IOException)
        {
            // A file still held: the temp folder is the OS's to clear.
        }
    }
}

/// <summary>A phone's socket as a test drives it: JSON messages, PMF1 frames, and what closed it.</summary>
internal sealed class PhoneClient : IAsyncDisposable
{
    private readonly ClientWebSocket _ws = new();

    public ClientWebSocket Socket => _ws;

    public static async Task<PhoneClient> ConnectAsync(int port, string? origin = null, bool subprotocol = true)
    {
        var c = new PhoneClient();
        if (subprotocol)
            c._ws.Options.AddSubProtocol(PhoneProtocol.SubProtocol);
        if (origin is not null)
            c._ws.Options.SetRequestHeader("Origin", origin);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await c._ws.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/scan/ws"), cts.Token);
        return c;
    }

    public static object Hello(string? ticket = null, string? key = null, string label = "camera2 0, facing back", bool locks = true) => new
    {
        t = "hello",
        v = 1,
        ticket,
        key,
        phone = new { name = "" },
        ua = new { platform = "android-chrome", browser = "test", model = "Pixel 8" },
        camera = new
        {
            label,
            width = 320,
            height = 240,
            fps = 30,
            caps = locks ? new { exposure = new[] { "manual", "continuous" }, exposureTime = new[] { 10, 100, 5 } } : new { exposure = Array.Empty<string>(), exposureTime = Array.Empty<int>() },
        },
        features = new { deflate = true },
    };

    public Task SendAsync(object message) => SendTextAsync(JsonSerializer.Serialize(message));

    public async Task SendTextAsync(string text)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await _ws.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, cts.Token);
    }

    public async Task SendBinaryAsync(byte[] data)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await _ws.SendAsync(data, WebSocketMessageType.Binary, true, cts.Token);
    }

    /// <summary>The next text message (null when the socket closed instead).</summary>
    public async Task<JsonElement?> ReceiveAsync(int timeoutMs = 10_000)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        var buffer = new byte[1 << 20];
        var length = 0;
        while (true)
        {
            var r = await _ws.ReceiveAsync(buffer.AsMemory(length), cts.Token);
            if (r.MessageType == WebSocketMessageType.Close)
                return null;
            length += r.Count;
            if (r.EndOfMessage)
                break;
        }
        return JsonDocument.Parse(buffer.AsMemory(0, length)).RootElement.Clone();
    }

    /// <summary>The next message of a type, skipping others (pongs, clocks, acks).</summary>
    public async Task<JsonElement> ReceiveTypeAsync(string type, int timeoutMs = 10_000)
    {
        var until = Environment.TickCount64 + timeoutMs;
        while (true)
        {
            var m = await ReceiveAsync((int)Math.Max(1, until - Environment.TickCount64))
                ?? throw new InvalidOperationException($"closed ({_ws.CloseStatus} {_ws.CloseStatusDescription}) waiting for {type}");
            if (m.GetProperty("t").GetString() == type)
                return m;
        }
    }

    /// <summary>Reads until the node closes the socket; answers its close status and words.</summary>
    public async Task<(WebSocketCloseStatus? Status, string Words)> ClosedAsync(int timeoutMs = 10_000)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        var buffer = new byte[1 << 16];
        try
        {
            while (_ws.State is WebSocketState.Open or WebSocketState.CloseSent)
            {
                var r = await _ws.ReceiveAsync(buffer, cts.Token);
                if (r.MessageType == WebSocketMessageType.Close)
                    break;
            }
        }
        catch (WebSocketException)
        {
            // The node dropped the connection without a close frame: the status says so.
        }
        return (_ws.CloseStatus, _ws.CloseStatusDescription ?? "");
    }

    public async ValueTask DisposeAsync()
    {
        if (_ws.State == WebSocketState.Open)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try
            {
                await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", cts.Token);
            }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
            {
                // The node went first.
            }
        }
        _ws.Dispose();
    }
}
