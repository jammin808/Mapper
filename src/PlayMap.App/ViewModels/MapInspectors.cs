using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Media;
using PlayMap.App.Controls;
using PlayMap.App.Infrastructure;
using PlayMap.App.Link;
using PlayMap.App.State;
using PlayMap.Core.Model;
using PlayMap.Core.Numerics;

namespace PlayMap.App.ViewModels;

/// <summary>What a surface shows and how: the content picker (media, playlist, generator, canvas, colour,
/// timer), the mapping mode, the region, opacity, colour, masks and position.</summary>
public sealed class SurfaceInspector : InspectorViewModel
{
    private string _name = "";
    private ContentKindChoice? _contentKind;
    private IReadOnlyList<ItemChoice> _contentItems = [];
    private ItemChoice? _contentItem;
    private string _colorHex = "#FFFFFF";
    private SurfaceMapping _mapping;
    private double _opacity = 1;
    private string _color = "#FFFFFF";
    private bool _visible;
    private bool _locked;
    private string _region = "";
    private IReadOnlyList<ItemChoice> _viewpoints = [];
    private ItemChoice? _viewpoint;
    private string _position = "";
    private Project? _project;

    public SurfaceInspector(DeskSession session, string id) : base(session, id)
    {
        AddMask = DeskAction.Send(session, "addMask", "Add a mask", () => "ADD mask {\"parent\":" + VerbText.JsonString(Id) + ","
            + VerbText.Json(new Mask("", "Mask", BezierPath.Rectangle(0.25, 0.25, 0.5, 0.5), MaskMode.Subtract, 0.02, false, true))[1..]).Look("pen");
        Actions =
        [
            DeskAction.Send(session, "show", "Show", () => $"SURFACE SHOW {Id} {(_visible ? "OFF" : "ON")}", () => _visible ? "Hide" : "Show", isChecked: () => _visible).Look("eye"),
            AddMask,
            DeskAction.Send(session, "remove", "Remove surface", () => "REMOVE " + Id).Look("trash", danger: true),
        ];
    }

    public override string Icon => "surface";

    public DeskAction AddMask { get; }

    public static IReadOnlyList<ContentKindChoice> ContentKinds { get; } =
    [
        new(SourceKind.None, "Nothing"),
        new(SourceKind.Media, "Media"),
        new(SourceKind.Playlist, "Playlist"),
        new(SourceKind.Generator, "Generator"),
        new(SourceKind.Canvas, "Canvas"),
        new(SourceKind.Color, "Colour"),
        new(SourceKind.Timer, "Stagewatch timer"),
    ];

    public IReadOnlyList<ContentKindChoice> Kinds => ContentKinds;

    public IReadOnlyList<SurfaceMapping> MappingChoices { get; } = Enum.GetValues<SurfaceMapping>();

    public ObservableCollection<MaskRow> Masks { get; } = [];

    public string Name
    {
        get => _name;
        set
        {
            if (Set(ref _name, value) && value.Trim().Length > 0)
                Edit("name", VerbText.JsonString(value.Trim()), throttle: true);
        }
    }

    public ContentKindChoice? ContentKind
    {
        get => _contentKind;
        set
        {
            if (value is null || !Set(ref _contentKind, value))
                return;
            Raise(nameof(IsColorContent), nameof(HasContentItems));
            ContentItems = ItemsFor(value.Kind);
            if (Loading)
                return;
            switch (value.Kind)
            {
                case SourceKind.None:
                    Send($"SURFACE SOURCE {Id} none");
                    break;
                case SourceKind.Color:
                    Send($"SURFACE SOURCE {Id} color:{_colorHex}");
                    break;
                case SourceKind.Timer:
                    Send($"SURFACE SOURCE {Id} timer:stagewatch");
                    break;
                default:
                    if (ContentItems.Count > 0)
                        ContentItem = ContentItems[0];
                    break;
            }
        }
    }

    public bool IsColorContent => _contentKind?.Kind == SourceKind.Color;

    public bool HasContentItems => _contentKind?.Kind is SourceKind.Media or SourceKind.Playlist or SourceKind.Generator or SourceKind.Canvas;

    public IReadOnlyList<ItemChoice> ContentItems
    {
        get => _contentItems;
        private set => Set(ref _contentItems, value);
    }

    public ItemChoice? ContentItem
    {
        get => _contentItem;
        set
        {
            if (value is null || !Set(ref _contentItem, value))
                return;
            if (_contentKind is { } k)
                Send($"SURFACE SOURCE {Id} {new SourceRef(k.Kind, value.Id)}");
        }
    }

    public string ColorHex
    {
        get => _colorHex;
        set
        {
            if (Set(ref _colorHex, value) && PlayMap.Core.Content.ColorText.TryParse(value, out var rgb) && !Loading)
                Session.Throttle.Post(Id + " source", $"SURFACE SOURCE {Id} color:{PlayMap.Core.Content.ColorText.Format(rgb)}");
        }
    }

    public SurfaceMapping Mapping
    {
        get => _mapping;
        set
        {
            if (Set(ref _mapping, value))
            {
                Raise(nameof(IsProjection), nameof(RegionHint));
                Edit("content.mode", VerbText.Json(value));
            }
        }
    }

    public bool IsProjection => _mapping == SurfaceMapping.Projection;

    public string RegionHint => _mapping switch
    {
        SurfaceMapping.CanvasRegion => "x, y, width, height in canvas pixels",
        SurfaceMapping.Uv => "u, v, width, height of the source (0–1)",
        _ => "the projected picture's region (0–1)",
    };

