using System.Collections.ObjectModel;
using System.Globalization;
using PlayMap.App.Infrastructure;
using PlayMap.App.Link;
using PlayMap.App.Scene;
using PlayMap.App.State;
using PlayMap.Core.Content;
using PlayMap.Core.Geometry;
using PlayMap.Core.Mapping;
using PlayMap.Core.Model;
using PlayMap.Core.Numerics;

namespace PlayMap.App.ViewModels;

public enum MapTool
{
    Select,
    Pin,
    Warp,
    Pen,
    Quad,
}

/// <summary>MAP: the scene outliner, the editor view (the engine's picture of an orbit camera with the desk's
/// overlays — frusta, pins, selection, gizmo) or the projector view (what one projector sees, with its warp
/// handles, its pen masks, its pins and its quads), the tools, the inspector, on-wall editing and the first-light guide.
/// Every edit is a verb; drags go through the throttle and land as one undo step on the engine (it coalesces a key's
/// edits). The quad tool's four clicks are wall points: Enter sends one SURFACE QUAD and leaves the new quad selected with the Select
/// tool; a selected quad's corner drags in the view (SURFACE CORNER … TO … through the throttle), is typed in its inspector and moves
/// by the arrows (SURFACE CORNER … BY … PX), and Tab picks the next corner; selecting a quad restores the corner last selected on it
/// and tells the wall (HANDLE SELECT), deselecting tells it too (HANDLE SELECT NONE); a selected pin's pixel moves by the arrows (SET
/// &lt;pin&gt; pixel). O puts the edit overlay on the wall (OVERLAY ON ON &lt;output&gt;), its lease renewed every 5 s of the desk's clock while
/// this desk holds it, and off again when the desk leaves MAP, closes or leaves the node. A quad half drawn is a draft of the projector
/// it was begun on (<see cref="ViewModels.Map.QuadDrafts"/>): kept on the desk and in desk.json through a tool change, another projector,
/// leaving MAP, a menu entry, a push, a restart and a reattach, until Enter or a Discard.</summary>
public sealed class MapViewModel : ObservableObject, IDisposable
{
    private readonly DeskSession _session;
    private Project? _project;
    private MapTool _tool = MapTool.Select;
    private bool _isProjectorView;
    private bool _isPatternMenuOpen;
    private string? _selectedId;
    private string _selectedKind = "";
    private string? _viewProjectorId;
    private int _maskCount;
    private string? _selectedMaskId;
    private readonly Func<long> _nowMs;

    /// <param name="nowMs">The desk's monotonic clock in milliseconds (the overlay's lease is renewed on it); the machine's when null.</param>
    /// <param name="prefs">The desk's preferences: its unfinished quads are kept there (in memory when null).</param>
    public MapViewModel(DeskSession session, Func<long>? nowMs = null, DeskPrefs? prefs = null)
    {
        _session = session;
        _nowMs = nowMs ?? EscapeLadder.MonotonicMs(System.Diagnostics.Stopwatch.GetTimestamp());
        Drafts = new ViewModels.Map.QuadDrafts(prefs ?? DeskPrefs.InMemory());
        Drafts.Changed += SyncDraftChips;
        Inspector = new InspectorHost(session);
        Pen.Closed += path => LastPenCommit = CommitPenAsync(path);
        Quad.Changed += () => SceneChanged?.Invoke();
        QuadTool = new RelayCommand(() => Tool = MapTool.Quad);
        FinishQuad = new RelayCommand(() => LastQuad = FinishQuadAsync(), () => Quad.IsReady && IsDraftShown);
        NextCorner = new RelayCommand(() => StepCorner(+1), () => CanNudge);
        PrevCorner = new RelayCommand(() => StepCorner(-1), () => CanNudge);
        OnWall = DeskAction.Run(session, "map.onWall", "On the wall", ToggleWallAsync, OnWallLine, isChecked: () => WallOutput is { } o && OverlayCovers(o.Id),
            isEnabled: () => session.IsLive && WallOutput is not null).Look("projector", "O");
        QuadToolAction = DeskAction.Local("map.quadTool", "Quad tool", () => Tool = MapTool.Quad, isChecked: () => _tool == MapTool.Quad).Look("surface", "Q");
        KeepQuadToolAction = DeskAction.Local("map.keepQuadTool", "Keep quad tool", () => KeepQuadTool = !KeepQuadTool, isChecked: () => KeepQuadTool).Look("surface");
        Guide = new ViewModels.Map.FirstLightGuide(session, LitOn, () => ArmQuad(null));
        FirstLight = DeskAction.Local("map.firstLight", "First light guide", () => Guide.Reopen()).Look("guide");
        ViewActions = [QuadToolAction, KeepQuadToolAction, OnWall, FirstLight];
        SelectTool = new RelayCommand(() => Tool = MapTool.Select);
        PinTool = new RelayCommand(() => Tool = MapTool.Pin);
        WarpTool = new RelayCommand(() => Tool = MapTool.Warp);
        PenTool = new RelayCommand(() => Tool = MapTool.Pen);
        ToggleView = new RelayCommand(() => IsProjectorView = !IsProjectorView);
        OpenPatternMenu = new RelayCommand(() => IsPatternMenuOpen = !IsPatternMenuOpen);
        RemoveSelection = new RelayCommand(() =>
        {
            if (_selectedMaskId is { } mask)
                session.Fire("REMOVE " + mask);
            else if (_selectedId is { } id)
                session.Fire("REMOVE " + id);
        });
        Outliner.Add(new OutlinerGroup("Surfaces", "surface"));
        Outliner.Add(new OutlinerGroup("Projectors", "projector"));
        Outliner.Add(new OutlinerGroup("Meshes", "mesh"));
        Outliner.Add(new OutlinerGroup("Cameras", "camera"));
        Outliner.Add(new OutlinerGroup("Viewpoints", "viewpoint"));
        Outliner.Add(new OutlinerGroup("Scans", "scan"));
        Outliner.Add(new OutlinerGroup("Outputs", "outputs"));
        BuildAddActions();
        PatternActions.Add(DeskAction.Send(session, "pattern.off", "Patterns off", () => "PATTERN OFF" + PatternTarget()).Look("close"));
        foreach (var kind in GeneratorCatalog.All.Where(k => k.Family == "pattern"))
            PatternActions.Add(DeskAction.Send(session, "pattern." + kind.Id, kind.Name, () => $"PATTERN {kind.Id[8..]}{PatternTarget()}").Look("pattern"));
    }

    public InspectorHost Inspector { get; }

    /// <summary>The send a closed pen path started (awaitable by whoever needs to know it landed).</summary>
    public Task LastPenCommit { get; private set; } = Task.CompletedTask;

    public ObservableCollection<OutlinerGroup> Outliner { get; } = [];

    public SceneCache Scene { get; } = new();

    public OrbitCamera Camera { get; } = new();

    public PenTool Pen { get; } = new();

    public Project? Project => _project;

    /// <summary>The node the desk is on (its preview rings are named after it).</summary>
    public string NodeId => _session.NodeId;

    /// <summary>The session the outliner's actions send through.</summary>
    internal DeskSession SessionForNodes => _session;

    public RelayCommand SelectTool { get; }

    /// <summary>Q: the quad tool (four clicks on the wall, Enter draws it).</summary>
    public RelayCommand QuadTool { get; }

    /// <summary>Enter with four corners: one SURFACE QUAD.</summary>
    public RelayCommand FinishQuad { get; }

    /// <summary>Tab / Shift+Tab: the selected quad's next or previous corner (HANDLE SELECT, so the wall shows it).</summary>
    public RelayCommand NextCorner { get; }

    public RelayCommand PrevCorner { get; }

    /// <summary>The quad tool's corners and rubber band.</summary>
    public ViewModels.Map.QuadTool Quad { get; } = new();

    /// <summary>The send the last Enter started (awaitable by whoever needs to know it landed).</summary>
    public Task LastQuad { get; private set; } = Task.CompletedTask;

    /// <summary>O / "On the wall": the edit overlay on the output that shows this projector (OVERLAY ON|OFF ON &lt;output&gt;); greyed
    /// with the node's words while the show is armed.</summary>
    public DeskAction OnWall { get; }

    /// <summary>Q as an entry: the quad tool (the projector view's right-click menu).</summary>
    public DeskAction QuadToolAction { get; }

    /// <summary>The projector view's own entries, its right-click menu where no quad or surface is under the pointer: the quad tool,
    /// Keep quad tool, On the wall and the first-light guide.</summary>
    public IReadOnlyList<DeskAction> ViewActions { get; }

    /// <summary>"Keep quad tool": after Enter the quad tool stays armed, to draw several in a row (a desk toggle).</summary>
    public DeskAction KeepQuadToolAction { get; }

    /// <summary>Opens the first-light guide again (the view's right-click and the help overlay).</summary>
    public DeskAction FirstLight { get; }

    private bool _keepQuadTool;

    public bool KeepQuadTool
    {
        get => _keepQuadTool;
        set
        {
            if (Set(ref _keepQuadTool, value))
                KeepQuadToolAction.Refresh();
        }
    }

    /// <summary>The unfinished quads, a draft per projector.</summary>
    public ViewModels.Map.QuadDrafts Drafts { get; }

    /// <summary>A chip per draft: "Quad 2/4" with Resume and Discard (the outliner lists them under Surfaces).</summary>
    public ObservableCollection<ViewModels.Map.QuadDraftChip> DraftChips { get; } = [];

