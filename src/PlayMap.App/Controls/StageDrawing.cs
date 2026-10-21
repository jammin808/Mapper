using System.Globalization;
using Avalonia;
using Avalonia.Media;
using PlayMap.App.Scene;
using PlayMap.Core.Geometry;
using PlayMap.Core.Model;
using PlayMap.Core.Numerics;

namespace PlayMap.App.Controls;

/// <summary>A pinhole view of the stage on a picture rectangle: the camera's raster maps onto the rectangle,
/// world points project through <see cref="CameraModel"/> exactly as the engine renders them, and segments
/// are clipped at a near plane so a line that passes behind the viewer still draws its visible part.</summary>
public readonly record struct StageView(Intrinsics K, LensDistortion Lens, Pose Pose, Rect Picture)
{
    private const double Near = 0.02;

    public double Sx => Picture.Width / K.Width;

    public double Sy => Picture.Height / K.Height;

    public Point FromPixel(Vec2d px) => new(Picture.X + px.X * Sx, Picture.Y + px.Y * Sy);

    public Vec2d ToPixel(Point p) => new((p.X - Picture.X) / Sx, (p.Y - Picture.Y) / Sy);

    public Point? Project(Vec3d world) => CameraModel.Project(world, K, Lens, Pose) is { } px ? FromPixel(px) : null;

    public Ray RayAt(Point p) => CameraModel.PixelRay(ToPixel(p), K, Lens, Pose);

    /// <summary>The visible part of a world segment on screen, or null when it is wholly behind the viewer.</summary>
    public (Point A, Point B)? Segment(Vec3d a, Vec3d b)
    {
        var ca = Pose.WorldToCamera(a);
        var cb = Pose.WorldToCamera(b);
        if (ca.Z < Near && cb.Z < Near)
            return null;
        if (ca.Z < Near)
            ca = Vec3d.Lerp(ca, cb, (Near - ca.Z) / (cb.Z - ca.Z));
        else if (cb.Z < Near)
            cb = Vec3d.Lerp(cb, ca, (Near - cb.Z) / (ca.Z - cb.Z));
        var pa = CameraModel.ProjectCamera(ca, K, Lens);
        var pb = CameraModel.ProjectCamera(cb, K, Lens);
        return pa is { } x && pb is { } y ? (FromPixel(x), FromPixel(y)) : null;
    }

    public double Depth(Vec3d world) => Pose.WorldToCamera(world).Z;
}

