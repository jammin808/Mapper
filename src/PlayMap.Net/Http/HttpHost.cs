using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Protocol;
using PlayMap.Core.Bus;
using PlayMap.Core.Time;
using PlayMap.Core.Wire;
using PlayMap.Net.Commands;
using PlayMap.Net.Mcp;
using PlayMap.Net.Tls;

namespace PlayMap.Net.Http;

/// <summary>How the web port runs; the defaults are the contract's.</summary>
public sealed record HttpHostOptions
{
    /// <summary>The port; null = <see cref="NodeSettings.HttpPort"/>, 0 = any free port.</summary>
    public int? Port { get; init; }

    /// <summary>The node's certificate: HTTPS on the same port (sniffed by the first byte). Null = plain only.</summary>
    public TlsIdentity? Tls { get; init; }

    /// <summary>The library this node serves to its peers; null = no media routes (404).</summary>
    public IMediaStore? Media { get; init; }

    /// <summary>Who decides an assistant's high-risk line; null = refused unless queued elsewhere.</summary>
    public IApprovalPolicy? Approvals { get; init; }

    /// <summary>Whether anything plays; null reads the endpoint's <see cref="INodeActivity"/>.</summary>
    public Func<bool>? IsAnimating { get; init; }

    public TimeSpan LongPollMax { get; init; } = TimeSpan.FromSeconds(25);

    public TimeSpan BeatEvery { get; init; } = TimeSpan.FromSeconds(2);

    public bool Mcp { get; init; } = true;

    public int MaxConnectionsPerAddress { get; init; } = 64;

    public ILoggerFactory? LoggerFactory { get; init; }

    public string Version { get; init; } = "0.1.0";

    /// <summary>Parts that serve pages and sockets under their own path (the phone's /scan); each admits its own requests before the
    /// token gate (open, its own credential, refused, or deferred to the gate).</summary>
    public IReadOnlyList<IWebModule> Modules { get; init; } = [];
}

/// <summary>PlayMap's web port (Kestrel, one port): plain HTTP and HTTPS side by side (a TLS handshake's first byte
/// picks the branch), the REST routes of docs/WIRE.md, the WebSocket, <c>/tls</c> and <c>/tls.cer</c>, the media
/// routes peers pull files through (HTTP ranges), and MCP at <c>/mcp</c>. Every route but the certificate's needs the
/// token from anything but this machine and the trusted list when a token is set; the listener honours the bind
/// address and stays closed when it cannot.</summary>
public sealed class HttpHost : IAsyncDisposable
{
    private const int MaxLineBytes = 64 * 1024;

    private readonly IWireEndpoint _endpoint;
    private readonly ShowWallClock _wall;
    private readonly HttpHostOptions _options;
    private readonly ILogger _log;
    private readonly CommandRunner _runner;
    private readonly ChangeSignal _changes = new();
    private readonly TokenGate _gate = new();
    private readonly ConnectionLedger _ledger = new();
    private readonly Func<bool> _isAnimating;
    private readonly Func<bool> _isLive;
    private readonly CancellationTokenSource _stop = new();
    private volatile NodeSettings _settings;
    private WebApplication? _app;
    private long _sessions;
    private int _webSockets;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> _mcpSessions = new(StringComparer.Ordinal);
    private int _disposed;

    public HttpHost(NodeSettings settings, IWireEndpoint endpoint, ShowWallClock wall, HttpHostOptions? options = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        _wall = wall ?? throw new ArgumentNullException(nameof(wall));
        _options = options ?? new HttpHostOptions();
        _log = _options.LoggerFactory?.CreateLogger("PlayMap.Net.Http") ?? NullLogger.Instance;
        _runner = new CommandRunner(endpoint, wall, _log);
        _isAnimating = _options.IsAnimating ?? (() => endpoint is INodeActivity { IsAnimating: true });
        _isLive = () => endpoint is not INodeActivity a || a.IsLive;
    }

    public int Port { get; private set; }

    public bool ServesTls => _options.Tls is not null;

    public long Faults => _runner.Faults;

    /// <summary>Whether MCP is served at <c>/mcp</c>.</summary>
    public bool ServesMcp => _options.Mcp;

    /// <summary>WebSockets open on <c>/ws</c> now.</summary>
    public int WebSockets => Volatile.Read(ref _webSockets);

