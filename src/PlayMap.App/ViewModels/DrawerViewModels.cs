using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using PlayMap.App.Infrastructure;
using PlayMap.App.Link;
using PlayMap.App.State;
using PlayMap.Core.Content;
using PlayMap.Core.Model;

namespace PlayMap.App.ViewModels;

/// <summary>An output as a rectangle on the multi-screen plan.</summary>
public sealed class PlanTile : ObservableObject
{
    private string _name = "";
    private RectD _plan;
    private string _color = "grey";
    private bool _enabled;
    private bool _isSelected;
    private string _light = "grey";
    private string _detail = "";

    public PlanTile(string id) => Id = id;

    public string Id { get; }

    public string Name
    {
        get => _name;
        set => Set(ref _name, value);
    }

    /// <summary>Where the output sits on the plan, in plan pixels (the engine's value, or the drag's).</summary>
    public RectD Plan
    {
        get => _plan;
        set => Set(ref _plan, value);
    }

    public string Color
    {
        get => _color;
        set => Set(ref _color, value);
    }

    public bool Enabled
    {
        get => _enabled;
        set => Set(ref _enabled, value);
    }

    public bool IsSelected
    {
        get => _isSelected;
        set => Set(ref _isSelected, value);
    }

    public string Light
    {
        get => _light;
        set => Set(ref _light, value);
    }

    public string Detail
    {
        get => _detail;
        set => Set(ref _detail, value);
    }

    /// <summary>True while the operator drags it (pushes do not move it under the pointer).</summary>
    public bool Dragging { get; set; }
}

/// <summary>A display this machine sees (from DISPLAYS).</summary>
public sealed record DisplayInfo(string Key, string Name, int Width, int Height, double RefreshHz, bool Primary)
{
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Key} · {Name} · {Width}×{Height} @ {RefreshHz:0} Hz");
}

/// <summary>The OUTPUTS drawer: the multi-screen plan (drag outputs where they sit), the selected output's
/// inspector, identify, a test pattern per output, and a 2D blend-grid helper that lays out a row of
/// overlapping projectors on a canvas in one go.</summary>
public sealed class OutputsDrawerViewModel : ObservableObject
{
    private readonly DeskSession _session;
    private Project? _project;
    private PlanTile? _selected;
    private int _gridColumns = 2;
    private int _gridRows = 1;
    private double _gridOverlap = 240;
    private IReadOnlyList<DisplayInfo> _displays = [];

    public OutputsDrawerViewModel(DeskSession session)
    {
        _session = session;
        Inspector = new InspectorHost(session);
        Add = DeskAction.Send(session, "add", "Add output", () => "ADD output " + NewOutputJson()).Look("plus");
        IdentifyAll = DeskAction.Send(session, "identify", "Identify all", () => "OUTPUT IDENTIFY ALL").Look("identify");
        PatternsOff = DeskAction.Send(session, "patternsOff", "Patterns off", () => "PATTERN OFF").Look("close");
        ApplyGrid = DeskAction.Run(session, "grid", "Lay out the blend grid", ApplyGridAsync, () => "SET <output> sourceRect · blend.edges · plan … for each").Look("warp");
        RefreshDisplays = new AsyncCommand(LoadDisplaysAsync);
        foreach (var k in GeneratorCatalog.All.Where(k => k.Family == "pattern"))
            Patterns.Add(DeskAction.Send(session, "p." + k.Id, k.Name, () => _selected is { } t ? $"PATTERN {k.Id[8..]} ON {t.Id}" : null));
        Patterns.Add(DeskAction.Send(session, "p.off", "Off", () => _selected is { } t ? $"PATTERN OFF ON {t.Id}" : null).Look(separator: true));
    }

    public InspectorHost Inspector { get; }

    public ObservableCollection<PlanTile> Tiles { get; } = [];

    /// <summary>Test patterns for the selected output.</summary>
    public ObservableCollection<DeskAction> Patterns { get; } = [];

    public DeskAction Add { get; }

    public DeskAction IdentifyAll { get; }

    public DeskAction PatternsOff { get; }

    public DeskAction ApplyGrid { get; }

    public AsyncCommand RefreshDisplays { get; }

