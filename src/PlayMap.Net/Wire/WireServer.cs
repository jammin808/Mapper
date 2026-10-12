using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PlayMap.Core.Bus;
using PlayMap.Core.Time;
using PlayMap.Core.Wire;
using PlayMap.Net.Commands;

namespace PlayMap.Net.Wire;

/// <summary>How the line wire runs; the defaults are the contract's (docs/WIRE.md).</summary>
public sealed record WireServerOptions
{
    public WireLimits Limits { get; init; } = WireLimits.Default;

    /// <summary>STATE pushes trail a change by this much; changes inside the window coalesce (latest wins).</summary>
    public TimeSpan PushThrottle { get; init; } = TimeSpan.FromMilliseconds(200);

    /// <summary>While the node animates, STATE is read at least this often for its listeners.</summary>
    public TimeSpan AnimatingPushEvery { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Whether anything plays; null reads the endpoint's <see cref="INodeActivity"/> (idle when it has none).</summary>
    public Func<bool>? IsAnimating { get; init; }

    /// <summary>Plain <c>AUTH &lt;token&gt;</c> is taken from this machine (off in tests that play a remote from loopback).</summary>
    public bool PlainAuthOnLoopback { get; init; } = true;

    /// <summary>The port to listen on; null = <see cref="NodeSettings.WirePort"/>, 0 = any free port.</summary>
    public int? Port { get; init; }
}

/// <summary>PlayMap's line wire (TCP, Patterns' grammar, docs/WIRE.md): a NONCE for a peer that has something to
/// prove, HELLO makes a STATE listener, AUTH-PROOF pairs, PING exchanges clocks, queries are open and mutating verbs
/// need pairing, fact lines and a peer's own OK/ERR lines are never answered, a fault is that line's. Each
/// connection has one writer (ordered replies, latest-wins pushes). The listener honours the bind address and stays
/// closed when it cannot (fail closed).</summary>
public sealed class WireServer : IAsyncDisposable
{
    private readonly IWireEndpoint _endpoint;
    private readonly ShowWallClock _wall;
    private readonly WireServerOptions _options;
    private readonly ILogger _log;
    private readonly CommandRunner _runner;
    private readonly ConnectionLedger _ledger = new();
    private readonly ConcurrentDictionary<WireSession, Task> _sessions = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly TrailingThrottle _stateThrottle;
    private readonly TrailingThrottle _projectThrottle;
    private readonly Func<bool> _isAnimating;
    private readonly object _pushGate = new();
    private volatile NodeSettings _settings;
    private Socket? _listener;
    private Task? _acceptLoop;
    private Task? _animateLoop;
    private string _lastState = "";
    private string _lastContext = "";
    private string _lastProject = "";
    private long _sessionCount;
    private long _skipped;
    private int _disposed;

    public WireServer(NodeSettings settings, IWireEndpoint endpoint, ShowWallClock wall, WireServerOptions? options = null, ILogger? log = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        _wall = wall ?? throw new ArgumentNullException(nameof(wall));
        _options = options ?? new WireServerOptions();
        _log = log ?? NullLogger.Instance;
        _runner = new CommandRunner(endpoint, wall, _log);
        _isAnimating = _options.IsAnimating ?? (() => endpoint is INodeActivity { IsAnimating: true });
        _stateThrottle = new TrailingThrottle(_options.PushThrottle, PushState, _log, _stop.Token);
        _projectThrottle = new TrailingThrottle(_options.PushThrottle, PushProject, _log, _stop.Token);
    }

    /// <summary>The port the wire listens on (after <see cref="Start"/>).</summary>
    public int Port { get; private set; }

    public int Connections => _ledger.Open;

    /// <summary>Lines that made the endpoint fault since start.</summary>
    public long Faults => _runner.Faults;

    /// <summary>STATE pushes skipped because they would have repeated the last one word for word.</summary>
    public long PushesSkipped => Interlocked.Read(ref _skipped);

    public NodeSettings Settings => _settings;

    internal WireServerOptions Options => _options;

    internal CommandRunner Runner => _runner;

    internal ShowWallClock Wall => _wall;

    internal IWireEndpoint Endpoint => _endpoint;

    internal ILogger Log => _log;

    /// <summary>Takes a new token, trusted list or loopback trust at once (a session paired with an old token is
    /// paired no longer). A new bind or port needs a restart.</summary>
    public void UpdateSettings(NodeSettings settings) => _settings = settings ?? throw new ArgumentNullException(nameof(settings));

    /// <summary>Opens the listener. Throws <see cref="BindRefusedException"/> (nothing listens) when the bind address
    /// is not an address or the machine refuses it.</summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (_listener is not null)
            return;
        var port = _options.Port ?? _settings.WirePort;
        _listener = NetAddress.Listen(_settings.Bind, port);
        Port = ((IPEndPoint)_listener.LocalEndPoint!).Port;
        _endpoint.StateChanged += OnStateChanged;
        _endpoint.ProjectChanged += OnProjectChanged;
        _acceptLoop = Task.Run(() => AcceptAsync(_listener, _stop.Token), CancellationToken.None);
        _animateLoop = Task.Run(() => AnimateAsync(_stop.Token), CancellationToken.None);
        _log.LogInformation("The line wire listens on port {Port}.", Port);
    }

    private void OnStateChanged(long revision) => _stateThrottle.Arm();

    private void OnProjectChanged(long revision) => _projectThrottle.Arm();

    private async Task AcceptAsync(Socket listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            Socket client;
            try
            {
                client = await listener.AcceptAsync(ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException ex)
            {
                // One failed accept (a peer that reset mid-handshake) is that peer's; the door stays open.
                _log.LogDebug(ex, "An accept on the line wire failed.");
                continue;
            }
            _ = ServeAsync(client, ct);
        }
    }

