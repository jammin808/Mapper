using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using PlayMap.App.Link;
using PlayMap.App.ViewModels;
using PlayMap.Core.Model;
using PlayMap.Core.Numerics;

namespace PlayMap.App.Controls;

/// <summary>The MAP 3D view: the engine's picture of the orbit camera (or, until one arrives, the desk's own
/// drawing of the stage) with the desk's overlays — the floor, every projector's frustum, the view
/// projector's pins, the selection outline and the transform gizmo. Click selects (projectors first, then
/// the nearest surface under the cursor), dragging empty space orbits (Shift or the middle button pans),
/// the wheel zooms, dragging a gizmo axis moves the surface along it, and a right click opens the menu of
/// what is under the cursor.</summary>
public sealed class MapViewport : PreviewSurface
{
    public static readonly StyledProperty<MapViewModel?> MapProperty = AvaloniaProperty.Register<MapViewport, MapViewModel?>(nameof(Map));

    private static readonly (Vec3d Axis, string Color)[] Axes = [(Vec3d.UnitX, "#FFB74D"), (Vec3d.UnitY, "#81C784"), (Vec3d.UnitZ, "#64B5F6")];
    private Point _press;
    private Point _last;
    private bool _pressed;
    private bool _moved;
    private Drag _drag;
    private int _axis = -1;
    private Vec3d _dragStart;
    private Vector _axisScreen;
    private Vec3d? _livePosition;

    private enum Drag
    {
        None,
        Orbit,
        Pan,
        Axis,
    }

    public MapViewport()
    {
        Focusable = true;
        SizeChanged += (_, e) =>
        {
            Aspect = e.NewSize.Height > 0 ? e.NewSize.Width / e.NewSize.Height : 16 / 9.0;
            Map?.CameraMoved(e.NewSize.Width, e.NewSize.Height);
        };
    }

    public MapViewModel? Map
    {
        get => GetValue(MapProperty);
        set => SetValue(MapProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == MapProperty)
        {
            if (change.OldValue is MapViewModel old)
                old.SceneChanged -= InvalidateVisual;
            if (change.NewValue is MapViewModel map)
                map.SceneChanged += InvalidateVisual;
            InvalidateVisual();
        }
    }

    private StageView? View(Rect picture)
    {
        if (Map is not { } map || picture.Width < 2 || picture.Height < 2)
            return null;
        var cam = map.Camera;
        return new StageView(cam.IntrinsicsFor(picture.Width, picture.Height), LensDistortion.None, cam.Pose, picture);
    }

    protected override bool DrawFallback(DrawingContext context, Rect picture)
    {
        context.FillRectangle(StageDrawing.Background, picture);
        if (View(picture) is not { } v || Map is not { } map)
            return true;
        StageDrawing.Floor(context, v);
        StageDrawing.Surfaces(context, v, map.Scene.Surfaces, map.SelectedKind == "surface" ? map.SelectedId : null);
        return true;
    }

    protected override void DrawOverlay(DrawingContext context, Rect picture)
    {
        if (View(picture) is not { } v || Map is not { } map || map.Project is not { } project)
            return;
        var selected = map.SelectedId;
        StageDrawing.Outlines(context, v, map.Scene.Surfaces, map.SelectedKind == "surface" ? selected : null);
        foreach (var pj in project.Stage.Projectors)
            StageDrawing.Frustum(context, v, pj, pj.Id == selected, StageDrawing.Reach(pj, map.Scene));
        if (map.ViewProjector is { } viewPj)
            StageDrawing.WorldPins(context, v, viewPj);
        if (map.SelectedKind == "surface" && project.FindSurface(selected) is { } sf)
            DrawGizmo(context, v, _livePosition ?? sf.Transform.Position, sf.Locked);
        StageDrawing.Label(context, map.ViewTitle, new Point(picture.X + 12, picture.Y + 10));
    }

    private void DrawGizmo(DrawingContext ctx, StageView v, Vec3d at, bool locked)
    {
        if (v.Project(at) is not { } o)
            return;
        var len = GizmoLength(v, at);
        for (var i = 0; i < 3; i++)
        {
            if (v.Project(at + Axes[i].Axis * len) is not { } tip)
                continue;
            var brush = new SolidColorBrush(StageDrawing.ColorOf(Axes[i].Color, locked ? (byte)90 : (byte)255));
            ctx.DrawLine(new Pen(brush, _axis == i ? 4 : 2.6), o, tip);
            ctx.DrawEllipse(brush, null, tip, 6, 6);
        }
        ctx.DrawEllipse(Brushes.White, new Pen(StageDrawing.Shadow, 2), o, 4, 4);
    }

    /// <summary>Where the selected surface's gizmo axis ends on this control (tests drive the pointer here).</summary>
    internal (Point Origin, Point Tip)? GizmoHandle(int axis)
    {
        var picture = PictureRect();
        if (View(picture) is not { } v || Map is not { } map || map.Project?.FindSurface(map.SelectedId) is not { } sf)
            return null;
        var at = sf.Transform.Position;
        return v.Project(at) is { } o && v.Project(at + Axes[axis].Axis * GizmoLength(v, at)) is { } tip ? (o, tip) : null;
    }