    /// <summary>The chip of the viewed projector's draft (the view's corner), or null.</summary>
    public ViewModels.Map.QuadDraftChip? ViewDraftChip => ViewProjector is { } pj ? DraftChips.FirstOrDefault(c => c.Projector == pj.Id) : null;

    /// <summary>True while the projector view has the keyboard (the view sets it): Esc then discards the draft it shows.</summary>
    public bool ViewHasKeyboard { get; set; }

    /// <summary>The first-light guide (Light a wall → the quad → a clip).</summary>
    public ViewModels.Map.FirstLightGuide Guide { get; }

    public RelayCommand PinTool { get; }

    public RelayCommand WarpTool { get; }

    public RelayCommand PenTool { get; }

    public RelayCommand ToggleView { get; }

    public RelayCommand OpenPatternMenu { get; }

    public RelayCommand RemoveSelection { get; }

    /// <summary>The test-pattern quick menu (T): on the projector's output in projector view, everywhere otherwise.</summary>
    public ObservableCollection<DeskAction> PatternActions { get; } = [];

    /// <summary>Raised when anything the editor views draw changed (they repaint).</summary>
    public event Action? SceneChanged;

    public MapTool Tool
    {
        get => _tool;
        set
        {
            if (!Set(ref _tool, value))
                return;
            Raise(nameof(IsSelectTool), nameof(IsPinTool), nameof(IsWarpTool), nameof(IsPenTool), nameof(IsQuadTool), nameof(ToolHint));
            if (value != MapTool.Pin)
                SelectPin(null);
            if (value is MapTool.Warp or MapTool.Pen or MapTool.Pin or MapTool.Quad && !_isProjectorView)
                IsProjectorView = true;
            if (value != MapTool.Pen)
                Pen.Cancel();
            if (value == MapTool.Quad)
                ResumeQuad();
            else
                ParkQuad();
            FinishQuad.RaiseCanExecuteChanged();
            NextCorner.RaiseCanExecuteChanged();
            PrevCorner.RaiseCanExecuteChanged();
            Raise(nameof(IsDraftShown), nameof(CanNudge));
            SceneChanged?.Invoke();
        }
    }

    public bool IsSelectTool
    {
        get => _tool == MapTool.Select;
        set
        {
            if (value)
                Tool = MapTool.Select;
        }
    }

    public bool IsPinTool
    {
        get => _tool == MapTool.Pin;
        set
        {
            if (value)
                Tool = MapTool.Pin;
        }
    }

    public bool IsWarpTool
    {
        get => _tool == MapTool.Warp;
        set
        {
            if (value)
                Tool = MapTool.Warp;
        }
    }

    public bool IsPenTool
    {
        get => _tool == MapTool.Pen;
        set
        {
            if (value)
                Tool = MapTool.Pen;
        }
    }

    public bool IsQuadTool
    {
        get => _tool == MapTool.Quad;
        set
        {
            if (value)
                Tool = MapTool.Quad;
        }
    }

    /// <summary>One sentence: what the tool does with a click.</summary>
    public string ToolHint => _tool switch
    {
        MapTool.Pin => ViewOutputHint ?? "Click a feature on a surface to pin it; drag a pin onto the real feature. Six spread pins solve the projector.",
        MapTool.Warp => ViewOutputHint ?? "Drag the corner handles to pin the picture's corners, the lattice nodes to bend it.",
        MapTool.Pen => ViewOutputHint ?? "Click for corners, drag for smooth nodes, Alt-click a node to toggle it, click the first node to close.",
        MapTool.Quad => Quad.Hint,
        _ => _isProjectorView ? "Click a surface to select it." : "Click to select · drag empty space to orbit · Shift-drag to pan · wheel to zoom · drag an axis to move.",
    };

    private string? ViewOutputHint => _isProjectorView && ViewOutput is null && ViewProjector is not null
        ? "No output drives this projector yet — assign one in Outputs to warp and mask it."
        : null;

    /// <summary>The projector view (true) or the 3D view. The 3D view shows no draft: the wall's rubber band goes while it shows and comes
    /// back with the projector view.</summary>
    public bool IsProjectorView
    {
        get => _isProjectorView;
        set
        {
            if (Set(ref _isProjectorView, value))
            {
                if (!value)
                    CursorOff();
                else if (IsDraftShown)
                    SendCursor();
                FinishQuad.RaiseCanExecuteChanged();
                NextCorner.RaiseCanExecuteChanged();
                PrevCorner.RaiseCanExecuteChanged();
                Raise(nameof(Is3DView), nameof(ToolHint), nameof(ViewTitle), nameof(ViewId), nameof(IsDraftShown), nameof(CanNudge));
                SceneChanged?.Invoke();
            }
        }
    }

    public bool Is3DView
    {
        get => !_isProjectorView;
        set => IsProjectorView = !value;
    }

    public bool IsPatternMenuOpen
    {
        get => _isPatternMenuOpen;
        set => Set(ref _isPatternMenuOpen, value);
    }

    public string? SelectedId => _selectedId;

    public string SelectedKind => _selectedKind;

    public string? SelectedMaskId
    {
        get => _selectedMaskId;
        set
        {
            if (Set(ref _selectedMaskId, value))
                SceneChanged?.Invoke();
        }
    }

    /// <summary>The projector the projector view shows: the selected one, the one the selected output
    /// drives, or the first.</summary>
    public Projector? ViewProjector => _project?.FindProjector(_viewProjectorId) ?? (_project?.Stage.Projectors is { Length: > 0 } pjs ? pjs[0] : null);

    /// <summary>The output that drives <see cref="ViewProjector"/> (its warp and masks are edited here).</summary>
    public Output? ViewOutput => ViewProjector is { } pj ? _project?.Outputs.FirstOrDefault(o => o.ProjectorId == pj.Id) : null;

    public string ViewTitle => _isProjectorView
        ? ViewProjector is { } pj ? $"Projector view · {pj.Name}{(ViewOutput is { } o ? " · " + o.Name : "")}" : "Projector view · no projector"
        : _showDraft ? "3D view · the blind draft" : "3D view";

    /// <summary>The engine's preview view id for what the centre shows.</summary>
    public string ViewId => _isProjectorView && ViewProjector is { } pj ? "pj-" + pj.Id : EditorViewId;

    private bool _showDraft;

    /// <summary>The 3D view draws this desk's blind draft instead of the air (VIEW editor-draft SHOW editor DRAFT): a view of its own,
    /// as the node keeps a view that drew the air for the air.</summary>
    public bool ShowDraft
    {
        get => _showDraft;
        set
        {
            if (!Set(ref _showDraft, value))
                return;
            Raise(nameof(EditorViewId), nameof(ViewId), nameof(ViewTitle));
            if (_editorPixels is { } px)
                _session.Throttle.Post("view " + EditorViewId, $"VIEW {EditorViewId} CAMERA " + Camera.ToJson(px.W, px.H));
            SceneChanged?.Invoke();
        }
    }

    /// <summary>The 3D view's preview: "editor-draft" while it draws the draft, "editor" otherwise.</summary>
    public string EditorViewId => _showDraft ? "editor-draft" : "editor";

    private (double W, double H)? _editorPixels;

    public void Select(string kind, string? id)
    {
        var viewed = ViewProjector?.Id;
        if (SelectedQuad is { } was)
            _lastCorner[was.Id] = _selectedCorner;
        _drawn = null;
        _selectedKind = id is null ? "" : kind;
        _selectedId = id;
        _selectedMaskId = null;
        _selectedPinId = null;
        _selectedCorner = id is not null && _lastCorner.TryGetValue(id, out var last) ? last : 0;
        if (kind == "projector" && id is not null)
            _viewProjectorId = id;
        else if (kind == "output" && id is not null && _project?.FindOutput(id)?.ProjectorId is { } pj)
            _viewProjectorId = pj;
        else if (kind == "surface" && _project?.FindSurface(id)?.Pixel is { } quad)
        {
            // A quad is drawn in its projector's picture: the view shows that projector, so the arrows move what the view shows.
            _viewProjectorId = quad.ProjectorId;
            IsProjectorView = true;
        }
        foreach (var g in Outliner)
            foreach (var n in g.Nodes)
                n.IsSelected = n.Id == id;
        Inspector.Show(kind switch
        {
            "surface" when id is not null => new SurfaceInspector(_session, id),
            "projector" when id is not null => new ProjectorInspector(_session, id, this),
            "output" when id is not null => new OutputInspector(_session, id),
            "mesh" when id is not null => new MeshInspector(_session, id),
            "camera" or "viewpoint" or "scan" when id is not null => new StageItemInspector(_session, id, kind),
            _ => null,
        });
        Raise(nameof(SelectedId), nameof(SelectedKind), nameof(ViewProjector), nameof(ViewOutput), nameof(ViewTitle), nameof(ViewId), nameof(ToolHint),
            nameof(SelectedQuad), nameof(SelectedCorner), nameof(ViewDraftChip), nameof(CanNudge), nameof(SelectedPinId));
        NextCorner.RaiseCanExecuteChanged();
        PrevCorner.RaiseCanExecuteChanged();
        if (ViewProjector?.Id != viewed)
            SwitchDraft();
        TellWallHandle();
        OnWall.Refresh();
        SceneChanged?.Invoke();
    }

