using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using PlayMap.App.Scene;
using PlayMap.App.ViewModels;
using PlayMap.Core.Geometry;
using PlayMap.Core.Model;
using PlayMap.Core.Numerics;

namespace PlayMap.App.Controls;

/// <summary>The projector view: what one projector sees (the engine's picture of its output, or the desk's
/// drawing of the stage from its pose), with what is edited on top of it, directly — the corner pin and the
/// warp lattice (Warp tool), the output's pen masks (Pen tool draws new ones; Select drags an existing one's
/// nodes and handles), the pins with their live residuals (Pin tool: click a surface to pin it, drag a
/// pin onto the real feature), and the quads drawn in its picture with their corners (Quad tool: four clicks with a
/// rubber band to the pointer; Select picks a quad, its selected corner drawn large). Coordinates: the projector's raster
/// maps onto the picture; masks, warps and the quad tool's clicks are in normalised output units (the wall's), a quad's
/// corners are raster-normalised and drawn where the output's warp puts them on the wall.</summary>
public sealed class ProjectorCanvas : PreviewSurface
{
    public static readonly StyledProperty<MapViewModel?> MapProperty = AvaloniaProperty.Register<ProjectorCanvas, MapViewModel?>(nameof(Map));

    private const double Grab = 11;
    private static readonly IBrush HandleFill = new SolidColorBrush(Color.Parse("#E7E9EC")).ToImmutable();
    private static readonly IPen HandlePen = new Pen(new SolidColorBrush(Color.Parse("#0E1012")).ToImmutable(), 1.5).ToImmutable();
    private static readonly IPen LatticePen = new Pen(new SolidColorBrush(Color.FromArgb(200, 61, 155, 255)).ToImmutable(), 1.2).ToImmutable();
    private static readonly IPen MaskPen = new Pen(new SolidColorBrush(Color.FromArgb(230, 231, 233, 236)).ToImmutable(), 1.4, dashStyle: new DashStyle([4, 3], 0)).ToImmutable();
    private static readonly IBrush MaskFill = new SolidColorBrush(Color.FromArgb(90, 0, 0, 0)).ToImmutable();
    private static readonly IPen PenPathPen = new Pen(StageDrawing.Accent, 2).ToImmutable();
    private static readonly IPen HandleLinePen = new Pen(new SolidColorBrush(Color.FromArgb(180, 61, 155, 255)).ToImmutable(), 1).ToImmutable();

    private DragTarget _drag;
    private bool _pressed;
    private readonly Dictionary<string, Vec2d> _livePins = new(StringComparer.Ordinal);
    private (int C, int R, Vec2d Offset)? _liveNode;
    private (int Corner, Vec2d Offset)? _liveCorner;
    private (int Mask, BezierPath Path)? _liveMask;

    private readonly record struct DragTarget(string Kind, string Id, int A, int B, int Part);