    public IReadOnlyList<DisplayInfo> Displays
    {
        get => _displays;
        private set => Set(ref _displays, value);
    }

    public PlanTile? Selected
    {
        get => _selected;
        set
        {
            if (!Set(ref _selected, value))
                return;
            foreach (var t in Tiles)
                t.IsSelected = t == value;
            Inspector.Show(value is null ? null : new OutputInspector(_session, value.Id));
            Patterns.RefreshAll();
        }
    }

    public int GridColumns
    {
        get => _gridColumns;
        set
        {
            if (Set(ref _gridColumns, Math.Clamp(value, 1, 8)))
                Raise(nameof(GridSentence));
        }
    }

    public int GridRows
    {
        get => _gridRows;
        set
        {
            if (Set(ref _gridRows, Math.Clamp(value, 1, 6)))
                Raise(nameof(GridSentence));
        }
    }

    public double GridOverlap
    {
        get => _gridOverlap;
        set
        {
            if (Set(ref _gridOverlap, Math.Round(Math.Clamp(value, 0, 1000))))
                Raise(nameof(GridSentence));
        }
    }

    /// <summary>The number boxes' view of the grid (they speak decimal).</summary>
    public decimal? GridColumnsValue
    {
        get => _gridColumns;
        set
        {
            if (value is { } v)
                GridColumns = (int)v;
            Raise();
        }
    }

    public decimal? GridRowsValue
    {
        get => _gridRows;
        set
        {
            if (value is { } v)
                GridRows = (int)v;
            Raise();
        }
    }

    public decimal? GridOverlapValue
    {
        get => (decimal)_gridOverlap;
        set
        {
            if (value is { } v)
                GridOverlap = (double)v;
            Raise();
        }
    }

    public string GridSentence => string.Create(CultureInfo.InvariantCulture,
        $"The first {_gridColumns * _gridRows} canvas output(s) become a {_gridColumns}×{_gridRows} grid over the main canvas, overlapping {_gridOverlap:0} px with soft edges.");

    /// <summary>The drawer opened: read the displays this machine sees.</summary>
    public void Opened() => RefreshDisplays.Execute(null);

    private async Task LoadDisplaysAsync(CancellationToken ct)
    {
        var reply = await _session.SendQuietAsync("DISPLAYS", ct);
        if (!reply.Ok || reply.Text.Length == 0 || reply.Text[0] != '[')
            return;
        try
        {
            using var doc = JsonDocument.Parse(reply.Text);
            Displays = doc.RootElement.EnumerateArray()
                .Select(e => new DisplayInfo(e.Str("key"), e.Str("name"), (int)e.Long("width"), (int)e.Long("height"), e.Num("refreshHz", 60), e.Bool("primary")))
                .ToList();
        }
        catch (JsonException)
        {
            // Another shape of DISPLAYS: the display field stays free text.
        }
    }

    private string NewOutputJson()
    {
        var n = (_project?.Outputs.Length ?? 0) + 1;
        var x = (_project?.Outputs.Select(o => o.Plan.Right).DefaultIfEmpty(0).Max() ?? 0) + 40;
        var o = new Output("", "Output " + n.ToString(CultureInfo.InvariantCulture), OutputKind.Virtual, "", 1920, 1080, 60, OutputMode.Canvas, null,
            _project?.Canvases is { Length: > 0 } cv ? SourceRef.Canvas(cv[0].Id) : SourceRef.None, new RectD(0, 0, 1920, 1080), OutputWarp.None,
            BlendSettings.Default with { Auto3D = false }, ColorTrim.Neutral, [], new RectD(x, 0, 1920, 1080), 0, false);
        return VerbText.Json(o);
    }

    /// <summary>A tile dragged on the plan: SET &lt;output&gt; plan (throttled; the last lands on release).</summary>
    public void MoveTile(PlanTile tile, RectD plan, bool commit)
    {
        ArgumentNullException.ThrowIfNull(tile);
        tile.Plan = plan;
        tile.Dragging = !commit;
        _session.Throttle.Post(tile.Id + " plan", VerbText.SetJson(tile.Id, "plan", plan));
        if (commit)
            _ = _session.Throttle.CommitAsync();
    }