    public void OnProject(Project p)
    {
        _project = p;
        Scene.Update(p);
        var surfaces = Outliner[0];
        surfaces.Nodes.SyncById(p.Stage.Surfaces, s => s.Id, n => n.Id, s => new OutlinerNode(this, "surface", s.Id), (n, s) => n.UpdateSurface(s, p));
        Outliner[1].Nodes.SyncById(p.Stage.Projectors, x => x.Id, n => n.Id, x => new OutlinerNode(this, "projector", x.Id), (n, x) => n.UpdateProjector(x, p));
        Outliner[2].Nodes.SyncById(p.Stage.Meshes, x => x.Id, n => n.Id, x => new OutlinerNode(this, "mesh", x.Id), (n, x) => n.UpdateMesh(x, p));
        Outliner[3].Nodes.SyncById(p.Stage.Cameras, x => x.Id, n => n.Id, x => new OutlinerNode(this, "camera", x.Id), (n, x) => n.UpdatePlain(x.Name, x.SourceKind + " · " + x.Source, x.Calibrated ? "green" : "grey"));
        Outliner[4].Nodes.SyncById(p.Stage.Viewpoints, x => x.Id, n => n.Id, x => new OutlinerNode(this, "viewpoint", x.Id), (n, x) => n.UpdatePlain(x.Name, string.Create(CultureInfo.InvariantCulture, $"{x.Intrinsics.HorizontalFovDegrees:0}° wide"), "grey"));
        Outliner[5].Nodes.SyncById(p.Stage.Scans, x => x.Id, n => n.Id, x => new OutlinerNode(this, "scan", x.Id), (n, x) => n.UpdatePlain(x.Name, string.Create(CultureInfo.InvariantCulture, $"{x.Kind} · {x.Points:N0} points"), "grey"));
        Outliner[6].Nodes.SyncById(p.Outputs, x => x.Id, n => n.Id, x => new OutlinerNode(this, "output", x.Id), (n, x) => n.UpdateOutput(x, p));
        foreach (var g in Outliner)
            g.Refresh();
        if (_drawn is { } drawn && _selectedId == drawn && p.FindSurface(drawn) is not null)
        {
            // The quad this desk drew is in the project now: its inspector opens, its corner kept.
            var corner = _selectedCorner;
            Select("surface", drawn);
            _selectedCorner = corner;
            Raise(nameof(SelectedCorner));
        }
        else if (_selectedId is not null && _selectedId != _drawn && !Outliner.Any(g => g.Nodes.Any(n => n.Id == _selectedId)))
            Select("", null);
        Inspector.Refresh(p, _session.State);
        Guide.OnProject(p);
        HoldLitOverlay();
        NoteNode();
        foreach (var gone in Drafts.DropGone(p))
        {
            if (_quadProjector == gone)
            {
                _quadProjector = null;
                Quad.Cancel();
            }
            _session.Notify(ToastKind.Info, $"The unfinished quad on {gone} was dropped: the project has no such projector now");
        }
        if (_quadProjector is null && _tool == MapTool.Quad && !ViewPending && ViewProjector is { } shown && Drafts.Of(shown.Id) is { } kept)
            TakeUpDraft(shown.Id, kept);
        SyncDraftChips();
        Raise(nameof(ViewProjector), nameof(ViewOutput), nameof(ViewTitle), nameof(ToolHint), nameof(NodeId), nameof(ViewId), nameof(SelectedQuad),
            nameof(ViewDraftChip), nameof(CanNudge));
        NextCorner.RaiseCanExecuteChanged();
        PrevCorner.RaiseCanExecuteChanged();
        OnWall.Refresh();
        SceneChanged?.Invoke();
    }

    /// <summary>The desk is on another node (a project or a STATE from it, whichever comes first): the tool's points are the old node's
    /// (kept there, as leaving the node keeps them), never this one's; the wall's band was the old node's wall; the drafts read are the
    /// new node's.</summary>
    private void NoteNode()
    {
        if (_session.NodeId != _draftsNode)
        {
            if (_quadProjector is { } begun && Quad.IsDrawing)
                Drafts.Keep(begun, [.. Quad.Clicks]);
            _quadProjector = null;
            Quad.Cancel();
            _cursorShown = false;
            _draftsNode = _session.NodeId;
            FinishQuad.RaiseCanExecuteChanged();
            Raise(nameof(IsDraftShown), nameof(ToolHint));
        }
        Drafts.UseNode(_session.NodeId);
    }

    public void OnState(StateSnapshot s)
    {
        if (_project is not null)
            NoteNode();
        foreach (var n in Outliner[6].Nodes)
            n.UpdateOutputState(s);
        foreach (var a in PatternActions)
            a.Refresh();
        if (_project is { } p)
            Inspector.Refresh(p, s);
        var overlay = WallFacts.Read(s);
        if (overlay != _overlay)
        {
            _overlay = overlay;
            if (!overlay.On)
                _overlayMine = null;
            SceneChanged?.Invoke();
        }
        OnWall.Refresh();
        Guide.OnState(s);
        Guide.RefreshActions();
        if (_handleEpoch != _session.LinkEpoch)
        {
            // A new link: what this desk told the last one is not this one's; the selected quad's handle is told again.
            _handleEpoch = _session.LinkEpoch;
            _wallHandle = null;
            if (SelectedQuad is not null)
                _heldHandle ??= $"{SelectedQuad.Id} {PixelQuadMath.CornerNames[_selectedCorner]}";
        }
        if (_heldHandle is { } held)
            TellWall(held);
    }

    // ---- the quad on the wall ---------------------------------------------------------------------------------------------

    private WallFacts _overlay = WallFacts.Off;
    private string? _overlayMine;
    private int _selectedCorner;
    private long _keptAtMs;
    private string? _quadProjector;

    /// <summary>The quad this desk drew, selected before the project that names it arrived (a push may lag the reply).</summary>
    private string? _drawn;

    /// <summary>The time between two renewals of this desk's overlay lease, on the desk's clock (the node lets it lapse after 15 s).</summary>
    public const long KeepOverlayEveryMs = 5000;

    /// <summary>The output that puts the view's projector on the wall, as the node chooses it: the first enabled output in Projector
    /// mode that shows it, else the first that shows it; null when none does (then only raster pixels mean anything).</summary>
    public Output? WallOutput => ViewProjector is { } pj && _project is { } p
        ? p.Outputs.FirstOrDefault(o => o.Mode == OutputMode.Projector && o.ProjectorId == pj.Id && o.Enabled)
          ?? p.Outputs.FirstOrDefault(o => o.Mode == OutputMode.Projector && o.ProjectorId == pj.Id)
        : null;

    /// <summary>The air's edit overlay as STATE says it.</summary>
    public WallFacts Overlay => _overlay;

    private bool OverlayCovers(string output) => _overlay.On && (_overlay.Outputs.IsDefaultOrEmpty || _overlay.Outputs.Contains(output));

    /// <summary>The selected surface when it is a quad drawn in a projector's picture, else null.</summary>
    public Surface? SelectedQuad => _selectedKind == "surface" && _project?.FindSurface(_selectedId) is { Pixel: not null } s ? s : null;

    /// <summary>The selected quad's corner the arrows move (0 TL, 1 TR, 2 BR, 3 BL).</summary>
    public int SelectedCorner => _selectedCorner;

    private string? OnWallLine() => WallOutput is { } o ? (OverlayCovers(o.Id) ? "OVERLAY OFF ON " : "OVERLAY ON ON ") + o.Id : null;

    private async Task ToggleWallAsync(CancellationToken ct)
    {
        if (OnWallLine() is not { } line || WallOutput is not { } o)
            return;
        var on = line.StartsWith("OVERLAY ON", StringComparison.Ordinal);
        var reply = await _session.SendAsync(line, ct);
        if (!reply.Ok)
            return;
        _overlayMine = on ? o.Id : null;
        _keptAtMs = _nowMs();
    }

    /// <summary>The window's tick: while this desk holds the overlay on the wall, its lease is renewed (quietly) once 5 s of the desk's
    /// clock have passed since it was put on or last renewed, however late the ticks come.</summary>
    public void TickWall()
    {
        var now = _nowMs();
        if (_overlayMine is not { } op || !_overlay.On || !_session.IsLive)
        {
            _keptAtMs = now;
            return;
        }
        if (now - _keptAtMs < KeepOverlayEveryMs)
            return;
        _keptAtMs = now;
        _ = _session.SendQuietAsync("OVERLAY KEEP ON " + op);
    }

    /// <summary>True while the edit overlay this desk put on a wall is on it.</summary>
    public bool HoldsOverlay => _overlayMine is { } op && OverlayCovers(op);

    /// <summary>The desk shows MAP: the guide's first step reads the displays again, and with the quad tool in hand the shown projector's
    /// draft is taken up and its rubber band is back on the wall.</summary>
    public void EnterMap()
    {
        _mapShown = true;
        Guide.Shown();
        if (_tool == MapTool.Quad)
            ResumeQuad();
        FinishQuad.RaiseCanExecuteChanged();
        Raise(nameof(IsDraftShown), nameof(CanNudge));
    }

    /// <summary>The desk left MAP: the overlay it put on the wall comes off, and a quad in progress is kept as its projector's draft.</summary>
    public void LeaveMap()
    {
        _mapShown = false;
        _ = LeaveMapAsync();
        FinishQuad.RaiseCanExecuteChanged();
        Raise(nameof(IsDraftShown), nameof(CanNudge));
    }

    /// <summary>True while the desk shows MAP (the window's workspace).</summary>
    private bool _mapShown;

    /// <summary>The node whose drafts the tool holds: another node's push lets the tool's points go.</summary>
    private string _draftsNode = "";