    /// <summary>"x, y, w, h" (invariant numbers); sent when it reads as four numbers.</summary>
    public string Region
    {
        get => _region;
        set
        {
            if (!Set(ref _region, value))
                return;
            var parts = value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 4 && parts.All(x => double.TryParse(x, NumberStyles.Float, CultureInfo.InvariantCulture, out _)))
            {
                var v = parts.Select(x => double.Parse(x, NumberStyles.Float, CultureInfo.InvariantCulture)).ToArray();
                if (v[2] > 0 && v[3] > 0)
                    Edit("content.region", VerbText.Json(new RectD(v[0], v[1], v[2], v[3])), throttle: true);
            }
        }
    }

    public IReadOnlyList<ItemChoice> Viewpoints
    {
        get => _viewpoints;
        private set => Set(ref _viewpoints, value);
    }

    public ItemChoice? Viewpoint
    {
        get => _viewpoint;
        set
        {
            if (value is not null && Set(ref _viewpoint, value))
                Edit("content.viewpointId", VerbText.JsonString(value.Id));
        }
    }

    public double Opacity
    {
        get => _opacity;
        set
        {
            if (Set(ref _opacity, Math.Round(Math.Clamp(value, 0, 1), 3)))
            {
                Raise(nameof(OpacityText));
                Edit("opacity", VerbText.Num(_opacity), throttle: true);
            }
        }
    }

    public string OpacityText => string.Create(CultureInfo.InvariantCulture, $"{_opacity * 100:0} %");

    public string Color
    {
        get => _color;
        set
        {
            if (Set(ref _color, value) && PlayMap.Core.Content.ColorText.TryParse(value, out var rgb))
            {
                Raise(nameof(ColorBrush));
                Edit("color", VerbText.JsonString(PlayMap.Core.Content.ColorText.Format(rgb)), throttle: true);
            }
        }
    }

    public IBrush ColorBrush => LightBrushConverter.Brushes.For(_color);

    public bool Locked
    {
        get => _locked;
        set
        {
            if (Set(ref _locked, value))
                Edit("locked", VerbText.JsonBool(value));
        }
    }

    /// <summary>"x, y, z" in metres; sent when it reads as three numbers.</summary>
    public string PositionText
    {
        get => _position;
        set
        {
            if (!Set(ref _position, value))
                return;
            var parts = value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 3 && parts.All(x => double.TryParse(x, NumberStyles.Float, CultureInfo.InvariantCulture, out _)))
            {
                var v = parts.Select(x => double.Parse(x, NumberStyles.Float, CultureInfo.InvariantCulture)).ToArray();
                Edit("transform.position", VerbText.Json(new Vec3d(v[0], v[1], v[2])), throttle: true);
            }
        }
    }

    private IReadOnlyList<ItemChoice> ItemsFor(SourceKind kind) => _project is not { } p ? [] : kind switch
    {
        SourceKind.Media => p.Media.Select(m => new ItemChoice(m.Id, m.Name)).ToList(),
        SourceKind.Playlist => p.Playlists.Select(m => new ItemChoice(m.Id, m.Name)).ToList(),
        SourceKind.Generator => p.Generators.Select(m => new ItemChoice(m.Id, m.Name)).ToList(),
        SourceKind.Canvas => p.Canvases.Select(m => new ItemChoice(m.Id, m.Name)).ToList(),
        _ => [],
    };

    protected override bool Load(Project p, StateSnapshot s)
    {
        var sf = p.FindSurface(Id);
        if (sf is null)
            return false;
        _project = p;
        Title = sf.Name;
        Subtitle = "Surface on " + (p.FindMesh(sf.MeshId)?.Name ?? sf.MeshId);
        if (Settled("name"))
            Name = sf.Name;
        var src = sf.Content.Source;
        ContentKind = ContentKinds.FirstOrDefault(k => k.Kind == src.Kind) ?? ContentKinds[0];
        ContentItems = ItemsFor(src.Kind);
        _contentItem = ContentItems.FirstOrDefault(i => i.Id == src.Id);
        Raise(nameof(ContentItem));
        if (src.Kind == SourceKind.Color)
            ColorHex = src.Id;
        Mapping = sf.Content.Mode;
        if (Settled("content.region"))
            Region = string.Create(CultureInfo.InvariantCulture, $"{sf.Content.Region.X:0.###}, {sf.Content.Region.Y:0.###}, {sf.Content.Region.W:0.###}, {sf.Content.Region.H:0.###}");
        Viewpoints = p.Stage.Viewpoints.Select(v => new ItemChoice(v.Id, v.Name)).ToList();
        _viewpoint = Viewpoints.FirstOrDefault(v => v.Id == sf.Content.ViewpointId);
        Raise(nameof(Viewpoint));
        if (Settled("opacity"))
            Opacity = sf.Opacity;
        if (Settled("color"))
            Color = sf.Color;
        _visible = sf.Visible;
        Locked = sf.Locked;
        if (Settled("transform.position"))
            PositionText = string.Create(CultureInfo.InvariantCulture, $"{sf.Transform.Position.X:0.###}, {sf.Transform.Position.Y:0.###}, {sf.Transform.Position.Z:0.###}");
        Masks.SyncById(sf.Masks.Select((m, i) => (m, i)).ToList(), x => x.m.Id, r => r.Id, x => new MaskRow(Session, Id, x.m, x.i), (r, x) => r.Update(x.m, x.i));
        return true;
    }
}