    /// <summary>The blend grid: for the first columns × rows canvas outputs, the canvas region each shows,
    /// its soft edges where it overlaps a neighbour, and its place on the plan.</summary>
    public IReadOnlyList<(string Id, RectD Source, EdgeBlend Edges, RectD Plan)> PlanGrid()
    {
        var p = _project;
        if (p is null)
            return [];
        var outputs = p.Outputs.Where(o => o.Mode == OutputMode.Canvas).Take(_gridColumns * _gridRows).ToList();
        if (outputs.Count == 0)
            return [];
        var canvas = p.FindCanvas(outputs[0].Source.Id) ?? (p.Canvases.Length > 0 ? p.Canvases[0] : null);
        var w = outputs[0].Width;
        var h = outputs[0].Height;
        var o = _gridOverlap;
        var totalW = _gridColumns * w - (_gridColumns - 1) * o;
        var totalH = _gridRows * h - (_gridRows - 1) * o;
        var sx = (canvas?.Width ?? totalW) / totalW;
        var sy = (canvas?.Height ?? totalH) / totalH;
        var result = new List<(string, RectD, EdgeBlend, RectD)>();
        for (var i = 0; i < outputs.Count; i++)
        {
            var c = i % _gridColumns;
            var r = i / _gridColumns;
            var x = c * (w - o);
            var y = r * (h - o);
            var edges = new EdgeBlend(c > 0 ? o : 0, r > 0 ? o : 0, c < _gridColumns - 1 ? o : 0, r < _gridRows - 1 ? o : 0);
            result.Add((outputs[i].Id, new RectD(x * sx, y * sy, w * sx, h * sy), edges, new RectD(x, y, w, h)));
        }
        return result;
    }

    private async Task ApplyGridAsync(CancellationToken ct)
    {
        var grid = PlanGrid();
        if (grid.Count == 0)
        {
            _session.Notify(ToastKind.Refused, "The grid needs canvas outputs — set an output's mode to Canvas first.");
            return;
        }
        foreach (var (id, source, edges, plan) in grid)
        {
            await _session.SendAsync(VerbText.SetJson(id, "sourceRect", source), ct);
            await _session.SendAsync(VerbText.SetBool(id, "blend.auto3D", false), ct);
            await _session.SendAsync(VerbText.SetJson(id, "blend.edges", edges), ct);
            await _session.SendAsync(VerbText.SetJson(id, "plan", plan), ct);
        }
        _session.Notify(ToastKind.Done, string.Create(CultureInfo.InvariantCulture, $"Blend grid laid out over {grid.Count} output(s)."));
    }

    public void OnProject(Project p)
    {
        _project = p;
        Tiles.SyncById(p.Outputs, o => o.Id, t => t.Id, o => new PlanTile(o.Id), (t, o) =>
        {
            t.Name = o.Name;
            if (!t.Dragging)
                t.Plan = o.Plan;
            t.Enabled = o.Enabled;
            t.Color = p.FindProjector(o.ProjectorId)?.Color ?? "#8B95A1";
            t.Detail = string.Create(CultureInfo.InvariantCulture, $"{o.Width}×{o.Height} · {o.Mode.ToString().ToLowerInvariant()}{(o.DisplayKey.Length > 0 ? " · " + o.DisplayKey : "")}");
        });
        foreach (var t in Tiles)
        {
            if (p.FindOutput(t.Id) is { } o)
            {
                t.Name = o.Name;
                if (!t.Dragging)
                    t.Plan = o.Plan;
            }
        }
        if (_selected is not null && !Tiles.Contains(_selected))
            Selected = null;
        Inspector.Refresh(p, _session.State);
    }

    public void OnState(StateSnapshot s)
    {
        foreach (var t in Tiles)
            t.Light = s.FindOutput(t.Id)?.Light ?? "grey";
        if (_project is { } p)
            Inspector.Refresh(p, s);
        Patterns.RefreshAll();
    }
}

/// <summary>A Show Bus peer row.</summary>
public sealed record PeerRow(string Name, string Kind, string Light, string Headline, bool Live);

/// <summary>A media copy in flight.</summary>
public sealed record TransferRow(string Name, string From, double Progress, string Text);

