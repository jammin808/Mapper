using System.Globalization;
using PlayMap.App.Controls;
using PlayMap.App.Infrastructure;
using PlayMap.App.Link;
using PlayMap.App.State;
using PlayMap.Core.Model;
using PlayMap.Core.Wire;

namespace PlayMap.App.ViewModels;

public enum Workspace
{
    Play,
    Map,
    Scan,
}

/// <summary>The desk's window: the top bar (project, workspaces, the master transport, BLACKOUT, the link
/// and assistant pills, undo/redo), the three workspaces, the Outputs and Network drawers, the help overlay
/// and the Connect choice. Everything it shows is rebuilt from the session's mirror; everything it does is
/// a verb through the session.</summary>
public sealed partial class MainViewModel : ObservableObject, IAsyncDisposable, Controls.IMenuExtras, IInspectorExtras, Controls.IBadgeSource
{
    private readonly ConnectionManager _connections;
    private Workspace _workspace = Workspace.Play;
    private bool _isHelpOpen;
    private bool _isBlackout;
    private string _projectName = "—";
    private string _saveText = "";
    private bool _isDirty;
    private string _networkText = "Not connected";
    private string _networkLight = "grey";
    private string _networkTip = "";
    private string _aiText = "AI";
    private string _aiTip = "";
    private bool _aiAttention;
    private string _vtChip = "";
    private string _focusedName = "";
    private readonly Dictionary<string, string> _views = new(StringComparer.Ordinal);

    /// <summary>The link the shown views were asked of (<see cref="DeskSession.LinkEpoch"/>): another link is asked for every view afresh.</summary>
    private long _viewsEpoch;

