using Microsoft.Extensions.Logging;
using PlayMap.Core.Fleet;
using PlayMap.Engine.Host;
using PlayMap.Net.Fleet;

namespace PlayMap.Engine.Fleet;

/// <summary>What the fleet's part is made with: the clock its pushes and liveness run on, the machine's counters, and the transport to
/// the peers' health (the defaults are the machine's own and the network's; a test injects its own).</summary>
public sealed record FleetOptions
{
    public TimeProvider Time { get; init; } = TimeProvider.System;

    public IMachineReader? Reader { get; init; }

    /// <summary>The transport to the peers' health; null = the WebSocket client over the fleet's own pin file.</summary>
    public Func<FleetPinFile, IHealthFeeds>? Feeds { get; init; }

    /// <summary>The most <c>/health</c> sockets this node feeds at once.</summary>
    public int HealthFeeds { get; init; } = HealthWebModule.MaxFeeds;

    /// <summary>How the hot backup paces itself.</summary>
    public BackupOptions Backup { get; init; } = new();
}

/// <summary>wp2-fleet's part of the node: the machine sampler (its own one-second timer), the watch on this machine's own lights, the
/// fleet monitor over the hello's peers (when the node's settings keep it on), the hub HEALTH is published through, <c>/health</c> and
/// <c>/fleet</c> on the web port, the fleet's preflight rows, and the peers' lights in STATE and the context. Everything it holds is let
/// go when the node stops.</summary>
public sealed class FleetExtension : IEngineExtension
{
    /// <summary>The fleet's pins, beside the node's own in its keys folder (fingerprints are public).</summary>
    public const string PinFileName = "fleet-pins.json";

    private readonly FleetOptions _options;
    private readonly HealthHub _hub = new();
    private readonly MachineSampler _sampler;
    private readonly FleetMonitor? _monitor;
    private readonly string _pinPath;
    private readonly BackupNode _backup;
    private volatile EngineCore? _engine;

    public FleetExtension(ExtensionSetup setup, FleetOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(setup);
        _options = options ?? new FleetOptions();
        var settings = setup.Settings;
        _sampler = new MachineSampler(_options.Reader ?? MachineSampler.ForThisMachine(setup.Home.Folder), _options.Time);
        _pinPath = Path.Combine(setup.Home.KeysFolder, PinFileName);
        var log = setup.Logs.CreateLogger("PlayMap.Net.Fleet");
        var pins = new FleetPinFile(_pinPath, log);
        _backup = new BackupNode(setup, pins.Current, _options.Backup);
        if (settings.Fleet.Monitor)
        {
            _monitor = new FleetMonitor(new FleetMonitorOptions
            {
                SelfId = settings.NodeId,
                Table = () => setup.Network.Hello?.Peers,
                Feeds = _options.Feeds?.Invoke(pins) ?? new HealthFeedClient(pins.Current, _options.Time, log),
                Time = _options.Time,
                Max = Math.Clamp(settings.Fleet.MonitorMax, 0, 32),
                Log = log,
            });
        }
        setup.Network.AddWebModule(new HealthWebModule(_hub, () => _engine is { } e ? HealthJson.Write(e.HealthNow()) : "{}", _options.HealthFeeds));
        setup.Network.AddWebModule(new FleetWebModule(() => _engine is { } e ? e.FleetBoardJson() : "{}"));
        Mirror = new MirrorWebModule(() => _engine is { } e ? Swap(e.ProjectJson()) : (-1, "{}"), setup.Media.PathForHash, () => setup.Wall.UnixMs);
        setup.Network.AddWebModule(Mirror);
        _backup.Web = Mirror;
    }

    /// <summary>The pulls a backup makes of this node's show and files (<c>/mirror</c>).</summary>
    public MirrorWebModule Mirror { get; }

    public string Name => "fleet";

    private static (long Rev, string Json) Swap((string Json, long Revision) p) => (p.Revision, p.Json);

    public EngineServices Configure(EngineServices services)
    {
        ArgumentNullException.ThrowIfNull(services);
        return services with
        {
            Machine = _sampler,
            FleetPeers = _monitor,
            HealthHub = _hub,
            FleetTime = _options.Time,
            Network = _monitor is null ? services.Network : new FleetNetworkView(services.Network, _monitor),
            Backup = _backup,
            OutputGains = Fence(services.OutputGains),
            PreflightChecks = [.. services.PreflightChecks, new FleetPreflight(() => _pinPath), new BackupPreflight()],
        };
    }

    /// <summary>The fence's gain, with any degrade gain an earlier part supplied multiplied in.</summary>
    private FenceGains Fence(IOutputGains? inner)
    {
        _backup.Gains.Inner = inner;
        return _backup.Gains;
    }

    public Task StartAsync(EngineCore engine, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(engine);
        _engine = engine;
        _sampler.Start();
        engine.Fleet.Start();
        _backup.Attach(engine);
        if (_monitor is { } monitor)
        {
            // The hello starts with the network, after the extensions: the monitor takes its peer table on the first fact after.
            engine.StateChanged += _ => monitor.Attach();
            monitor.Start();
        }
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _backup.DisposeAsync();
        if (_engine is { } engine)
            await engine.Fleet.StopAsync();
        if (_monitor is not null)
            await _monitor.DisposeAsync();
        _sampler.Dispose();
    }
}