    /// <summary>A gizmo about a tenth of the view tall wherever the object is.</summary>
    private static double GizmoLength(StageView v, Vec3d at)
    {
        var depth = Math.Max(0.2, v.Depth(at));
        return depth * 0.12 * v.K.Height / v.K.Fy;
    }

    private int HitAxis(StageView v, Vec3d at, Point p)
    {
        if (v.Project(at) is not { } o)
            return -1;
        var len = GizmoLength(v, at);
        for (var i = 0; i < 3; i++)
        {
            if (v.Project(at + Axes[i].Axis * len) is not { } tip)
                continue;
            if (DistanceToSegment(p, o, tip) < 9)
                return i;
        }
        return -1;
    }

    private static double DistanceToSegment(Point p, Point a, Point b)
    {
        var ab = b - a;
        var len2 = ab.X * ab.X + ab.Y * ab.Y;
        var t = len2 < 1e-9 ? 0 : Math.Clamp(((p.X - a.X) * ab.X + (p.Y - a.Y) * ab.Y) / len2, 0, 1);
        var q = a + ab * t;
        return Point.Distance(p, q);
    }

    /// <summary>What is under a point: a projector (by its body), else the nearest surface.</summary>
    public (string Kind, string Id)? HitTestObject(Point p)
    {
        var picture = PictureRect();
        if (View(picture) is not { } v || Map is not { } map || map.Project is not { } project)
            return null;
        foreach (var pj in project.Stage.Projectors)
            if (v.Project(pj.Pose.Position) is { } body && Point.Distance(body, p) < 14)
                return ("projector", pj.Id);
        if (map.Scene.Pick(v.RayAt(p)) is { } hit)
            return ("surface", hit.Surface.Surface.Id);
        return null;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (Map is not { } map)
            return;
        Focus();
        var pt = e.GetCurrentPoint(this);
        var p = pt.Position;
        if (pt.Properties.IsRightButtonPressed)
        {
            OpenMenuAt(p);
            e.Handled = true;
            return;
        }
        _press = p;
        _last = p;
        _pressed = true;
        _moved = false;
        _drag = pt.Properties.IsMiddleButtonPressed || e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? Drag.Pan : Drag.Orbit;
        var picture = PictureRect();
        if (map.SelectedKind == "surface" && map.Project?.FindSurface(map.SelectedId) is { Locked: false } sf && View(picture) is { } v)
        {
            var axis = HitAxis(v, sf.Transform.Position, p);
            if (axis >= 0 && v.Project(sf.Transform.Position) is { } o && v.Project(sf.Transform.Position + Axes[axis].Axis) is { } tip)
            {
                _drag = Drag.Axis;
                _axis = axis;
                _dragStart = sf.Transform.Position;
                _axisScreen = tip - o;
            }
        }
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!_pressed || Map is not { } map)
            return;
        var p = e.GetPosition(this);
        if (!_moved && Point.Distance(p, _press) < 3)
            return;
        _moved = true;
        var d = p - _last;
        _last = p;
        switch (_drag)
        {
            case Drag.Orbit:
                map.Camera.Orbit(d.X, d.Y);
                map.CameraMoved(Bounds.Width, Bounds.Height);
                break;
            case Drag.Pan:
                map.Camera.Pan(d.X, d.Y, Bounds.Height);
                map.CameraMoved(Bounds.Width, Bounds.Height);
                break;
            case Drag.Axis when map.SelectedId is { } id:
                var len2 = _axisScreen.X * _axisScreen.X + _axisScreen.Y * _axisScreen.Y;
                if (len2 < 1e-6)
                    break;
                var total = p - _press;
                var metres = (total.X * _axisScreen.X + total.Y * _axisScreen.Y) / len2;
                _livePosition = _dragStart + Axes[_axis].Axis * Math.Round(metres, 3);
                map.MoveSurface(id, _livePosition.Value, commit: false);
                InvalidateVisual();
                break;
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!_pressed || Map is not { } map)
            return;
        _pressed = false;
        e.Pointer.Capture(null);
        if (_drag == Drag.Axis && _livePosition is { } pos && map.SelectedId is { } id)
            map.MoveSurface(id, pos, commit: true);
        else if (!_moved)
        {
            var hit = HitTestObject(e.GetPosition(this));
            map.Select(hit?.Kind ?? "", hit?.Id);
        }
        _drag = Drag.None;
        _axis = -1;
        _livePosition = null;
        InvalidateVisual();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (Map is not { } map)
            return;
        map.Camera.Zoom(e.Delta.Y);
        map.CameraMoved(Bounds.Width, Bounds.Height);
        e.Handled = true;
    }

    private void OpenMenuAt(Point p)
    {
        if (Map is not { } map)
            return;
        var hit = HitTestObject(p);
        if (hit is null)
            return;
        map.Select(hit.Value.Kind, hit.Value.Id);
        var node = map.Outliner.SelectMany(g => g.Nodes).FirstOrDefault(n => n.Id == hit.Value.Id);
        if (node is null)
            return;
        var menu = new ContextMenu();
        DeskMenu.Fill(menu.Items, node.Actions);
        ContextMenu = menu;
        menu.Open(this);
    }
}
