using System.Runtime.CompilerServices;
using PlayMap.Assistant;
using PlayMap.Core.Time;
using PlayMap.Devices.Model;
using PlayMap.Engine.Assistant;
using PlayMap.Engine.Devices;
using PlayMap.Engine.Host;
using PlayMap.Engine.Scanning;

namespace PlayMap.Engine;

/// <summary>How the operations parts are made in a host (tests turn the PJLink socket off, hand in a camera or a
/// model).</summary>
public sealed record OpsPartsOptions
{
    public ScanRunnerOptions Scan { get; init; } = new();

    public DeviceServiceOptions Devices { get; init; } = new();

    public AssistantServiceOptions Assistant { get; init; } = new();

    /// <summary>The assistant's settings; null reads them from the node's folder (<see cref="AssistantSettingsFile"/>).</summary>
    public AssistantSettings? AssistantSettings { get; init; }
}

/// <summary>
/// The operations side's parts as the host composes them (<see cref="EngineParts"/>; the exe's are plugged in by
/// EngineParts.Ops.cs):
/// <list type="bullet">
/// <item>the scan backend — a <see cref="ScanRunner"/> over the host's scan display (the render host, or the display
/// that refuses with the reason);</item>
/// <item>"devices" — projector control (<see cref="EngineServices.Devices"/>);</item>
/// <item>"assistant" — the node's assistant (<see cref="EngineServices.Assistant"/>, its waiting proposals as
/// <see cref="EngineServices.Proposals"/>), whose queue becomes the host's approvals before any door opens, so an
/// MCP assistant's high-risk line waits on this node's list for a person;</item>
/// <item>"scan" — binds the runner to the engine (its project, its lane, the node's camera and scan views).</item>
/// </list>
/// The devices and the assistant share one credential store per node, in its keys folder. A part that fails is left
/// out by the host with its words in STATE's faults; the others run. They stop in reverse: a running scan first, while
/// the projectors it drives are still reachable; then the assistant leaves the approvals; then the projector links
/// close. The scan runner itself is the host's to dispose, after the engine.
/// </summary>
public static class OpsParts
{
    /// <summary>The scan backend factory: a <see cref="ScanRunner"/> on the request's display.</summary>
    public static Func<ScanBackendRequest, IScanBackend> ScanBackend(OpsPartsOptions? options = null)
    {
        var scan = (options ?? new OpsPartsOptions()).Scan;
        return request => new ScanRunner(request.Display, scan);
    }

    /// <summary>The extension factories, in the order they attach: devices, assistant, scan.</summary>
    public static IReadOnlyList<EngineExtensionFactory> Extensions(OpsPartsOptions? options = null)
    {
        var o = options ?? new OpsPartsOptions();
        // One store per node (the setup is one per host): the passwords and the key in one file, one cache.
        var stores = new ConditionalWeakTable<ExtensionSetup, NodeCredentialStore>();
        NodeCredentialStore Store(ExtensionSetup setup) => stores.GetValue(setup, s => new NodeCredentialStore(s.Home.KeysFolder));
        return
        [
            setup => new DevicesExtension(Store(setup), setup.Mono, o.Devices),
            setup => new AssistantExtension(setup, Store(setup), o),
            _ => new ScanExtension(),
        ];
    }

    /// <summary><paramref name="parts"/> with the operations parts: the scan backend and the extensions after its own.</summary>
    public static EngineParts With(EngineParts parts, OpsPartsOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(parts);
        return parts with { ScanBackend = ScanBackend(options), Extensions = [.. parts.Extensions, .. Extensions(options)] };
    }

    private sealed class DevicesExtension(ICredentialStore credentials, IMonoClock mono, DeviceServiceOptions options) : IEngineExtension
    {
        private readonly DeviceService _service = new(credentials, mono, options);
        private IAsyncDisposable? _attached;

        public string Name => "devices";

        public EngineServices Configure(EngineServices services) => services with { Devices = _service };

        public Task StartAsync(EngineCore engine, CancellationToken ct)
        {
            _attached = _service.Attach(engine);
            return Task.CompletedTask;
        }

        public async ValueTask DisposeAsync()
        {
            if (_attached is not null)
                await _attached.DisposeAsync().ConfigureAwait(false);
            await _service.DisposeAsync().ConfigureAwait(false);
        }
    }

    private sealed class AssistantExtension(ExtensionSetup setup, ICredentialStore credentials, OpsPartsOptions options) : IEngineExtension
    {
        private AssistantService? _service;
        private IAsyncDisposable? _attached;

        public string Name => "assistant";

        public EngineServices Configure(EngineServices services)
        {
            // Read here: a settings file that does not read is this part's fault, and the node runs without it.
            var settings = options.AssistantSettings ?? AssistantSettingsFile.Load(setup.Home);
            // ASSISTANT SET keeps what it changes in the node's folder, beside the node's own settings.
            var home = setup.Home;
            _service = new AssistantService(credentials, settings, options.Assistant with
            {
                Save = options.Assistant.Save ?? (s => AssistantSettingsFile.Save(home, s)),
                Log = options.Assistant.Log ?? setup.Logs.CreateLogger("PlayMap.Assistant"),
            });
            return services with { Assistant = _service, Proposals = _service };
        }

        public Task StartAsync(EngineCore engine, CancellationToken ct)
        {
            var service = _service ?? throw new InvalidOperationException("the assistant was not configured");
            _attached = service.Attach(engine);
            // Before the doors open: from the first MCP call, a high-risk line waits on this node's list.
            setup.Approvals.Use(service.Approvals);
            return Task.CompletedTask;
        }

        public async ValueTask DisposeAsync()
        {
            setup.Approvals.Use(null);
            if (_attached is not null)
                await _attached.DisposeAsync().ConfigureAwait(false);
            if (_service is not null)
                await _service.DisposeAsync().ConfigureAwait(false);
        }
    }

    private sealed class ScanExtension : IEngineExtension
    {
        private ScanRunner? _runner;

        public string Name => "scan";

        public Task StartAsync(EngineCore engine, CancellationToken ct)
        {
            if (engine.Services.Scan is ScanRunner runner)
            {
                runner.Bind(engine);
                _runner = runner;
            }
            return Task.CompletedTask;
        }

        /// <summary>The scan running now stops (its outputs back on the show, its cameras let go); the host disposes
        /// the runner after the engine.</summary>
        public async ValueTask DisposeAsync()
        {
            if (_runner is not null)
                await _runner.StopAsync().ConfigureAwait(false);
        }
    }
}