    public MainViewModel(DeskSession session, DeskPrefs? prefs = null, Func<long>? nowMs = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        Session = session;
        Prefs = prefs ?? DeskPrefs.InMemory();
        Escape = new EscapeLadder(nowMs ?? EscapeLadder.MonotonicMs(System.Diagnostics.Stopwatch.GetTimestamp()));
        _connections = new ConnectionManager(session);
        // The supervisor speaks from its own worker; the toast goes up on the UI thread.
        _connections.Note += text => session.OnUi(() => session.Notify(ToastKind.Info, text));
        Master = new TransportViewModel(session, () => session.State.FocusedPlaylist, master: true);
        // The air's own controls send every press at once, never waiting for the reply before, and on a stale link too.
        Blackout = DeskAction.Send(session, "blackout", "Blackout", () => _isBlackout ? "BLACKOUT OFF" : "BLACKOUT ON",
            () => _isBlackout ? "Back on air" : "Blackout", isChecked: () => _isBlackout, isEnabled: () => session.CanSend, everyPress: true)
            .Look("blackout", "Ctrl+B", danger: true);
        var master = DeskAction.Local("master", "Master", () => { }).Look("blackout", menuOnly: true, separator: true);
        foreach (var level in new[] { 100, 75, 50, 0 })
            master.Children.Add(DeskAction.Send(session, "master." + level.ToString(CultureInfo.InvariantCulture), level.ToString(CultureInfo.InvariantCulture) + " % over 2 s",
                () => "MASTER " + level.ToString(CultureInfo.InvariantCulture) + " FADE 2000", isEnabled: () => session.CanSend, everyPress: true));
        BlackoutMenu = [Blackout, master];
        Undo = DeskAction.Send(session, "undo", "Undo", () => "UNDO", isEnabled: () => session.IsLive).Look("undo", "Ctrl+Z");
        Redo = DeskAction.Send(session, "redo", "Redo", () => "REDO", isEnabled: () => session.IsLive).Look("redo", "Ctrl+Y");
        Save = DeskAction.Send(session, "save", "Save", () => "PROJECT SAVE", isEnabled: () => session.IsLive).Look("save", "Ctrl+S");
        ShowPlay = new RelayCommand(() => Workspace = Workspace.Play);
        ShowMap = new RelayCommand(() => Workspace = Workspace.Map);
        ShowScan = new RelayCommand(() => Workspace = Workspace.Scan);
        ToggleOutputs = new RelayCommand(() => Drawers.Toggle("outputs"));
        ToggleNetwork = new RelayCommand(() => Drawers.Toggle("network"));
        ToggleInputs = new RelayCommand(() => Drawers.Toggle("inputs"));
        ToggleHealth = new RelayCommand(() => Drawers.Toggle("health"));
        ToggleHelp = new RelayCommand(() => IsHelpOpen = !IsHelpOpen);
        TogglePro = new RelayCommand(() => Prefs.IsPro = !Prefs.IsPro);
        ToggleBanners = new RelayCommand(() => IsBannersExpanded = !IsBannersExpanded);
        CloseOverlays = new RelayCommand(() =>
        {
            IsHelpOpen = false;
            Drawers.Open = null;
        });
        EscapeCommand = new RelayCommand(() => Escape.Escape(IsTyping));
        DismissToast = new RelayCommand(t =>
        {
            if (t is Toast toast)
                session.Dismiss(toast);
        });
        Play = new PlayViewModel(session);
        Map = new MapViewModel(session);
        Scan = new ScanWizardViewModel(session);
        Outputs = new OutputsDrawerViewModel(session);
        Network = new NetworkDrawerViewModel(session, _connections);
        Connect = new ConnectViewModel(_connections);
        ModuleViews.Register<OutputsDrawerViewModel>(() => new Views.OutputsDrawer());
        ModuleViews.Register<NetworkDrawerViewModel>(() => new Views.NetworkDrawer());
        Drawers.Add(new DrawerTab("outputs", "plan", "Plan", Outputs, 0));
        Drawers.Add(new DrawerTab("network", "links", "Links", Network, 0));
        Drawers.Opened += d =>
        {
            // A drawer closed however it closed (Esc, its button, its pill): Esc right after it does not reach PANIC.
            if (d is null)
                Escape.Closed();
            Raise(nameof(IsOutputsOpen), nameof(IsNetworkOpen), nameof(IsInputsOpen), nameof(IsHealthOpen));
            if (d == Drawers.Outputs && d.Selected?.Content == Outputs)
                Outputs.Opened();
        };
        ComposeSpine(Modules);
        ComposeShowDesk(Modules);
        ComposeContentDesk(Modules);
        ComposeMapDesk(Modules);
        ShowSlot = Modules.Select(m => m.Slot("show")).FirstOrDefault(v => v is not null);
        TimecodeSlot = Modules.Select(m => m.Slot("timecode")).FirstOrDefault(v => v is not null);
        TempoSlot = Modules.Select(m => m.Slot("tempo")).FirstOrDefault(v => v is not null);
        foreach (var m in Modules)
        {
            foreach (var tab in m.DrawerTabs())
                Drawers.Add(tab);
            if (m.EscapeOrder != int.MaxValue)
                Escape.Add(new EscapeRung(m.EscapeOrder, m.GetType().Name, m.TryEscape));
        }
        Escape.Add(new EscapeRung(EscapeLadder.PatternTask, "pattern task", CancelPatternTask));
        Escape.Add(new EscapeRung(EscapeLadder.Selection, "selection", () =>
        {
            if (Workspace != Workspace.Map || Map.Inspector.Current is null)
                return false;
            Map.Select("", null);
            return true;
        }));
        Escape.Add(new EscapeRung(EscapeLadder.Draft, "draft", () =>
        {
            if (Workspace != Workspace.Map || !Map.Pen.IsDrawing)
                return false;
            Map.Pen.Cancel();
            return true;
        }));
        Escape.Add(new EscapeRung(EscapeLadder.Overlays, "overlays", () =>
        {
            if (!IsHelpOpen && !Drawers.IsOpen)
                return false;
            CloseOverlays.Execute(null);
            return true;
        }));
        Session.Gate = GateLine;
        Session.VerbsUpdated += RefreshEverything;
        Shortcuts = ShortcutMap.Build(this, Modules.SelectMany(m => m.Shortcuts()));
        foreach (var host in new[] { Play.Inspector, Map.Inspector, Outputs.Inspector })
            host.Use(this);
        Map.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(MapViewModel.ViewId) or nameof(MapViewModel.IsProjectorView) or nameof(MapViewModel.EditorSize))
                UpdateViews();
        };
        session.StateUpdated += OnState;
        session.ProjectUpdated += OnProject;
        session.StatusUpdated += OnStatus;
        if (session.Project is { } p)
            OnProject(p);
        OnState(session.State);
        OnStatus(session.Status);
    }

    public DeskSession Session { get; }

    /// <summary>The round-2 packages' parts of the desk, in composition order.</summary>
    public List<DeskModule> Modules { get; } = [];

    /// <summary>The desk's own preferences (Simple or Pro, Esc is panic).</summary>
    public DeskPrefs Prefs { get; }

    /// <summary>True while a text field has the focus (the window sets it before each key): Esc is then the field's, and only the
    /// overlays answer it too.</summary>
    public bool IsTyping { get; set; }

    /// <summary>The first rung: a scan, an align run or a calibration task putting patterns on the wall is cancelled — the
    /// safe direction, the wall goes back to the show. A cancel the gate would refuse (armed, the node's policy holds it) is not
    /// sent: Esc goes on down the ladder.</summary>
    private bool CancelPatternTask()
    {
        var s = Session.State;
        string? line = null;
        if (s.Scan is { IsRunning: true })
            line = "SCAN CANCEL";
        else if (Running(s, "align", "run", PlayMap.Core.Calibration.PatternTasks.AlignRunning))
            line = "ALIGN CANCEL";
        else if (Running(s, "calibration", "task", PlayMap.Core.Calibration.PatternTasks.CalibrationRunning))
            line = "CALIBRATION CANCEL";
        if (line is null || Session.GateWords(line) is not null)
            return false;
        Session.Fire(line);
        return true;
    }

    /// <summary>STATE.&lt;section&gt;.&lt;task&gt;.state is a state the task runs in.</summary>
    private static bool Running(StateSnapshot s, string section, string task, Func<string?, bool> running) =>
        s.Root.ValueKind == System.Text.Json.JsonValueKind.Object && s.Root.TryGetProperty(section, out var sec)
        && sec.ValueKind == System.Text.Json.JsonValueKind.Object && sec.TryGetProperty(task, out var t)
        && t.ValueKind == System.Text.Json.JsonValueKind.Object && t.TryGetProperty("state", out var st)
        && st.ValueKind == System.Text.Json.JsonValueKind.String && running(st.GetString());

    /// <summary>The Esc ladder (Esc's one row in the key table runs it).</summary>
    public EscapeLadder Escape { get; }

    public RelayCommand EscapeCommand { get; }

    /// <summary>The four right-hand drawers.</summary>
    public DrawerHostViewModel Drawers { get; } = new();

    /// <summary>The top bar's show-strip slot (the show package's view model; null without it).</summary>
    public object? ShowSlot { get; }

    /// <summary>The top bar's timecode slot (null without a package that fills it).</summary>
    public object? TimecodeSlot { get; }

    /// <summary>The top bar's tempo slot (null without a package that fills it).</summary>
    public object? TempoSlot { get; }

    /// <summary>The attention bar: what needs a person now, worst first.</summary>
    public System.Collections.ObjectModel.ObservableCollection<Banner> Banners { get; } = [];

    /// <summary>The banners the bar shows: the two worst, or all of them when expanded.</summary>
    public System.Collections.ObjectModel.ObservableCollection<Banner> ShownBanners { get; } = [];

    private bool _isBannersExpanded;

    public bool IsBannersExpanded
    {
        get => _isBannersExpanded;
        set
        {
            if (Set(ref _isBannersExpanded, value))
                SyncShownBanners();
        }
    }

    /// <summary>"+3 more" while banners are folded away; "" otherwise.</summary>
    public string MoreBannersText => Banners.Count > 2 && !_isBannersExpanded
        ? "+" + (Banners.Count - 2).ToString(CultureInfo.InvariantCulture) + " more"
        : Banners.Count > 2 ? "fewer" : "";

    public bool HasMoreBanners => Banners.Count > 2;

    public RelayCommand ToggleBanners { get; }

    public RelayCommand ToggleInputs { get; }

    public RelayCommand ToggleHealth { get; }

    public RelayCommand TogglePro { get; }

    /// <summary>The mode frame round the window: "red" armed, "amber" when anything but the show is on a wall, "" in
    /// rehearsal with only the show on the wall.</summary>
    public string Frame
    {
        get => _frame;
        private set
        {
            if (Set(ref _frame, value))
                Raise(nameof(FrameThickness), nameof(HasFrame));
        }
    }

    private string _frame = "";

    public bool HasFrame => _frame.Length > 0;

    public Avalonia.Thickness FrameThickness => _frame switch
    {
        "red" => new Avalonia.Thickness(4),
        "amber" => new Avalonia.Thickness(3),
        _ => new Avalonia.Thickness(0),
    };

    // One composition hook per desk package; a package implements its own in its own file, and a hook nobody
    // implements compiles away. The spine's own modules (the generic cards, the generated entries) come first.
    partial void ComposeSpine(List<DeskModule> modules);

    partial void ComposeShowDesk(List<DeskModule> modules);

    partial void ComposeContentDesk(List<DeskModule> modules);

    partial void ComposeMapDesk(List<DeskModule> modules);

    public TransportViewModel Master { get; }

    public DeskAction Blackout { get; }

    /// <summary>Blackout's right-click menu: itself, then Master ▸ (the master level over two seconds).</summary>
    public IReadOnlyList<DeskAction> BlackoutMenu { get; }

    public DeskAction Undo { get; }

    public DeskAction Redo { get; }

    public DeskAction Save { get; }

    public RelayCommand ShowPlay { get; }

    public RelayCommand ShowMap { get; }

    public RelayCommand ShowScan { get; }

    public RelayCommand ToggleOutputs { get; }

    public RelayCommand ToggleNetwork { get; }

    public RelayCommand ToggleHelp { get; }

    public RelayCommand CloseOverlays { get; }

    public bool IsInputsOpen
    {
        get => Drawers.Open == Drawers.Inputs;
        set => Drawers.Open = value ? Drawers.Inputs : Drawers.Open == Drawers.Inputs ? null : Drawers.Open;
    }

    public bool IsHealthOpen
    {
        get => Drawers.Open == Drawers.Health;
        set => Drawers.Open = value ? Drawers.Health : Drawers.Open == Drawers.Health ? null : Drawers.Open;
    }

    public RelayCommand DismissToast { get; }

    public PlayViewModel Play { get; }

    public MapViewModel Map { get; }

    public ScanWizardViewModel Scan { get; }

    public OutputsDrawerViewModel Outputs { get; }

    public NetworkDrawerViewModel Network { get; }

    public ConnectViewModel Connect { get; }

    public ShortcutMap Shortcuts { get; }

    public Workspace Workspace
    {
        get => _workspace;
        set
        {
            if (Set(ref _workspace, value))
            {
                Raise(nameof(IsPlay), nameof(IsMap), nameof(IsScan));
                UpdateViews();
            }
        }
    }

    public bool IsPlay
    {
        get => _workspace == Workspace.Play;
        set
        {
            if (value)
                Workspace = Workspace.Play;
            else
                Raise();
        }
    }

    public bool IsMap
    {
        get => _workspace == Workspace.Map;
        set
        {
            if (value)
                Workspace = Workspace.Map;
            else
                Raise();
        }
    }

    public bool IsScan
    {
        get => _workspace == Workspace.Scan;
        set
        {
            if (value)
                Workspace = Workspace.Scan;
            else
                Raise();
        }
    }

    /// <summary>The Outputs drawer is open (at its Plan tab when opened this way).</summary>
    public bool IsOutputsOpen
    {
        get => Drawers.Open == Drawers.Outputs;
        set
        {
            if (value)
                Drawers.Show("outputs", "plan");
            else if (Drawers.Open == Drawers.Outputs)
                Drawers.Open = null;
        }
    }

    /// <summary>The Network drawer is open (at its Links tab when opened this way).</summary>
    public bool IsNetworkOpen
    {
        get => Drawers.Open == Drawers.Network;
        set
        {
            if (value)
                Drawers.Show("network", "links");
            else if (Drawers.Open == Drawers.Network)
                Drawers.Open = null;
        }
    }

    public bool IsHelpOpen
    {
        get => _isHelpOpen;
        set
        {
            if (Set(ref _isHelpOpen, value) && !value)
                Escape.Closed();
        }
    }

    /// <summary>STATE.air.blackout — the button follows the engine, never its own press (a press sends the
    /// verb; the next STATE says whether the air went black).</summary>
    public bool IsBlackout => _isBlackout;

    public string ProjectName
    {
        get => _projectName;
        private set => Set(ref _projectName, value);
    }

    public string SaveText
    {
        get => _saveText;
        private set => Set(ref _saveText, value);
    }

    public bool IsDirty
    {
        get => _isDirty;
        private set => Set(ref _isDirty, value);
    }

    public string NetworkText
    {
        get => _networkText;
        private set => Set(ref _networkText, value);
    }

    public string NetworkLight
    {
        get => _networkLight;
        private set => Set(ref _networkLight, value);
    }

    public string NetworkTip
    {
        get => _networkTip;
        private set => Set(ref _networkTip, value);
    }

    /// <summary>The Patterns link's light (grey when not linked).</summary>
    public string PatternsLight
    {
        get => _patternsLight;
        private set => Set(ref _patternsLight, value);
    }

    /// <summary>The Stagewatch link's light (grey when not linked).</summary>
    public string StagewatchLight
    {
        get => _stagewatchLight;
        private set => Set(ref _stagewatchLight, value);
    }

    /// <summary>"0.21 ms" while following a sync leader, "lead" while leading, "" alone.</summary>
    public string SyncText
    {
        get => _syncText;
        private set => Set(ref _syncText, value);
    }

    private string _patternsLight = "grey";
    private string _stagewatchLight = "grey";
    private string _syncText = "";

    private static string LinkLight(string state) => state switch
    {
        "live" => "green",
        "stale" or "connecting" => "amber",
        "down" or "failed" => "red",
        _ => "grey",
    };

    public string AiText
    {
        get => _aiText;
        private set => Set(ref _aiText, value);
    }

    public string AiTip
    {
        get => _aiTip;
        private set => Set(ref _aiTip, value);
    }

    public bool AiAttention
    {
        get => _aiAttention;
        private set => Set(ref _aiAttention, value);
    }

    /// <summary>Patterns' VT chip for the focused playlist ("Show 2:28").</summary>
    public string VtChip
    {
        get => _vtChip;
        private set => Set(ref _vtChip, value);
    }

    public string FocusedName
    {
        get => _focusedName;
        private set => Set(ref _focusedName, value);
    }

    public bool IsConnected => Session.Link is not null;

    /// <summary>Starts with what the command line asked for (or opens the Connect choice).</summary>
    public void Start(DeskOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        switch (options.Choice)
        {
            case ConnectChoice.Demo:
                Connect.Choice = ConnectChoice.Demo;
                Connect.ConnectCommand.Execute(null);
                break;
            case ConnectChoice.OwnEngine:
                Connect.Choice = ConnectChoice.OwnEngine;
                Connect.ConnectCommand.Execute(null);
                break;
            case ConnectChoice.Network:
                Connect.Choice = ConnectChoice.Network;
                Connect.Host = options.Host ?? "";
                Connect.Token = options.Token ?? "";
                Connect.ConnectCommand.Execute(null);
                break;
            default:
                Connect.IsOpen = true;
                break;
        }
    }

    /// <summary>Attaches a link the caller owns (tests, the designer): the window works on it at once.</summary>
    public void Attach(IEngineLink link, string label)
    {
        Session.Attach(link, label);
        Connect.IsOpen = false;
        Raise(nameof(IsConnected));
    }

    /// <summary>The desk is closing: stop watching (the engine keeps playing).</summary>
    public void BeginShutdown() => _ = DisposeAsync().AsTask();

    /// <summary>Ticks between two renewals of the shown views' leases: 5 s at the window's 4 Hz (the node lets a view
    /// go after 15 s without one).</summary>
    public const int KeepViewsEveryTicks = 20;

    private int _ticksSinceKeep;

    /// <summary>Moves the counters between pushes (the window's 4 Hz tick) and keeps the shown views alive.</summary>
    public void Tick()
    {
        if (Master.IsRunning)
            Master.Advance();
        Play.Tick();
        TickModules();
        if (++_ticksSinceKeep >= KeepViewsEveryTicks)
        {
            _ticksSinceKeep = 0;
            KeepViews();
        }
    }

    /// <summary>Renews the lease of every view the desk shows (VIEW &lt;view&gt; KEEP), quietly; a view the node let go
    /// meanwhile (it restarted, the lease ran out) is asked for again.</summary>
    private void KeepViews()
    {
        if (!Session.IsLive)
            return;
        foreach (var (view, what) in _views.ToList())
            _ = KeepAsync(view, what);
    }

    private async Task KeepAsync(string view, string what)
    {
        var epoch = Session.LinkEpoch;
        var reply = await Session.SendQuietAsync($"VIEW {view} KEEP");
        if (reply is { Ok: false } && Session.LinkEpoch == epoch && _views.TryGetValue(view, out var still) && still == what)
            _ = ShowViewAsync(view, what);
    }

    /// <summary>Asks the node for a view (VIEW &lt;view&gt; SHOW &lt;what&gt;) and takes the ring its reply names, or its words why not,
    /// while the view is still wanted as asked of the same link: a later SHOW or HIDE of it, or another link, supersedes this reply.</summary>
    private async Task ShowViewAsync(string view, string what)
    {
        var epoch = Session.LinkEpoch;
        var reply = await Session.SendQuietAsync($"VIEW {view} SHOW {what}");
        if (Session.LinkEpoch == epoch && _views.TryGetValue(view, out var still) && still == what)
            Session.Previews.Shown(view, reply);
    }

    private void OnStatus(LinkStatus status)
    {
        Raise(nameof(IsConnected));
        // What a control may send moves with the link (PANIC stays live on a stale link, an edit does not): every action re-reads.
        foreach (var m in Modules)
            m.RefreshActions();
        RefreshEverything();
        UpdateNetworkPill(Session.State);
        if (status == LinkStatus.Live)
            Connect.IsOpen = false;
    }

    private void OnProject(Project project)
    {
        // The modules take the project first; the gate's facts then move, and every action asks the new gate.
        foreach (var m in Modules)
            m.OnProject(project);
        Session.GateMoved();
        foreach (var m in Modules)
            m.RefreshActions();
        Play.OnProject(project);
        Map.OnProject(project);
        Scan.OnProject(project);
        Outputs.OnProject(project);
        RefreshEverything();
    }

    /// <summary>The gate every action asks: the modules' words for a line (the first that refuses), or null.</summary>
    private GateRefusal? GateLine(string text)
    {
        if (!WireGrammar.TryParse(text, out var line, out _) || VerbCatalog.Match(line) is not { } match)
            return null;
        foreach (var m in Modules)
        {
            if (m.Gate(line, match.Spec) is { } why)
                return why;
        }
        return null;
    }

    /// <summary>After STATE or the verb list moved: the gate's words on every control, the badges, the inspectors' extras.</summary>
    private void RefreshEverything()
    {
        new[] { Blackout, Undo, Redo, Save }.RefreshAll();
        BlackoutMenu.RefreshAll();
        Master.Actions.RefreshAll();
        foreach (var host in new[] { Play.Inspector, Map.Inspector, Outputs.Inspector })
            host.RefreshExtras();
        BadgesChanged?.Invoke();
    }

    private void SyncShownBanners()
    {
        var shown = _isBannersExpanded ? Banners.ToList() : Banners.Take(2).ToList();
        if (!shown.SequenceEqual(ShownBanners))
        {
            ShownBanners.Clear();
            foreach (var b in shown)
                ShownBanners.Add(b);
        }
        Raise(nameof(MoreBannersText), nameof(HasMoreBanners));
    }

    /// <summary>Red while armed; amber while anything but the show is on a wall (a pattern, identify, on-wall editing, a
    /// running scan); none otherwise.</summary>
    private static string FrameOf(StateSnapshot s)
    {
        var root = s.Root;
        if (root.ValueKind == System.Text.Json.JsonValueKind.Object && root.TryGetProperty("show", out var show)
            && show.ValueKind == System.Text.Json.JsonValueKind.Object && show.TryGetProperty("armed", out var armed)
            && armed.ValueKind == System.Text.Json.JsonValueKind.True)
            return "red";
        var overlay = root.ValueKind == System.Text.Json.JsonValueKind.Object && root.TryGetProperty("air", out var air)
            && air.ValueKind == System.Text.Json.JsonValueKind.Object && air.TryGetProperty("overlay", out var o)
            && o.ValueKind == System.Text.Json.JsonValueKind.Object && o.TryGetProperty("on", out var on) && on.ValueKind == System.Text.Json.JsonValueKind.True;
        if (s.Air.Pattern is not null || s.Air.Identify || overlay || s.Scan is { IsRunning: true } || s.Outputs.Any(x => x.Pattern is not null))
            return "amber";
        return "";
    }

    public event Action? BadgesChanged;

    public IReadOnlyList<DeskAction> ExtrasFor(IMenuTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return [.. Modules.SelectMany(m => m.MenuExtras(target))];
    }

    public IReadOnlyList<object> SectionsFor(IMenuTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return [.. Modules.SelectMany(m => m.InspectorSections(target))];
    }

    public IReadOnlyList<ObjectBadge> BadgesFor(IMenuTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return [.. Modules.SelectMany(m => m.Badges(target))];
    }

    private double _barWidth = 1600;

    /// <summary>The top bar's width (the window sets it as it resizes): below 1366 px the bar wraps into two rows, and
    /// the compact forms come in as it narrows further.</summary>
    public double BarWidth
    {
        get => _barWidth;
        set
        {
            if (Set(ref _barWidth, value))
                Raise(nameof(IsTwoRow), nameof(ShowPositionColumn), nameof(ShowPillWords), nameof(ShowProToggle));
        }
    }

    /// <summary>Below 1366 px: identity, workspaces and the drawer buttons on the first row; transport and the show
    /// cluster on the second.</summary>
    public bool IsTwoRow => _barWidth < 1366;

    /// <summary>The transport's position column and the VT chip (dropped first as the bar narrows).</summary>
    public bool ShowPositionColumn => _barWidth >= 1280;

    /// <summary>Drawer pills show their words (they become icons below 1280 px).</summary>
    public bool ShowPillWords => _barWidth >= 1280;

    /// <summary>Simple|Pro sits in the bar (it moves into the project menu below 1200 px).</summary>
    public bool ShowProToggle => _barWidth >= 1200;

    private void TickModules()
    {
        foreach (var m in Modules)
            m.Tick();
    }

    private void OnState(StateSnapshot s)
    {
        // Two passes: every module takes the push (the gate's facts among them), the gate's facts move, then every module re-reads
        // its actions, so none asks the gate before a later module has taken its facts from this push.
        foreach (var m in Modules)
            m.OnState(s);
        Session.GateMoved();
        foreach (var m in Modules)
            m.RefreshActions();
        Set(ref _isBlackout, s.Air.Blackout, nameof(IsBlackout));
        Blackout.Refresh();
        ProjectName = s.Project.Name.Length > 0 ? s.Project.Name : Session.Project?.Name ?? "—";
        IsDirty = s.Project.Dirty;
        SaveText = s.Project.Dirty ? "Unsaved changes" : s.Project.Path.Length > 0 ? "Saved" : "";
        var focused = s.FocusedPlaylist;
        FocusedName = focused?.Name ?? "No playlist";
        VtChip = s.Video?.Chip ?? (focused is null ? "" : focused.Name + " idle");
        Master.Update();
        UpdateNetworkPill(s);
        var ai = s.Ai;
        AiAttention = ai.Proposals > 0;
        AiText = ai.Proposals > 0 ? "AI · " + ai.Proposals.ToString(CultureInfo.InvariantCulture) : "AI";
        AiTip = !ai.Mcp
            ? "The assistant door (MCP) is closed on this node."
            : string.Create(CultureInfo.InvariantCulture,
                $"An assistant can see and drive this node through MCP at /mcp on port 9940.\n{ai.Clients} connected · {ai.Proposals} proposal(s) waiting for a human — high-risk verbs are never pre-approved.");
        new[] { Undo, Redo, Save }.RefreshAll();
        Play.OnState(s);
        Map.OnState(s);
        Scan.OnState(s);
        Outputs.OnState(s);
        Network.OnState(s);
        var banners = Modules.SelectMany(m => m.Banners(s)).OrderBy(b => b.Priority).ThenBy(b => b.Id, StringComparer.Ordinal).ToList();
        if (!banners.SequenceEqual(Banners))
        {
            Banners.Clear();
            foreach (var b in banners)
                Banners.Add(b);
            SyncShownBanners();
        }
        foreach (var b in Banners)
            b.Actions.RefreshAll();
        Frame = FrameOf(s);
        RefreshEverything();
        UpdateViews();
    }

    /// <summary>Asks the engine for the preview views the current workspace shows and lets go of the rest
    /// (VIEW &lt;view&gt; SHOW &lt;what&gt; &lt;w&gt;x&lt;h&gt; / VIEW &lt;view&gt; HIDE), quietly: a node without
    /// previews still plays, and the views draw their own stand-in with its words. Each view reads the ring the node names for it
    /// (<see cref="DeskSession.Previews"/>).</summary>
    private void UpdateViews()
    {
        if (!Session.IsLive)
            return;
        // Views asked of another link are not shown on this one: every view is asked of it afresh.
        if (_viewsEpoch != Session.LinkEpoch)
        {
            _views.Clear();
            _viewsEpoch = Session.LinkEpoch;
        }
        var want = new Dictionary<string, string>(StringComparer.Ordinal);
        switch (_workspace)
        {
            case Workspace.Play:
                foreach (var p in Play.Previews)
                    want[p.ViewId] = $"output:{p.Id} 480x270";
                break;
            case Workspace.Map when Map.IsProjectorView && Map.ViewProjector is { } pj:
                want[Map.ViewId] = $"projector:{pj.Id} {pj.Intrinsics.Width.ToString(CultureInfo.InvariantCulture)}x{pj.Intrinsics.Height.ToString(CultureInfo.InvariantCulture)}";
                break;
            case Workspace.Map:
                want["editor"] = "editor " + Map.EditorSize;
                break;
            case Workspace.Scan:
                if (Scan.Camera is { } cam)
                    want["scan-camera"] = $"camera:{cam.Id} 960x540";
                if (Scan.ScanId is { } id && Scan.IsDone)
                    want[Scan.ReviewViewId] = $"scan:{id}:{(Scan.ShowConfidence ? "confidence" : "smart")} 1280x720";
                break;
        }
        foreach (var view in _views.Keys.Where(v => !want.ContainsKey(v)).ToList())
        {
            _views.Remove(view);
            Session.Previews.Hidden(view);
            _ = Session.SendQuietAsync($"VIEW {view} HIDE");
        }
        foreach (var (view, what) in want)
        {
            if (_views.TryGetValue(view, out var had) && had == what)
                continue;
            _views[view] = what;
            _ = ShowViewAsync(view, what);
        }
    }

    private void UpdateNetworkPill(StateSnapshot s)
    {
        var status = Session.Status;
        NetworkLight = status switch
        {
            LinkStatus.Live => s.Peers.Any(p => p.Light == "red") ? "red" : s.Light,
            LinkStatus.Connecting or LinkStatus.Stale => "amber",
            _ => "red",
        };
        var linkWord = status switch
        {
            LinkStatus.Live => "live",
            LinkStatus.Connecting => "connecting",
            LinkStatus.Stale => "stale",
            _ => "down",
        };
        NetworkText = Session.Link is null ? "Not connected" : $"{Session.LinkLabel} · {linkWord}";
        PatternsLight = LinkLight(s.Patterns.State);
        StagewatchLight = LinkLight(s.Stagewatch.State);
        var sync = s.Sync;
        SyncText = sync.Role switch
        {
            "follower" => sync.UncertaintyText,
            "leader" => "lead",
            _ => "",
        };
        NetworkTip = string.Create(CultureInfo.InvariantCulture,
            $"Engine: {Session.LinkLabel} ({linkWord})\nPatterns: {s.Patterns.State}{(s.Patterns.Host.Length > 0 ? " · " + s.Patterns.Host : "")}\n"
            + $"Stagewatch: {s.Stagewatch.State}{(s.Stagewatch.Host.Length > 0 ? " · " + s.Stagewatch.Host : "")}\n"
            + $"Sync: {sync.Role}{(sync.Role == "follower" ? $" · leader time {sync.UncertaintyText} (half the round trip)" : "")}\n{s.Peers.Length} peer(s) on the Show Bus");
    }

    public async ValueTask DisposeAsync()
    {
        Session.StateUpdated -= OnState;
        Session.ProjectUpdated -= OnProject;
        Session.StatusUpdated -= OnStatus;
        Map.Dispose();
        await _connections.DisposeAsync();
        await Session.DisposeAsync();
    }
}