public sealed record ContentKindChoice(SourceKind Kind, string Name)
{
    public override string ToString() => Name;
}

/// <summary>One mask of a surface or an output: mode, feather, invert, on/off, remove.</summary>
public sealed class MaskRow : ObservableObject
{
    private readonly DeskSession _session;
    private readonly string _ownerId;
    private int _index;
    private string _name = "";
    private MaskMode _mode;
    private double _feather;
    private bool _invert;
    private bool _enabled;
    private bool _loading;

    public MaskRow(DeskSession session, string ownerId, Mask m, int index)
    {
        _session = session;
        _ownerId = ownerId;
        Id = m.Id;
        Remove = DeskAction.Send(session, "remove", "Remove mask", () => "REMOVE " + Id).Look("trash", danger: true);
        Update(m, index);
    }

    public string Id { get; }

    public DeskAction Remove { get; }

    public IReadOnlyList<MaskMode> Modes { get; } = Enum.GetValues<MaskMode>();

    public string Name
    {
        get => _name;
        private set => Set(ref _name, value);
    }

    public string NodesText { get; private set; } = "";

    public MaskMode Mode
    {
        get => _mode;
        set
        {
            if (Set(ref _mode, value))
                Edit("mode", VerbText.Json(value), false);
        }
    }

    public double Feather
    {
        get => _feather;
        set
        {
            if (Set(ref _feather, Math.Round(Math.Clamp(value, 0, 0.25), 4)))
            {
                Raise(nameof(FeatherText));
                Edit("feather", VerbText.Num(_feather), true);
            }
        }
    }

    public string FeatherText => string.Create(CultureInfo.InvariantCulture, $"{_feather * 100:0.#} %");

    public bool Invert
    {
        get => _invert;
        set
        {
            if (Set(ref _invert, value))
                Edit("invert", VerbText.JsonBool(value), false);
        }
    }

    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (Set(ref _enabled, value))
                Edit("enabled", VerbText.JsonBool(value), false);
        }
    }

    private void Edit(string field, string json, bool throttle)
    {
        if (_loading)
            return;
        // A mask is an entity: SET by its own id.
        var line = VerbText.Set(Id, field, json);
        if (throttle)
            _session.Throttle.Post(Id + " " + field, line);
        else
            _session.Fire(line);
    }

    public void Update(Mask m, int index)
    {
        _loading = true;
        try
        {
            _index = index;
            Name = m.Name;
            Mode = m.Mode;
            Feather = m.Feather;
            Invert = m.Invert;
            Enabled = m.Enabled;
            NodesText = string.Create(CultureInfo.InvariantCulture, $"{m.Path.Nodes.Length} nodes · {m.Path.Nodes.Count(n => n.Smooth)} smooth · #{_index + 1} on {_ownerId}");
            Raise(nameof(NodesText));
        }
        finally
        {
            _loading = false;
        }
    }
}

/// <summary>A projector: optics with throw-ratio and lens-shift helpers, lock toggles for the solve, the pins
/// with their live residuals (green under 1 px, amber under 3, red above), Solve, and the blend and colour of
/// the output that drives it.</summary>
public sealed class ProjectorInspector : InspectorViewModel
{
    private readonly MapViewModel _map;
    private string _name = "";
    private string _color = "#FFFFFF";
    private int _width;
    private int _height;
    private double _throw;
    private double _shiftX;
    private double _shiftY;
    private bool _lockShift;
    private bool _lockFocal;
    private bool _lockDistortion;
    private string _lumens = "";
    private string _poseText = "";
    private string _solveText = "";
    private string _residualLevel = "grey";
    private OutputInspector? _output;

    public ProjectorInspector(DeskSession session, string id, MapViewModel map) : base(session, id)
    {
        _map = map;
        Solve = DeskAction.Send(session, "solve", "Solve", () => "PROJECTOR SOLVE " + Id, isEnabled: () => Pins.Count(p => p.Enabled) >= 6).Look("solve");
        var solveWith = DeskAction.Local("solveWith", "Solve with…", () => { }).Look("more", menuOnly: true);
        solveWith.Children.Add(DeskAction.Send(session, "solve.shift", "Shift locked", () => $"PROJECTOR SOLVE {Id} LOCK SHIFT"));
        solveWith.Children.Add(DeskAction.Send(session, "solve.focal", "Zoom locked", () => $"PROJECTOR SOLVE {Id} LOCK FOCAL"));
        solveWith.Children.Add(DeskAction.Send(session, "solve.none", "Nothing locked", () => $"PROJECTOR SOLVE {Id} LOCK NONE"));
        Actions =
        [
            Solve,
            solveWith,
            DeskAction.Local("view", "Projector view", () => _map.IsProjectorView = true).Look("projector"),
            DeskAction.Send(session, "remove", "Remove projector", () => "REMOVE " + Id).Look("trash", danger: true),
        ];
    }

    public override string Icon => "projector";

    public DeskAction Solve { get; }

    public ObservableCollection<PinRow> Pins { get; } = [];

    /// <summary>The inspector of the output that drives this projector (its warp, blend and colour).</summary>
    public OutputInspector? Output
    {
        get => _output;
        private set
        {
            if (Set(ref _output, value))
                Raise(nameof(HasOutput));
        }
    }

    public bool HasOutput => _output is not null;

