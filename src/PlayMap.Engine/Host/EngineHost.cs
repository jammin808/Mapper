using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PlayMap.Core.Bus;
using PlayMap.Core.Time;
using PlayMap.Core.Wire;
using PlayMap.Net;
using PlayMap.Net.Bus;
using PlayMap.Net.Tls;

namespace PlayMap.Engine.Host;

/// <summary>How a host runs beyond what the command line says: the parts it is composed of, where its log goes, and
/// what a test changes (the clock, the hello's ports).</summary>
public sealed record EngineHostOptions
{
    public EngineParts Parts { get; init; } = EngineParts.Bare;

    /// <summary>Where the host, the transports and the links log; null logs nowhere.</summary>
    public ILoggerFactory? Logs { get; init; }

    /// <summary>The node's monotonic clock; null = the process's.</summary>
    public IMonoClock? Mono { get; init; }

    /// <summary>The hello's ports and pace; null = the contract's (UDP 9928, every 2 s).</summary>
    public HelloOptions? Hello { get; init; }

    /// <summary>How often the recovery record is brought up to date when something it holds has moved.</summary>
    public TimeSpan RecoveryEvery { get; init; } = TimeSpan.FromSeconds(2);
}

/// <summary>The composition root of a node process. <see cref="StartAsync"/> loads the node's settings (with the
/// command line's name and ports for this run), its TLS identity and pins, then builds — in this order — the media
/// backend, the network host, the render host (or none), the scan backend, the extensions, the engine's services and
/// the engine; puts the project back (the command line's, else the recovery record's); then starts the render host,
/// the extensions, the line wire, the web port, the hello, the clock sync and the links. A part that fails leaves the
/// node up without it and says so in STATE's faults; only a node with no door at all (neither the wire nor the web
/// port could listen) refuses to start, with <see cref="BindRefusedException"/>. <see cref="DisposeAsync"/> first stops
/// the engine taking lines, then undoes the start in reverse — every part closed even when one before it throws — and
/// writes the recovery record and flushes the journal on the way out. One host per process.</summary>
public sealed class EngineHost : IAsyncDisposable
{
    private readonly EngineOptions _options;
    private readonly NodeHome _home;
    private readonly EngineHostOptions _host;
    private readonly HostFaults _faults = new();
    private readonly List<IEngineExtension> _extensions = [];
    private readonly List<IEngineExtension> _started = [];
    private readonly CancellationTokenSource _stop = new();
    private ILogger _log = NullLogger.Instance;
    private TlsIdentity? _tls;
    private MediaBackend? _media;
    private NetworkHost? _network;
    private IRenderHost? _render;
    private bool _renderStarted;
    private IScanBackend? _scan;
    private EngineCore? _engine;
    private Task _recovery = Task.CompletedTask;
    private int _phase;

    public EngineHost(EngineOptions options, NodeHome home, EngineHostOptions? host = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _home = home ?? throw new ArgumentNullException(nameof(home));
        _host = host ?? new EngineHostOptions();
    }

    /// <summary>The running engine (after <see cref="StartAsync"/>).</summary>
    public EngineCore Engine => _engine ?? throw new InvalidOperationException("the host has not started");

    public NetworkHost Network => _network ?? throw new InvalidOperationException("the host has not started");

    public MediaBackend Media => _media ?? throw new InvalidOperationException("the host has not started");

    /// <summary>The render host, or null on a node without one.</summary>
    public IRenderHost? Render => _render;

    public NodeHome Home => _home;

    /// <summary>What the node is missing now (also STATE's faults).</summary>
    public IReadOnlyList<NodeFault> Faults => _faults.Faults();

