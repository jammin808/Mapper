using System.Collections.ObjectModel;
using System.Globalization;
using PlayMap.App.Infrastructure;
using PlayMap.App.Link;
using PlayMap.App.Scene;
using PlayMap.App.State;
using PlayMap.Core.Content;
using PlayMap.Core.Geometry;
using PlayMap.Core.Model;
using PlayMap.Core.Numerics;

namespace PlayMap.App.ViewModels;

public enum MapTool
{
    Select,
    Pin,
    Warp,
    Pen,
}

/// <summary>MAP: the scene outliner, the editor view (the engine's picture of an orbit camera with the desk's
/// overlays — frusta, pins, selection, gizmo) or the projector view (what one projector sees, with its warp
/// handles, its pen masks and its pins), the tools, and the inspector. Every edit is a verb; drags go through
/// the throttle and land as one undo step on the engine (it coalesces a key's edits).</summary>
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

    public MapViewModel(DeskSession session)
    {
        _session = session;
        Inspector = new InspectorHost(session);
        Pen.Closed += path => LastPenCommit = CommitPenAsync(path);
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
            Raise(nameof(IsSelectTool), nameof(IsPinTool), nameof(IsWarpTool), nameof(IsPenTool), nameof(ToolHint));
            if (value is MapTool.Warp or MapTool.Pen or MapTool.Pin && !_isProjectorView)
                IsProjectorView = true;
            if (value != MapTool.Pen)
                Pen.Cancel();
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

    /// <summary>One sentence: what the tool does with a click.</summary>
    public string ToolHint => _tool switch
    {
        MapTool.Pin => ViewOutputHint ?? "Click a feature on a surface to pin it; drag a pin onto the real feature. Six spread pins solve the projector.",
        MapTool.Warp => ViewOutputHint ?? "Drag the corner handles to pin the picture's corners, the lattice nodes to bend it.",
        MapTool.Pen => ViewOutputHint ?? "Click for corners, drag for smooth nodes, Alt-click a node to toggle it, click the first node to close.",
        _ => _isProjectorView ? "Click a surface to select it." : "Click to select · drag empty space to orbit · Shift-drag to pan · wheel to zoom · drag an axis to move.",
    };

    private string? ViewOutputHint => _isProjectorView && ViewOutput is null && ViewProjector is not null
        ? "No output drives this projector yet — assign one in Outputs to warp and mask it."
        : null;

    public bool IsProjectorView
    {
        get => _isProjectorView;
        set
        {
            if (Set(ref _isProjectorView, value))
            {
                Raise(nameof(Is3DView), nameof(ToolHint), nameof(ViewTitle), nameof(ViewId));
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
        : "3D view";

    /// <summary>The engine's preview view id for what the centre shows.</summary>
    public string ViewId => _isProjectorView && ViewProjector is { } pj ? "pj-" + pj.Id : "editor";

    public void Select(string kind, string? id)
    {
        _selectedKind = id is null ? "" : kind;
        _selectedId = id;
        _selectedMaskId = null;
        if (kind == "projector" && id is not null)
            _viewProjectorId = id;
        else if (kind == "output" && id is not null && _project?.FindOutput(id)?.ProjectorId is { } pj)
            _viewProjectorId = pj;
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
        Raise(nameof(SelectedId), nameof(SelectedKind), nameof(ViewProjector), nameof(ViewOutput), nameof(ViewTitle), nameof(ViewId), nameof(ToolHint));
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
        if (_selectedId is not null && !Outliner.Any(g => g.Nodes.Any(n => n.Id == _selectedId)))
            Select("", null);
        Inspector.Refresh(p, _session.State);
        Raise(nameof(ViewProjector), nameof(ViewOutput), nameof(ViewTitle), nameof(ToolHint), nameof(NodeId), nameof(ViewId));
        SceneChanged?.Invoke();
    }

    public void OnState(StateSnapshot s)
    {
        foreach (var n in Outliner[6].Nodes)
            n.UpdateOutputState(s);
        foreach (var a in PatternActions)
            a.Refresh();
        if (_project is { } p)
            Inspector.Refresh(p, s);
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
            _session.Throttle.Post("view editor", "VIEW editor CAMERA " + Camera.ToJson(w, h));
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
        Detail = $"{(mesh?.Name ?? "no mesh")} · {SourceWords(s.Content.Source, p)}";
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