    /// <summary>The desk leaves MAP, closes or leaves the node: the overlay it put on the wall comes off (OVERLAY OFF ON &lt;output&gt;,
    /// the send awaited so a closing desk sends it before its link goes), and a quad in progress is kept as its projector's draft (its
    /// rubber band off the wall).</summary>
    public Task LeaveMapAsync(CancellationToken ct = default)
    {
        ParkQuad();
        var held = HoldsOverlay ? _overlayMine : null;
        _overlayMine = null;
        return held is not null && _session.CanSend ? _session.SendAsync("OVERLAY OFF ON " + held, ct) : Task.CompletedTask;
    }

    /// <summary>LIGHT WALL lit this projector (and put the overlay on its output for this desk): its view, with the quad tool armed.</summary>
    private void LitOn(string projectorId)
    {
        _litProjector = projectorId;
        ArmQuad(projectorId);
        HoldLitOverlay();
        Lit?.Invoke();
    }

    /// <summary>The projector LIGHT WALL lit, until the project that names it has arrived (its output's overlay is this desk's).</summary>
    private string? _litProjector;

    private void HoldLitOverlay()
    {
        if (_litProjector is not { } pj || _project?.FindProjector(pj) is null)
            return;
        _litProjector = null;
        if (_selectedId != pj)
            Select("projector", pj);
        if (WallOutput is { } o)
        {
            _overlayMine = o.Id;
            _keptAtMs = _nowMs();
        }
    }

    /// <summary>Raised after LIGHT WALL lit a projector from the guide (the window shows MAP).</summary>
    public event Action? Lit;

    /// <summary>The quad tool on a projector's view (the lit one, or the one shown); a projector the mirrored project does not name
    /// yet is the view's as soon as the project that names it arrives.</summary>
    private void ArmQuad(string? projectorId)
    {
        if (projectorId is not null)
        {
            if (_project?.FindProjector(projectorId) is not null)
                Select("projector", projectorId);
            else
            {
                // A projector the mirror does not name yet: the draft in hand is its own projector's, kept there; the new view starts
                // with its own (none until the project names it).
                ParkQuad();
                _viewProjectorId = projectorId;
            }
        }
        IsProjectorView = true;
        Tool = MapTool.Quad;
        Raise(nameof(ViewProjector), nameof(ViewOutput), nameof(ViewTitle), nameof(ViewId), nameof(ToolHint));
    }

    /// <summary>The corner last selected on each quad: reselecting one restores it.</summary>
    private readonly Dictionary<string, int> _lastCorner = new(StringComparer.Ordinal);

    /// <summary>The handle this desk last told the wall of on this link ("&lt;sf&gt; TL", "NONE"), or null before any.</summary>
    private string? _wallHandle;

    /// <summary>The link <see cref="_wallHandle"/> was told on.</summary>
    private long _handleEpoch = -1;

    /// <summary>The STATE this desk held when it told the wall <see cref="_wallHandle"/> (a later push says what the wall shows).</summary>
    private StateSnapshot? _wallHandleAt;

    /// <summary>A handle the gate held back (armed): told when the gate next lets HANDLE SELECT through.</summary>
    private string? _heldHandle;

    private string? _selectedPinId;

    /// <summary>The pin the arrows move (picked with the Pin tool or in its row), or null.</summary>
    public string? SelectedPinId => _selectedPinId;

    /// <summary>True while the quad tool shows a draft on the view: its points take the arrows and Tab, and the wall shows its rubber
    /// band.</summary>
    public bool IsDraftShown => _mapShown && _isProjectorView && _tool == MapTool.Quad && Quad.IsDrawing && _quadProjector is { } pj && ViewProjector?.Id == pj;

    /// <summary>True while the view waits for a projector LIGHT WALL lit and the mirror does not name yet: no other projector's draft is
    /// taken up meanwhile.</summary>
    private bool ViewPending => _litProjector is { } lit && _project?.FindProjector(lit) is null;

    /// <summary>True when the tool's points are the projector the view shows (the view draws them only then).</summary>
    public bool ToolIsOnView => _quadProjector is { } pj && ViewProjector?.Id == pj && Quad.IsDrawing;

    /// <summary>The quad in progress becomes its projector's kept draft and the wall's rubber band goes (the person is elsewhere): a
    /// tool change, another projector, leaving MAP. Nothing is dropped.</summary>
    private void ParkQuad()
    {
        if (_quadProjector is { } pj && Quad.IsDrawing)
            Drafts.Keep(pj, [.. Quad.Clicks]);
        // The points are the draft's now: the tool lets them go, and takes them up again when the person comes back.
        _quadProjector = null;
        Quad.Cancel();
        CursorOff();
        FinishQuad.RaiseCanExecuteChanged();
        Raise(nameof(ToolHint), nameof(IsDraftShown));
    }

    /// <summary>The quad tool armed on the view: the viewed projector's draft is taken up again and its rubber band back on the wall.</summary>
    private void ResumeQuad()
    {
        if (_quadProjector is not null && ViewProjector?.Id != _quadProjector)
            SwitchDraft();
        if (!ViewPending && ViewProjector is { } pj && Drafts.Of(pj.Id) is { } kept && _quadProjector != pj.Id)
            TakeUpDraft(pj.Id, kept);
        if (_quadProjector is { } shown)
            Drafts.Persist(shown);
        if (IsDraftShown)
            SendCursor();
        Raise(nameof(ToolHint), nameof(IsDraftShown));
    }

    /// <summary>The view shows another projector: the quad in progress is kept as its own projector's draft, and the shown projector's
    /// draft, if it has one, is taken up.</summary>
    private void SwitchDraft()
    {
        if (_quadProjector is { } begun && ViewProjector?.Id == begun)
            return;
        ParkQuad();
        if (_tool == MapTool.Quad && !ViewPending && ViewProjector is { } pj && Drafts.Of(pj.Id) is { } kept)
            TakeUpDraft(pj.Id, kept);
        if (IsDraftShown)
            SendCursor();
        Raise(nameof(ViewDraftChip), nameof(IsDraftShown));
    }

    private void TakeUpDraft(string projector, IReadOnlyList<Vec2d> points)
    {
        _quadProjector = projector;
        Quad.Restore(points);
        FinishQuad.RaiseCanExecuteChanged();
        Raise(nameof(ToolHint), nameof(IsDraftShown));
    }

    /// <summary>Drops a projector's draft (its chip's Discard, or Esc while the view has the keyboard and shows it).</summary>
    public void DiscardDraft(string projector)
    {
        Drafts.Drop(projector);
        if (_quadProjector != projector)
            return;
        _quadProjector = null;
        Quad.Cancel();
        CursorOff();
        FinishQuad.RaiseCanExecuteChanged();
        Raise(nameof(ToolHint), nameof(IsDraftShown));
    }

    /// <summary>Resume: the draft's projector shown in the projector view with the quad tool armed.</summary>
    public void ResumeDraft(string projector)
    {
        if (_project?.FindProjector(projector) is not null && ViewProjector?.Id != projector)
            Select("projector", projector);
        IsProjectorView = true;
        if (Tool == MapTool.Quad)
            ResumeQuad();
        else
            Tool = MapTool.Quad;
    }

    private void SyncDraftChips()
    {
        var projectors = Drafts.Projectors;
        for (var i = DraftChips.Count - 1; i >= 0; i--)
        {
            if (!projectors.Contains(DraftChips[i].Projector))
                DraftChips.RemoveAt(i);
        }
        foreach (var pj in projectors)
        {
            if (DraftChips.FirstOrDefault(c => c.Projector == pj) is not { } chip)
            {
                var id = pj;
                chip = new ViewModels.Map.QuadDraftChip(id,
                    DeskAction.Local("quad.resume." + id, "Resume", () => ResumeDraft(id)).Look("surface"),
                    DeskAction.Local("quad.discard." + id, "Discard", () => DiscardDraft(id)).Look("close"));
                DraftChips.Add(chip);
            }
            chip.Update(Drafts.Of(pj)?.Count ?? 0, _project?.FindProjector(pj)?.Name ?? pj);
        }
        Raise(nameof(ViewDraftChip), nameof(HasDraftChips));
    }

    /// <summary>True while any projector holds an unfinished quad.</summary>
    public bool HasDraftChips => DraftChips.Count > 0;

    private void CursorOff()
    {
        if (_cursorShown && _session.CanSend)
            _ = _session.SendQuietAsync("CURSOR OFF");
        _cursorShown = false;
    }

    private bool _cursorShown;

    /// <summary>The wall shows the draft's rubber band (CURSOR at its newest point with the others as its trail): an ephemeral line that
    /// changes nothing in the project.</summary>
    private void SendCursor()
    {
        if (WallOutput is not { } o || !_session.CanSend || !Quad.IsDrawing)
            return;
        var clicks = Quad.Clicks;
        var last = clicks[^1];
        var trail = clicks.Count > 1 ? " TRAIL " + string.Join(' ', clicks.Take(clicks.Count - 1).Select(c => VerbText.Num(Math.Round(c.X, 4)) + " " + VerbText.Num(Math.Round(c.Y, 4)))) : "";
        _session.Throttle.Post("cursor", $"CURSOR {o.Id} {VerbText.Num(Math.Round(last.X, 4))} {VerbText.Num(Math.Round(last.Y, 4))}{trail}");
        _cursorShown = true;
    }