    /// <summary>MCP clients heard from in the last minute (an MCP client over HTTP holds nothing open, so it counts while
    /// it keeps asking; a session that says goodbye stops counting at once).</summary>
    public int McpSessions
    {
        get
        {
            var now = Stopwatch.GetTimestamp();
            foreach (var (id, seen) in _mcpSessions)
                if (Stopwatch.GetElapsedTime(seen, now) > McpSessionQuiet)
                    _mcpSessions.TryRemove(new KeyValuePair<string, long>(id, seen));
            return _mcpSessions.Count;
        }
    }

    private static readonly TimeSpan McpSessionQuiet = TimeSpan.FromMinutes(1);

    /// <summary>An MCP client is its session when it has one (the initialize handshake); a client on a later revision has
    /// none, so it is its connection (a client keeps its connection between calls).</summary>
    private void NoteMcpClient(HttpContext context)
    {
        var session = context.Request.Headers["Mcp-Session-Id"].ToString();
        var key = session.Length is > 0 and <= 128 ? "s:" + session : "c:" + context.Connection.Id;
        if (session.Length > 0 && HttpMethods.IsDelete(context.Request.Method))
        {
            _mcpSessions.TryRemove(key, out _);
            return;
        }
        if (_mcpSessions.Count < 4096 || _mcpSessions.ContainsKey(key))
            _mcpSessions[key] = Stopwatch.GetTimestamp();
    }

    public NodeSettings Settings => _settings;

    /// <summary>Takes a new token, trusted list, loopback trust or Require HTTPS at once; a new bind or port needs a restart.</summary>
    public void UpdateSettings(NodeSettings settings) => _settings = settings ?? throw new ArgumentNullException(nameof(settings));

