using System.Collections.Immutable;
using System.Globalization;
using System.Net;
using PlayMap.Core.Model;
using PlayMap.Core.Time;
using PlayMap.Devices.Commands;
using PlayMap.Devices.Drivers;
using PlayMap.Devices.Drivers.PjLink;
using PlayMap.Devices.Manager;
using PlayMap.Devices.Model;
using PlayMap.Devices.Workflows;

namespace PlayMap.Engine.Devices;

/// <summary>How the device service runs.</summary>
public sealed record DeviceServiceOptions
{
    /// <summary>The PJLink UDP port shared by discovery and notifications (4352 in a venue; 0 = any free port, for
    /// tests; −1 = none: no search, no notifications).</summary>
    public int PjLinkUdpPort { get; init; } = PjLinkProtocol.Port;

    public DriverTimings Timings { get; init; } = DriverTimings.Default;

    public DeviceManagerOptions Manager { get; init; } = new();

    /// <summary>How long a shutter takes to settle (no vendor documents a mechanical shutter's time).</summary>
    public ShutterTiming Shutter { get; init; } = ShutterTiming.Default;

    /// <summary>How long the PJLink search collects answers.</summary>
    public TimeSpan DiscoveryWindow { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>The PJLink TCP port the discovery identifies on (4352; tests use their own).</summary>
    public int DiscoveryPort { get; init; } = PjLinkProtocol.Port;

    /// <summary>Drivers for links (null: the drivers PlayMap ships). Tests hand in their own.</summary>
    public IProjectorDriverFactory? Drivers { get; init; }
}

/// <summary>The last thing a link was told and how far it got (STATE shows it beside the projector's facts).</summary>
public sealed record DeviceCommandView(string Command, CommandStage Stage, string Words, long AtUnixMs);

/// <summary>
/// Projector control for the engine: owns the <see cref="DeviceManager"/> (the one writer of device state), the
/// shared PJLink UDP socket (bound once, handed to discovery and to the manager's notifications), the discovery and
/// the credential store the drivers read their secrets from. Attached to an engine, it keeps the manager's links in
/// step with the project's <see cref="Stage.Devices"/>, moves STATE when a projector's facts change and journals
/// what each projector did. Built before the engine (it rides in <see cref="EngineServices.Devices"/>), attached
/// after.
/// </summary>
public sealed class DeviceService : IAsyncDisposable
{
    private readonly DeviceServiceOptions _options;
    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _sync = new(1, 1);
    private readonly Dictionary<string, DeviceCommandView> _last = new(StringComparer.Ordinal);
    private ImmutableArray<ProjectorLink> _links = [];
    private EngineCore? _engine;
    private PjLinkUdp? _udp;
    private string _udpProblem = "";

    public DeviceService(ICredentialStore credentials, IMonoClock clock, DeviceServiceOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentNullException.ThrowIfNull(clock);
        Credentials = credentials;
        _options = options ?? new DeviceServiceOptions();
        var log = DeviceLog.To(line => Journal("device", line, true));
        Manager = new DeviceManager(_options.Drivers ?? new ProjectorDrivers(credentials, log, _options.Timings), clock, log, _options.Manager);
        Discovery = new ProjectorDiscovery(credentials, log);
        Manager.StateChanged += (_, _) => _engine?.FactsChanged();
    }

    public ICredentialStore Credentials { get; }

    public DeviceManager Manager { get; }

    public ProjectorDiscovery Discovery { get; }

    /// <summary>The shared PJLink UDP socket once bound (null before <see cref="Attach"/> or when the port was taken).</summary>
    public PjLinkUdp? Udp => _udp;

    /// <summary>Why the PJLink UDP socket is not bound ("" when it is, or when none was asked for).</summary>
    public string UdpProblem => _udpProblem;

    /// <summary>What a link's projector reports now (null for a link the manager does not have yet).</summary>
    public DeviceState? State(string linkId) => Manager.State(linkId);

    /// <summary>The last command sent to a link and its outcome, or null.</summary>
    public DeviceCommandView? LastCommand(string linkId)
    {
        lock (_gate)
            return _last.GetValueOrDefault(linkId);
    }