    /// <summary>Esc's draft rung for the quad: while the view has the keyboard and shows the draft, the draft is discarded.</summary>
    public bool CancelQuadDraft()
    {
        if (!IsDraftShown || !ViewHasKeyboard || _quadProjector is not { } pj)
            return false;
        DiscardDraft(pj);
        return true;
    }

    /// <summary>A click of the quad tool at a wall point: drawn at once, kept as the projector's draft, and the wall shows the same rubber
    /// band (CURSOR with its trail).</summary>
    public void QuadClick(Vec2d wall)
    {
        if (ViewProjector is not { } pj)
            return;
        // The tool's points are another projector's (the view moved without a switch): they are that projector's draft, never this one's.
        if (_quadProjector is not null && _quadProjector != pj.Id)
            SwitchDraft();
        if (!Quad.Click(wall))
            return;
        _quadProjector = pj.Id;
        Drafts.Keep(pj.Id, [.. Quad.Clicks]);
        Raise(nameof(ToolHint), nameof(IsDraftShown));
        FinishQuad.RaiseCanExecuteChanged();
        SendCursor();
    }

    /// <summary>A placed point of the draft dragged to a wall point (the desk alone: nothing reaches the project before Enter).</summary>
    public void MoveDraftPoint(int index, Vec2d wall)
    {
        if (!IsDraftShown)
            return;
        if (Quad.MovePoint(index, wall))
        {
            // Held on the desk while the gesture lasts; desk.json is written when it ends.
            Drafts.Keep(_quadProjector!, [.. Quad.Clicks], persist: false);
            SendCursor();
        }
        SceneChanged?.Invoke();
    }

    /// <summary>A placed point's drag let go: the draft as it stands now is written to desk.json.</summary>
    public void EndDraftDrag()
    {
        if (_quadProjector is { } pj)
            Drafts.Persist(pj);
    }

    /// <summary>The line the four corners make: wall pixels of the output that shows the projector (its quarter turns applied, as the
    /// node's wall frame reads them), or raster pixels when none does.</summary>
    public string? QuadLine(IReadOnlyList<Vec2d> corners)
    {
        ArgumentNullException.ThrowIfNull(corners);
        if (corners.Count != 4 || ViewProjector is not { } pj || _project is not { } p || ViewModels.Map.QuadWall.Of(p, pj.Id) is not { } wall)
            return null;
        var points = string.Join(' ', corners.Select(c => wall.PixelsAt(c)).Select(c => VerbText.Num(Math.Round(c.X, 2)) + " " + VerbText.Num(Math.Round(c.Y, 2))));
        return $"SURFACE QUAD {pj.Id} {points} {wall.Units}";
    }

    private async Task FinishQuadAsync()
    {
        if (!Quad.IsReady || QuadLine(Quad.Clicks) is not { } line || _quadProjector is not { } pj)
            return;
        var points = Quad.Clicks.ToArray();
        Quad.Take();
        _quadProjector = null;
        // The quad is on its way: it is no draft while the reply is due, so no push takes it up again and no park keeps it.
        Drafts.Drop(pj);
        FinishQuad.RaiseCanExecuteChanged();
        Raise(nameof(ToolHint), nameof(IsDraftShown));
        await _session.Throttle.CommitAsync();
        CursorOff();
        var reply = await _session.SendAsync(line);
        if (!reply.Ok)
        {
            // The node refused the quad (too small, crossed): the four points are the projector's draft again, to move and send again.
            Drafts.Keep(pj, points);
            if (!Quad.IsDrawing && _tool == MapTool.Quad && ViewProjector?.Id == pj)
                TakeUpDraft(pj, points);
            return;
        }
        if (ViewModels.Map.QuadTool.Field(reply.Text, "id") is not { Length: > 0 } id)
            return;
        // The new quad is selected with the Select tool (unless Keep quad tool), its first corner on the desk and on the wall.
        if (!_keepQuadTool)
            Tool = MapTool.Select;
        if (_project?.FindSurface(id) is not null)
            Select("surface", id);
        else
        {
            _selectedKind = "surface";
            _selectedId = id;
            _drawn = id;
            _selectedCorner = 0;
            Raise(nameof(SelectedQuad), nameof(SelectedCorner));
            TellWall($"{id} {PixelQuadMath.CornerNames[0]}");
        }
    }

    /// <summary>The wall shows the selected quad's selected corner (HANDLE SELECT &lt;sf&gt; &lt;c&gt;), or nothing selected (HANDLE SELECT
    /// NONE) once a quad's selection goes; told only when that moves.</summary>
    private void TellWallHandle()
    {
        if (SelectedQuad is { } q)
            TellWall($"{q.Id} {PixelQuadMath.CornerNames[_selectedCorner]}");
        else if (_drawn is null)
            TellWall("NONE");
    }

    /// <summary>Tells the wall a handle (HANDLE SELECT), quietly, unless it shows it already: what this desk last told it while no push
    /// has come since, else the node's published handle (STATE.air.overlay.handle), which another client or a restart may have moved. A
    /// line the gate refuses (armed) is not sent and says nothing; the handle is held and told when the gate next lets it through.</summary>
    private void TellWall(string handle)
    {
        var line = "HANDLE SELECT " + handle;
        if (!_session.CanSend || _session.GateRefusalOf(line) is not null)
        {
            _heldHandle = handle;
            return;
        }
        _heldHandle = null;
        if (_handleEpoch != _session.LinkEpoch)
        {
            _handleEpoch = _session.LinkEpoch;
            _wallHandle = null;
        }
        var state = _session.State;
        var shown = _wallHandle is { } told && ReferenceEquals(state, _wallHandleAt) ? told : PublishedHandle(state) ?? _wallHandle ?? "NONE";
        if (handle == shown)
            return;
        _wallHandle = handle;
        _wallHandleAt = state;
        _ = _session.SendQuietAsync(line);
    }

    /// <summary>The handle the node's wall shows, as STATE.air.overlay.handle says it ("sf TL", "sf 2,1", "NONE"), or null when STATE
    /// does not say.</summary>
    public static string? PublishedHandle(StateSnapshot s)
    {
        ArgumentNullException.ThrowIfNull(s);
        if (s.Root.ValueKind != System.Text.Json.JsonValueKind.Object || !s.Root.TryGetProperty("air", out var air) || air.Obj("overlay") is not { } o
            || !o.TryGetProperty("handle", out var h))
            return null;
        if (h.ValueKind != System.Text.Json.JsonValueKind.Object)
            return "NONE";
        var surface = h.Str("surface");
        return h.Str("kind") == "corner"
            ? $"{surface} {PixelQuadMath.CornerNames[Math.Clamp((int)h.Long("index"), 0, 3)]}"
            : string.Create(CultureInfo.InvariantCulture, $"{surface} {h.Long("col")},{h.Long("row")}");
    }

    private void StepCorner(int step)
    {
        if (IsDraftShown)
        {
            Quad.Step(step);
            SceneChanged?.Invoke();
            return;
        }
        if (SelectedQuad is not { } q)
            return;
        _selectedCorner = (_selectedCorner + step + 4) % 4;
        _lastCorner[q.Id] = _selectedCorner;
        Raise(nameof(SelectedCorner));
        SceneChanged?.Invoke();
        TellWall($"{q.Id} {PixelQuadMath.CornerNames[_selectedCorner]}");
    }

    /// <summary>An arrow (Shift ×10, Alt ×0.1): the draft's selected point while the quad tool shows a draft (on the desk alone), else a
    /// selected pin's pixel by that many raster pixels (SET &lt;pin&gt; pixel), else the selected quad's corner by that many wall pixels
    /// (SURFACE CORNER … BY … PX).</summary>
    public void Nudge(double dx, double dy)
    {
        if (IsDraftShown && ViewProjector is { } pj)
        {
            var (w, h) = WallOutput is { } o ? (o.Width, o.Height) : (pj.Intrinsics.Width, pj.Intrinsics.Height);
            var i = Quad.Selected;
            MoveDraftPoint(i, Quad.Clicks[i] + new Vec2d(dx / Math.Max(1, w), dy / Math.Max(1, h)));
            return;
        }
        if (SelectedPin is { } pin)
        {
            var from = PinTarget(pin);
            var to = new Vec2d(Math.Round(from.X + dx, 3), Math.Round(from.Y + dy, 3));
            var line = VerbText.SetJson(pin.Id, "pixel", to);
            if (_session.GateWords(line) is { } why)
            {
                _session.Notify(ToastKind.Refused, why, line);
                return;
            }
            if (_pinRun?.Pin != pin.Id)
                _pinRun = new PinRun(pin.Id, pin.Pixel, _session.LinkEpoch);
            _pinRun.Sent.Add(to);
            _pinRun.Revision = _session.ProjectRevision;
            LastPinNudge = SendPinAsync(line, _pinRun);
            return;
        }
        if (SelectedQuad is not { } q)
            return;
        SendGated($"SURFACE CORNER {q.Id} {PixelQuadMath.CornerNames[_selectedCorner]} BY {VerbText.Num(dx)} {VerbText.Num(dy)} PX");
    }

    /// <summary>The send the last arrow on a pin started (awaitable by whoever needs to know it landed).</summary>
    public Task LastPinNudge { get; private set; } = Task.CompletedTask;