    public string Name
    {
        get => _name;
        set
        {
            if (Set(ref _name, value) && value.Trim().Length > 0)
                Edit("name", VerbText.JsonString(value.Trim()), throttle: true);
        }
    }

    public string Color
    {
        get => _color;
        set
        {
            if (Set(ref _color, value) && PlayMap.Core.Content.ColorText.TryParse(value, out var rgb))
            {
                Raise(nameof(ColorBrush));
                Edit("color", VerbText.JsonString(PlayMap.Core.Content.ColorText.Format(rgb)), throttle: true);
            }
        }
    }

    public IBrush ColorBrush => LightBrushConverter.Brushes.For(_color);

    public string Resolution => string.Create(CultureInfo.InvariantCulture, $"{_width} × {_height}");

    /// <summary>Distance ÷ image width (the datasheet's throw ratio).</summary>
    public double ThrowRatio
    {
        get => _throw;
        set
        {
            if (Set(ref _throw, Math.Round(Math.Clamp(value, 0.2, 8), 3)))
            {
                Raise(nameof(ThrowText), nameof(OpticsHint));
                SendOptics();
            }
        }
    }

    public string ThrowText => string.Create(CultureInfo.InvariantCulture, $"{_throw:0.00}:1");

    /// <summary>Lens shift as a fraction of the image width, in <see cref="Intrinsics.FromThrowRatio"/>'s sense.</summary>
    public double ShiftX
    {
        get => _shiftX;
        set
        {
            if (Set(ref _shiftX, Math.Round(Math.Clamp(value, -1.5, 1.5), 3)))
            {
                Raise(nameof(ShiftText), nameof(OpticsHint));
                SendOptics();
            }
        }
    }

    /// <summary>Lens shift as a fraction of the image height (+ up, the datasheet's sign).</summary>
    public double ShiftY
    {
        get => _shiftY;
        set
        {
            if (Set(ref _shiftY, Math.Round(Math.Clamp(value, -1.5, 1.5), 3)))
            {
                Raise(nameof(ShiftText), nameof(OpticsHint));
                SendOptics();
            }
        }
    }

    public string ShiftText => string.Create(CultureInfo.InvariantCulture, $"H {_shiftX * 100:+0;-0;0} % · V {_shiftY * 100:+0;-0;0} %");

    /// <summary>What the numbers mean on the wall: the picture's size at 5 m and how far the shift moves it.</summary>
    public string OpticsHint => _throw <= 0 || _width <= 0 ? "" : string.Create(CultureInfo.InvariantCulture,
        $"At 5 m the picture is {5 / _throw:0.00} × {5 / _throw * _height / _width:0.00} m; the vertical shift moves it {_shiftY * 5 / _throw * _height / _width:+0.00;-0.00;0} m.");

    public bool LockShift
    {
        get => _lockShift;
        set
        {
            if (Set(ref _lockShift, value))
                SendLocks();
        }
    }

    public bool LockFocal
    {
        get => _lockFocal;
        set
        {
            if (Set(ref _lockFocal, value))
                SendLocks();
        }
    }

    public bool LockDistortion
    {
        get => _lockDistortion;
        set
        {
            if (Set(ref _lockDistortion, value))
                SendLocks();
        }
    }

    /// <summary>ANSI lumens as typed (0 = unknown).</summary>
    public string LumensText
    {
        get => _lumens;
        set
        {
            if (Set(ref _lumens, value) && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var l) && l >= 0)
                Edit("lumens", VerbText.Num(Math.Round(l)), throttle: true);
        }
    }

    public string PoseText
    {
        get => _poseText;
        private set => Set(ref _poseText, value);
    }

    /// <summary>"Last solve 1.4 px RMS · 8 pins (6 needed)".</summary>
    public string SolveText
    {
        get => _solveText;
        private set => Set(ref _solveText, value);
    }

    public string ResidualLevel
    {
        get => _residualLevel;
        private set => Set(ref _residualLevel, value);
    }

    private void SendOptics()
    {
        if (_width <= 0 || _height <= 0)
            return;
        var k = Intrinsics.FromThrowRatio(_width, _height, _throw, _shiftX, _shiftY);
        Edit("intrinsics", VerbText.Json(k), throttle: true);
    }

    private void SendLocks()
    {
        var locks = (_lockShift ? SolveLocks.Shift : 0) | (_lockFocal ? SolveLocks.Focal : 0) | (_lockDistortion ? SolveLocks.Distortion : 0);
        Edit("locks", VerbText.Json(locks));
    }

    protected override bool Load(Project p, StateSnapshot s)
    {
        var pj = p.FindProjector(Id);
        if (pj is null)
            return false;
        Title = pj.Name;
        Subtitle = "Projector";
        if (Settled("name"))
            Name = pj.Name;
        if (Settled("color"))
            Color = pj.Color;
        var k = pj.Intrinsics;
        _width = k.Width;
        _height = k.Height;
        Raise(nameof(Resolution));
        if (Settled("intrinsics"))
        {
            ThrowRatio = k.ThrowRatio;
            ShiftX = k.Width > 0 ? (k.Width / 2.0 - k.Cx) / k.Width : 0;
            ShiftY = k.Height > 0 ? (k.Cy - k.Height / 2.0) / k.Height : 0;
        }
        LockShift = pj.Locks.HasFlag(SolveLocks.Shift);
        LockFocal = pj.Locks.HasFlag(SolveLocks.Focal);
        LockDistortion = pj.Locks.HasFlag(SolveLocks.Distortion);
        if (Settled("lumens"))
            LumensText = pj.Lumens.ToString("0", CultureInfo.InvariantCulture);
        var pos = pj.Pose.Position;
        var fwd = pj.Pose.Forward;
        PoseText = string.Create(CultureInfo.InvariantCulture, $"At ({pos.X:0.00}, {pos.Y:0.00}, {pos.Z:0.00}) m, looking ({fwd.X:0.00}, {fwd.Y:0.00}, {fwd.Z:0.00})");
        Pins.SyncById(pj.Pins.Select((pin, i) => (pin, i)).ToList(), x => x.pin.Id, r => r.Id, x => new PinRow(Session, x.pin, x.i, pj), (r, x) => r.Update(x.pin, x.i, pj));
        var enabled = pj.Pins.Count(x => x.Enabled);
        SolveText = pj.SolveResidualPx > 0
            ? string.Create(CultureInfo.InvariantCulture, $"Last solve {pj.SolveResidualPx:0.0} px RMS · {enabled} pins")
            : string.Create(CultureInfo.InvariantCulture, $"Not solved · {enabled} of 6 pins — spread them, off one plane");
        ResidualLevel = pj.SolveResidualPx <= 0 ? "grey" : PinRow.LevelOf(pj.SolveResidualPx);
        var output = p.Outputs.FirstOrDefault(o => o.ProjectorId == Id);
        if (output is null)
            Output = null;
        else if (_output?.Id != output.Id)
            Output = new OutputInspector(Session, output.Id);
        _output?.Refresh(p, s);
        return true;
    }
}

