using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PlayMap.Core.Bus;
using PlayMap.Core.Time;
using PlayMap.Core.Wire;
using PlayMap.Engine.Host;
using PlayMap.Media.Ffmpeg;
using PlayMap.Net.Bus;

namespace PlayMap.Engine.Tests.Host;

/// <summary>A real headless node for a test: its own node folder in a temp directory, settings written there first
/// (ephemeral ports, a pairing token, loopback trusted), no GL, the hello on a port of its own aimed at nothing until
/// a test introduces two nodes, and its log in its folder.</summary>
internal sealed class HostRig : IAsyncDisposable
{
    public const string Token = "K7QM-3XWD-P9RA";

    private readonly ILoggerFactory _logs;
    private readonly NodeLogProvider _log;
    private int _stopped;

    private HostRig(DirectoryInfo dir, NodeHome home, EngineHost host, ILoggerFactory logs, NodeLogProvider log)
    {
        Dir = dir;
        Home = home;
        Host = host;
        _logs = logs;
        _log = log;
    }

    public DirectoryInfo Dir { get; }

    public NodeHome Home { get; }

    public EngineHost Host { get; }

    public EngineCore Engine => Host.Engine;

    public NetworkHost Network => Host.Network;

    public string Id => Network.Settings.NodeId;

    /// <summary>A started node. <paramref name="network"/> binds every interface and says hello (two nodes find each
    /// other); otherwise it binds loopback only and stays quiet.</summary>
    public static async Task<HostRig> StartAsync(string name = "Stage left", bool network = false, IMonoClock? mono = null, EngineParts? parts = null,
        Func<NodeSettings, NodeSettings>? settings = null, Func<string, EngineOptions>? options = null, DirectoryInfo? dir = null)
    {
        dir ??= Directory.CreateTempSubdirectory("playmap-host-");
        var home = new NodeHome(dir.FullName);
        if (!File.Exists(home.SettingsPath))
        {
            var s = NodeSettings.CreateDefault(name) with
            {
                Bind = network ? "" : "127.0.0.1",
                HttpPort = 0,
                WirePort = 0,
                SyncPort = 0,
                Token = Token,
                TrustLoopback = true,
                Announce = network,
            };
            home.SaveSettings(settings?.Invoke(s) ?? s);
        }
        // A provider handed to the factory is not the factory's to dispose: the rig closes the log itself, as the engine's
        // entry does, so the file is let go when the node stops.
        var log = NodeLogProvider.For(home);
        var logs = LoggerFactory.Create(b => b.AddProvider(log));
        var host = new EngineHost((options ?? Options)(dir.FullName), home, new EngineHostOptions
        {
            Parts = parts ?? EngineParts.Bare,
            Mono = mono,
            Hello = new HelloOptions { ListenPort = 0, Targets = [], Every = TimeSpan.FromMilliseconds(200), SweepEvery = TimeSpan.FromMilliseconds(200) },
            Logs = logs,
        });
        var rig = new HostRig(dir, home, host, logs, log);
        try
        {
            await host.StartAsync();
        }
        catch
        {
            await rig.DisposeAsync();
            throw;
        }
        return rig;
    }

    /// <summary>Headless, this rig's folder, nothing put back.</summary>
    public static EngineOptions Options(string home) => Parse("engine", "headless", "--home", home, "--no-recover");

    public static EngineOptions Parse(params string[] args)
    {
        var o = EngineOptions.Parse(args, out var error);
        Assert.Null(error);
        return o;
    }

    /// <summary>Points two nodes' hellos at each other (loopback, their own ports).</summary>
    public static void Introduce(HostRig a, HostRig b)
    {
        a.Network.Hello!.Targets = [new IPEndPoint(IPAddress.Loopback, b.Network.HelloPort)];
        b.Network.Hello!.Targets = [new IPEndPoint(IPAddress.Loopback, a.Network.HelloPort)];
    }

    public Task<LineClient> WireAsync() => LineClient.ConnectAsync(Network.WirePort);

    public HttpClient Http() => new() { BaseAddress = new Uri("http://127.0.0.1:" + Network.HttpPort.ToString(CultureInfo.InvariantCulture)), Timeout = TimeSpan.FromSeconds(30) };

    /// <summary>STATE now, parsed.</summary>
    public JsonElement State()
    {
        using var doc = JsonDocument.Parse(Engine.State().Json);
        return doc.RootElement.Clone();
    }

    /// <summary>A line through the engine as the desk sends it (loopback, paired).</summary>
    public async Task<CommandReply> RunAsync(string line)
    {
        Assert.True(WireGrammar.TryParse(line, out var parsed, out var error), error?.Reason);
        return await Engine.HandleAsync(parsed, SessionInfo.Desk("test"), CancellationToken.None);
    }

    public async Task<string> OkAsync(string line)
    {
        var r = await RunAsync(line);
        Assert.True(r.Ok, $"{line} → {r.Payload}");
        return r.Payload ?? "";
    }