/// <summary>The drawing the MAP views share: the stage's surfaces filled and outlined, the floor grid, the
/// projectors' frusta and pins. Pens and brushes are made once.</summary>
public static class StageDrawing
{
    public static readonly IBrush Background = new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
        GradientStops = [new GradientStop(Color.Parse("#16191D"), 0), new GradientStop(Color.Parse("#0E1012"), 1)],
    }.ToImmutable();

    public static readonly IPen GridPen = new Pen(new SolidColorBrush(Color.Parse("#232A31")).ToImmutable(), 1).ToImmutable();
    public static readonly IPen GridAxisPen = new Pen(new SolidColorBrush(Color.Parse("#34404B")).ToImmutable(), 1.2).ToImmutable();
    public static readonly IBrush Accent = new SolidColorBrush(Color.Parse("#3D9BFF")).ToImmutable();
    public static readonly IPen AccentPen = new Pen(Accent, 2.2).ToImmutable();
    public static readonly IBrush TextBrush = new SolidColorBrush(Color.Parse("#E7E9EC")).ToImmutable();
    public static readonly IBrush DimText = new SolidColorBrush(Color.Parse("#A2A9B2")).ToImmutable();
    public static readonly IBrush Shadow = new SolidColorBrush(Color.FromArgb(170, 10, 12, 14)).ToImmutable();

    public static Color ColorOf(string? hex, byte alpha = 255)
    {
        if (PlayMap.Core.Content.ColorText.TryParse(hex, out var rgb))
            return Color.FromArgb(alpha, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        return Color.FromArgb(alpha, 139, 149, 161);
    }

    public static void Floor(DrawingContext ctx, StageView v, double half = 5, double step = 0.5)
    {
        for (var x = -half; x <= half + 1e-9; x += step)
        {
            if (v.Segment(new Vec3d(x, 0, -half + 2), new Vec3d(x, 0, half + 2)) is { } s)
                ctx.DrawLine(Math.Abs(x) < 1e-9 ? GridAxisPen : GridPen, s.A, s.B);
        }
        for (var z = -half + 2; z <= half + 2 + 1e-9; z += step)
        {
            if (v.Segment(new Vec3d(-half, 0, z), new Vec3d(half, 0, z)) is { } s)
                ctx.DrawLine(Math.Abs(z) < 1e-9 ? GridAxisPen : GridPen, s.A, s.B);
        }
    }

    /// <summary>The surfaces' faces, far to near, tinted by their colour and shaded by how squarely they face
    /// the viewer; hidden surfaces are skipped, the selected one is brighter.</summary>
    public static void Surfaces(DrawingContext ctx, StageView v, IReadOnlyList<SceneSurface> surfaces, string? selectedId, double fill = 0.34)
    {
        var faces = new List<(double Depth, Point A, Point B, Point C, Color Color)>();
        var eye = v.Pose.Position;
        foreach (var s in surfaces)
        {
            if (!s.Surface.Visible || s.World is not { } mesh)
                continue;
            var selected = s.Surface.Id == selectedId;
            for (var t = 0; t < mesh.TriangleCount; t++)
            {
                var (ia, ib, ic) = mesh.Triangle(t);
                var a = mesh.Position(ia);
                var b = mesh.Position(ib);
                var c = mesh.Position(ic);
                if (v.Project(a) is not { } pa || v.Project(b) is not { } pb || v.Project(c) is not { } pc)
                    continue;
                var n = Vec3d.Cross(b - a, c - a).Normalized();
                var centre = (a + b + c) / 3;
                var toEye = (eye - centre).Normalized();
                var facing = Math.Abs(Vec3d.Dot(n, toEye));
                var shade = 0.45 + 0.55 * facing;
                var baseColor = ColorOf(s.Surface.Color);
                var alpha = (byte)Math.Clamp((selected ? fill + 0.18 : fill) * 255, 0, 255);
                var col = Color.FromArgb(alpha, (byte)(baseColor.R * shade), (byte)(baseColor.G * shade), (byte)(baseColor.B * shade));
                faces.Add((v.Depth(centre), pa, pb, pc, col));
            }
        }
        faces.Sort((x, y) => y.Depth.CompareTo(x.Depth));
        foreach (var f in faces)
        {
            var g = new StreamGeometry();
            using (var gc = g.Open())
            {
                gc.BeginFigure(f.A, true);
                gc.LineTo(f.B);
                gc.LineTo(f.C);
                gc.EndFigure(true);
            }
            ctx.DrawGeometry(new SolidColorBrush(f.Color), null, g);
        }
    }

    public static void Outlines(DrawingContext ctx, StageView v, IReadOnlyList<SceneSurface> surfaces, string? selectedId)
    {
        foreach (var s in surfaces)
        {
            if (s.World is not { } mesh)
                continue;
            var selected = s.Surface.Id == selectedId;
            var pen = selected ? AccentPen : new Pen(new SolidColorBrush(ColorOf(s.Surface.Color, s.Surface.Visible ? (byte)210 : (byte)70)), s.Surface.Visible ? 1.3 : 1,
                dashStyle: s.Surface.Visible ? null : DashStyle.Dash);
            foreach (var (a, b) in s.Edges)
                if (v.Segment(mesh.Position(a), mesh.Position(b)) is { } seg)
                    ctx.DrawLine(pen, seg.A, seg.B);
        }
    }

    /// <summary>A projector's frustum out to where its picture lands (about the stage), its body and name.</summary>
    public static void Frustum(DrawingContext ctx, StageView v, Projector pj, bool selected, double reach)
    {
        var color = ColorOf(pj.Color, selected ? (byte)255 : (byte)190);
        var pen = new Pen(new SolidColorBrush(color), selected ? 2 : 1.2);
        var thin = new Pen(new SolidColorBrush(ColorOf(pj.Color, 90)), 1);
        var k = pj.Intrinsics;
        Vec2d[] corners = [new(0, 0), new(k.Width, 0), new(k.Width, k.Height), new(0, k.Height)];
        var far = corners.Select(c => CameraModel.PixelRay(c, k, pj.Lens, pj.Pose).At(reach)).ToArray();
        for (var i = 0; i < 4; i++)
        {
            if (v.Segment(pj.Pose.Position, far[i]) is { } s)
                ctx.DrawLine(thin, s.A, s.B);
            if (v.Segment(far[i], far[(i + 1) % 4]) is { } e)
                ctx.DrawLine(pen, e.A, e.B);
        }
        if (v.Project(pj.Pose.Position) is { } body)
        {
            ctx.DrawEllipse(new SolidColorBrush(color), new Pen(Shadow, 2), body, selected ? 8 : 6.5, selected ? 8 : 6.5);
            Label(ctx, pj.Name, new Point(body.X + 11, body.Y - 8), selected ? TextBrush : DimText);
        }
    }

    /// <summary>The pins as world markers (3D view): coloured by how far the solve lands them from their pixel, each with its light's
    /// shape beside it (✓ △ ✕), never the colour alone.</summary>
    public static void WorldPins(DrawingContext ctx, StageView v, Projector pj)
    {
        foreach (var pin in pj.Pins)
        {
            if (v.Project(pin.World) is not { } p)
                continue;
            var r = Link.PinResiduals.Of(pj, pin);
            var level = ViewModels.PinRow.LevelOf(r);
            var brush = LightBrushConverter.Brushes.For(level);
            var g = new StreamGeometry();
            using (var gc = g.Open())
            {
                gc.BeginFigure(new Point(p.X, p.Y - 5), true);
                gc.LineTo(new Point(p.X + 5, p.Y));
                gc.LineTo(new Point(p.X, p.Y + 5));
                gc.LineTo(new Point(p.X - 5, p.Y));
                gc.EndFigure(true);
            }
            ctx.DrawGeometry(pin.Enabled ? brush : null, new Pen(brush, 1.2), g);
            Label(ctx, Infrastructure.StatusShapes.Shape(level), new Point(p.X + 7, p.Y - 7), brush, 9.5);
        }
    }

    public static void Label(DrawingContext ctx, string text, Point at, IBrush? brush = null, double size = 11.5)
    {
        var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, size, brush ?? TextBrush);
        ctx.FillRectangle(Shadow, new Rect(at.X - 3, at.Y - 1, ft.Width + 6, ft.Height + 2), 3);
        ctx.DrawText(ft, at);
    }

    /// <summary>A rough reach for a projector's frustum: to the stage's far side from the projector.</summary>
    public static double Reach(Projector pj, SceneCache scene)
    {
        ArgumentNullException.ThrowIfNull(pj);
        ArgumentNullException.ThrowIfNull(scene);
        var (min, max) = scene.Bounds();
        var centre = (min + max) / 2;
        return Math.Max(1, Vec3d.Distance(pj.Pose.Position, centre) * 1.05);
    }
}