    /// <summary>The project's link as the drivers take it, or the reason it cannot be one.</summary>
    public static (ProjectorLink? Link, string Problem) ToProjectorLink(DeviceLink link)
    {
        ArgumentNullException.ThrowIfNull(link);
        if (ProjectorProtocols.Parse(link.Protocol) is not { } protocol)
            return (null, $"'{link.Protocol}' is not a protocol PlayMap speaks — pjlink, christie, panasonic, barco-pulse, sony-adcp, epson or nec");
        if (string.IsNullOrWhiteSpace(link.Host))
            return (null, "a link needs the projector's host");
        if (link.Port is < 0 or > 65535)
            return (null, "a port is 1 to 65535 (0 = the protocol's own)");
        return (new ProjectorLink(link.Id, link.Name, protocol, link.Host.Trim(), link.Port, link.CredentialRef, link.ProjectorId, link.OutputId,
            link.PollIntervalMs, link.AllowUnverified), "");
    }

    /// <summary>Attaches to an engine: the PJLink socket bound, the manager polling, its links the project's, STATE
    /// moving with the projectors' facts. Disposing the result detaches (the service itself stays usable).</summary>
    public IAsyncDisposable Attach(EngineCore engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        lock (_gate)
        {
            if (_engine is not null)
                throw new InvalidOperationException("the device service is attached to an engine already");
            _engine = engine;
        }
        BindUdp();
        Manager.Start();
        void OnProject(long revision) => _ = SyncLinksAsync();
        engine.ProjectChanged += OnProject;
        _ = SyncLinksAsync();
        return new Detach(() =>
        {
            engine.ProjectChanged -= OnProject;
            lock (_gate)
                _engine = null;
        });
    }

    private void BindUdp()
    {
        if (_options.PjLinkUdpPort < 0 || _udp is not null)
            return;
        try
        {
            var udp = new PjLinkUdp(_options.PjLinkUdpPort);
            _udp = udp;
            Manager.AttachNotifications(udp);
        }
        catch (System.Net.Sockets.SocketException ex)
        {
            // Another controller (or another PlayMap) holds 4352: no search and no notifications, polling still works.
            _udpProblem = $"PJLink UDP {_options.PjLinkUdpPort.ToString(CultureInfo.InvariantCulture)} is taken ({ex.SocketErrorCode}): no search and no notifications; polling still works";
            Journal("device", _udpProblem, false);
        }
    }

    /// <summary>Makes the manager's links the project's (latest wins; a link that cannot be one is journalled and
    /// left out).</summary>
    public async Task SyncLinksAsync()
    {
        EngineCore? engine;
        lock (_gate)
            engine = _engine;
        if (engine is null)
            return;
        await _sync.WaitAsync().ConfigureAwait(false);
        try
        {
            var links = ImmutableArray.CreateBuilder<ProjectorLink>();
            foreach (var d in engine.Store.Project.Stage.Devices)
            {
                var (link, problem) = ToProjectorLink(d);
                if (link is not null)
                    links.Add(link);
                else
                    Journal("device", $"{d.Name}: {problem}", false);
            }
            var next = links.ToImmutable();
            if (next.SequenceEqual(_links))
                return;
            _links = next;
            await Manager.SetLinksAsync(next).ConfigureAwait(false);
            engine.FactsChanged();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A link change that fails (a driver that will not close) is the journal's to say; the next edit tries again.
            _links = [];
            Journal("device", "the projector links could not be updated — " + ex.Message, false);
        }
        finally
        {
            _sync.Release();
        }
    }

    /// <summary>One command through the link's gate; the outcome is remembered for STATE and journalled.</summary>
    public async Task<CommandResult> RunAsync(string linkId, string words, Func<IProjectorDriver, CancellationToken, Task<CommandResult>> command, CancellationToken ct)
    {
        await SyncLinksAsync().ConfigureAwait(false);
        var result = await Manager.RunAsync(linkId, command, ct).ConfigureAwait(false);
        Remember(linkId, words, result);
        return result;
    }

    /// <summary>The prepare-for-scan plan for a link (null when the manager does not have the link).</summary>
    public WorkflowPlan? PreparePlan(string linkId) =>
        Manager.Driver(linkId) is { } d ? MappingWorkflows.PrepareForScan(d, _options.Shutter) : null;

    /// <summary>Readies a projector for a scan: shutter open, freeze and patterns off, its own geometry and blend off,
    /// the lens read and recorded; instructions for what its protocol lacks.</summary>
    public async Task<WorkflowOutcome> PrepareForScanAsync(string linkId, CancellationToken ct)
    {
        await SyncLinksAsync().ConfigureAwait(false);
        var driver = Manager.Driver(linkId) ?? throw new InvalidOperationException($"no projector link {linkId}");
        var outcome = await new WorkflowRunner().RunAsync(Manager, MappingWorkflows.PrepareForScan(driver, _options.Shutter), null, ct).ConfigureAwait(false);
        Remember(linkId, "prepare for scan", Outcome("prepare for scan", outcome));
        return outcome;
    }