    public async Task StartAsync(CancellationToken ct = default)
    {
        if (Interlocked.CompareExchange(ref _phase, 1, 0) != 0)
            throw new InvalidOperationException("a host starts once");
        var logs = _host.Logs ?? NullLoggerFactory.Instance;
        _log = logs.CreateLogger("PlayMap.Engine.Host");
        var mono = _host.Mono ?? MonoClock.Instance;
        var wall = new ShowWallClock(mono, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        // The node's own: its settings (this run's name and ports over the stored ones), its certificate, its pins.
        var stored = _home.LoadSettings(_options.Name ?? Environment.MachineName);
        var running = stored with
        {
            Name = _options.Name ?? stored.Name,
            HttpPort = _options.HttpPort ?? stored.HttpPort,
            WirePort = _options.WirePort ?? stored.WirePort,
        };
        _tls = LoadTls(running, logs);
        var pins = new PinStore(Path.Combine(_home.KeysFolder, "pins.json"), logs.CreateLogger("PlayMap.Net.Tls"));

        // Built in order: media, network, render, scan, extensions, services, engine.
        _media = new MediaBackend(_home, logs.CreateLogger("PlayMap.Engine.Media"));
        var approvals = new ApprovalHook();
        _network = new NetworkHost(new NetworkHostOptions
        {
            Home = _home,
            Stored = stored,
            Running = running,
            Mono = mono,
            Wall = wall,
            Logs = logs,
            Media = _media,
            Approvals = approvals,
            Tls = _tls,
            Pins = pins,
            Hello = _host.Hello,
        });
        _faults.Network = _network;

        _render = MakeRenderHost(new RenderHostRequest(_options, _home, running, mono, wall, _media, logs));
        IOutputBackend outputs = _render is null ? NullBackends.Instance : _render;
        IScanDisplay display = _render is null ? NoScanDisplay.Instance : _render;
        _scan = MakeScanBackend(new ScanBackendRequest(display, outputs, _home, running, mono, logs));

        var setup = new ExtensionSetup(_options, _home, running, mono, wall, logs, _media, _network, outputs, display, _scan, approvals)
        {
            AddFaults = _faults.AddSource,
        };
        foreach (var factory in _host.Parts.Extensions)
        {
            try
            {
                _extensions.Add(factory(setup));
            }
            catch (Exception ex)
            {
                Fault("extension", "an extension could not be made and the node runs without it: " + ex.Message, ex);
            }
        }

        var folder = _options.Project is { Length: > 0 } p ? Path.GetDirectoryName(Path.GetFullPath(p)) : null;
        var services = new EngineServices(running, mono, wall, _network.Clock, _media, outputs, _scan, _network, _network, _home.JournalPath, folder)
        {
            Faults = _faults,
            Transfers = _network,
            Pairing = _network,
            Ai = _network,
        };
        foreach (var extension in _extensions.ToList())
        {
            try
            {
                services = extension.Configure(services);
            }
            catch (Exception ex)
            {
                Fault(extension.Name, $"{extension.Name} could not add its services and the node runs without it: {ex.Message}", ex);
                _extensions.Remove(extension);
                await Quietly(extension.Name, () => extension.DisposeAsync().AsTask());
            }
        }

        var engine = new EngineCore(services);
        _engine = engine;
        foreach (var f in _faults.Faults())
            engine.Journal.Add("fault", f.Words, ok: false);
        _media.Changed += engine.FactsChanged;
        _media.Said += words => engine.Journal.Add("media", words, ok: false);
        await PutProjectBackAsync(engine);
        _media.Attach(engine);

        // Started in order: the render host, the extensions, then the doors (the network host opens the wire, the web
        // port, the hello, the clock sync and the links).
        StartRenderHost(engine);
        foreach (var extension in _extensions)
        {
            try
            {
                await extension.StartAsync(engine, ct);
                _started.Add(extension);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                Fault(extension.Name, $"{extension.Name} could not start and the node runs without it: {ex.Message}", ex);
            }
        }
        await _network.StartAsync(engine, ct);
        _recovery = Task.Run(() => KeepRecoveryAsync(engine, _stop.Token), CancellationToken.None);
        engine.Journal.Add("node", $"{running.Name} is up: wire {Port(_network.WirePort)}, web {Port(_network.HttpPort)}, renderer {outputs.RendererName} · {engine.Services.Build.Display}");
        _log.LogInformation("{Name} ({Id}) is up: wire {Wire}, web {Web}, renderer {Renderer}.", running.Name, running.NodeId,
            Port(_network.WirePort), Port(_network.HttpPort), outputs.RendererName);
        Volatile.Write(ref _phase, 2);
    }

    private static string Port(int port) => port > 0 ? port.ToString(System.Globalization.CultureInfo.InvariantCulture) : "closed";

    private TlsIdentity? LoadTls(NodeSettings settings, ILoggerFactory logs)
    {
        try
        {
            Directory.CreateDirectory(_home.KeysFolder);
            return TlsIdentity.LoadOrCreate(_home.KeysFolder, settings.Name, DateTimeOffset.UtcNow, log: logs.CreateLogger("PlayMap.Net.Tls"));
        }
        catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            Fault("tls", "this node has no certificate, so the web port serves plain HTTP only: " + ex.Message, ex);
            return null;
        }
    }