/// <summary>The NETWORK drawer: Show Bus peers, the Patterns and Stagewatch links, sync, media transfers,
/// the assistant's door, and this node's pairing token (read where it lives, never from a line).</summary>
public sealed class NetworkDrawerViewModel : ObservableObject
{
    private readonly ConnectionManager _connections;
    private string _patternsHost = "";
    private string _patternsToken = "";
    private bool _followBlackout = true;
    private string _patternsState = "off";
    private string _stagewatchHost = "";
    private string _stagewatchKey = "";
    private string _stagewatchState = "off";
    private TimerState? _timer;
    private string _syncText = "";
    private string _syncGroup = "show";
    private string _token = "";
    private bool _tokenShown;
    private string _aiText = "";

    public NetworkDrawerViewModel(DeskSession session, ConnectionManager connections)
    {
        _connections = connections;
        LinkPatterns = DeskAction.Send(session, "patterns", "Follow this Patterns desk",
            () => _patternsHost.Trim().Length == 0 ? null : $"LINK PATTERNS {VerbText.Word(_patternsHost.Trim())}{(_patternsToken.Trim().Length > 0 ? " " + VerbText.Word(_patternsToken.Trim()) : "")}")
            .Look("network");
        UnlinkPatterns = DeskAction.Send(session, "patternsOff", "Stop following", () => "LINK PATTERNS OFF", isEnabled: () => _patternsState != "off").Look("close");
        FollowBlackoutAction = DeskAction.Send(session, "followBlackout", "Follow Patterns' blackout", () => $"LINK PATTERNS FOLLOW-BLACKOUT {(_followBlackout ? "ON" : "OFF")}");
        LinkStagewatch = DeskAction.Send(session, "stagewatch", "Follow this Stagewatch",
            () => $"LINK STAGEWATCH {(_stagewatchHost.Trim().Length == 0 ? "\"\"" : VerbText.Word(_stagewatchHost.Trim()))}{(_stagewatchKey.Trim().Length > 0 ? " " + VerbText.Word(_stagewatchKey.Trim()) : "")}")
            .Look("network");
        UnlinkStagewatch = DeskAction.Send(session, "stagewatchOff", "Stop following", () => "LINK STAGEWATCH OFF", isEnabled: () => _stagewatchState != "off").Look("close");
        Lead = DeskAction.Send(session, "lead", "Lead this group", () => _syncGroup.Trim().Length == 0 ? null : "SYNC LEAD " + VerbText.Word(_syncGroup.Trim())).Look("sync");
        FetchAll = DeskAction.Send(session, "fetchAll", "Fetch every missing file", () => "MEDIA FETCH ALL").Look("fetch");
        Regenerate = DeskAction.Run(session, "regen", "New pairing token", async ct =>
        {
            var reply = await session.SendAsync("PAIRING NEW", ct);
            if (reply.Ok)
            {
                ReadToken();
                session.Notify(ToastKind.Done, "A new pairing token — every paired peer pairs again.");
            }
        }, () => "PAIRING NEW").Look("sync", danger: true);
        ShowToken = new RelayCommand(() =>
        {
            ReadToken();
            TokenShown = !TokenShown;
        });
    }

    public ObservableCollection<PeerRow> Peers { get; } = [];

    public ObservableCollection<TransferRow> Transfers { get; } = [];

    public DeskAction LinkPatterns { get; }

    public DeskAction UnlinkPatterns { get; }

    public DeskAction FollowBlackoutAction { get; }

    public DeskAction LinkStagewatch { get; }

    public DeskAction UnlinkStagewatch { get; }

    public DeskAction Lead { get; }

    public DeskAction FetchAll { get; }

    public DeskAction Regenerate { get; }

    public RelayCommand ShowToken { get; }

    public string PatternsHost
    {
        get => _patternsHost;
        set
        {
            if (Set(ref _patternsHost, value))
                LinkPatterns.Refresh();
        }
    }

    /// <summary>Patterns' pairing token (it rides only the LINK line to this node, never a journal).</summary>
    public string PatternsToken
    {
        get => _patternsToken;
        set
        {
            if (Set(ref _patternsToken, value))
                LinkPatterns.Refresh();
        }
    }

    public bool FollowBlackout
    {
        get => _followBlackout;
        set
        {
            if (Set(ref _followBlackout, value))
                FollowBlackoutAction.Command.Execute(null);
        }
    }

    public string PatternsState
    {
        get => _patternsState;
        private set => Set(ref _patternsState, value);
    }

    public string StagewatchHost
    {
        get => _stagewatchHost;
        set
        {
            if (Set(ref _stagewatchHost, value))
                LinkStagewatch.Refresh();
        }
    }

    public string StagewatchKey
    {
        get => _stagewatchKey;
        set
        {
            if (Set(ref _stagewatchKey, value))
                LinkStagewatch.Refresh();
        }
    }

    public string StagewatchState
    {
        get => _stagewatchState;
        private set => Set(ref _stagewatchState, value);
    }

    public TimerState? Timer
    {
        get => _timer;
        private set
        {
            if (Set(ref _timer, value))
                Raise(nameof(HasTimer));
        }
    }

    public bool HasTimer => _timer is not null;

    public string SyncText
    {
        get => _syncText;
        private set => Set(ref _syncText, value);
    }

    public string SyncGroup
    {
        get => _syncGroup;
        set
        {
            if (Set(ref _syncGroup, value))
                Lead.Refresh();
        }
    }

    public string Token
    {
        get => _token;
        private set => Set(ref _token, value);
    }

    public bool TokenShown
    {
        get => _tokenShown;
        private set
        {
            if (Set(ref _tokenShown, value))
                Raise(nameof(TokenText));
        }
    }

    public string TokenText => _token.Length == 0 ? "This node's token is not readable from here (it lives on the node)." : _tokenShown ? _token : "••••-••••-••••";

    public string AiText
    {
        get => _aiText;
        private set => Set(ref _aiText, value);
    }

    public void ReadToken()
    {
        Token = _connections.ReadToken() ?? "";
        Raise(nameof(TokenText));
    }

    public void OnState(StateSnapshot s)
    {
        Peers.Clear();
        foreach (var p in s.Peers)
            Peers.Add(new PeerRow(p.Name, p.Kind switch { "patterns" => "Patterns", "main" => "Stagewatch", "playmap" => "PlayMap", _ => p.Kind }, p.Light, p.Headline, p.Live));
        PatternsState = s.Patterns.State + (s.Patterns.Host.Length > 0 ? " · " + s.Patterns.Host : "") + (s.Patterns.Clip.Length > 0 ? " · " + s.Patterns.Clip : "");
        StagewatchState = s.Stagewatch.State + (s.Stagewatch.Host.Length > 0 ? " · " + s.Stagewatch.Host : "");
        Timer = s.Stagewatch.Timer;
        var sync = s.Sync;
        SyncText = string.Create(CultureInfo.InvariantCulture,
            $"{(sync.Role == "leader" ? "Leading" : sync.Role == "follower" ? "Following " + (sync.Leader ?? "?") : "On its own")} · offset {sync.OffsetMs:0.00} ms · round trip {sync.RttMs:0.00} ms · drift {sync.DriftPpm:0.0} ppm{(sync.Groups.Length > 0 ? " · groups " + string.Join(", ", sync.Groups) : "")}");
        Transfers.Clear();
        foreach (var t in s.Media.Transfers)
            Transfers.Add(new TransferRow(t.Name, t.From, t.Progress, string.Create(CultureInfo.InvariantCulture, $"{t.Name} from {t.From} · {t.Progress * 100:0} %")));
        AiText = s.Ai.Mcp
            ? string.Create(CultureInfo.InvariantCulture, $"MCP is served at /mcp (port 9940): an assistant sees this node's STATE and context and may use every verb; high-risk verbs wait for a human. {s.Ai.Clients} connected · {s.Ai.Proposals} proposal(s) waiting.")
            : "The assistant's door (MCP) is closed on this node.";
        new[] { UnlinkPatterns, UnlinkStagewatch, LinkPatterns, LinkStagewatch, Lead, FetchAll }.RefreshAll();
        if (_token.Length == 0)
            ReadToken();
    }
}
