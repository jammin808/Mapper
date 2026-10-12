using System.Globalization;
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
public sealed class MainViewModel : ObservableObject, IAsyncDisposable
{
    private readonly ConnectionManager _connections;
    private Workspace _workspace = Workspace.Play;
    private bool _isOutputsOpen;
    private bool _isNetworkOpen;
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

    public MainViewModel(DeskSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        Session = session;
        _connections = new ConnectionManager(session);
        _connections.Note += text => session.Notify(ToastKind.Info, text);
        Master = new TransportViewModel(session, () => session.State.FocusedPlaylist, master: true);
        Blackout = DeskAction.Send(session, "blackout", "Blackout", () => _isBlackout ? "BLACKOUT OFF" : "BLACKOUT ON",
            () => _isBlackout ? "Back on air" : "Blackout", isChecked: () => _isBlackout, isEnabled: () => session.IsLive)
            .Look("blackout", "Ctrl+B", danger: true);
        Undo = DeskAction.Send(session, "undo", "Undo", () => "UNDO", isEnabled: () => session.IsLive).Look("undo", "Ctrl+Z");
        Redo = DeskAction.Send(session, "redo", "Redo", () => "REDO", isEnabled: () => session.IsLive).Look("redo", "Ctrl+Y");
        Save = DeskAction.Send(session, "save", "Save", () => "PROJECT SAVE", isEnabled: () => session.IsLive).Look("save", "Ctrl+S");
        ShowPlay = new RelayCommand(() => Workspace = Workspace.Play);
        ShowMap = new RelayCommand(() => Workspace = Workspace.Map);
        ShowScan = new RelayCommand(() => Workspace = Workspace.Scan);
        ToggleOutputs = new RelayCommand(() => IsOutputsOpen = !IsOutputsOpen);
        ToggleNetwork = new RelayCommand(() => IsNetworkOpen = !IsNetworkOpen);
        ToggleHelp = new RelayCommand(() => IsHelpOpen = !IsHelpOpen);
        CloseOverlays = new RelayCommand(() =>
        {
            IsHelpOpen = false;
            IsOutputsOpen = false;
            IsNetworkOpen = false;
        });
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
        Shortcuts = ShortcutMap.Build(this);
        session.StateUpdated += OnState;
        session.ProjectUpdated += OnProject;
        session.StatusUpdated += OnStatus;
        if (session.Project is { } p)
            OnProject(p);
        OnState(session.State);
        OnStatus(session.Status);
    }

    public DeskSession Session { get; }

    public TransportViewModel Master { get; }

    public DeskAction Blackout { get; }

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
                Raise(nameof(IsPlay), nameof(IsMap), nameof(IsScan));
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

    public bool IsOutputsOpen
    {
        get => _isOutputsOpen;
        set
        {
            if (Set(ref _isOutputsOpen, value) && value)
            {
                IsNetworkOpen = false;
                Outputs.Opened();
            }
        }
    }

    public bool IsNetworkOpen
    {
        get => _isNetworkOpen;
        set
        {
            if (Set(ref _isNetworkOpen, value) && value)
                IsOutputsOpen = false;
        }
    }

    public bool IsHelpOpen
    {
        get => _isHelpOpen;
        set => Set(ref _isHelpOpen, value);
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

    /// <summary>Moves the counters between pushes (the window's 4 Hz tick).</summary>
    public void Tick()
    {
        if (Master.IsRunning)
            Master.Advance();
        Play.Tick();
    }

    private void OnStatus(LinkStatus status)
    {
        Raise(nameof(IsConnected));
        new[] { Blackout, Undo, Redo, Save }.RefreshAll();
        UpdateNetworkPill(Session.State);
        if (status == LinkStatus.Live)
            Connect.IsOpen = false;
    }

    private void OnProject(Project project)
    {
        Play.OnProject(project);
        Map.OnProject(project);
        Scan.OnProject(project);
        Outputs.OnProject(project);
    }

    private void OnState(StateSnapshot s)
    {
        if (Set(ref _isBlackout, s.Air.Blackout, nameof(IsBlackout)))
            Blackout.Refresh();
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
        var sync = s.Sync;
        NetworkTip = string.Create(CultureInfo.InvariantCulture,
            $"Engine: {Session.LinkLabel} ({linkWord})\nPatterns: {s.Patterns.State}{(s.Patterns.Host.Length > 0 ? " · " + s.Patterns.Host : "")}\n"
            + $"Stagewatch: {s.Stagewatch.State}{(s.Stagewatch.Host.Length > 0 ? " · " + s.Stagewatch.Host : "")}\n"
            + $"Sync: {sync.Role}{(sync.Role == "follower" ? $" · offset {sync.OffsetMs:0.00} ms" : "")}\n{s.Peers.Length} peer(s) on the Show Bus");
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
