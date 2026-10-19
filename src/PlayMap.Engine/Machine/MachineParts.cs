using PlayMap.Core.Machine;
using PlayMap.Core.Model;
using PlayMap.Core.Time;
using PlayMap.Engine.Host;
using PlayMap.Platform.Windows;

namespace PlayMap.Engine.Machine;

/// <summary>wp3-platform's part of the node: the machine platform (Windows' on Windows, elsewhere the null platform with the words
/// <see cref="NotWindowsWords"/>), read back through <see cref="RecordedPlatform"/>; the process's claims made once in Configure,
/// before any part starts a thread; this machine's settings (kept in the node home by the network host, the settings file's one
/// writer); the machine preflight; the GC's show mode as an arm hook, with the collector's pauses counted; and keep-awake held while the
/// project has an enabled display output and AWAKE is ON.</summary>
internal sealed class MachineParts(ExtensionSetup? setup) : IEngineExtension
{
    /// <summary>The words of the platform on any node but Windows.</summary>
    public const string NotWindowsWords = "the platform seam is Windows only";

    /// <summary>The factory the exe's composition holds (one instance, so a test can find it there).</summary>
    public static readonly EngineExtensionFactory Factory = s => new MachineParts(s);

    private readonly Lock _gate = new();
    private MachineNode? _node;
    private RecordedPlatform? _platform;
    private EngineCore? _engine;
    private IDisposable? _awake;
    private GcPauses? _pauses;

    public string Name => "machine";

    public EngineServices Configure(EngineServices services)
    {
        ArgumentNullException.ThrowIfNull(services);
        // The node home's machine settings as the file holds them (read only: the network host is the file's one writer).
        var settings = setup is null ? MachineSettings.Default : (setup.Home.ReadSettings() ?? setup.Settings).Machine;
        _node = setup is null ? null : new MachineNode(settings, s => setup.Network.KeepSettings(n => n with { Machine = s }));
        var node = _node;
        // Deadline waits are on the machine's monotonic clock, as every caller's deadline is, whatever clock the node's show runs on.
        IMachinePlatform inner = OperatingSystem.IsWindows()
            ? WindowsPlatformProject.Create(MonoClock.Instance, () => services.Wall.UnixMs, () => node?.Runtimes ?? RuntimeIdentity.Empty)
            : new NullMachinePlatform(NotWindowsWords, MonoClock.Instance);
        _platform = new RecordedPlatform(inner, MonoClock.Instance, () => node?.Runtimes ?? RuntimeIdentity.Empty);
        _ = _platform.ClaimProcess(settings);
        // The identity and the capabilities are read at start, so no reader of STATE waits on DXGI, NVAPI or the registry.
        _ = _platform.Identity();
        _ = _platform.Capabilities();
        return services with
        {
            MachinePlatform = _platform,
            MachineNode = _node,
            PreflightChecks = services.PreflightChecks.Add(new MachinePreflight()),
            ArmHooks = services.ArmHooks.Add(new GcShowHook(() => node?.Saved ?? MachineSettings.Default, node)),
        };
    }

    public Task StartAsync(EngineCore engine, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(engine);
        _engine = engine;
        foreach (var c in _platform?.Claims ?? [])
            if (!c.Applied && c.Asked.Length > 0)
                engine.Journal.Add("machine", $"{c.What} {c.Asked} was refused: {c.Words}", ok: false);
        if (_node is not null)
        {
            _pauses = new GcPauses();
            _node.CountPauses(_pauses);
        }
        engine.ProjectChanged += OnProject;
        if (_node is not null)
            _node.Changed += OnSettings;
        Hold();
        return Task.CompletedTask;
    }

    private void OnProject(long revision) => Hold();

    private void OnSettings(MachineSettings settings) => Hold();

    /// <summary>Holds keep-awake while an enabled display output is in the project and AWAKE is ON; lets it go otherwise.</summary>
    private void Hold()
    {
        if (_engine is not { } engine || _platform is not { } platform)
            return;
        var want = (_node?.Saved ?? MachineSettings.Default).Awake == "on" && engine.Store.Project.Outputs.Any(o => o.Enabled && o.Kind == OutputKind.Display);
        IDisposable? release = null;
        lock (_gate)
        {
            if (want && _awake is null)
                _awake = platform.KeepAwake();
            else if (!want && _awake is not null)
                (release, _awake) = (_awake, null);
        }
        release?.Dispose();
        if (_node is { } node && node.AwakeHeld != want)
        {
            node.AwakeHeld = want;
            engine.FactsChanged();
        }
    }

    public ValueTask DisposeAsync()
    {
        if (_engine is not null)
            _engine.ProjectChanged -= OnProject;
        if (_node is not null)
            _node.Changed -= OnSettings;
        IDisposable? awake;
        lock (_gate)
            (awake, _awake) = (_awake, null);
        awake?.Dispose();
        _node?.CountPauses(null);
        _pauses?.Dispose();
        _platform?.Dispose();
        return ValueTask.CompletedTask;
    }
}
