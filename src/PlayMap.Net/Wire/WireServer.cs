using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PlayMap.Core.Bus;
using PlayMap.Core.Time;
using PlayMap.Core.Wire;
using PlayMap.Net.Commands;
using PlayMap.Net.Http;

namespace PlayMap.Net.Wire;

/// <summary>How the line wire runs; the defaults are the contract's (docs/WIRE.md).</summary>
public sealed record WireServerOptions
{
    public WireLimits Limits { get; init; } = WireLimits.Default;

    /// <summary>STATE pushes trail a change by this much; changes inside the window coalesce (latest wins).</summary>
    public TimeSpan PushThrottle { get; init; } = TimeSpan.FromMilliseconds(200);

    /// <summary>The clock <see cref="PushThrottle"/> is counted on: the system's on a node; a test moves its own, so the pushes a run of
    /// changes makes do not hang on how fast the test makes them.</summary>
    public TimeProvider Time { get; init; } = TimeProvider.System;

    /// <summary>While the node animates, STATE is read at least this often for its listeners.</summary>
    public TimeSpan AnimatingPushEvery { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Whether anything plays; null reads the endpoint's <see cref="INodeActivity"/> (idle when it has none).</summary>
    public Func<bool>? IsAnimating { get; init; }

    /// <summary>Plain <c>AUTH &lt;token&gt;</c> is taken from this machine (off in tests that play a remote from loopback).</summary>
    public bool PlainAuthOnLoopback { get; init; } = true;

    /// <summary>Told when a TCP connection that plain AUTH is not taken from sends the node's own token in it: the AUTH is refused, and
    /// reported, since the token crossed the network in clear; null = not reported.</summary>
    public Action<ClearSighting>? SeenInClear { get; init; }

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
    private readonly WireServerOptions _options;
    private readonly ILogger _log;
    private readonly ConnectionLedger _ledger = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly LineSessions _lines;
    private volatile NodeSettings _settings;
    private Socket? _listener;
    private Task? _acceptLoop;
    private int _disposed;

    public WireServer(NodeSettings settings, IWireEndpoint endpoint, ShowWallClock wall, WireServerOptions? options = null, ILogger? log = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(wall);
        _options = options ?? new WireServerOptions();
        _log = log ?? NullLogger.Instance;
        _lines = new LineSessions(() => _settings, endpoint, wall, _options, _log, "w");
    }

    /// <summary>The port the wire listens on (after <see cref="Start"/>).</summary>
    public int Port { get; private set; }

    public int Connections => _ledger.Open;

    /// <summary>Lines that made the endpoint fault since start.</summary>
    public long Faults => _lines.Runner.Faults;

    /// <summary>STATE pushes skipped because they would have repeated the last one word for word.</summary>
    public long PushesSkipped => _lines.PushesSkipped;

    public NodeSettings Settings => _settings;

    /// <summary>Takes a new token, trusted list or loopback trust at once (a session paired with an old token is
    /// paired no longer). A new bind or port needs a restart.</summary>
    public void UpdateSettings(NodeSettings settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _lines.Refresh();
    }

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
        _lines.Start();
        _acceptLoop = Task.Run(() => AcceptAsync(_listener, _stop.Token), CancellationToken.None);
        _log.LogInformation("The line wire listens on port {Port}.", Port);
    }

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
        TcpLineChannel? channel = null;
        try
        {
            client.NoDelay = true;
            channel = new TcpLineChannel(client, new NetworkStream(client, ownsSocket: true), remote, limits);
            await _lines.ServeAsync(channel, ct);
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
        {
            // A connection's end is routine; the ledger and the session table are released below.
            _log.LogDebug(ex, "A line wire connection from {Address} ended.", address);
        }
        catch (Exception ex)
        {
            // Anything else ends this connection only — never the listener — and leaves its trace.
            _log.LogWarning(ex, "A line wire connection from {Address} faulted and was closed.", address);
        }
        finally
        {
            if (channel is not null)
                await channel.DisposeAsync();
            else
                client.Dispose();
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

    public static string ProjectEnvelope(string projectJson, long revision) =>
        "{\"revision\":" + revision.ToString(System.Globalization.CultureInfo.InvariantCulture) + ",\"project\":" +
        (string.IsNullOrWhiteSpace(projectJson) ? "null" : projectJson) + "}";

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        await _stop.CancelAsync();
        _listener?.Dispose();
        await _lines.DisposeAsync();
        if (_acceptLoop is not null)
        {
            try
            {
                await _acceptLoop.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException or SocketException or ObjectDisposedException)
            {
                // Closing: the accept loop ends with the listener.
                _log.LogDebug(ex, "The line wire stopped with its accept loop still closing.");
            }
        }
        _stop.Dispose();
    }
}