/// <summary>One pin in the projector's table: where it is in the world, which pixel lights it, and how far
/// the projector's current pose lands it from that pixel.</summary>
public sealed class PinRow : ObservableObject
{
    private readonly DeskSession _session;
    private int _index;
    private string _world = "";
    private string _pixel = "";
    private double _residual;
    private bool _enabled;
    private bool _loading;

    public PinRow(DeskSession session, CalibrationPin pin, int index, Projector pj)
    {
        _session = session;
        Id = pin.Id;
        Remove = DeskAction.Send(session, "remove", "Remove pin", () => "REMOVE " + Id).Look("trash", danger: true);
        Update(pin, index, pj);
    }

    public string Id { get; }

    public DeskAction Remove { get; }

    public int Number => _index + 1;

    public string World
    {
        get => _world;
        private set => Set(ref _world, value);
    }

    public string Pixel
    {
        get => _pixel;
        private set => Set(ref _pixel, value);
    }

    public double ResidualPx
    {
        get => _residual;
        private set
        {
            if (Set(ref _residual, value))
                Raise(nameof(ResidualText), nameof(Level), nameof(Brush));
        }
    }

    public string ResidualText => double.IsFinite(_residual) ? string.Create(CultureInfo.InvariantCulture, $"{_residual:0.0} px") : "behind";

    /// <summary>"good" (under 1 px), "fair" (under 3 px), "poor".</summary>
    public string Level => LevelOf(_residual);