    /// <summary>A black frame: the shutter closed and settled, <paramref name="capture"/>, the shutter open again
    /// (video black without a shutter).</summary>
    public async Task<WorkflowOutcome> BlackFrameAsync(string linkId, Func<CancellationToken, Task>? capture, CancellationToken ct)
    {
        await SyncLinksAsync().ConfigureAwait(false);
        var driver = Manager.Driver(linkId) ?? throw new InvalidOperationException($"no projector link {linkId}");
        var outcome = await new WorkflowRunner().RunAsync(Manager, MappingWorkflows.BlackFrame(driver, _options.Shutter), capture, ct).ConfigureAwait(false);
        Remember(linkId, "black frame", Outcome("black frame", outcome));
        return outcome;
    }

    /// <summary>The projector's own identify pattern for <paramref name="hold"/>, then patterns off.</summary>
    public async Task<WorkflowOutcome> IdentifyAsync(string linkId, TimeSpan hold, CancellationToken ct)
    {
        await SyncLinksAsync().ConfigureAwait(false);
        var driver = Manager.Driver(linkId) ?? throw new InvalidOperationException($"no projector link {linkId}");
        var outcome = await new WorkflowRunner().RunAsync(Manager, MappingWorkflows.Identify(driver, hold), null, ct).ConfigureAwait(false);
        Remember(linkId, "identify", Outcome("identify", outcome));
        return outcome;
    }

    /// <summary>Searches the network: the PJLink class 2 search on the shared socket (and a /24 sweep around an
    /// address when asked), each answer identified over PJLink.</summary>
    public async Task<IReadOnlyList<DiscoveredProjector>> DiscoverAsync(IPAddress? sweepAround, CancellationToken ct)
    {
        BindUdp();
        var udp = _udp ?? throw new InvalidOperationException(_udpProblem.Length > 0 ? _udpProblem : "no PJLink UDP socket on this node");
        var options = new DiscoveryOptions
        {
            Window = _options.DiscoveryWindow,
            SweepSubnet = sweepAround is not null,
            SweepAround = sweepAround,
            PjLinkPort = _options.DiscoveryPort,
            Timings = _options.Timings,
        };
        return await Discovery.DiscoverAsync(udp, options, ct).ConfigureAwait(false);
    }

    /// <summary>A workflow's outcome in a few words: what ran and how far it got, then the operator's instructions.</summary>
    public static string Summary(WorkflowOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        var ran = outcome.Steps.Where(s => s.Step.Kind != StepKind.Instruction)
            .Select(s => s.Step.Words + ": " + CommandResult.StageWord(s.Result.Stage)).ToList();
        var told = outcome.Plan.Instructions;
        var parts = new List<string>();
        if (ran.Count > 0)
            parts.Add(string.Join(", ", ran));
        if (told.Count > 0)
            parts.Add("by hand: " + string.Join(" ", told));
        return parts.Count == 0 ? "nothing to do" : string.Join("; ", parts);
    }

    private static CommandResult Outcome(string command, WorkflowOutcome outcome) =>
        outcome.AllSucceeded
            ? CommandResult.Accepted(command, Summary(outcome))
            : new CommandResult(command, outcome.Steps.Where(s => s.Step.Kind != StepKind.Instruction).Select(s => s.Result.Stage).DefaultIfEmpty(CommandStage.Refused).Min(), Summary(outcome));

    private void Remember(string linkId, string words, CommandResult result)
    {
        EngineCore? engine;
        lock (_gate)
        {
            engine = _engine;
            _last[linkId] = new DeviceCommandView(words, result.Stage, result.Words, engine?.Services.Wall.UnixMs ?? 0);
        }
        engine?.FactsChanged();
    }

    private void Journal(string kind, string text, bool ok)
    {
        EngineCore? engine;
        lock (_gate)
            engine = _engine;
        engine?.Journal.Add(kind, text, ok: ok);
    }

    public async ValueTask DisposeAsync()
    {
        lock (_gate)
            _engine = null;
        await Manager.DisposeAsync().ConfigureAwait(false);
        if (_udp is not null)
            await _udp.DisposeAsync().ConfigureAwait(false);
        _sync.Dispose();
    }

    private sealed class Detach(Action detach) : IAsyncDisposable
    {
        private int _done;

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0)
                detach();
            return ValueTask.CompletedTask;
        }
    }
}
