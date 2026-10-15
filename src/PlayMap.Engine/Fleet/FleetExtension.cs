using Microsoft.Extensions.Logging;
using PlayMap.Core.Fleet;
using PlayMap.Engine.Host;
using PlayMap.Net.Fleet;
using PlayMap.Net.Tls;

namespace PlayMap.Engine.Fleet;

/// <summary>What the fleet's part is made with: the clock its pushes and liveness run on, the machine's counters, and the transport to
/// the peers' health (the defaults are the machine's own and the network's; a test injects its own).</summary>
public sealed record FleetOptions
{
    public TimeProvider Time { get; init; } = TimeProvider.System;

    public IMachineReader? Reader { get; init; }

    /// <summary>The transport to the peers' health; null = the WebSocket client over the fleet's own pin file.</summary>
    public Func<PinStore, IHealthFeeds>? Feeds { get; init; }
}

/// <summary>wp2-fleet's part of the node: the machine sampler (its own one-second timer), the fleet monitor over the hello's peers (when
/// the node's settings keep it on), the hub HEALTH is published through, <c>/health</c> and <c>/fleet</c> on the web port, the fleet's
/// preflight rows, and the peers' lights in STATE and the context. Everything it holds is let go when the node stops.</summary>
public sealed class FleetExtension : IEngineExtension
{
    /// <summary>The fleet's pins, beside the node's own in its keys folder (fingerprints are public).</summary>
    public const string PinFileName = "fleet-pins.json";

    private readonly FleetOptions _options;
    private readonly HealthHub _hub = new();
    private readonly MachineSampler _sampler;
    private readonly FleetMonitor? _monitor;
    private readonly string _pinPath;
    private volatile EngineCore? _engine;

    public FleetExtension(ExtensionSetup setup, FleetOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(setup);
        _options = options ?? new FleetOptions();
        var settings = setup.Settings;
        _sampler = new MachineSampler(_options.Reader ?? MachineSampler.ForThisMachine(setup.Home.Folder), _options.Time);
        _pinPath = Path.Combine(setup.Home.KeysFolder, PinFileName);
        if (settings.Fleet.Monitor)
        {
            var log = setup.Logs.CreateLogger("PlayMap.Net.Fleet");
            var pins = new PinStore(_pinPath, log);
            _monitor = new FleetMonitor(new FleetMonitorOptions
            {
                SelfId = settings.NodeId,
                Table = () => setup.Network.Hello?.Peers,
                Feeds = _options.Feeds?.Invoke(pins) ?? new HealthFeedClient(pins, _options.Time, log),
                Time = _options.Time,
                Max = Math.Clamp(settings.Fleet.MonitorMax, 0, 32),
                Log = log,
            });
        }
        setup.Network.AddWebModule(new HealthWebModule(_hub, () => _engine is { } e ? HealthJson.Write(e.HealthNow()) : "{}"));
        setup.Network.AddWebModule(new FleetWebModule(() => _engine is { } e ? e.FleetBoardJson() : "{}"));
    }

    public string Name => "fleet";

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
            PreflightChecks = [.. services.PreflightChecks, new FleetPreflight(() => _pinPath)],
        };
    }

    public Task StartAsync(EngineCore engine, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(engine);
        _engine = engine;
        _sampler.Start();
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
        if (_monitor is not null)
            await _monitor.DisposeAsync();
        _sampler.Dispose();
    }
}