    /// <summary>The journal's recent rows as text, for asserting what the node said.</summary>
    public string Journal() => string.Join("\n", Engine.Journal.Recent(200).Select(r => $"{r.Kind}: {r.Text}"));

    /// <summary>Stops the node but keeps its folder (a restart reads it again).</summary>
    public async ValueTask StopAsync()
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0)
            return;
        await Host.DisposeAsync();
        _logs.Dispose();
        _log.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        try
        {
            Dir.Delete(recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A file still held by the OS stays in the private temp folder.
        }
    }
}

/// <summary>A monotonic clock moved by a fixed amount (another machine's clock).</summary>
internal sealed class OffsetClock(IMonoClock inner, long offsetNs) : IMonoClock
{
    public long NowNs => inner.NowNs + offsetNs;
}

/// <summary>A line client for the tests: sends lines, reads lines with a timeout, keeps pushes aside.</summary>
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
        await c._tcp.ConnectAsync(IPAddress.Loopback, port);
        c._tcp.NoDelay = true;
        c._stream = c._tcp.GetStream();
        c._reader = new StreamReader(c._stream, new UTF8Encoding(false));
        return c;
    }

    public async Task SendAsync(string line) => await _stream!.WriteAsync(Encoding.UTF8.GetBytes(line + "\n"));

    public async Task<string?> ReadAsync(int timeoutMs = 10000)
    {
        _pending ??= _reader!.ReadLineAsync();
        var done = await Task.WhenAny(_pending, Task.Delay(timeoutMs));
        if (done != _pending)
            throw new TimeoutException("no line within " + timeoutMs + " ms");
        var line = await _pending;
        _pending = null;
        return line;
    }

    /// <summary>The next line that is not a push (STATE, CONTEXT and PROJECT pushes go to <see cref="Pushes"/>).</summary>
    public async Task<string?> ReplyAsync(int timeoutMs = 10000)
    {
        var sw = Stopwatch.StartNew();
        while (true)
        {
            var line = await ReadAsync(Math.Max(1, timeoutMs - (int)sw.ElapsedMilliseconds));
            // A push (a topic's line, never a reply: it has no id) is kept for PushAsync, whatever topic sent it.
            if (line is not null && (line.StartsWith("STATE ", StringComparison.Ordinal) || line.StartsWith("CONTEXT ", StringComparison.Ordinal)
                || line.StartsWith("PROJECT ", StringComparison.Ordinal) || line.StartsWith("ANCHOR ", StringComparison.Ordinal)))
            {
                Pushes.Add(line);
                continue;
            }
            return line;
        }
    }

    public async Task<string?> AskAsync(string line, int timeoutMs = 10000)
    {
        await SendAsync(line);
        return await ReplyAsync(timeoutMs);
    }

    /// <summary>The first push (kept or still to come) that matches.</summary>
    public async Task<string> PushAsync(Func<string, bool> match, int timeoutMs = 10000)
    {
        if (Pushes.FirstOrDefault(match) is { } kept)
            return kept;
        var sw = Stopwatch.StartNew();
        while (true)
        {
            var line = await ReadAsync(Math.Max(1, timeoutMs - (int)sw.ElapsedMilliseconds));
            if (line is not null && match(line))
                return line;
        }
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
    public static async Task<bool> TrueAsync(Func<Task<bool>> condition, int timeoutMs = 10000, int stepMs = 50)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (await condition())
                return true;
            await Task.Delay(stepMs);
        }
        return await condition();
    }

    public static async Task<bool> TrueAsync(Func<bool> condition, int timeoutMs = 10000, int stepMs = 20)
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

/// <summary>Log lines kept in memory, for the entry's tests.</summary>
internal sealed class KeptLog : ILoggerProvider
{
    public ConcurrentQueue<string> Lines { get; } = new();

    public ILogger CreateLogger(string categoryName) => new Logger(this);

    public void Dispose()
    {
        // Nothing held.
    }

    private sealed class Logger(KeptLog owner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            owner.Lines.Enqueue(formatter(state, exception));
    }
}

/// <summary>A test that needs FFmpeg 9 (the libraries and the command-line tool that makes its clip): skipped, with the
/// reason, on a machine without them. The libraries are looked for where PLAYMAP_FFMPEG_DIR says, else in
/// /opt/ffmpeg9/lib, the folder this repository's CI installs them into.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
internal sealed class FfmpegFactAttribute : FactAttribute
{
    public const string DefaultLibraries = "/opt/ffmpeg9/lib";

    public FfmpegFactAttribute()
    {
        UseDefaultLibraries();
        if (!MediaBackend.FfmpegReady(out var words))
            Skip = words;
        else if (Tool() is null)
            Skip = "the ffmpeg command-line tool that makes the test clip was not found beside the libraries or in /opt/ffmpeg9/bin";
    }

