using System.Globalization;
using PlayMap.Core.Numerics;

namespace PlayMap.Core.Align;

/// <summary>Where the canvas lands on the measured plane: a rectangle (x, y, width, height in plane coordinates, y down), how it was
/// chosen, how much of it the outputs cover, and the words. Canvas uv (0–1, y down) is the point's place in the rectangle.</summary>
public sealed record CanvasTargetResult(double X, double Y, double Width, double Height, string Fit, double CoverageShare, string Words)
{
    public Vec2d ToUv(Vec2d plane) => new((plane.X - X) / Width, (plane.Y - Y) / Height);

    public Vec2d FromUv(Vec2d uv) => new(X + uv.X * Width, Y + uv.Y * Height);
}

/// <summary>
/// The canvas's place on the plane. FIT INSIDE: the largest rectangle of the canvas's aspect wholly inside what the outputs cover
/// (a coverage raster 1024 cells across; for each height, a summed-area table tells whether some placement is fully covered —
/// binary search on the height; of the placements that fit, the one nearest the coverage's centroid), shrunk by the margin. FIT
/// CORNERS: the plane is the screen's corners already, so the target is that rectangle. Pure.
/// </summary>
public static class CanvasTarget
{
    /// <summary>The rectangle [0, aspect] × [0, 1] the corners' rectification made, with its coverage measured.</summary>
    public static CanvasTargetResult Corners(double aspect, Func<Vec2d, bool> covered)
    {
        ArgumentNullException.ThrowIfNull(covered);
        var target = new CanvasTargetResult(0, 0, aspect, 1, "corners", 0, "");
        var share = CoverageOf(target, covered);
        return target with
        {
            CoverageShare = share,
            Words = string.Create(CultureInfo.InvariantCulture, $"the outputs cover {share * 100:0.#} % of it"),
        };
    }

    /// <summary>The largest rectangle of <paramref name="aspect"/> inside the covered part of <paramref name="min"/>–<paramref name="max"/>
    /// (plane coordinates), shrunk by <paramref name="marginPct"/> % on every side; null when nothing is covered.</summary>
    public static CanvasTargetResult? Inside(Func<Vec2d, bool> covered, Vec2d min, Vec2d max, double aspect, double marginPct, int cellsAcross = 1024)
    {
        ArgumentNullException.ThrowIfNull(covered);
        if (!(aspect > 0) || !(max.X > min.X) || !(max.Y > min.Y))
            return null;
        var cell = (max.X - min.X) / cellsAcross;
        var w = cellsAcross;
        var h = Math.Max(1, (int)Math.Ceiling((max.Y - min.Y) / cell));
        var grid = new bool[w * h];
        long sx = 0, sy = 0, count = 0;
        Parallel.For(0, h, y =>
        {
            for (var x = 0; x < w; x++)
                grid[y * w + x] = covered(new Vec2d(min.X + (x + 0.5) * cell, min.Y + (y + 0.5) * cell));
        });
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                if (!grid[y * w + x])
                    continue;
                sx += x;
                sy += y;
                count++;
            }
        }
        if (count == 0)
            return null;
        var sat = new int[(w + 1) * (h + 1)];
        for (var y = 0; y < h; y++)
        {
            var row = 0;
            for (var x = 0; x < w; x++)
            {
                row += grid[y * w + x] ? 1 : 0;
                sat[(y + 1) * (w + 1) + x + 1] = sat[y * (w + 1) + x + 1] + row;
            }
        }
        var cx = (double)sx / count + 0.5;
        var cy = (double)sy / count + 0.5;
        int lo = 0, hi = h;
        (int X, int Y, int W, int H)? best = null;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) / 2;
            if (Place(sat, w, h, mid, aspect, cx, cy) is { } fit)
            {
                lo = mid;
                best = fit;
            }
            else
            {
                hi = mid - 1;
            }
        }
        if (best is not { } b)
            return null;
        var rx = min.X + b.X * cell;
        var ry = min.Y + b.Y * cell;
        var rw = b.W * cell;
        var rh = b.H * cell;
        var m = Math.Clamp(marginPct, 0, 40) / 100;
        var target = new CanvasTargetResult(rx + m * rw, ry + m * rh, rw * (1 - 2 * m), rh * (1 - 2 * m), "inside", 1, "");
        return target with
        {
            Words = string.Create(CultureInfo.InvariantCulture,
                $"the canvas is the largest {aspect:0.###}:1 rectangle the outputs cover{(m > 0 ? $", less a {marginPct:0.#} % margin" : "")}"),
        };
    }

    /// <summary>The share of the target's cells (64 × 64 samples) the outputs cover.</summary>
    public static double CoverageOf(CanvasTargetResult target, Func<Vec2d, bool> covered)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(covered);
        var hits = 0;
        for (var y = 0; y < 64; y++)
            for (var x = 0; x < 64; x++)
                if (covered(target.FromUv(new Vec2d((x + 0.5) / 64, (y + 0.5) / 64))))
                    hits++;
        return hits / 4096.0;
    }

    /// <summary>A fully covered placement of a rectangle <paramref name="height"/> cells tall and aspect wide (the one nearest the
    /// centroid), or null.</summary>
    private static (int X, int Y, int W, int H)? Place(int[] sat, int w, int h, int height, double aspect, double cx, double cy)
    {
        var width = (int)Math.Floor(height * aspect);
        if (width < 1 || height < 1 || width > w || height > h)
            return null;
        var area = width * height;
        (int X, int Y, int W, int H)? best = null;
        var bestD = double.MaxValue;
        for (var y = 0; y + height <= h; y++)
        {
            for (var x = 0; x + width <= w; x++)
            {
                var sum = sat[(y + height) * (w + 1) + x + width] - sat[y * (w + 1) + x + width] - sat[(y + height) * (w + 1) + x] + sat[y * (w + 1) + x];
                if (sum != area)
                    continue;
                var dx = x + width / 2.0 - cx;
                var dy = y + height / 2.0 - cy;
                var d = dx * dx + dy * dy;
                if (d < bestD)
                {
                    bestD = d;
                    best = (x, y, width, height);
                }
            }
        }
        return best;
    }
}