    /// <summary>The selected pin's arrows in flight: where the pin was when they began, the pixels sent since (in order), and the project
    /// revision the last was sent after. The node pushes the project after its edits on a trailing throttle, so the mirror lags quick
    /// arrows; each arrow moves on from the last pixel sent, until a mirrored revision carries it, or moves the pin to a pixel none of
    /// the arrows sent (another edit: the mirror wins).</summary>
    private sealed class PinRun(string pin, Vec2d origin, long epoch)
    {
        public string Pin { get; } = pin;

        public Vec2d Origin { get; } = origin;

        public long Epoch { get; } = epoch;

        public List<Vec2d> Sent { get; } = [];

        public long Revision { get; set; }
    }

    private PinRun? _pinRun;

    /// <summary>Where the next arrow on <paramref name="pin"/> moves from: the last pixel sent while the mirror has not carried it, else
    /// the pin as the mirror has it.</summary>
    private Vec2d PinTarget(CalibrationPin pin)
    {
        if (_pinRun is not { } run || run.Pin != pin.Id || run.Epoch != _session.LinkEpoch || run.Sent.Count == 0)
        {
            _pinRun = null;
            return pin.Pixel;
        }
        var last = run.Sent[^1];
        if (Near(pin.Pixel, last))
        {
            // The mirror carries the last arrow: it is the truth again.
            _pinRun = null;
            return pin.Pixel;
        }
        if (_session.ProjectRevision == run.Revision || Near(pin.Pixel, run.Origin) || run.Sent.Exists(p => Near(p, pin.Pixel)))
            return last;
        _pinRun = null;
        return pin.Pixel;
    }

    private static bool Near(Vec2d a, Vec2d b) => Math.Abs(a.X - b.X) < 1e-6 && Math.Abs(a.Y - b.Y) < 1e-6;

    /// <summary>Sends an arrow's line; a refusal (the toast says why) ends the run, so the next arrow moves on from the mirror.</summary>
    private async Task SendPinAsync(string line, PinRun run)
    {
        var reply = await _session.SendAsync(line);
        if (!reply.Ok && ReferenceEquals(_pinRun, run))
            _pinRun = null;
    }

    /// <summary>True when the arrows move something now (MAP: the draft shown, a pin or a quad selected).</summary>
    public bool CanNudge => IsDraftShown || SelectedPin is not null || SelectedQuad is not null;

    /// <summary>The selected pin as the mirrored project has it, on the viewed projector, or null.</summary>
    public CalibrationPin? SelectedPin => _selectedPinId is { } id && ViewProjector?.Pins.Find(id) is { } pin ? pin : null;

    /// <summary>A pin picked (the Pin tool's press on it, or its row): the arrows move it.</summary>
    public void SelectPin(string? pinId)
    {
        if (_selectedPinId == pinId)
            return;
        _selectedPinId = pinId;
        _pinRun = null;
        if (Inspector.Current is ProjectorInspector pi)
        {
            foreach (var row in pi.Pins)
                row.IsSelected = row.Id == pinId;
        }
        Raise(nameof(SelectedPinId), nameof(CanNudge));
        NextCorner.RaiseCanExecuteChanged();
        PrevCorner.RaiseCanExecuteChanged();
        SceneChanged?.Invoke();
    }

    /// <summary>A quad's corner dragged in the projector view to a point of the view: SURFACE CORNER &lt;sf&gt; &lt;c&gt; TO &lt;x&gt; &lt;y&gt; RASTER, the
    /// raster point under the pointer (the output's warp taken back; the view draws no quarter turns, so none is taken back) in the
    /// pixels the node's wall frame reads RASTER in, through the throttle (the newest in each interval); the release commits. The node
    /// keeps a burst of one corner's moves as one undo step. A point off the warped picture sends nothing.</summary>
    public void DragQuadCorner(string surfaceId, int corner, Vec2d wall, bool commit)
    {
        if (ViewProjector is { } pj && _project is { } p && ViewModels.Map.QuadWall.Of(p, pj.Id) is { } frame
            && frame.RasterAt(new Vec2d(Math.Clamp(wall.X, 0, 1), Math.Clamp(wall.Y, 0, 1))) is { } raster)
        {
            var x = Math.Round(raster.X * frame.Width, 2);
            var y = Math.Round(raster.Y * frame.Height, 2);
            _session.Throttle.Post($"quad {surfaceId} {corner}",
                $"SURFACE CORNER {surfaceId} {PixelQuadMath.CornerNames[corner]} TO {VerbText.Num(x)} {VerbText.Num(y)} RASTER");
        }
        if (commit)
            _ = _session.Throttle.CommitAsync();
    }

    /// <summary>A corner of a quad picked in the view (a press on it with the Select tool): the quad selected with that corner, on the
    /// desk and on the wall.</summary>
    public void PickQuadCorner(string surfaceId, int corner)
    {
        if (SelectedQuad?.Id != surfaceId)
            Select("surface", surfaceId);
        if (SelectedQuad is not { } q || _selectedCorner == corner)
            return;
        _selectedCorner = corner;
        _lastCorner[q.Id] = corner;
        Raise(nameof(SelectedCorner));
        SceneChanged?.Invoke();
        TellWall($"{q.Id} {PixelQuadMath.CornerNames[corner]}");
    }

    /// <summary>Sends a line the gate lets through (true); one it refuses is not sent and the toast gives the node's words (false).</summary>
    private bool SendGated(string line)
    {
        if (_session.GateWords(line) is { } why)
        {
            _session.Notify(ToastKind.Refused, why, line);
            return false;
        }
        _session.Fire(line);
        return true;
    }

    private string PatternTarget() => _isProjectorView && ViewOutput is { } o ? " ON " + o.Id : "";

    private void BuildAddActions()
    {
        var s = _session;
        Outliner[0].AddActions =
        [
            DeskAction.Run(s, "add.plane", "Plane surface", ct => AddPrimitiveSurfaceAsync("plane", ct), () => "ADD mesh {…} · ADD surface {…}").Look("plus"),
            DeskAction.Run(s, "add.box", "Box surface", ct => AddPrimitiveSurfaceAsync("box", ct), () => "ADD mesh {…} · ADD surface {…}").Look("plus"),
            DeskAction.Run(s, "add.cylinder", "Cylinder surface", ct => AddPrimitiveSurfaceAsync("cylinder", ct), () => "ADD mesh {…} · ADD surface {…}").Look("plus"),
        ];
        Outliner[1].AddActions =
        [
            DeskAction.Send(s, "add.projector", "Projector", () => "ADD projector " + NewProjectorJson()).Look("plus"),
        ];
        Outliner[3].AddActions =
        [
            DeskAction.Send(s, "add.camera", "Camera", () => "ADD camera " + VerbText.Json(new CameraRig("", "Camera", CameraSourceKind.Device, "video0",
                Intrinsics.FromFieldOfView(1920, 1080, 70), LensDistortion.None, Camera.Pose, false))).Look("plus"),
        ];
        Outliner[4].AddActions =
        [
            DeskAction.Send(s, "add.viewpoint", "Viewpoint from this view", () => "ADD viewpoint " + VerbText.Json(new Viewpoint("", "Viewpoint",
                Intrinsics.FromFieldOfView(1920, 1080, Camera.FovDegrees), Camera.Pose))).Look("plus"),
        ];
        Outliner[6].AddActions =
        [
            DeskAction.Send(s, "add.output", "Output", () => "ADD output " + NewOutputJson()).Look("plus"),
        ];
    }

    private string NewProjectorJson()
    {
        var n = (_project?.Stage.Projectors.Length ?? 0) + 1;
        var pj = new Projector("", "Projector " + n.ToString(CultureInfo.InvariantCulture), Intrinsics.FromThrowRatio(1920, 1080, 1.4), LensDistortion.None,
            Pose.LookAt(Camera.Eye, Camera.Target, Vec3d.UnitY), 0, n % 2 == 0 ? "#FFB74D" : "#4FC3F7", false, [], SolveLocks.None, 0);
        return VerbText.Json(pj);
    }

    public string NewOutputJson()
    {
        var n = (_project?.Outputs.Length ?? 0) + 1;
        var x = (_project?.Outputs.Select(o => o.Plan.Right).DefaultIfEmpty(0).Max() ?? 0) + 40;
        var o = new Output("", "Output " + n.ToString(CultureInfo.InvariantCulture), OutputKind.Virtual, "", 1920, 1080, 60, OutputMode.Canvas, null,
            _project?.Canvases is { Length: > 0 } cv ? SourceRef.Canvas(cv[0].Id) : SourceRef.None, new RectD(0, 0, 1920, 1080), OutputWarp.None,
            BlendSettings.Default with { Auto3D = false }, ColorTrim.Neutral, [], new RectD(x, 0, 1920, 1080), 0, false);
        return VerbText.Json(o);
    }

    /// <summary>A primitive mesh and a surface on it, in front of the camera's target (two verbs: the mesh
    /// first, then the surface that names it).</summary>
    public async Task AddPrimitiveSurfaceAsync(string primitive, CancellationToken ct)
    {
        var parameters = primitive switch
        {
            "box" => ParamSet.Empty.With("width", 1.0).With("height", 1.0).With("depth", 1.0),
            "cylinder" => ParamSet.Empty.With("radius", 0.3).With("height", 2.0).With("segments", 24),
            _ => ParamSet.Empty.With("width", 2.0).With("height", 1.5),
        };
        var name = char.ToUpperInvariant(primitive[0]) + primitive[1..];
        var mesh = new MeshAsset("", name, MeshOrigin.Primitive, "", "", primitive, parameters, 0, 0);
        var reply = await _session.SendAsync("ADD mesh " + VerbText.Json(mesh), ct);
        if (!reply.Ok)
            return;
        var meshId = reply.Text.Trim();
        var surface = new Surface("", name, meshId, Transform3.Identity with { Position = Camera.Target }, [], SurfaceContent.Empty, 1, [], true, false, "#CE93D8", []);
        var added = await _session.SendAsync("ADD surface " + VerbText.Json(surface), ct);
        if (added.Ok)
            Select("surface", added.Text.Trim());
    }