    public ProjectorCanvas()
    {
        Focusable = true;
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
            {
                old.SceneChanged -= OnScene;
                old.Pen.PropertyChanged -= OnPen;
            }
            if (change.NewValue is MapViewModel map)
            {
                map.SceneChanged += OnScene;
                map.Pen.PropertyChanged += OnPen;
            }
            OnScene();
        }
    }

    private void OnPen(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => InvalidateVisual();

    private void OnScene()
    {
        if (!_pressed)
        {
            _livePins.Clear();
            _liveNode = null;
            _liveCorner = null;
            _liveMask = null;
        }
        if (Map?.ViewProjector is { } pj)
            Aspect = pj.Intrinsics.Width / (double)Math.Max(1, pj.Intrinsics.Height);
        InvalidateVisual();
    }

    private StageView? View(Rect picture) =>
        Map?.ViewProjector is { } pj && picture.Width > 2 ? new StageView(pj.Intrinsics, pj.Lens, pj.Pose, picture) : null;

    /// <summary>Where a point in normalised output units is on this control (tests drive the pointer here).</summary>
    internal Point PointFor(Vec2d n) => Norm(PictureRect(), n);

    /// <summary>Where a projector pixel is on this control.</summary>
    internal Point PointForPixel(Vec2d pixel) => View(PictureRect()) is { } v ? v.FromPixel(pixel) : default;

    private static Point Norm(Rect r, Vec2d n) => new(r.X + n.X * r.Width, r.Y + n.Y * r.Height);

    private static Vec2d ToNorm(Rect r, Point p) => new((p.X - r.X) / r.Width, (p.Y - r.Y) / r.Height);

    protected override bool DrawFallback(DrawingContext context, Rect picture)
    {
        context.FillRectangle(new SolidColorBrush(Color.Parse("#0B0D0F")), picture);
        if (View(picture) is not { } v || Map is not { } map)
            return false;
        // The projector lights its raster and nothing beyond it.
        using (context.PushClip(picture))
            StageDrawing.Surfaces(context, v, map.Scene.Surfaces, map.SelectedKind == "surface" ? map.SelectedId : null, 0.5);
        return true;
    }

    protected override void DrawOverlay(DrawingContext context, Rect r)
    {
        if (Map is not { } map)
            return;
        if (map.ViewProjector is not { } pj || View(r) is not { } v)
        {
            StageDrawing.Label(context, "No projector yet — add one in the outliner.", new Point(r.X + 12, r.Y + 10));
            return;
        }
        using (context.PushClip(r))
            StageDrawing.Outlines(context, v, map.Scene.Surfaces, map.SelectedKind == "surface" ? map.SelectedId : null);
        var output = map.ViewOutput;
        if (output is not null)
        {
            DrawMasks(context, r, output, map);
            if (map.Tool == MapTool.Warp)
                DrawWarp(context, r, output);
        }
        DrawPen(context, r, map.Pen);
        DrawQuads(context, r, map, pj);
        DrawQuadTool(context, r, map.Quad);
        DrawPins(context, v, pj, map.Tool == MapTool.Pin);
        context.DrawRectangle(new Pen(new SolidColorBrush(StageDrawing.ColorOf(pj.Color)), 2), r.Deflate(1), 0);
        StageDrawing.Label(context, map.ViewTitle + "  ·  " + pj.Intrinsics.Width.ToString(CultureInfo.InvariantCulture) + "×" + pj.Intrinsics.Height.ToString(CultureInfo.InvariantCulture),
            new Point(r.X + 12, r.Bottom - 28));
    }

    private (OutputWarp Warp, Core.Mapping.WarpField Field)? _field;

    /// <summary>Where a raster-normalised point of the view's projector lands on the wall (the output's warp), or the point itself
    /// when no output shows the projector.</summary>
    private Vec2d WallOf(MapViewModel map, Vec2d raster)
    {
        if (map.WallOutput is not { } o)
            return raster;
        if (_field is not { } f || !ReferenceEquals(f.Warp, o.Warp))
        {
            f = (o.Warp, new Core.Mapping.WarpField(o.Warp));
            _field = f;
        }
        return f.Field.Evaluate(raster.X, raster.Y);
    }

    /// <summary>A quad's corners on the wall (wall-normalised), TL TR BR BL.</summary>
    private Vec2d[] QuadCorners(MapViewModel map, PixelQuad q) =>
        [WallOf(map, q.TopLeft), WallOf(map, q.TopRight), WallOf(map, q.BottomRight), WallOf(map, q.BottomLeft)];

    /// <summary>The quads drawn in this projector's picture: outlines, names and corners; the selected one's selected corner large.</summary>
    private void DrawQuads(DrawingContext ctx, Rect r, MapViewModel map, Projector pj)
    {
        if (map.Project is not { } p)
            return;
        foreach (var s in p.Stage.Surfaces)
        {
            if (s.Pixel is not { } q || q.ProjectorId != pj.Id || !s.Visible)
                continue;
            var selected = map.SelectedKind == "surface" && map.SelectedId == s.Id;
            var c = QuadCorners(map, q);
            var pen = new Pen(selected ? StageDrawing.Accent : new SolidColorBrush(StageDrawing.ColorOf(s.Color)), selected ? 2 : 1.4);
            for (var i = 0; i < 4; i++)
                ctx.DrawLine(pen, Norm(r, c[i]), Norm(r, c[(i + 1) % 4]));
            StageDrawing.Label(ctx, s.Name, Norm(r, c[0]) + new Point(8, 6), StageDrawing.DimText, 10.5);
            for (var i = 0; i < 4; i++)
            {
                var at = Norm(r, c[i]);
                var size = selected && map.SelectedCorner == i ? 9 : 5;
                ctx.DrawRectangle(selected && map.SelectedCorner == i ? StageDrawing.Accent : HandleFill, HandlePen, new Rect(at.X - size, at.Y - size, size * 2, size * 2), 2, 2);
            }
        }
    }

    /// <summary>The quad tool's corners so far, joined in order, and the rubber band from the last to the pointer.</summary>
    private static void DrawQuadTool(DrawingContext ctx, Rect r, ViewModels.Map.QuadTool quad)
    {
        var clicks = quad.Clicks;
        if (clicks.Count == 0)
            return;
        var pen = new Pen(StageDrawing.Accent, 2);
        for (var i = 1; i < clicks.Count; i++)
            ctx.DrawLine(pen, Norm(r, clicks[i - 1]), Norm(r, clicks[i]));
        if (clicks.Count == 4)
            ctx.DrawLine(pen, Norm(r, clicks[3]), Norm(r, clicks[0]));
        else if (quad.Pointer is { } to)
            ctx.DrawLine(new Pen(StageDrawing.Accent, 1.2, dashStyle: new DashStyle([4, 3], 0)), Norm(r, clicks[^1]), Norm(r, to));
        foreach (var c in clicks)
            ctx.DrawEllipse(StageDrawing.Accent, HandlePen, Norm(r, c), 6, 6);
    }

    /// <summary>The quad of this projector under a point of the picture (wall-normalised), topmost first, or null.</summary>
    private string? QuadAt(MapViewModel map, Projector pj, Vec2d n)
    {
        if (map.Project is not { } p)
            return null;
        for (var i = p.Stage.Surfaces.Length - 1; i >= 0; i--)
        {
            var s = p.Stage.Surfaces[i];
            if (s.Pixel is { } q && q.ProjectorId == pj.Id && s.Visible && Inside(QuadCorners(map, q), n))
                return s.Id;
        }
        return null;
    }

    private Vec2d NodeOffset(WarpLattice l, int c, int row) =>
        _liveNode is { } live && live.C == c && live.R == row ? live.Offset : l.Offset(c, row);

    private static Vec2d RestOf(WarpLattice l, int c, int row) => new(c / (double)(l.Columns - 1), row / (double)(l.Rows - 1));

    private Vec2d[] CornerPoints(Output o)
    {
        var pin = o.Warp.Pin;
        Vec2d[] rest = [new(0, 0), new(1, 0), new(1, 1), new(0, 1)];
        Vec2d[] off = [pin.TopLeft, pin.TopRight, pin.BottomRight, pin.BottomLeft];
        if (_liveCorner is { } lc)
            off[lc.Corner] = lc.Offset;
        return [rest[0] + off[0], rest[1] + off[1], rest[2] + off[2], rest[3] + off[3]];
    }

    private void DrawWarp(DrawingContext ctx, Rect r, Output o)
    {
        var corners = CornerPoints(o);
        for (var i = 0; i < 4; i++)
            ctx.DrawLine(new Pen(StageDrawing.Accent, 1.6), Norm(r, corners[i]), Norm(r, corners[(i + 1) % 4]));
        if (o.Warp.Lattice is { } l)
        {
            for (var row = 0; row < l.Rows; row++)
            {
                for (var c = 0; c < l.Columns; c++)
                {
                    var p = Norm(r, RestOf(l, c, row) + NodeOffset(l, c, row));
                    if (c + 1 < l.Columns)
                        ctx.DrawLine(LatticePen, p, Norm(r, RestOf(l, c + 1, row) + NodeOffset(l, c + 1, row)));
                    if (row + 1 < l.Rows)
                        ctx.DrawLine(LatticePen, p, Norm(r, RestOf(l, c, row + 1) + NodeOffset(l, c, row + 1)));
                }
            }
            for (var row = 0; row < l.Rows; row++)
                for (var c = 0; c < l.Columns; c++)
                    ctx.DrawEllipse(HandleFill, HandlePen, Norm(r, RestOf(l, c, row) + NodeOffset(l, c, row)), 5, 5);
        }
        foreach (var corner in corners)
        {
            var p = Norm(r, corner);
            ctx.DrawRectangle(StageDrawing.Accent, HandlePen, new Rect(p.X - 7, p.Y - 7, 14, 14), 3, 3);
        }
    }

    private void DrawMasks(DrawingContext ctx, Rect r, Output o, MapViewModel map)
    {
        for (var i = 0; i < o.Masks.Length; i++)
        {
            var m = o.Masks[i];
            var path = _liveMask is { } lm && lm.Mask == i ? lm.Path : m.Path;
            var geo = PathGeometry(r, path);
            ctx.DrawGeometry(m.Enabled && m.Mode == MaskMode.Subtract ? MaskFill : null, m.Id == map.SelectedMaskId ? PenPathPen : MaskPen, geo);
            if (m.Id == map.SelectedMaskId)
                DrawNodes(ctx, r, path.Nodes);
            if (path.Nodes.Length > 0)
                StageDrawing.Label(ctx, m.Name, Norm(r, path.Nodes[0].Point) + new Point(8, -22), StageDrawing.DimText, 10.5);
        }
    }

    private static StreamGeometry PathGeometry(Rect r, BezierPath path)
    {
        var g = new StreamGeometry();
        var nodes = path.Nodes;
        if (nodes.Length == 0)
            return g;
        using var gc = g.Open();
        gc.BeginFigure(Norm(r, nodes[0].Point), path.Closed);
        var count = path.Closed ? nodes.Length : nodes.Length - 1;
        for (var i = 0; i < count; i++)
        {
            var a = nodes[i];
            var b = nodes[(i + 1) % nodes.Length];
            gc.CubicBezierTo(Norm(r, a.Point + a.Out), Norm(r, b.Point + b.In), Norm(r, b.Point));
        }
        gc.EndFigure(path.Closed);
        return g;
    }

    private static void DrawNodes(DrawingContext ctx, Rect r, IReadOnlyList<BezierNode> nodes)
    {
        foreach (var n in nodes)
        {
            var p = Norm(r, n.Point);
            if (n.Smooth)
            {
                var hin = Norm(r, n.Point + n.In);
                var hout = Norm(r, n.Point + n.Out);
                ctx.DrawLine(HandleLinePen, hin, hout);
                ctx.DrawEllipse(StageDrawing.Accent, null, hin, 3.5, 3.5);
                ctx.DrawEllipse(StageDrawing.Accent, null, hout, 3.5, 3.5);
                ctx.DrawEllipse(HandleFill, HandlePen, p, 5, 5);
            }
            else
            {
                ctx.DrawRectangle(HandleFill, HandlePen, new Rect(p.X - 4.5, p.Y - 4.5, 9, 9), 0, 0);
            }
        }
    }

    private static void DrawPen(DrawingContext ctx, Rect r, PenTool pen)
    {
        if (pen.Nodes.Count == 0)
            return;
        ctx.DrawGeometry(null, PenPathPen, PathGeometry(r, pen.Path));
        DrawNodes(ctx, r, pen.Nodes);
        if (pen.CanClose)
        {
            var first = Norm(r, pen.Nodes[0].Point);
            ctx.DrawEllipse(null, new Pen(StageDrawing.Accent, 2), first, 9, 9);
        }
    }

    private void DrawPins(DrawingContext ctx, StageView v, Projector pj, bool active)
    {
        for (var i = 0; i < pj.Pins.Length; i++)
        {
            var pin = pj.Pins[i];
            var pixel = _livePins.TryGetValue(pin.Id, out var live) ? live : pin.Pixel;
            var at = v.FromPixel(pixel);
            var predicted = CameraModel.Project(pin.World, pj.Intrinsics, pj.Lens, pj.Pose);
            var residual = predicted is { } pr ? (pr - pixel).Length : double.PositiveInfinity;
            var brush = LightBrushConverter.Brushes.For(PinRow.LevelOf(residual));
            var pen = new Pen(brush, active ? 2 : 1.4);
            if (predicted is { } pp)
            {
                var ps = v.FromPixel(pp);
                ctx.DrawLine(new Pen(brush, 1, dashStyle: DashStyle.Dot), ps, at);
                ctx.DrawEllipse(null, new Pen(brush, 1), ps, 2.5, 2.5);
            }
            ctx.DrawLine(pen, new Point(at.X - 9, at.Y), new Point(at.X - 3, at.Y));
            ctx.DrawLine(pen, new Point(at.X + 3, at.Y), new Point(at.X + 9, at.Y));
            ctx.DrawLine(pen, new Point(at.X, at.Y - 9), new Point(at.X, at.Y - 3));
            ctx.DrawLine(pen, new Point(at.X, at.Y + 3), new Point(at.X, at.Y + 9));
            ctx.DrawEllipse(null, pen, at, 6, 6);
            if (active || _livePins.ContainsKey(pin.Id))
            {
                var words = double.IsFinite(residual) ? string.Create(CultureInfo.InvariantCulture, $"{i + 1} · {residual:0.0} px") : $"{i + 1} · behind";
                StageDrawing.Label(ctx, words, new Point(at.X + 10, at.Y + 6), brush, 10.5);
            }
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (Map is not { } map || map.ViewProjector is not { } pj)
            return;
        Focus();
        var r = PictureRect();
        var pt = e.GetCurrentPoint(this);
        var p = pt.Position;
        if (pt.Properties.IsRightButtonPressed)
        {
            OpenMenuAt(p, r);
            e.Handled = true;
            return;
        }
        var n = ToNorm(r, p);
        var hit = Grab / Math.Max(1, r.Width);
        var output = map.ViewOutput;
        _drag = default;
        switch (map.Tool)
        {
            case MapTool.Quad:
                map.QuadClick(n);
                break;
            case MapTool.Pen:
                map.Pen.Press(n, e.KeyModifiers.HasFlag(KeyModifiers.Alt), hit);
                break;
            case MapTool.Warp when output is not null:
                _drag = HitWarp(r, output, p);
                break;
            case MapTool.Pin:
                _drag = HitPin(r, pj, p);
                if (_drag.Kind is null && View(r) is { } v)
                {
                    var pixel = v.ToPixel(p);
                    if (map.Scene.Pick(v.RayAt(p)) is { } h)
                        map.AddPin(pj, h.Hit.Point, pixel);
                    else
                        map.SessionForNodes.Notify(Link.ToastKind.Refused, "A pin goes on a surface — click where the projector lights one.");
                }
                break;
            default:
                if (output is not null && HitMaskNode(r, output, map, p) is { Kind: not null } m)
                {
                    _drag = m;
                    break;
                }
                SelectAt(r, p, map, output);
                break;
        }
        _pressed = true;
        e.Pointer.Capture(this);
        e.Handled = true;
        InvalidateVisual();
    }

    private void SelectAt(Rect r, Point p, MapViewModel map, Output? output)
    {
        if (output is not null)
        {
            for (var i = output.Masks.Length - 1; i >= 0; i--)
            {
                var flat = PenTool.Flatten(output.Masks[i].Path);
                if (Inside(flat, ToNorm(r, p)))
                {
                    map.SelectedMaskId = output.Masks[i].Id;
                    return;
                }
            }
        }
        map.SelectedMaskId = null;
        if (map.ViewProjector is { } pj && QuadAt(map, pj, ToNorm(r, p)) is { } quad)
        {
            map.Select("surface", quad);
            return;
        }
        if (View(r) is { } v && map.Scene.Pick(v.RayAt(p)) is { } h)
            map.Select("surface", h.Surface.Surface.Id);
    }

    private static bool Inside(IReadOnlyList<Vec2d> poly, Vec2d q)
    {
        var inside = false;
        for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
        {
            if ((poly[i].Y > q.Y) != (poly[j].Y > q.Y) && q.X < (poly[j].X - poly[i].X) * (q.Y - poly[i].Y) / (poly[j].Y - poly[i].Y) + poly[i].X)
                inside = !inside;
        }
        return inside;
    }

    private DragTarget HitWarp(Rect r, Output o, Point p)
    {
        var corners = CornerPoints(o);
        for (var i = 0; i < 4; i++)
            if (Point.Distance(Norm(r, corners[i]), p) <= Grab)
                return new DragTarget("corner", o.Id, i, 0, 0);
        if (o.Warp.Lattice is { } l)
        {
            for (var row = 0; row < l.Rows; row++)
                for (var c = 0; c < l.Columns; c++)
                    if (Point.Distance(Norm(r, RestOf(l, c, row) + l.Offset(c, row)), p) <= Grab)
                        return new DragTarget("node", o.Id, c, row, 0);
        }
        return default;
    }

    private DragTarget HitPin(Rect r, Projector pj, Point p)
    {
        var v = new StageView(pj.Intrinsics, pj.Lens, pj.Pose, r);
        foreach (var pin in pj.Pins)
            if (Point.Distance(v.FromPixel(pin.Pixel), p) <= Grab)
                return new DragTarget("pin", pin.Id, 0, 0, 0);
        return default;
    }

    private static DragTarget HitMaskNode(Rect r, Output o, MapViewModel map, Point p)
    {
        var i = o.Masks.IndexOfId(map.SelectedMaskId);
        if (i < 0)
            return default;
        var nodes = o.Masks[i].Path.Nodes;
        for (var k = 0; k < nodes.Length; k++)
        {
            var n = nodes[k];
            if (n.Smooth && Point.Distance(Norm(r, n.Point + n.Out), p) <= Grab * 0.8)
                return new DragTarget("mask", o.Id, i, k, 2);
            if (n.Smooth && Point.Distance(Norm(r, n.Point + n.In), p) <= Grab * 0.8)
                return new DragTarget("mask", o.Id, i, k, 1);
            if (Point.Distance(Norm(r, n.Point), p) <= Grab)
                return new DragTarget("mask", o.Id, i, k, 0);
        }
        return default;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (Map is { Tool: MapTool.Quad } quadMap)
        {
            var at = ToNorm(PictureRect(), e.GetPosition(this));
            quadMap.Quad.MoveTo(at.X is >= 0 and <= 1 && at.Y is >= 0 and <= 1 ? at : null);
        }
        if (!_pressed || Map is not { } map || map.ViewProjector is not { } pj)
            return;
        var r = PictureRect();
        var p = e.GetPosition(this);
        var n = ToNorm(r, p);
        var output = map.ViewOutput;
        if (map.Tool == MapTool.Pen)
        {
            map.Pen.Drag(n, Grab / Math.Max(1, r.Width));
            return;
        }
        switch (_drag.Kind)
        {
            case "corner" when output is not null:
                Vec2d[] rest = [new(0, 0), new(1, 0), new(1, 1), new(0, 1)];
                _liveCorner = (_drag.A, n - rest[_drag.A]);
                map.MoveCorner(output, _drag.A, _liveCorner.Value.Offset, commit: false);
                break;
            case "node" when output?.Warp.Lattice is { } l:
                _liveNode = (_drag.A, _drag.B, n - RestOf(l, _drag.A, _drag.B));
                map.MoveLatticeNode(output, _drag.A, _drag.B, _liveNode.Value.Offset, commit: false);
                break;
            case "pin":
                var pixel = new StageView(pj.Intrinsics, pj.Lens, pj.Pose, r).ToPixel(p);
                _livePins[_drag.Id] = pixel;
                if (map.Inspector.Current is ProjectorInspector pi && pi.Pins.FirstOrDefault(x => x.Id == _drag.Id) is { } row)
                    row.Live(pixel, pj);
                map.MovePin(_drag.Id, pixel, commit: false);
                break;
            case "mask" when output is not null:
                var path = _liveMask is { } lm && lm.Mask == _drag.A ? lm.Path : output.Masks[_drag.A].Path;
                var node = path.Nodes[_drag.B];
                node = _drag.Part switch
                {
                    0 => node with { Point = n },
                    2 => node with { Out = n - node.Point, In = node.Smooth ? Mirror(n - node.Point, node.In) : node.In },
                    _ => node with { In = n - node.Point, Out = node.Smooth ? Mirror(n - node.Point, node.Out) : node.Out },
                };
                var next = path with { Nodes = path.Nodes.SetItem(_drag.B, node) };
                _liveMask = (_drag.A, next);
                map.MoveMask(output, _drag.A, next, commit: false);
                break;
        }
        InvalidateVisual();
    }

    /// <summary>The opposite handle of a smooth node: collinear with the dragged one, keeping its own length.</summary>
    private static Vec2d Mirror(Vec2d dragged, Vec2d other)
    {
        var len = other.Length;
        var d = dragged.Length;
        return d < 1e-9 ? other : dragged * (-(len < 1e-9 ? 1 : len / d));
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!_pressed || Map is not { } map)
            return;
        _pressed = false;
        e.Pointer.Capture(null);
        var output = map.ViewOutput;
        if (map.Tool == MapTool.Pen)
            map.Pen.Release();
        switch (_drag.Kind)
        {
            case "corner" when output is not null && _liveCorner is { } lc:
                map.MoveCorner(output, lc.Corner, lc.Offset, commit: true);
                break;
            case "node" when output is not null && _liveNode is { } ln:
                map.MoveLatticeNode(output, ln.C, ln.R, ln.Offset, commit: true);
                break;
            case "pin" when _livePins.TryGetValue(_drag.Id, out var px):
                map.MovePin(_drag.Id, px, commit: true);
                break;
            case "mask" when output is not null && _liveMask is { } lm:
                map.MoveMask(output, lm.Mask, lm.Path, commit: true);
                break;
        }
        _drag = default;
        InvalidateVisual();
    }

    /// <summary>The right-click menu of what is under the pointer, picked as a left click picks it: a quad drawn in this projector's
    /// picture over the meshes behind it, else the surface the projector lights there (each selected, its outliner entries); on
    /// nothing, the view's own entries (the quad tool, On the wall).</summary>
    private void OpenMenuAt(Point p, Rect r)
    {
        if (Map is not { } map)
            return;
        var id = map.ViewProjector is { } pj && QuadAt(map, pj, ToNorm(r, p)) is { } quad ? quad
            : View(r) is { } v && map.Scene.Pick(v.RayAt(p)) is { } hit ? hit.Surface.Surface.Id
            : null;
        IEnumerable<DeskAction> actions = map.ViewActions;
        if (id is not null)
        {
            map.Select("surface", id);
            if (map.Outliner.SelectMany(g => g.Nodes).FirstOrDefault(x => x.Id == id) is not { } node)
                return;
            actions = node.Actions;
        }
        var menu = new ContextMenu();
        DeskMenu.Fill(menu.Items, actions);
        ContextMenu = menu;
        menu.Open(this);
    }
}
