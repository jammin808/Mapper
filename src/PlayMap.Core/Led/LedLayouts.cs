using PlayMap.Core.Model;
using PlayMap.Core.Numerics;

namespace PlayMap.Core.Led;

/// <summary>A fixture's pixels in its space, in address order: the layout's local points, placed by the fixture's placement (2D
/// spaces use position x and y, rotation about z in radians and scale x and y), reversed when the fixture is.</summary>
public static class LedLayouts
{
    /// <summary>The most pixels one fixture holds.</summary>
    public const int MaxPixels = 65536;

    /// <summary>The layout's points before placement, in address order. A line runs from (0,0) to (size.x, 0) with its first and
    /// last LED on the ends; a grid fills size.x × size.y from its start corner, rows first unless columns first, alternating
    /// direction when snaked; a ring sits on a circle of diameter size.x from angle 0 clockwise (y down); points are as given.</summary>
    public static Vec3d[] Local(LedLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        switch (layout.Shape)
        {
            case LedShape.Line:
                {
                    var n = Math.Clamp(layout.Count, 0, MaxPixels);
                    var pts = new Vec3d[n];
                    for (var i = 0; i < n; i++)
                        pts[i] = new Vec3d(n == 1 ? 0 : layout.Size.X * i / (n - 1), 0, 0);
                    return pts;
                }
            case LedShape.Grid:
                {
                    var cols = Math.Max(0, layout.Count);
                    var rows = Math.Max(0, layout.Rows);
                    if ((long)cols * rows > MaxPixels)
                        return [];
                    var pts = new Vec3d[cols * rows];
                    var right = layout.Start is LedStart.TopRight or LedStart.BottomRight;
                    var bottom = layout.Start is LedStart.BottomLeft or LedStart.BottomRight;
                    var outer = layout.ColumnsFirst ? cols : rows;
                    var inner = layout.ColumnsFirst ? rows : cols;
                    var k = 0;
                    for (var a = 0; a < outer; a++)
                    {
                        for (var b0 = 0; b0 < inner; b0++)
                        {
                            var b = layout.Snake && (a & 1) == 1 ? inner - 1 - b0 : b0;
                            var (c, r) = layout.ColumnsFirst ? (a, b) : (b, a);
                            if (right)
                                c = cols - 1 - c;
                            if (bottom)
                                r = rows - 1 - r;
                            pts[k++] = new Vec3d(cols == 1 ? 0 : layout.Size.X * c / (cols - 1), rows == 1 ? 0 : layout.Size.Y * r / (rows - 1), 0);
                        }
                    }
                    return pts;
                }
            case LedShape.Ring:
                {
                    var n = Math.Clamp(layout.Count, 0, MaxPixels);
                    var radius = layout.Size.X / 2;
                    var pts = new Vec3d[n];
                    for (var i = 0; i < n; i++)
                    {
                        var a = 2 * Math.PI * i / Math.Max(1, n);
                        // Clockwise with y down: angle grows towards +y.
                        pts[i] = new Vec3d(radius * Math.Cos(a), radius * Math.Sin(a), 0);
                    }
                    return pts;
                }
            case LedShape.Points:
                return layout.Points.IsDefault ? [] : [.. layout.Points.Take(MaxPixels)];
            default:
                return [];
        }
    }

    /// <summary>Pixels in the fixture's space, in address order.</summary>
    public static Vec3d[] Placed(LedFixture fixture)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        var local = Local(fixture.Layout);
        var t = fixture.Placement;
        var pts = new Vec3d[local.Length];
        if (fixture.Space == LedSpace.World)
        {
            for (var i = 0; i < local.Length; i++)
                pts[i] = t.Apply(local[i]);
        }
        else
        {
            var cos = Math.Cos(t.Rotation.Z);
            var sin = Math.Sin(t.Rotation.Z);
            for (var i = 0; i < local.Length; i++)
            {
                var x = local[i].X * t.Scale.X;
                var y = local[i].Y * t.Scale.Y;
                pts[i] = new Vec3d(t.Position.X + x * cos - y * sin, t.Position.Y + x * sin + y * cos, 0);
            }
        }
        if (fixture.Reverse)
            Array.Reverse(pts);
        return pts;
    }

    /// <summary>Half the distance to each pixel's nearest neighbour in address order (the area footprint's default), in space units;
    /// a fixture of one pixel answers 0.</summary>
    public static double HalfPitch(ReadOnlySpan<Vec3d> pts)
    {
        if (pts.Length < 2)
            return 0;
        var best = double.MaxValue;
        for (var i = 1; i < pts.Length; i++)
        {
            var d = (pts[i] - pts[i - 1]).Length;
            if (d > 1e-12 && d < best)
                best = d;
        }
        return best == double.MaxValue ? 0 : best / 2;
    }
}