    /// <summary>The size the engine renders the editor view at: the viewport's own aspect, at most 1920
    /// wide, in steps of 32 so a window drag does not ask for a new size every pixel.</summary>
    public string EditorSize
    {
        get => _editorSize;
        private set => Set(ref _editorSize, value);
    }

    private string _editorSize = "1280x720";

    /// <summary>The editor camera moved (or the viewport changed size): the engine's editor view follows
    /// (throttled, quiet). The camera's intrinsics are for the size the engine is asked to render.</summary>
    public void CameraMoved(double width, double height)
    {
        if (width >= 1 && height >= 1)
        {
            var w = Math.Min(1920, Math.Max(64, Math.Round(width / 32) * 32));
            var h = Math.Max(32, Math.Round(w * height / width / 2) * 2);
            EditorSize = string.Create(CultureInfo.InvariantCulture, $"{w:0}x{h:0}");
            _editorPixels = (w, h);
            _session.Throttle.Post("view " + EditorViewId, $"VIEW {EditorViewId} CAMERA " + Camera.ToJson(w, h));
        }
        SceneChanged?.Invoke();
    }

    /// <summary>Pin tool: a click on a surface in projector view adds a pin there (world point under the
    /// pixel, the pixel as it is now).</summary>
    public void AddPin(Projector pj, Vec3d world, Vec2d pixel)
    {
        ArgumentNullException.ThrowIfNull(pj);
        var pin = new CalibrationPin("", world, pixel, true);
        var json = VerbText.Json(pin);
        _session.Fire("ADD pin {\"parent\":" + VerbText.JsonString(pj.Id) + "," + json[1..]);
    }

    /// <summary>A pin dragged to a new pixel. The live residual is the desk's own (the pose as solved).</summary>
    public void MovePin(string pinId, Vec2d pixel, bool commit)
    {
        _session.Throttle.Post("pin " + pinId, VerbText.SetJson(pinId, "pixel", pixel));
        if (commit)
            _ = _session.Throttle.CommitAsync();
    }

    /// <summary>A warp lattice node dragged: its offset from rest (absolute, so a coalesced drag is
    /// idempotent), WARP NODE.</summary>
    public void MoveLatticeNode(Output output, int column, int row, Vec2d offset, bool commit)
    {
        ArgumentNullException.ThrowIfNull(output);
        _session.Throttle.Post($"warp {output.Id} {column} {row}",
            $"WARP NODE {output.Id} {VerbText.Int(column)} {VerbText.Int(row)} {VerbText.Num(offset.X)} {VerbText.Num(offset.Y)}");
        if (commit)
            _ = _session.Throttle.CommitAsync();
    }

    /// <summary>A corner-pin handle dragged (0 TL, 1 TR, 2 BR, 3 BL): SET &lt;output&gt; warp.pin.&lt;corner&gt;.</summary>
    public void MoveCorner(Output output, int corner, Vec2d offset, bool commit)
    {
        ArgumentNullException.ThrowIfNull(output);
        var path = "warp.pin." + corner switch { 0 => "topLeft", 1 => "topRight", 2 => "bottomRight", _ => "bottomLeft" };
        if (!output.Warp.Enabled)
            _session.Throttle.Post(output.Id + " warp.enabled", VerbText.SetBool(output.Id, "warp.enabled", true));
        _session.Throttle.Post(output.Id + " " + path, VerbText.SetJson(output.Id, path, offset));
        if (commit)
            _ = _session.Throttle.CommitAsync();
    }

    /// <summary>A mask's node moved (the whole path is sent: SET &lt;output&gt; masks.&lt;i&gt;.path).</summary>
    public void MoveMask(Output output, int maskIndex, BezierPath path, bool commit)
    {
        ArgumentNullException.ThrowIfNull(output);
        _session.Throttle.Post($"{output.Id} mask {maskIndex}", VerbText.SetJson(output.Id, $"masks.{VerbText.Int(maskIndex)}.path", path));
        if (commit)
            _ = _session.Throttle.CommitAsync();
    }

    /// <summary>The gizmo moved a surface: SET &lt;surface&gt; transform.position.</summary>
    public void MoveSurface(string surfaceId, Vec3d position, bool commit)
    {
        _session.Throttle.Post(surfaceId + " position", VerbText.SetJson(surfaceId, "transform.position", position));
        if (commit)
            _ = _session.Throttle.CommitAsync();
    }

    /// <summary>The pen closed a path: ADD mask on the projector's output (or the selected surface in the
    /// 3D view, in its UV space).</summary>
    public async Task CommitPenAsync(BezierPath path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var parent = _isProjectorView ? ViewOutput?.Id : _selectedKind == "surface" ? _selectedId : ViewOutput?.Id;
        if (parent is null)
        {
            _session.Notify(ToastKind.Refused, "A mask needs an output or a surface to belong to — select one first.");
            Pen.Reset();
            return;
        }
        _maskCount++;
        var name = "Mask " + _maskCount.ToString(CultureInfo.InvariantCulture);
        var reply = await _session.SendAsync("ADD mask " + Pen.MaskJson(parent, name));
        if (reply.Ok)
            SelectedMaskId = reply.Text.Trim();
        Pen.Reset();
    }

    public void Dispose() => Pen.Cancel();
}

/// <summary>A group in the outliner (Surfaces, Projectors…) with its add menu.</summary>
public sealed class OutlinerGroup : ObservableObject
{
    private IReadOnlyList<DeskAction> _addActions = [];
    private bool _isExpanded = true;
    private string _count = "";

    public OutlinerGroup(string title, string icon)
    {
        Title = title;
        Icon = icon;
    }

    public string Title { get; }

    public string Icon { get; }

    /// <summary>The Surfaces group: its unfinished quads' chips sit under it.</summary>
    public bool IsSurfaces => Icon == "surface";

    public ObservableCollection<OutlinerNode> Nodes { get; } = [];

    public IReadOnlyList<DeskAction> AddActions
    {
        get => _addActions;
        set
        {
            if (Set(ref _addActions, value))
                Raise(nameof(CanAdd));
        }
    }

    public bool CanAdd => _addActions.Count > 0;

    public bool IsExpanded
    {
        get => _isExpanded;
        set => Set(ref _isExpanded, value);
    }

    public string Count
    {
        get => _count;
        private set => Set(ref _count, value);
    }

    public void Refresh() => Count = Nodes.Count.ToString(CultureInfo.InvariantCulture);
}

/// <summary>One object in the outliner: its name, colour, visibility and lock toggles, and its right-click
/// menu (the same actions as its inspector's).</summary>
public sealed class OutlinerNode : ObservableObject, IMenuTarget
{
    IReadOnlyList<DeskAction> IMenuTarget.MenuActions => Actions;

    private readonly MapViewModel _map;
    private string _name = "";
    private string _detail = "";
    private string _color = "grey";
    private bool _visible = true;
    private bool _locked;
    private bool _isSelected;
    private IReadOnlyList<DeskAction> _actions = [];

    public OutlinerNode(MapViewModel map, string kind, string id)
    {
        _map = map;
        Kind = kind;
        Id = id;
        SelectCommand = new RelayCommand(() => _map.Select(Kind, Id));
    }

    public string Kind { get; }

    public string Id { get; }

    public RelayCommand SelectCommand { get; }

    public bool HasEye => Kind is "surface" or "output";

    public bool HasLock => Kind is "surface" or "projector";

    public DeskAction? EyeAction { get; private set; }

    public DeskAction? LockAction { get; private set; }

    public string Name
    {
        get => _name;
        private set => Set(ref _name, value);
    }

    public string Detail
    {
        get => _detail;
        private set => Set(ref _detail, value);
    }

    /// <summary>#RRGGBB from the model (a surface's or projector's colour) or a light word.</summary>
    public string Color
    {
        get => _color;
        private set => Set(ref _color, value);
    }

    public bool Visible
    {
        get => _visible;
        private set => Set(ref _visible, value);
    }

    public bool Locked
    {
        get => _locked;
        private set => Set(ref _locked, value);
    }

    public bool IsSelected
    {
        get => _isSelected;
        set => Set(ref _isSelected, value);
    }

    public IReadOnlyList<DeskAction> Actions
    {
        get => _actions;
        private set => Set(ref _actions, value);
    }

    private DeskSession Session => _map.SessionForNodes;