    public static void UseDefaultLibraries()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(FfmpegLoader.EnvironmentVariable)))
            Environment.SetEnvironmentVariable(FfmpegLoader.EnvironmentVariable, DefaultLibraries);
    }

    public static string? Tool()
    {
        var libs = Environment.GetEnvironmentVariable(FfmpegLoader.EnvironmentVariable) ?? DefaultLibraries;
        var name = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
        return new[] { Path.Combine(libs, "..", "bin", name), Path.Combine("/opt/ffmpeg9/bin", name), Path.Combine(libs, name) }
            .Select(Path.GetFullPath).FirstOrDefault(File.Exists);
    }

    /// <summary>A two-second 320×240 clip at 25 fps (MPEG-4 part 2: an encoder every FFmpeg build has).</summary>
    public static async Task<string> MakeClipAsync(string folder)
    {
        var path = Path.Combine(folder, "clip.mp4");
        var psi = new ProcessStartInfo(Tool()!) { UseShellExecute = false, RedirectStandardError = true };
        foreach (var a in new[] { "-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", "testsrc2=size=320x240:rate=25", "-t", "2", "-pix_fmt", "yuv420p", "-c:v", "mpeg4", path })
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var errors = await p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        Assert.True(p.ExitCode == 0, "ffmpeg could not make the clip: " + errors);
        return path;
    }
}

/// <summary>A Patterns desk as the tests play it (the Net tests' fake): STATE then NONCE on connect, HELLO answered,
/// AUTH-PROOF checked against the nonce and the HELLO name, PING and CONTEXT answered — and a test can push a STATE.</summary>
internal sealed class FakePatternsDesk : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentBag<TcpClient> _clients = [];
    private readonly Task _accept;
    private volatile StreamWriter? _current;

    public FakePatternsDesk(string token)
    {
        Token = token;
        _listener.Start();
        _accept = Task.Run(AcceptAsync, CancellationToken.None);
    }

    public string Token { get; }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public ConcurrentQueue<string> Heard { get; } = new();

    public string State { get; set; } = StateWith(blackout: false);

    public static string StateWith(bool blackout) => string.Create(CultureInfo.InvariantCulture,
        $"{{\"blackout\":{(blackout ? "true" : "false")},\"video\":{{\"file\":\"sponsor.mp4\",\"tag\":\"VT\",\"position\":62,\"length\":210,\"playing\":true,\"ended\":false,\"loops\":false,\"positionMs\":62000,\"lengthMs\":210000,\"remainingMs\":148000,\"paused\":false,\"rate\":1,\"seq\":7}}}}");

    public async Task PushAsync(string state)
    {
        State = state;
        if (_current is { } w)
            await w.WriteLineAsync("STATE " + state);
    }

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
            _ = Task.Run(() => ServeAsync(client), CancellationToken.None);
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        try
        {
            var stream = client.GetStream();
            using var reader = new StreamReader(stream, new UTF8Encoding(false));
            var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
            _current = writer;
            var nonce = Pairing.NewNonce();
            var name = "";
            await writer.WriteLineAsync("STATE " + State);
            await writer.WriteLineAsync("NONCE " + nonce);
            while (await reader.ReadLineAsync() is { } line)
            {
                Heard.Enqueue(line);
                var (id, rest) = line.StartsWith('@') ? (line[1..line.IndexOf(' ', StringComparison.Ordinal)], line[(line.IndexOf(' ', StringComparison.Ordinal) + 1)..]) : ("", line);
                var tag = id.Length > 0 ? " @" + id : "";
                var words = rest.Split(' ');
                switch (words[0])
                {
                    case "HELLO":
                        name = rest[6..];
                        await writer.WriteLineAsync("OK" + tag);
                        break;
                    case "AUTH-PROOF":
                        var right = Pairing.Verify(Token, nonce, name, words[1]);
                        nonce = Pairing.NewNonce();
                        await writer.WriteLineAsync(right ? "OK" + tag + " paired" : "ERR" + tag + " wrong token");
                        if (!right)
                            await writer.WriteLineAsync("NONCE " + nonce);
                        break;
                    case "PING":
                        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
                        await writer.WriteLineAsync("OK" + tag + " PONG " + words[1] + " " + now + " " + now);
                        break;
                    case "CONTEXT":
                        await writer.WriteLineAsync("OK" + tag + " {\"v\":1,\"node\":{\"t\":\"patterns\",\"id\":\"9f8e7d6c\",\"name\":\"FOH\"},\"at\":1,\"light\":\"green\",\"headline\":\"Programme: VT1\",\"items\":[],\"links\":[],\"recent\":[],\"commands\":[],\"peers\":[]}");
                        break;
                    default:
                        await writer.WriteLineAsync("ERR" + tag + " unknown command");
                        break;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException)
        {
            // The link went away; the next connection is served afresh.
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