    public IBrush Brush => LightBrushConverter.Brushes.For(Level);

    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (Set(ref _enabled, value) && !_loading)
                _session.Fire(VerbText.SetBool(Id, "enabled", value));
        }
    }

    public static string LevelOf(double px) => !double.IsFinite(px) ? "poor" : px < 1 ? "good" : px < 3 ? "fair" : "poor";

    public void Update(CalibrationPin pin, int index, Projector pj)
    {
        _loading = true;
        try
        {
            _index = index;
            Raise(nameof(Number));
            World = string.Create(CultureInfo.InvariantCulture, $"{pin.World.X:0.00}, {pin.World.Y:0.00}, {pin.World.Z:0.00}");
            Pixel = string.Create(CultureInfo.InvariantCulture, $"{pin.Pixel.X:0}, {pin.Pixel.Y:0}");
            ResidualPx = PinResiduals.Of(pj, pin);
            Enabled = pin.Enabled;
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>The residual while the pin is dragged (before the engine has the new pixel).</summary>
    public void Live(Vec2d pixel, Projector pj)
    {
        Pixel = string.Create(CultureInfo.InvariantCulture, $"{pixel.X:0}, {pixel.Y:0}");
        var p = PlayMap.Core.Geometry.CameraModel.Project(pj.Pins.Find(Id)?.World ?? Vec3d.Zero, pj.Intrinsics, pj.Lens, pj.Pose);
        ResidualPx = p is { } px ? (px - pixel).Length : double.PositiveInfinity;
    }
}

/// <summary>An output: what it is, where it goes, what it shows, its warp, blend and colour trim.</summary>
public sealed class OutputInspector : InspectorViewModel
{
    private string _name = "";
    private OutputMode _mode;
    private ItemChoice? _projector;
    private IReadOnlyList<ItemChoice> _projectors = [];
    private ItemChoice? _canvas;
    private IReadOnlyList<ItemChoice> _canvases = [];
    private string _display = "";
    private string _sourceRect = "";
    private int _rotation;
    private bool _enabled;
    private bool _warpEnabled;
    private string _latticeText = "";
    private bool _auto3D;
    private BlendCurve _curve;
    private double _gamma;
    private double _blackLevel;
    private string _edges = "";
    private double _brightness = 1;
    private double _contrast = 1;
    private double _colorGamma = 1;
    private double _gainR = 1;
    private double _gainG = 1;
    private double _gainB = 1;
    private string _facts = "";

    public OutputInspector(DeskSession session, string id, IReadOnlyList<DisplayInfo>? displays = null) : base(session, id)
    {
        DisplayChoices = displays is { Count: > 0 } d ? ["", .. d.Select(x => x.Key)] : [];
        Actions =
        [
            DeskAction.Send(session, "enable", "Open", () => $"OUTPUT ENABLE {Id} {(_enabled ? "OFF" : "ON")}", () => _enabled ? "Close output" : "Open output",
                isChecked: () => _enabled).Look("eye", danger: true),
            DeskAction.Send(session, "identify", "Identify", () => "OUTPUT IDENTIFY " + Id).Look("identify"),
            DeskAction.Send(session, "warpReset", "Reset warp", () => "WARP RESET " + Id).Look("warp"),
            DeskAction.Send(session, "remove", "Remove output", () => "REMOVE " + Id).Look("trash", danger: true),
        ];
    }

    public override string Icon => "outputs";

    public IReadOnlyList<OutputMode> Modes { get; } = Enum.GetValues<OutputMode>();

    public IReadOnlyList<BlendCurve> Curves { get; } = Enum.GetValues<BlendCurve>();

    public IReadOnlyList<int> Rotations { get; } = [0, 90, 180, 270];

    public IReadOnlyList<string> LatticeSizes { get; } = ["2 × 2", "3 × 3", "4 × 4", "5 × 4", "6 × 5", "8 × 6", "12 × 9", "16 × 12"];

    public string Name
    {
        get => _name;
        set
        {
            if (Set(ref _name, value) && value.Trim().Length > 0)
                Edit("name", VerbText.JsonString(value.Trim()), throttle: true);
        }
    }

    public OutputMode Mode
    {
        get => _mode;
        set
        {
            if (Set(ref _mode, value))
            {
                Raise(nameof(IsProjectorMode), nameof(IsCanvasMode));
                Edit("mode", VerbText.Json(value));
            }
        }
    }

    public bool IsProjectorMode => _mode == OutputMode.Projector;

    public bool IsCanvasMode => _mode == OutputMode.Canvas;

    public IReadOnlyList<ItemChoice> Projectors
    {
        get => _projectors;
        private set => Set(ref _projectors, value);
    }

    public ItemChoice? Projector
    {
        get => _projector;
        set
        {
            if (value is not null && Set(ref _projector, value))
                Edit("projectorId", VerbText.JsonString(value.Id));
        }
    }

    public IReadOnlyList<ItemChoice> Canvases
    {
        get => _canvases;
        private set => Set(ref _canvases, value);
    }

    public ItemChoice? Canvas
    {
        get => _canvas;
        set
        {
            if (value is not null && Set(ref _canvas, value))
                Send($"OUTPUT SOURCE {Id} canvas:{value.Id}");
        }
    }

    /// <summary>The displays this machine sees (from DISPLAYS; "" = the first free one), or none when unknown.</summary>
    public IReadOnlyList<string> DisplayChoices { get; }

    public bool HasDisplayChoices => DisplayChoices.Count > 0;

    /// <summary>Which display ("" = the first free one).</summary>
    public string Display
    {
        get => _display;
        set
        {
            if (Set(ref _display, value))
                Edit("displayKey", VerbText.JsonString(value.Trim()), throttle: true);
        }
    }

    public string SourceRect
    {
        get => _sourceRect;
        set
        {
            if (!Set(ref _sourceRect, value))
                return;
            var parts = value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 4 && parts.All(x => double.TryParse(x, NumberStyles.Float, CultureInfo.InvariantCulture, out _)))
            {
                var v = parts.Select(x => double.Parse(x, NumberStyles.Float, CultureInfo.InvariantCulture)).ToArray();
                if (v[2] > 0 && v[3] > 0)
                    Edit("sourceRect", VerbText.Json(new RectD(v[0], v[1], v[2], v[3])), throttle: true);
            }
        }
    }

    public int Rotation
    {
        get => _rotation;
        set
        {
            if (Set(ref _rotation, value))
                Edit("rotation", VerbText.Int(value));
        }
    }

    public bool WarpEnabled
    {
        get => _warpEnabled;
        set
        {
            if (Set(ref _warpEnabled, value))
                Edit("warp.enabled", VerbText.JsonBool(value));
        }
    }

    /// <summary>"5 × 4" — a new lattice at rest of that size (WARP RESET keeps the size).</summary>
    public string LatticeText
    {
        get => _latticeText;
        set
        {
            if (!Set(ref _latticeText, value) || Loading)
                return;
            var parts = value.Split('×', StringSplitOptions.TrimEntries);
            if (parts.Length == 2 && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var c)
                && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var r))
                Edit("warp.lattice", VerbText.Json(WarpLattice.AtRest(c, r)));
        }
    }

    public bool Auto3D
    {
        get => _auto3D;
        set
        {
            if (Set(ref _auto3D, value))
                Edit("blend.auto3D", VerbText.JsonBool(value));
        }
    }

    public BlendCurve Curve
    {
        get => _curve;
        set
        {
            if (Set(ref _curve, value))
                Edit("blend.curve", VerbText.Json(value));
        }
    }

    public double Gamma
    {
        get => _gamma;
        set
        {
            if (Set(ref _gamma, Math.Round(Math.Clamp(value, 1, 3), 2)))
                Edit("blend.gamma", VerbText.Num(_gamma), throttle: true);
        }
    }

    public double BlackLevel
    {
        get => _blackLevel;
        set
        {
            if (Set(ref _blackLevel, Math.Round(Math.Clamp(value, 0, 0.25), 3)))
                Edit("blend.blackLevel", VerbText.Num(_blackLevel), throttle: true);
        }
    }

    /// <summary>"left, top, right, bottom" soft edges in output pixels (manual blends).</summary>
    public string Edges
    {
        get => _edges;
        set
        {
            if (!Set(ref _edges, value))
                return;
            var parts = value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 4 && parts.All(x => double.TryParse(x, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && d >= 0))
            {
                var v = parts.Select(x => double.Parse(x, NumberStyles.Float, CultureInfo.InvariantCulture)).ToArray();
                Edit("blend.edges", VerbText.Json(new EdgeBlend(v[0], v[1], v[2], v[3])), throttle: true);
            }
        }
    }

    public double Brightness
    {
        get => _brightness;
        set => SetTrim(ref _brightness, value, "brightness");
    }

    public double Contrast
    {
        get => _contrast;
        set => SetTrim(ref _contrast, value, "contrast");
    }

    public double ColorGamma
    {
        get => _colorGamma;
        set => SetTrim(ref _colorGamma, value, "gamma");
    }

    public double GainR
    {
        get => _gainR;
        set => SetTrim(ref _gainR, value, "gainR");
    }

    public double GainG
    {
        get => _gainG;
        set => SetTrim(ref _gainG, value, "gainG");
    }

    public double GainB
    {
        get => _gainB;
        set => SetTrim(ref _gainB, value, "gainB");
    }

    public string Facts
    {
        get => _facts;
        private set => Set(ref _facts, value);
    }

    private void SetTrim(ref double field, double value, string name)
    {
        if (Set(ref field, Math.Round(Math.Clamp(value, 0, 2), 3), name switch
        {
            "brightness" => nameof(Brightness),
            "contrast" => nameof(Contrast),
            "gamma" => nameof(ColorGamma),
            "gainR" => nameof(GainR),
            "gainG" => nameof(GainG),
            _ => nameof(GainB),
        }))
            Edit("color." + name, VerbText.Num(field), throttle: true);
    }

    protected override bool Load(Project p, StateSnapshot s)
    {
        var o = p.FindOutput(Id);
        if (o is null)
            return false;
        Title = o.Name;
        Subtitle = $"{o.Kind} output";
        if (Settled("name"))
            Name = o.Name;
        Mode = o.Mode;
        Projectors = p.Stage.Projectors.Select(x => new ItemChoice(x.Id, x.Name)).ToList();
        _projector = Projectors.FirstOrDefault(x => x.Id == o.ProjectorId);
        Raise(nameof(Projector));
        Canvases = p.Canvases.Select(x => new ItemChoice(x.Id, x.Name)).ToList();
        _canvas = o.Source.Kind == SourceKind.Canvas ? Canvases.FirstOrDefault(x => x.Id == o.Source.Id) : null;
        Raise(nameof(Canvas));
        if (Settled("displayKey"))
            Display = o.DisplayKey;
        if (Settled("sourceRect"))
            SourceRect = string.Create(CultureInfo.InvariantCulture, $"{o.SourceRect.X:0.##}, {o.SourceRect.Y:0.##}, {o.SourceRect.W:0.##}, {o.SourceRect.H:0.##}");
        Rotation = o.Rotation;
        _enabled = o.Enabled;
        WarpEnabled = o.Warp.Enabled;
        LatticeText = o.Warp.Lattice is { } l ? string.Create(CultureInfo.InvariantCulture, $"{l.Columns} × {l.Rows}") : "";
        Auto3D = o.Blend.Auto3D;
        Curve = o.Blend.Curve;
        if (Settled("blend.gamma"))
            Gamma = o.DrawnBlendGamma();
        if (Settled("blend.blackLevel"))
            BlackLevel = o.Blend.BlackLevel;
        if (Settled("blend.edges"))
            Edges = string.Create(CultureInfo.InvariantCulture, $"{o.Blend.Edges.Left:0}, {o.Blend.Edges.Top:0}, {o.Blend.Edges.Right:0}, {o.Blend.Edges.Bottom:0}");
        var c = o.Color;
        if (Settled("color.brightness"))
            Brightness = c.Brightness;
        if (Settled("color.contrast"))
            Contrast = c.Contrast;
        if (Settled("color.gamma"))
            ColorGamma = c.Gamma;
        if (Settled("color.gainR"))
            GainR = c.GainR;
        if (Settled("color.gainG"))
            GainG = c.GainG;
        if (Settled("color.gainB"))
            GainB = c.GainB;
        Facts = s.FindOutput(Id) is { } st
            ? OutputFacts(st)
            : "";
        return true;
    }

    /// <summary>An output's facts in words: its raster and refresh, then, open, its rate, its late presents past the budget in the last
    /// 10 s, its frame time p95, and the display latency the node holds it to with the path its frames take.</summary>
    public static string OutputFacts(OutputState st)
    {
        ArgumentNullException.ThrowIfNull(st);
        var latency = st.LatencyMs >= 0 ? string.Create(CultureInfo.InvariantCulture, $" · display latency {st.LatencyMs:0} ms") : "";
        var path = st.PresentPath.Length > 0 ? " · " + st.PresentPath : "";
        return string.Create(CultureInfo.InvariantCulture,
            $"{st.Width}×{st.Height} @ {st.RefreshHz:0.##} Hz · {(st.Open ? $"open · {st.Fps:0} fps · {st.Dropped} late in 10 s · frame p95 {st.FrameMsP95:0.0} ms" : "closed")}{latency}{path}");
    }
}