    private async Task ServeAsync(Socket client, CancellationToken ct)
    {
        var remote = (client.RemoteEndPoint as IPEndPoint)?.Address;
        var address = NetAddress.Text(remote);
        var limits = _options.Limits;
        if (!_ledger.TryAdmit(address, limits.MaxConnections, limits.MaxPerAddress))
        {
            await RefuseBusyAsync(client, limits.BusyWords(_ledger.From(address)));
            return;
        }
        WireSession? session = null;
        try
        {
            client.NoDelay = true;
            var stream = new NetworkStream(client, ownsSocket: true);
            session = new WireSession(this, client, stream, remote, "w" + Interlocked.Increment(ref _sessionCount).ToString(System.Globalization.CultureInfo.InvariantCulture));
            var run = session.RunAsync(ct);
            _sessions[session] = run;
            await run;
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
        {
            // A connection's end is routine; the ledger and the session table are released below.
            _log.LogDebug(ex, "A line wire connection from {Address} ended.", address);
        }
        finally
        {
            if (session is not null)
            {
                _sessions.TryRemove(session, out _);
                await session.DisposeAsync();
            }
            else
            {
                client.Dispose();
            }
            _ledger.Release(address);
        }
    }

    /// <summary>The ceiling, said in the wire's words before the door closes, so a client reconnecting in a loop
    /// reads why.</summary>
    private async Task RefuseBusyAsync(Socket client, string words)
    {
        try
        {
            using var stream = new NetworkStream(client, ownsSocket: true);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await stream.WriteAsync(Encoding.UTF8.GetBytes(WireGrammar.Err(null, words) + "\n"), deadline.Token);
            client.Shutdown(SocketShutdown.Send);
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
        {
            // Gone already: there is nobody to tell.
            _log.LogDebug(ex, "A refused connection left before it heard why.");
        }
        finally
        {
            client.Dispose();
        }
    }

    private async Task AnimateAsync(CancellationToken ct)
    {
        while (await Waits.DelayAsync(_options.AnimatingPushEvery, ct))
        {
            try
            {
                if (_isAnimating() && HasListeners())
                    _stateThrottle.Arm();
            }
            catch (Exception ex)
            {
                // An activity probe that faults says nothing this second; the next second asks again.
                _log.LogWarning(ex, "The node's activity probe faulted.");
            }
        }
    }

    private bool HasListeners()
    {
        foreach (var s in _sessions.Keys)
            if (s.WantsState || s.WantsContext)
                return true;
        return false;
    }

    private void PushState()
    {
        lock (_pushGate)
        {
            var wantsState = false;
            var wantsContext = false;
            foreach (var s in _sessions.Keys)
            {
                wantsState |= s.WantsState;
                wantsContext |= s.WantsContext;
            }
            string? stateLine = null;
            string? contextLine = null;
            if (wantsState)
            {
                var json = _endpoint.State().Json;
                if (string.Equals(json, _lastState, StringComparison.Ordinal))
                {
                    Interlocked.Increment(ref _skipped);
                }
                else
                {
                    _lastState = json;
                    stateLine = "STATE " + json;
                }
            }
            if (wantsContext)
            {
                var context = _endpoint.Context();
                if (!string.Equals(context, _lastContext, StringComparison.Ordinal))
                {
                    _lastContext = context;
                    contextLine = "CONTEXT " + context;
                }
            }
            foreach (var s in _sessions.Keys)
            {
                if (stateLine is not null && s.WantsState)
                    s.Writer.Push(PushKind.State, stateLine);
                if (contextLine is not null && s.WantsContext)
                    s.Writer.Push(PushKind.Context, contextLine);
            }
        }
    }

    private void PushProject()
    {
        lock (_pushGate)
        {
            var any = false;
            foreach (var s in _sessions.Keys)
                any |= s.WantsProject;
            if (!any)
                return;
            var line = ProjectLine();
            if (string.Equals(line, _lastProject, StringComparison.Ordinal))
                return;
            _lastProject = line;
            foreach (var s in _sessions.Keys)
                if (s.WantsProject)
                    s.Writer.Push(PushKind.Project, line);
        }
    }

    /// <summary>"PROJECT {"revision":N,"project":{…}}" — the push carries the revision beside the document, because
    /// the document itself does not (readers compare revisions, never contents).</summary>
    internal string ProjectLine()
    {
        var (json, revision) = _endpoint.ProjectJson();
        return "PROJECT " + ProjectEnvelope(json, revision);
    }

    public static string ProjectEnvelope(string projectJson, long revision) =>
        "{\"revision\":" + revision.ToString(System.Globalization.CultureInfo.InvariantCulture) + ",\"project\":" +
        (string.IsNullOrWhiteSpace(projectJson) ? "null" : projectJson) + "}";

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _endpoint.StateChanged -= OnStateChanged;
        _endpoint.ProjectChanged -= OnProjectChanged;
        await _stop.CancelAsync();
        _listener?.Dispose();
        foreach (var s in _sessions.Keys)
            s.Abort();
        var waits = new List<Task>(_sessions.Values);
        if (_acceptLoop is not null)
            waits.Add(_acceptLoop);
        if (_animateLoop is not null)
            waits.Add(_animateLoop);
        try
        {
            await Task.WhenAll(waits).WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException or IOException or SocketException or ObjectDisposedException)
        {
            // Closing: a connection that ends badly while the server stops is not news.
            _log.LogDebug(ex, "The line wire stopped with a connection still closing.");
        }
        _stop.Dispose();
    }
}