    private IRenderHost? MakeRenderHost(RenderHostRequest request)
    {
        if (_host.Parts.RenderHost is not { } factory)
            return null;
        try
        {
            return factory(request);
        }
        catch (Exception ex)
        {
            Fault("render", "the render host could not be made, so this node has no outputs: " + ex.Message, ex);
            return null;
        }
    }

    private IScanBackend MakeScanBackend(ScanBackendRequest request)
    {
        if (_host.Parts.ScanBackend is not { } factory)
            return NullBackends.Instance;
        try
        {
            return factory(request);
        }
        catch (Exception ex)
        {
            Fault("scan", "the scanner could not be made, so this node cannot scan: " + ex.Message, ex);
            return NullBackends.Instance;
        }
    }

    private void StartRenderHost(EngineCore engine)
    {
        if (_render is null)
            return;
        try
        {
            _render.Start(engine);
            _renderStarted = true;
        }
        catch (Exception ex)
        {
            Fault("render", "the render host could not start, so this node shows nothing on its outputs: " + ex.Message, ex);
        }
    }

    /// <summary>The command line's project through the same verb the desk uses, else the recovery record (the project,
    /// the air and every playlist at where the show is now). A project that will not open leaves the empty one and a
    /// fault saying why.</summary>
    private async Task PutProjectBackAsync(EngineCore engine)
    {
        if (_options.Project is { Length: > 0 } path)
        {
            var full = Path.GetFullPath(path);
            if (!WireGrammar.TryParse("PROJECT OPEN " + WireGrammar.Quote(full), out var line, out var error))
            {
                Fault("project", $"the project path {full} cannot be said on a line: {error!.Reason}");
                return;
            }
            var reply = await engine.HandleAsync(line, HostSession, CancellationToken.None);
            if (!reply.Ok)
                Fault("project", reply.Payload ?? "the project would not open");
            return;
        }
        if (!_options.Recover || RecoveryRecord.Load(_home.RecoveryPath) is not { } record)
            return;
        try
        {
            var words = await engine.RestoreAsync(record);
            _log.LogInformation("Recovery: {Words}.", words);
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException)
        {
            Fault("recovery", "the recovery record could not be put back, so the node starts with an empty project: " + ex.Message, ex);
        }
    }

    private static SessionInfo HostSession => new("host", "desk", "127.0.0.1", true, "Node host", true);

    /// <summary>Keeps the recovery record as current as the show: written whenever the project, a transport or the air
    /// has moved, so a node that dies without saying so comes back to where the show is.</summary>
    private async Task KeepRecoveryAsync(EngineCore engine, CancellationToken ct)
    {
        (long, long, long, string?, bool) written = default;
        while (await Waits.DelayAsync(_host.RecoveryEvery, ct))
        {
            var now = (engine.Store.Current.Revision, engine.Playback.Snapshot.Revision, engine.Air.Revision, engine.ProjectPath, engine.Dirty);
            if (now == written)
                continue;
            if (await WriteRecoveryAsync(engine))
                written = now;
        }
    }

    private async Task<bool> WriteRecoveryAsync(EngineCore engine)
    {
        try
        {
            var record = await engine.Lane.RunAsync(() => RecoveryRecord.Capture(engine));
            record.Save(_home.RecoveryPath);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            _log.LogWarning(ex, "The recovery record could not be written.");
            return false;
        }
    }