    public void UpdateSurface(Surface s, Project p)
    {
        Name = s.Name;
        Color = s.Color;
        Visible = s.Visible;
        Locked = s.Locked;
        var mesh = p.FindMesh(s.MeshId);
        var on = s.Pixel is { } q ? "quad on " + (p.FindProjector(q.ProjectorId)?.Name ?? q.ProjectorId) : mesh?.Name ?? "no mesh";
        Detail = $"{on} · {SourceWords(s.Content.Source, p)}";
        if (_actions.Count == 0)
        {
            var session = Session;
            EyeAction = DeskAction.Send(session, "show", "Show", () => $"SURFACE SHOW {Id} {(_visible ? "OFF" : "ON")}", () => _visible ? "Hide" : "Show",
                isChecked: () => _visible).Look("eye");
            LockAction = DeskAction.Send(session, "lock", "Lock", () => VerbText.SetBool(Id, "locked", !_locked), () => _locked ? "Unlock" : "Lock",
                isChecked: () => _locked).Look("lock");
            Actions =
            [
                DeskAction.Local("select", "Select", () => _map.Select(Kind, Id)).Look("select"),
                EyeAction,
                LockAction,
                ContentMenu(session, p),
                DeskAction.Send(session, "duplicate", "Duplicate", () => _map.Project?.FindSurface(Id) is { } sf
                    ? "ADD surface " + VerbText.Json(sf with { Id = "", Name = sf.Name + " copy", Transform = sf.Transform with { Position = sf.Transform.Position + new Vec3d(0.3, 0, 0.3) } })
                    : null).Look("plus", separator: true),
                DeskAction.Send(session, "remove", "Remove", () => "REMOVE " + Id).Look("trash", danger: true, separator: true),
            ];
        }
        Actions.RefreshAll();
    }

    private DeskAction ContentMenu(DeskSession session, Project p)
    {
        var menu = DeskAction.Local("content", "Content", () => _map.Select(Kind, Id)).Look("media");
        foreach (var pl in p.Playlists)
            menu.Children.Add(DeskAction.Send(session, "c." + pl.Id, "Playlist · " + pl.Name, () => $"SURFACE SOURCE {Id} playlist:{pl.Id}"));
        foreach (var g in p.Generators)
            menu.Children.Add(DeskAction.Send(session, "c." + g.Id, "Generator · " + g.Name, () => $"SURFACE SOURCE {Id} generator:{g.Id}"));
        foreach (var c in p.Canvases)
            menu.Children.Add(DeskAction.Send(session, "c." + c.Id, "Canvas · " + c.Name, () => $"SURFACE SOURCE {Id} canvas:{c.Id}"));
        menu.Children.Add(DeskAction.Send(session, "c.white", "Colour · white", () => $"SURFACE SOURCE {Id} color:#FFFFFF"));
        menu.Children.Add(DeskAction.Send(session, "c.none", "Nothing", () => $"SURFACE SOURCE {Id} none"));
        return menu;
    }

    public void UpdateProjector(Projector x, Project p)
    {
        Name = x.Name;
        Color = x.Color;
        Locked = x.Locked;
        var output = p.Outputs.FirstOrDefault(o => o.ProjectorId == x.Id);
        Detail = string.Create(CultureInfo.InvariantCulture,
            $"{x.Intrinsics.Width}×{x.Intrinsics.Height} · throw {x.Intrinsics.ThrowRatio:0.00} · {(x.SolveResidualPx > 0 ? $"solved {x.SolveResidualPx:0.0} px" : "not solved")}{(output is null ? " · no output" : "")}");
        if (_actions.Count == 0)
        {
            var session = Session;
            LockAction = DeskAction.Send(session, "lock", "Lock", () => VerbText.SetBool(Id, "locked", !_locked), () => _locked ? "Unlock" : "Lock",
                isChecked: () => _locked).Look("lock");
            Actions =
            [
                DeskAction.Local("select", "Select", () => _map.Select(Kind, Id)).Look("select"),
                DeskAction.Local("view", "Projector view", () =>
                {
                    _map.Select(Kind, Id);
                    _map.IsProjectorView = true;
                }).Look("projector"),
                DeskAction.Send(session, "identify", "Identify", () => _map.Project?.Outputs.FirstOrDefault(o => o.ProjectorId == Id) is { } o
                    ? "PATTERN identify ON " + o.Id : "PATTERN identify").Look("identify"),
                DeskAction.Send(session, "solve", "Solve from pins", () => "PROJECTOR SOLVE " + Id).Look("solve"),
                LockAction,
                DeskAction.Send(session, "remove", "Remove", () => "REMOVE " + Id).Look("trash", danger: true, separator: true),
            ];
        }
        Actions.RefreshAll();
    }

    public void UpdateMesh(MeshAsset m, Project p)
    {
        Name = m.Name;
        Color = "grey";
        var used = p.Stage.Surfaces.Count(s => s.MeshId == m.Id);
        Detail = m.Origin == MeshOrigin.Primitive
            ? string.Create(CultureInfo.InvariantCulture, $"{m.Primitive} · {used} surface(s)")
            : string.Create(CultureInfo.InvariantCulture, $"{m.Origin.ToString().ToLowerInvariant()} · {m.Triangles:N0} triangles · {used} surface(s)");
        if (_actions.Count == 0)
        {
            var session = Session;
            Actions =
            [
                DeskAction.Local("select", "Select", () => _map.Select(Kind, Id)).Look("select"),
                DeskAction.Send(session, "surface", "Make a surface of it", () => "ADD surface " + VerbText.Json(new Surface("", Name, Id, Transform3.Identity, [],
                    SurfaceContent.Empty, 1, [], true, false, "#CE93D8", []))).Look("surface"),
                DeskAction.Send(session, "remove", "Remove", () => "REMOVE " + Id).Look("trash", danger: true, separator: true),
            ];
        }
    }

    public void UpdatePlain(string name, string detail, string color)
    {
        Name = name;
        Detail = detail;
        Color = color;
        if (_actions.Count == 0)
        {
            var session = Session;
            var list = new List<DeskAction> { DeskAction.Local("select", "Select", () => _map.Select(Kind, Id)).Look("select") };
            if (Kind == "scan")
                list.Add(DeskAction.Send(session, "apply", "Apply", () => "SCAN APPLY " + Id).Look("scan"));
            list.Add(DeskAction.Send(session, "remove", "Remove", () => "REMOVE " + Id).Look("trash", danger: true, separator: true));
            Actions = list;
        }
    }

    public void UpdateOutput(Output o, Project p)
    {
        Name = o.Name;
        Visible = o.Enabled;
        var pj = p.FindProjector(o.ProjectorId);
        Color = pj?.Color ?? "grey";
        Detail = string.Create(CultureInfo.InvariantCulture, $"{o.Mode.ToString().ToLowerInvariant()}{(pj is null ? "" : " · " + pj.Name)} · {(o.DisplayKey.Length > 0 ? o.DisplayKey : o.Kind.ToString())}");
        if (_actions.Count == 0)
        {
            var session = Session;
            EyeAction = DeskAction.Send(session, "enable", "Enable", () => $"OUTPUT ENABLE {Id} {(_visible ? "OFF" : "ON")}", () => _visible ? "Close output" : "Open output",
                isChecked: () => _visible).Look("eye", danger: true);
            var patterns = DeskAction.Local("pattern", "Test pattern", () => { }).Look("pattern");
            patterns.Children.Add(DeskAction.Send(session, "p.off", "Off", () => $"PATTERN OFF ON {Id}"));
            foreach (var k in GeneratorCatalog.All.Where(k => k.Family == "pattern"))
                patterns.Children.Add(DeskAction.Send(session, "p." + k.Id, k.Name, () => $"PATTERN {k.Id[8..]} ON {Id}"));
            Actions =
            [
                DeskAction.Local("select", "Select", () => _map.Select(Kind, Id)).Look("select"),
                EyeAction,
                DeskAction.Send(session, "identify", "Identify", () => "OUTPUT IDENTIFY " + Id).Look("identify"),
                patterns,
                DeskAction.Send(session, "warpReset", "Reset warp", () => "WARP RESET " + Id).Look("warp", separator: true),
                DeskAction.Send(session, "remove", "Remove", () => "REMOVE " + Id).Look("trash", danger: true, separator: true),
            ];
        }
        Actions.RefreshAll();
    }

    public void UpdateOutputState(StateSnapshot s)
    {
        if (s.FindOutput(Id) is { } o && o.Pattern is { } pattern)
            Detail = Detail.Split(" · pattern", 2)[0] + " · pattern " + pattern;
    }

    private static string SourceWords(SourceRef src, Project p) => src.Kind switch
    {
        SourceKind.None => "no content",
        SourceKind.Media => p.FindMedia(src.Id)?.Name ?? src.Id,
        SourceKind.Playlist => "playlist " + (p.FindPlaylist(src.Id)?.Name ?? src.Id),
        SourceKind.Generator => p.FindGenerator(src.Id)?.Name ?? src.Id,
        SourceKind.Canvas => p.FindCanvas(src.Id)?.Name ?? src.Id,
        SourceKind.Color => src.Id,
        _ => src.ToString(),
    };
}

/// <summary>The air's edit overlay as STATE.air.overlay says it: on, and the outputs it covers (none: every output).</summary>
public sealed record WallFacts(bool On, System.Collections.Immutable.ImmutableArray<string> Outputs)
{
    public static readonly WallFacts Off = new(false, []);

    public bool Equals(WallFacts? other) => other is not null && On == other.On && Outputs.AsSpan().SequenceEqual(other.Outputs.AsSpan());

    public override int GetHashCode() => HashCode.Combine(On, Outputs.Length);

    public static WallFacts Read(StateSnapshot s)
    {
        ArgumentNullException.ThrowIfNull(s);
        if (s.Root.ValueKind != System.Text.Json.JsonValueKind.Object || !s.Root.TryGetProperty("air", out var air) || air.Obj("overlay") is not { } o)
            return Off;
        return new WallFacts(o.Bool("on"), [.. o.Arr("outputs").Where(x => x.ValueKind == System.Text.Json.JsonValueKind.String).Select(x => x.GetString()!)]);
    }
}
