using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Extensions.Logging;
using PlayMap.Core.Bus;
using PlayMap.Core.Time;
using PlayMap.Core.Wire;
using PlayMap.Net.Commands;

namespace PlayMap.Net.Wire;

/// <summary>The line wire's sessions behind one door (the TCP port, or the web port's <c>/ws</c> line channel): the session table,
/// the STATE, CONTEXT and PROJECT pushes (trailing throttle, latest wins, a repeat skipped) and the once-a-second STATE while the node
/// animates. Each door has its own, so a door that cannot open leaves the other serving lines.</summary>
internal sealed class LineSessions : IAsyncDisposable
{
    private readonly Func<NodeSettings> _settings;
    private readonly IWireEndpoint _endpoint;
    private readonly ShowWallClock _wall;
    private readonly WireServerOptions _options;
    private readonly ILogger _log;
    private readonly string _prefix;
    private readonly CommandRunner _runner;
    private readonly ConcurrentDictionary<WireSession, Task> _sessions = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly TrailingThrottle _stateThrottle;
    private readonly TrailingThrottle _projectThrottle;
    private readonly Func<bool> _isAnimating;
    private readonly Lock _pushGate = new();
    private Task? _animateLoop;
    private string _lastState = "";
    private string _lastContext = "";
    private string _lastProject = "";
    private long _sessionCount;
    private long _skipped;
    private int _started;
    private int _disposed;

    /// <param name="prefix">The first letter of this door's session ids ("w" for TCP, "l" for the web port's line channel), so ids
    /// never repeat across doors.</param>
    public LineSessions(Func<NodeSettings> settings, IWireEndpoint endpoint, ShowWallClock wall, WireServerOptions options, ILogger log, string prefix)
    {
        _settings = settings;
        _endpoint = endpoint;
        _wall = wall;
        _options = options;
        _log = log;
        _prefix = prefix;
        _runner = new CommandRunner(endpoint, wall, log);
        _isAnimating = options.IsAnimating ?? (() => endpoint is INodeActivity { IsAnimating: true });
        _stateThrottle = new TrailingThrottle(options.PushThrottle, PushState, log, _stop.Token);
        _projectThrottle = new TrailingThrottle(options.PushThrottle, PushProject, log, _stop.Token);
    }

    public NodeSettings Settings => _settings();

    public WireServerOptions Options => _options;

    public CommandRunner Runner => _runner;

    public ShowWallClock Wall => _wall;

    public IWireEndpoint Endpoint => _endpoint;

    public ILogger Log => _log;

    public int Count => _sessions.Count;

    public long PushesSkipped => Interlocked.Read(ref _skipped);

    /// <summary>A wrong proof from an address, counted where the door counts them (the web port's lockout ledger); null = the
    /// connection's own count only.</summary>
    public Action<string>? WrongProof { get; init; }

    /// <summary>True while an address is locked out for wrong tokens (the web port's ledger); null = never.</summary>
    public Func<string, bool>? LockedOut { get; init; }

    /// <summary>Starts the pushes (once).</summary>
    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
            return;
        _endpoint.StateChanged += OnStateChanged;
        _endpoint.ProjectChanged += OnProjectChanged;
        _animateLoop = Task.Run(() => AnimateAsync(_stop.Token), CancellationToken.None);
    }

    private void OnStateChanged(long revision) => _stateThrottle.Arm();

    /// <summary>The node's settings changed: every session tells the client table who it is now (paired or not).</summary>
    public void Refresh()
    {
        foreach (var s in _sessions.Keys)
            s.Refresh();
    }

    private void OnProjectChanged(long revision) => _projectThrottle.Arm();

    /// <summary>Runs one connection's session to its end; the channel is the caller's to dispose. The session is in the table before
    /// it reads its first line: lines it handles before its run first waits (they can, when they are in already) are refreshed and
    /// stopped as any other.</summary>
    public async Task ServeAsync(ILineChannel channel, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(channel);
        var id = _prefix + Interlocked.Increment(ref _sessionCount).ToString(CultureInfo.InvariantCulture);
        var session = new WireSession(this, channel, id);
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _sessions[session] = ended.Task;
        try
        {
            await session.RunAsync(ct);
        }
        finally
        {
            ended.SetResult();
            _sessions.TryRemove(session, out _);
            await session.DisposeAsync();
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

    /// <summary>"PROJECT {"revision":N,"project":{…}}" — the push carries the revision beside the document.</summary>
    public string ProjectLine()
    {
        var (json, revision) = _endpoint.ProjectJson();
        return "PROJECT " + WireServer.ProjectEnvelope(json, revision);
    }

    /// <summary>Stops the pushes and ends every session: a WebSocket hears 1001 (the node is stopping).</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _endpoint.StateChanged -= OnStateChanged;
        _endpoint.ProjectChanged -= OnProjectChanged;
        await _stop.CancelAsync();
        foreach (var s in _sessions.Keys)
            await s.StopAsync();
        var waits = new List<Task>(_sessions.Values);
        if (_animateLoop is not null)
            waits.Add(_animateLoop);
        try
        {
            await Task.WhenAll(waits).WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException or IOException or System.Net.Sockets.SocketException
            or ObjectDisposedException or System.Net.WebSockets.WebSocketException)
        {
            // Closing: a connection that ends badly while the door stops is not news.
            _log.LogDebug(ex, "The line sessions stopped with a connection still closing.");
        }
        _stop.Dispose();
    }
}