    private void Fault(string part, string words, Exception? ex = null)
    {
        _faults.Add(part, words);
        _log.LogWarning(ex, "{Part}: {Words}", part, words);
        if (_engine is { } engine)
        {
            engine.Journal.Add("fault", words, ok: false);
            engine.FactsChanged();
        }
    }

    /// <summary>Undoes <see cref="StartAsync"/> in reverse. First the engine stops taking lines: from here on every line
    /// from any source — a door, a link, a binding, OSC, the assistant, an approval — is refused with the words why, so
    /// no line source that closes later starts work on a part closed before it. Then the network host closes the links,
    /// the clock sync, the hello, the web port and the wire; the extensions stop in reverse order; the render host stops;
    /// the recovery record is written; the engine stops and its journal is flushed; the scan backend, the media backend
    /// and the certificate go last. Every part is closed even when one throws.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _phase, 3) == 3)
            return;
        await _stop.CancelAsync();
        _engine?.BeginStop();
        await Quietly("the recovery writer", () => _recovery);
        await Quietly("the network", async () =>
        {
            if (_network is not null)
                await _network.DisposeAsync();
        });
        for (var i = _extensions.Count - 1; i >= 0; i--)
        {
            var extension = _extensions[i];
            await Quietly(extension.Name, () => extension.DisposeAsync().AsTask());
        }
        await Quietly("the render host", async () =>
        {
            if (_render is not null)
                await _render.DisposeAsync();
        });
        if (_engine is { } engine)
        {
            await Quietly("the recovery record", async () =>
            {
                if (await WriteRecoveryAsync(engine))
                    engine.Journal.Add("node", $"{engine.Services.Settings.Name} stopped; the recovery record holds the show");
            });
            await Quietly("the engine", async () => await _engine.DisposeAsync());
        }
        switch (_scan)
        {
            case IAsyncDisposable a:
                await Quietly("the scanner", () => a.DisposeAsync().AsTask());
                break;
            case IDisposable d:
                await Quietly("the scanner", () =>
                {
                    d.Dispose();
                    return Task.CompletedTask;
                });
                break;
        }
        await Quietly("the media backend", async () =>
        {
            if (_media is not null)
                await _media.DisposeAsync();
        });
        _tls?.Dispose();
        _stop.Dispose();
        _log.LogInformation("The node host has stopped{Render}.", _renderStarted ? "" : " (it ran no render host)");
    }

    private async Task Quietly(string what, Func<Task> close)
    {
        try
        {
            await close();
        }
        catch (Exception ex)
        {
            // Shutdown goes on: the next part is closed whatever this one did.
            _log.LogWarning(ex, "Closing {What} faulted.", what);
        }
    }

    /// <summary>The faults of the host's own parts beside the network's.</summary>
    private sealed class HostFaults : INodeFaults
    {
        private readonly ConcurrentDictionary<string, NodeFault> _own = new(StringComparer.Ordinal);
        private ImmutableArray<INodeFaults> _sources = [];

        public NetworkHost? Network { get; set; }

        public void Add(string part, string words) => _own[part] = new NodeFault(part, words);

        /// <summary>An extension's faults, listed beside the host's own from now on.</summary>
        public void AddSource(INodeFaults source)
        {
            ArgumentNullException.ThrowIfNull(source);
            ImmutableInterlocked.Update(ref _sources, s => s.Add(source));
        }

        public IReadOnlyList<NodeFault> Faults() =>
            (Network?.Faults() ?? []).Concat(_own.Values).Concat(_sources.SelectMany(s => s.Faults()))
                .OrderBy(f => f.Part, StringComparer.Ordinal).ToList();
    }
}

/// <summary>The scan display of a node without a render host: every frame is refused with the reason, so a scan says
/// why it cannot run instead of waiting for a present that never comes.</summary>
public sealed class NoScanDisplay : IScanDisplay
{
    public static readonly NoScanDisplay Instance = new();

    private NoScanDisplay()
    {
    }

    public Task<long> ShowAsync(string outputId, ReadOnlyMemory<byte> gray8, int width, int height, CancellationToken ct) =>
        Task.FromException<long>(new InvalidOperationException("this node has no render host, so it cannot show a scan frame on " + outputId));

    public void Release()
    {
        // Nothing was taken from the show.
    }
}