/// <summary>A mesh: what it is, its primitive's dimensions (editable), its size.</summary>
public sealed class MeshInspector : InspectorViewModel
{
    private string _name = "";
    private string _facts = "";

    public MeshInspector(DeskSession session, string id) : base(session, id)
    {
        Actions = [DeskAction.Send(session, "remove", "Remove mesh", () => "REMOVE " + Id).Look("trash", danger: true)];
    }

    public override string Icon => "mesh";

    public ObservableCollection<MeshParamRow> Params { get; } = [];

    public string Name
    {
        get => _name;
        set
        {
            if (Set(ref _name, value) && value.Trim().Length > 0)
                Edit("name", VerbText.JsonString(value.Trim()), throttle: true);
        }
    }

    public string Facts
    {
        get => _facts;
        private set => Set(ref _facts, value);
    }

    protected override bool Load(Project p, StateSnapshot s)
    {
        var m = p.FindMesh(Id);
        if (m is null)
            return false;
        Title = m.Name;
        Subtitle = m.Origin == MeshOrigin.Primitive ? "Primitive · " + m.Primitive : m.Origin + " mesh";
        if (Settled("name"))
            Name = m.Name;
        Facts = m.Origin == MeshOrigin.Primitive
            ? "Dimensions in metres."
            : string.Create(CultureInfo.InvariantCulture, $"{m.Vertices:N0} vertices · {m.Triangles:N0} triangles · {m.Path}");
        Params.SyncById(m.PrimitiveParams.Values.ToList(), kv => kv.Key, r => r.Name, kv => new MeshParamRow(Session, Id, kv.Key, kv.Value), (r, kv) => r.Load(kv.Value));
        return true;
    }
}