    /// <summary>Opens the port. Throws <see cref="BindRefusedException"/> (nothing listens) when the bind address is
    /// not an address or the machine refuses it.</summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (_app is not null)
            return;
        var bind = NetAddress.ParseBind(_settings.Bind);
        var port = _options.Port ?? _settings.HttpPort;
        var app = Build(bind, port);
        _endpoint.StateChanged += OnStateChanged;
        try
        {
            await app.StartAsync(ct);
        }
        catch (Exception ex) when (ex is IOException or SocketException or InvalidOperationException)
        {
            _endpoint.StateChanged -= OnStateChanged;
            await app.DisposeAsync();
            throw new BindRefusedException($"the web port could not listen on {(bind is null ? "every interface" : bind.ToString())} port {port} — it stays closed ({ex.Message})", ex);
        }
        _app = app;
        Port = new Uri(app.Urls.First()).Port;
        _log.LogInformation("The web port listens on {Port}{Tls}.", Port, ServesTls ? " (HTTP and HTTPS)" : "");
    }

    private void OnStateChanged(long revision) => _changes.Fire();

    private WebApplication Build(IPAddress? bind, int port)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(HttpHost).Assembly.GetName().Name,
            ContentRootPath = AppContext.BaseDirectory,
            EnvironmentName = Environments.Production,
            Args = [],
        });
        // The node's own settings name its doors: no appsettings, environment or command line opens another.
        builder.Configuration.Sources.Clear();
        builder.Logging.ClearProviders();
        if (_options.LoggerFactory is { } factory)
            builder.Logging.AddProvider(new ForwardingLoggerProvider(factory));
        builder.WebHost.UseKestrelHttpsConfiguration();
        var sniffer = new TlsSniffer(_ledger, _options.MaxConnectionsPerAddress, _log);
        builder.WebHost.ConfigureKestrel(k =>
        {
            k.AddServerHeader = false;
            k.Limits.MaxConcurrentConnections = 256;
            k.Limits.MaxConcurrentUpgradedConnections = 256;
            k.Limits.MaxRequestBodySize = 1024 * 1024;
            k.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(10);
            void Listen(ListenOptions o)
            {
                o.Protocols = HttpProtocols.Http1;
                o.Use(sniffer.Outer);
                if (_options.Tls is { } tls)
                {
                    o.UseHttps(new HttpsConnectionAdapterOptions
                    {
                        ServerCertificate = tls.Certificate,
                        SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                        HandshakeTimeout = TimeSpan.FromSeconds(10),
                    });
                    o.Use(sniffer.Inner);
                }
            }
            if (bind is null)
                k.ListenAnyIP(port, Listen);
            else
                k.Listen(bind, port, Listen);
        });
        if (_options.Mcp)
        {
            var mcp = new McpTools(_runner, _options.Approvals ?? new SwitchApprovalPolicy(() => false), _log);
            builder.Services.AddMcpServer(o =>
                {
                    o.ServerInfo = new Implementation { Name = "PlayMap", Version = _options.Version };
                    o.ServerInstructions = "A PlayMap node (media player and projection mapping). Read get_context first: its light and headline are computed from facts, and its commands list says each verb's risk. Act with run_command; high-risk verbs change what the audience sees and need the operator's approval.";
                })
                .WithHttpTransport(o => o.SessionMode = HttpServerSessionMode.StatefulForInitializeClients)
                .WithListToolsHandler(mcp.ListToolsAsync)
                .WithCallToolHandler(mcp.CallToolAsync)
                .WithListResourcesHandler(mcp.ListResourcesAsync)
                .WithReadResourceHandler(mcp.ReadResourceAsync);
        }
        var app = builder.Build();
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.Zero });
        app.Use(GateAsync);
        Map(app);
        foreach (var module in _options.Modules)
            module.Map(app);
        if (_options.Mcp)
            app.MapMcp("/mcp");
        return app;
    }

    /// <summary>The gate before every route but the certificate's. A request through it counts as paired, and
    /// carries its address to MCP's handlers as claims.</summary>
    private async Task GateAsync(HttpContext context, RequestDelegate next)
    {
        var path = context.Request.Path.Value ?? "";
        if (path is "/tls" or "/tls.cer")
        {
            await next(context);
            return;
        }
        if (ModuleOf(path) is { } module)
        {
            switch (module.Admit(context, _settings, out var why))
            {
                case WebAdmission.Open or WebAdmission.Capability:
                    await next(context);
                    return;
                case WebAdmission.Refuse:
                    await WriteErrorAsync(context, StatusCodes.Status403Forbidden, why.Length > 0 ? why : $"{module.Name} refused this request");
                    return;
                default:
                    break;
            }
        }
        var verdict = _gate.Check(context, _settings);
        if (verdict is not (GateVerdict.Open or GateVerdict.Token))
        {
            var (status, words) = GateWords.Of(verdict);
            if (verdict == GateVerdict.WrongToken)
                _log.LogWarning("A wrong token from {Address} on the web port.", NetAddress.Text(context.Connection.RemoteIpAddress));
            await WriteErrorAsync(context, status, words);
            return;
        }
        var remote = context.Connection.RemoteIpAddress;
        if (path.StartsWith("/mcp", StringComparison.Ordinal))
            NoteMcpClient(context);
        context.User = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(McpTools.AddressClaim, NetAddress.Text(remote)),
            new Claim(McpTools.LoopbackClaim, NetAddress.IsLoopback(remote) ? "1" : "0"),
        ], "playmap"));
        await next(context);
    }

    /// <summary>The module whose path a request is under (the longest prefix), or null.</summary>
    private IWebModule? ModuleOf(string path)
    {
        IWebModule? found = null;
        foreach (var m in _options.Modules)
        {
            var prefix = m.PathPrefix.TrimEnd('/');
            var under = path.Equals(prefix, StringComparison.Ordinal) || path.StartsWith(prefix + "/", StringComparison.Ordinal);
            if (under && prefix.Length > 0 && (found is null || prefix.Length > found.PathPrefix.TrimEnd('/').Length))
                found = m;
        }
        return found;
    }

    private void Map(WebApplication app)
    {
        app.MapGet("/api/state", StateAsync);
        app.MapGet("/api/context", (HttpContext c) => WriteJsonAsync(c, StatusCodes.Status200OK, _endpoint.Context()));
        app.MapGet("/api/project", (HttpContext c) => WriteJsonAsync(c, StatusCodes.Status200OK, _endpoint.ProjectJson().Json));
        app.MapGet("/api/verbs", (HttpContext c) => WriteJsonAsync(c, StatusCodes.Status200OK, VerbJson.For(_endpoint)));
        app.MapPost("/api/cmd", CommandAsync);
        app.Map("/ws", WebSocketAsync);
        app.MapGet("/tls", TlsPageAsync);
        app.MapGet("/tls.cer", TlsCertificateAsync);
        app.MapGet("/api/media", ManifestAsync);
        app.MapGet("/api/media/{hash}", MediaFileAsync);
        app.MapFallback((HttpContext c) => WriteErrorAsync(c, StatusCodes.Status404NotFound, "no such route — /api/state, /api/context, /api/project, /api/verbs, /api/cmd, /ws, /tls, /api/media, /mcp"));
    }

    /// <summary>STATE now; with <c>?since=&lt;seq&gt;</c> it waits (at most the long-poll time) for a newer one.</summary>
    private async Task StateAsync(HttpContext context)
    {
        var (json, revision) = _endpoint.State();
        if (long.TryParse(context.Request.Query["since"].ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var since))
        {
            var started = Stopwatch.GetTimestamp();
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, _stop.Token);
            while (revision <= since)
            {
                var left = _options.LongPollMax - Stopwatch.GetElapsedTime(started);
                if (left <= TimeSpan.Zero)
                    break;
                var next = _changes.Next;
                (json, revision) = _endpoint.State();
                if (revision > since)
                    break;
                try
                {
                    await _changes.WaitAsync(next, left, stop.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                (json, revision) = _endpoint.State();
            }
        }
        await WriteJsonAsync(context, StatusCodes.Status200OK, json);
    }

    /// <summary>POST /api/cmd: the body is one line; <c>X-Request-Id</c> (or the line's own "@id") asks for a receipt.</summary>
    private async Task CommandAsync(HttpContext context)
    {
        var text = await Bodies.ReadLineAsync(context.Request.Body, MaxLineBytes, context.RequestAborted);
        if (text is null)
        {
            await WriteErrorAsync(context, StatusCodes.Status413PayloadTooLarge, "a command is one line of at most 64 KiB");
            return;
        }
        var header = context.Request.Headers["X-Request-Id"].ToString();
        string? requestId = header.Length > 0 && WireGrammar.IsRequestId(header) ? header : null;
        if (header.Length > 0 && requestId is null)
        {
            await WriteErrorAsync(context, StatusCodes.Status400BadRequest, "X-Request-Id is 1-32 letters, digits, '-' or '_'");
            return;
        }
        CommandReply reply;
        if (!WireGrammar.TryParse(text.Trim(), out var line, out var error))
        {
            requestId ??= error!.RequestId;
            reply = CommandReply.Fail(error!.Reason);
        }
        else
        {
            requestId ??= line.RequestId;
            reply = line.Verb is "HELLO" or "NONCE" or "AUTH" or "AUTH-PROOF" or "SUBSCRIBE"
                ? CommandReply.Fail(WireWords.WireOnly)
                : await _runner.RunAsync(line, Session(context, "http"), context.RequestAborted, _wall.UnixMs);
        }
        var status = reply.Ok ? StatusCodes.Status200OK : StatusCodes.Status409Conflict;
        if (requestId is not null || context.Request.Headers.ContainsKey("X-Request-Id"))
        {
            await WriteJsonAsync(context, status, CommandRunner.ReceiptJson(requestId, reply));
            return;
        }
        await WriteJsonAsync(context, status, JsonText.Write(w =>
        {
            w.WriteStartObject();
            w.WriteBoolean("ok", reply.Ok);
            w.WriteString("msg", CommandRunner.WireLineFor(null, reply) ?? "OK");
            w.WriteEndObject();
        }));
    }

    private async Task WebSocketAsync(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            await WriteErrorAsync(context, StatusCodes.Status400BadRequest, "/ws is a WebSocket");
            return;
        }
        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, _stop.Token);
        using var session = new WebSocketSession(socket, _runner, _changes, _isAnimating, _isLive, Session(context, "ws"), _options.BeatEvery, _log);
        Interlocked.Increment(ref _webSockets);
        try
        {
            await session.RunAsync(stop.Token);
        }
        finally
        {
            Interlocked.Decrement(ref _webSockets);
        }
    }

    private SessionInfo Session(HttpContext context, string transport)
    {
        var remote = context.Connection.RemoteIpAddress;
        var agent = context.Request.Headers.UserAgent.ToString();
        var name = agent.Length > 64 ? agent[..64] : agent;
        return new SessionInfo(transport[0] + Interlocked.Increment(ref _sessions).ToString(CultureInfo.InvariantCulture),
            transport, NetAddress.Text(remote), NetAddress.IsLoopback(remote), name, Paired: true);
    }

    private async Task TlsPageAsync(HttpContext context)
    {
        var tls = _options.Tls;
        var body = tls is null
            ? "<p>This node serves no certificate (HTTPS is off).</p>"
            : $"""
               <p>Fingerprint (SHA-256): <code>{WebUtility.HtmlEncode(tls.Fingerprint[TlsFingerprint.Prefix.Length..])}</code></p>
               <p><a href="/tls.cer">Download the certificate</a>, check the fingerprint matches the one PlayMap shows in its Network drawer, then trust it on this device:</p>
               <ul>
               <li><b>Windows</b>: open the file → Install Certificate → Local Machine → "Trusted Root Certification Authorities".</li>
               <li><b>macOS</b>: open it in Keychain Access → System → double-click it → Trust → Always Trust.</li>
               <li><b>iPhone / iPad</b>: open the download → Settings → Profile Downloaded → Install; then Settings → General → About → Certificate Trust Settings → turn it on.</li>
               <li><b>Android</b>: Settings → Security → Encryption &amp; credentials → Install a certificate → CA certificate.</li>
               </ul>
               <p>Then open this node with <code>https://</code> on port {Port.ToString(CultureInfo.InvariantCulture)}.</p>
               """;
        var html = "<!doctype html><meta charset=utf-8><meta name=viewport content=\"width=device-width\"><title>PlayMap certificate</title>"
            + "<style>body{font:16px system-ui;background:#0b0d12;color:#e6e8ee;max-width:42em;margin:2em auto;padding:0 1em}code{word-break:break-all;color:#22d3ee}a{color:#a78bfa}</style>"
            + "<h1>PlayMap: encrypted connections</h1>" + body;
        context.Response.ContentType = "text/html; charset=utf-8";
        await context.Response.WriteAsync(html, context.RequestAborted);
    }

    private async Task TlsCertificateAsync(HttpContext context)
    {
        if (_options.Tls is not { } tls)
        {
            await WriteErrorAsync(context, StatusCodes.Status404NotFound, "this node serves no certificate");
            return;
        }
        context.Response.ContentType = "application/pkix-cert";
        context.Response.Headers.ContentDisposition = "attachment; filename=\"playmap.cer\"";
        await context.Response.Body.WriteAsync(tls.PublicDer, context.RequestAborted);
    }

    private async Task ManifestAsync(HttpContext context)
    {
        if (_options.Media is not { } store)
        {
            await WriteErrorAsync(context, StatusCodes.Status404NotFound, "this node serves no media");
            return;
        }
        await WriteJsonAsync(context, StatusCodes.Status200OK, JsonText.Write(w =>
        {
            w.WriteStartArray();
            foreach (var f in store.Manifest())
            {
                w.WriteStartObject();
                w.WriteString("hash", f.Hash);
                w.WriteNumber("size", f.Length);
                w.WriteString("name", f.Name);
                w.WriteEndObject();
            }
            w.WriteEndArray();
        }));
    }

    /// <summary>One file by its content hash, with HTTP ranges (a peer pulls it in chunks and resumes). Only a file
    /// the store names is served: a hash is never a path.</summary>
    private async Task MediaFileAsync(HttpContext context)
    {
        var hash = (context.Request.RouteValues["hash"] as string ?? "").ToLowerInvariant();
        if (hash.StartsWith("sha256:", StringComparison.Ordinal))
            hash = hash[7..];
        if (hash.Length != 64 || !hash.All(Uri.IsHexDigit))
        {
            await WriteErrorAsync(context, StatusCodes.Status400BadRequest, "a media hash is 64 hex characters of SHA-256");
            return;
        }
        if (_options.Media?.Lookup(hash) is not { } file || !File.Exists(file.Path))
        {
            await WriteErrorAsync(context, StatusCodes.Status404NotFound, "this node does not hold that file");
            return;
        }
        context.Response.Headers["X-Content-SHA256"] = hash;
        await TypedResults.PhysicalFile(file.Path, "application/octet-stream", enableRangeProcessing: true).ExecuteAsync(context);
    }

    private static async Task WriteJsonAsync(HttpContext context, int status, string json)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.Headers.CacheControl = "no-store";
        await context.Response.Body.WriteAsync(Encoding.UTF8.GetBytes(json), context.RequestAborted);
    }

    private static Task WriteErrorAsync(HttpContext context, int status, string words) =>
        WriteJsonAsync(context, status, JsonText.Write(w =>
        {
            w.WriteStartObject();
            w.WriteBoolean("ok", false);
            w.WriteString("error", words);
            w.WriteEndObject();
        }));

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _endpoint.StateChanged -= OnStateChanged;
        await _stop.CancelAsync();
        if (_app is not null)
        {
            using var grace = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                await _app.StopAsync(grace.Token);
            }
            catch (OperationCanceledException ex)
            {
                // A connection that would not close in five seconds is cut with the host.
                _log.LogDebug(ex, "The web port stopped with connections still open.");
            }
            await _app.DisposeAsync();
        }
        _stop.Dispose();
    }

    /// <summary>Hands the host's log lines to the node's logger factory.</summary>
    private sealed class ForwardingLoggerProvider(ILoggerFactory factory) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => factory.CreateLogger(categoryName);

        public void Dispose()
        {
            // The factory is the caller's: it outlives this provider.
        }
    }
}