/// <summary>One dimension of a primitive mesh (SET &lt;mesh&gt; primitiveParams.values.&lt;name&gt;).</summary>
public sealed class MeshParamRow : ObservableObject
{
    private readonly DeskSession _session;
    private readonly string _meshId;
    private string _value;
    private bool _loading;

    public MeshParamRow(DeskSession session, string meshId, string name, string value)
    {
        _session = session;
        _meshId = meshId;
        Name = name;
        _value = value;
    }

    public string Name { get; }

    public string Value
    {
        get => _value;
        set
        {
            if (Set(ref _value, value) && !_loading && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && d > 0)
                _session.Throttle.Post(_meshId + " " + Name, VerbText.Set(_meshId, "primitiveParams.values." + Name, VerbText.JsonString(VerbText.Num(d))));
        }
    }

    public void Load(string value)
    {
        _loading = true;
        try
        {
            Value = value;
        }
        finally
        {
            _loading = false;
        }
    }
}

/// <summary>A camera, a viewpoint or a scan: its name and facts.</summary>
public sealed class StageItemInspector : InspectorViewModel
{
    private readonly string _kind;
    private string _name = "";
    private string _facts = "";

    public StageItemInspector(DeskSession session, string id, string kind) : base(session, id)
    {
        _kind = kind;
        var list = new List<DeskAction>();
        if (kind == "scan")
            list.Add(DeskAction.Send(session, "apply", "Apply scan", () => "SCAN APPLY " + Id).Look("scan"));
        list.Add(DeskAction.Send(session, "remove", "Remove", () => "REMOVE " + Id).Look("trash", danger: true));
        Actions = list;
    }

    public override string Icon => _kind;

    public string Name
    {
        get => _name;
        set
        {
            if (Set(ref _name, value) && value.Trim().Length > 0)
                Edit("name", VerbText.JsonString(value.Trim()), throttle: true);
        }
    }

    public string Facts
    {
        get => _facts;
        private set => Set(ref _facts, value);
    }

    protected override bool Load(Project p, StateSnapshot s)
    {
        switch (_kind)
        {
            case "camera" when p.Stage.Cameras.Find(Id) is { } c:
                Title = c.Name;
                Subtitle = "Camera";
                Facts = string.Create(CultureInfo.InvariantCulture,
                    $"{c.SourceKind} · {c.Source}\n{c.Intrinsics.Width}×{c.Intrinsics.Height} · {c.Intrinsics.HorizontalFovDegrees:0}° wide · {(c.Calibrated ? "calibrated" : "optics estimated")}");
                if (Settled("name"))
                    Name = c.Name;
                return true;
            case "viewpoint" when p.Stage.Viewpoints.Find(Id) is { } v:
                Title = v.Name;
                Subtitle = "Viewpoint";
                Facts = string.Create(CultureInfo.InvariantCulture, $"{v.Intrinsics.HorizontalFovDegrees:0}° wide · at ({v.Pose.Position.X:0.0}, {v.Pose.Position.Y:0.0}, {v.Pose.Position.Z:0.0}) m");
                if (Settled("name"))
                    Name = v.Name;
                return true;
            case "scan" when p.Stage.Scans.Find(Id) is { } sc:
                Title = sc.Name;
                Subtitle = "Scan · " + sc.Kind;
                Facts = string.Create(CultureInfo.InvariantCulture,
                    $"{sc.Points:N0} points · residual {sc.Residual:0.0#}\n{sc.ProjectorIds.Length} projector(s) · camera {sc.CameraId}\n{sc.Notes}");
                if (Settled("name"))
                    Name = sc.Name;
                return true;
            default:
                return false;
        }
    }
}
