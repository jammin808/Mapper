using System.Numerics;
using PlayMap.Core.Model;
using PlayMap.Core.Numerics;

namespace PlayMap.Core.Mapping;

/// <summary>Rasterises a list of soft masks into a coverage map (0..1 per pixel, row-major, pixel (x, y)
/// sampled at its centre (x + ½, y + ½)). The masks combine in order:
/// <list type="bullet">
/// <item>the start is nothing when the first enabled mask adds, everything otherwise (no masks: everything);</item>
/// <item>Add: c ← min(1, c + m) — two masks that abut with the same feather sum to one across the seam;</item>
/// <item>Subtract: c ← max(0, c − m);</item>
/// <item>Intersect: c ← c · m;</item>
/// </list>
/// where m is the mask's own soft coverage (1 − m when inverted): a smooth step across a band of width
/// <see cref="Mask.Feather"/> centred on the outline (½ exactly on it), or a one-pixel linear ramp when the
/// feather is narrower than a pixel — the anti-aliased hard edge. Inside/outside comes from scanline
/// crossings (non-zero winding); the exact distance to the outline is computed only for pixels within half
/// a band of an edge, so the cost is the area plus the band, not the area times the outline.
/// Path coordinates are normalised (0..1 across the raster): output units for an output mask, UV units for
/// a surface mask. Deterministic; rows run in parallel.</summary>
public static class MaskRasterizer
{
    /// <summary>Flattening tolerance in pixels.</summary>
    private const double TolerancePx = 0.15;

    /// <summary>An output's masks over a <paramref name="width"/> × <paramref name="height"/> raster;
    /// <see cref="Mask.Feather"/> is in output pixels, so a raster smaller than the output scales it.</summary>
    public static float[] RasterizeOutputMasks(IReadOnlyList<Mask> masks, int width, int height, int outputWidth, int outputHeight)
    {
        var scale = Math.Sqrt(width / (double)Math.Max(1, outputWidth) * (height / (double)Math.Max(1, outputHeight)));
        var map = new float[Math.Max(0, width) * Math.Max(0, height)];
        Rasterize(masks, width, height, scale, map);
        return map;
    }

    /// <summary>A surface's masks over a <paramref name="width"/> × <paramref name="height"/> texture of its
    /// UV square; <see cref="Mask.Feather"/> is in UV units (a fraction of the texture, its mean side).</summary>
    public static float[] RasterizeSurfaceMasks(IReadOnlyList<Mask> masks, int width, int height)
    {
        var map = new float[Math.Max(0, width) * Math.Max(0, height)];
        Rasterize(masks, width, height, (width + height) / 2.0, map);
        return map;
    }

    /// <summary>The core: <paramref name="featherScale"/> raster pixels per unit of
    /// <see cref="Mask.Feather"/>; writes width × height values into <paramref name="coverage"/>.</summary>
    public static void Rasterize(IReadOnlyList<Mask> masks, int width, int height, double featherScale, float[] coverage)
    {
        ArgumentNullException.ThrowIfNull(masks);
        ArgumentNullException.ThrowIfNull(coverage);
        if (width <= 0 || height <= 0)
            return;
        if (coverage.Length < width * height)
            throw new ArgumentException("The coverage map is smaller than the raster.", nameof(coverage));
        var shapes = new List<Shape>(masks.Count);
        foreach (var m in masks)
            if (m.Enabled)
                shapes.Add(new Shape(m, width, height, featherScale));
        var start = shapes.Count == 0 || shapes[0].Mode != MaskMode.Add ? 1f : 0f;
        if (shapes.Count == 0)
        {
            coverage.AsSpan(0, width * height).Fill(1f);
            return;
        }
        var maxEdges = shapes.Max(s => s.EdgeCount);
        Parallel.For(0, height, () => new RowScratch(width, maxEdges), (y, _, scratch) =>
        {
            var row = coverage.AsSpan(y * width, width);
            row.Fill(start);
            foreach (var shape in shapes)
            {
                // The mask is written on [lo, hi); outside it is the background (0, or 1 inverted).
                shape.RasterizeRow(y, scratch, out var lo, out var hi);
                var m = scratch.Mask.AsSpan(lo, hi - lo);
                var span = row[lo..hi];
                var outside = shape.Background;
                switch (shape.Mode)
                {
                    case MaskMode.Add:
                        if (outside > 0)
                        {
                            row[..lo].Fill(1f);
                            row[hi..].Fill(1f);
                        }
                        AddClamped(span, m);
                        break;
                    case MaskMode.Subtract:
                        if (outside > 0)
                        {
                            row[..lo].Clear();
                            row[hi..].Clear();
                        }
                        SubtractClamped(span, m);
                        break;
                    default:
                        if (outside <= 0)
                        {
                            row[..lo].Clear();
                            row[hi..].Clear();
                        }
                        Multiply(span, m);
                        break;
                }
            }
            return scratch;
        }, _ => { /* scratch is garbage when the row loop ends */ });
    }

    private static void AddClamped(Span<float> row, ReadOnlySpan<float> m)
    {
        var i = 0;
        if (Vector.IsHardwareAccelerated)
        {
            var one = Vector<float>.One;
            for (; i <= row.Length - Vector<float>.Count; i += Vector<float>.Count)
                Vector.Min(new Vector<float>(row[i..]) + new Vector<float>(m[i..]), one).CopyTo(row[i..]);
        }
        for (; i < row.Length; i++)
            row[i] = Math.Min(1f, row[i] + m[i]);
    }

    private static void SubtractClamped(Span<float> row, ReadOnlySpan<float> m)
    {
        var i = 0;
        if (Vector.IsHardwareAccelerated)
        {
            var zero = Vector<float>.Zero;
            for (; i <= row.Length - Vector<float>.Count; i += Vector<float>.Count)
                Vector.Max(new Vector<float>(row[i..]) - new Vector<float>(m[i..]), zero).CopyTo(row[i..]);
        }
        for (; i < row.Length; i++)
            row[i] = Math.Max(0f, row[i] - m[i]);
    }

    private static void Multiply(Span<float> row, ReadOnlySpan<float> m)
    {
        var i = 0;
        if (Vector.IsHardwareAccelerated)
        {
            for (; i <= row.Length - Vector<float>.Count; i += Vector<float>.Count)
                (new Vector<float>(row[i..]) * new Vector<float>(m[i..])).CopyTo(row[i..]);
        }
        for (; i < row.Length; i++)
            row[i] *= m[i];
    }

    /// <summary>The soft coverage of one mask at a point given in pixels (the same rule as the raster):
    /// for tests and for the editor's readout under the cursor.</summary>
    public static double CoverageAt(Mask mask, int width, int height, double featherScale, Vec2d pixel)
    {
        ArgumentNullException.ThrowIfNull(mask);
        var shape = new Shape(mask, width, height, featherScale);
        return shape.CoverageAt(pixel);
    }

    private sealed class RowScratch(int width, int edges)
    {
        public readonly float[] Mask = new float[width];
        public readonly float[] Distance = new float[width];
        public readonly double[] CrossX = new double[Math.Max(1, edges)];
        public readonly int[] CrossDir = new int[Math.Max(1, edges)];
    }

    /// <summary>One mask in raster pixels: its flattened outline as edges, and its band.</summary>
    private sealed class Shape
    {
        private readonly Vec2d[] _a;
        private readonly Vec2d[] _b;
        private readonly double _half; // half the band, pixels
        private readonly bool _smooth; // smooth step (feather ≥ 1 px) or linear ramp (anti-aliasing)
        private readonly bool _invert;
        private readonly double _minY;
        private readonly double _maxY;
        private readonly int _spanLo; // pixels the mask can differ from its background on, every row
        private readonly int _spanHi;

        public MaskMode Mode { get; }
        public int EdgeCount => _a.Length;

        /// <summary>The mask's value away from its outline's reach: 0, or 1 when inverted.</summary>
        public float Background => _invert ? 1f : 0f;

        public Shape(Mask mask, int width, int height, double featherScale)
        {
            Mode = mask.Mode;
            _invert = mask.Invert;
            var band = Math.Max(0, mask.Feather) * featherScale;
            _smooth = band >= 1;
            _half = Math.Max(band, 1) / 2;
            var path = BezierTools.Transform(mask.Path, new Vec2d(width, height), Vec2d.Zero);
            var poly = BezierTools.Flatten(path, TolerancePx);
            var n = poly.Length >= 3 ? poly.Length : 0;
            _a = new Vec2d[n];
            _b = new Vec2d[n];
            _minY = double.MaxValue;
            _maxY = double.MinValue;
            var minX = double.MaxValue;
            var maxX = double.MinValue;
            for (var i = 0; i < n; i++)
            {
                _a[i] = poly[i];
                _b[i] = poly[(i + 1) % n];
                _minY = Math.Min(_minY, poly[i].Y);
                _maxY = Math.Max(_maxY, poly[i].Y);
                minX = Math.Min(minX, poly[i].X);
                maxX = Math.Max(maxX, poly[i].X);
            }
            if (n > 0)
            {
                _spanLo = (int)Math.Clamp(Math.Floor(minX - _half - 1), 0, width);
                _spanHi = (int)Math.Clamp(Math.Ceiling(maxX + _half + 1), _spanLo, width);
            }
        }

        public double CoverageAt(Vec2d p)
        {
            var d2 = double.MaxValue;
            var winding = 0;
            for (var e = 0; e < _a.Length; e++)
            {
                d2 = Math.Min(d2, SegmentDistanceSquared(p, _a[e], _b[e]));
                winding += Crossing(_a[e], _b[e], p);
            }
            var inside = winding != 0;
            var d = _a.Length == 0 ? double.PositiveInfinity : Math.Sqrt(d2);
            var m = Ramp(inside ? d : -d);
            return _invert ? 1 - m : m;
        }

        private static int Crossing(Vec2d a, Vec2d b, Vec2d p)
        {
            if (a.Y <= p.Y)
                return b.Y > p.Y && Vec2d.Cross(b - a, p - a) > 0 ? 1 : 0;
            return b.Y <= p.Y && Vec2d.Cross(b - a, p - a) < 0 ? -1 : 0;
        }

        /// <summary>Soft coverage from the signed distance (positive inside), in pixels.</summary>
        private double Ramp(double signed)
        {
            var t = Math.Clamp((signed + _half) / (2 * _half), 0, 1);
            return _smooth ? t * t * (3 - 2 * t) : t;
        }

        /// <summary>Writes the row's mask on [lo, hi); every pixel outside is <see cref="Background"/>.</summary>
        public void RasterizeRow(int y, RowScratch s, out int lo, out int hi)
        {
            var mask = s.Mask;
            var cy = y + 0.5;
            if (_a.Length == 0 || cy + _half < _minY || cy - _half > _maxY)
            {
                lo = hi = 0;
                return;
            }
            lo = _spanLo;
            hi = _spanHi;
            // 1. Inside/outside along the scanline: crossings of the row's centre line, sorted, wound.
            var count = 0;
            for (var e = 0; e < _a.Length; e++)
            {
                var a = _a[e];
                var b = _b[e];
                int dir;
                if (a.Y <= cy && b.Y > cy)
                    dir = 1;
                else if (b.Y <= cy && a.Y > cy)
                    dir = -1;
                else
                    continue;
                s.CrossX[count] = a.X + (cy - a.Y) / (b.Y - a.Y) * (b.X - a.X);
                s.CrossDir[count] = dir;
                count++;
            }
            Array.Sort(s.CrossX, s.CrossDir, 0, count);
            // Pixel x is inside when the winding of the crossings at or left of its centre is non-zero:
            // between two crossings the state is constant, so the span fills piece by piece. Every crossing
            // lies within the outline's x-extent, so the winding is 0 outside [lo, hi).
            var winding = 0;
            var from = lo;
            for (var c = 0; c <= count; c++)
            {
                // Pixels whose centre lies before this crossing: x + ½ < crossing ⇔ x < ceil(crossing − ½).
                var to = c < count ? (int)Math.Clamp(Math.Ceiling(s.CrossX[c] - 0.5), from, hi) : hi;
                if (to > from)
                    mask.AsSpan(from, to - from).Fill(winding != 0 ? 1f : 0f);
                from = to;
                if (c < count)
                    winding += s.CrossDir[c];
            }
            // 2. The band: for each edge near the row, the pixels of the row within half a band of it.
            var dist = s.Distance;
            var touchedLo = hi;
            var touchedHi = -1;
            var r = _half;
            for (var e = 0; e < _a.Length; e++)
            {
                var a = _a[e];
                var b = _b[e];
                if (Math.Min(a.Y, b.Y) - r > cy || Math.Max(a.Y, b.Y) + r < cy)
                    continue;
                if (!CapsuleRowSpan(a, b, cy, r, out var x0, out var x1))
                    continue;
                var first = Math.Max(lo, (int)Math.Floor(x0 - 0.5));
                var last = Math.Min(hi - 1, (int)Math.Ceiling(x1 - 0.5));
                if (first > last)
                    continue;
                if (first < touchedLo || last > touchedHi)
                {
                    // Widen the touched span, resetting the newly covered pixels to "far".
                    var newLo = Math.Min(first, touchedLo);
                    var newHi = Math.Max(last, touchedHi);
                    if (touchedHi < touchedLo)
                    {
                        dist.AsSpan(newLo, newHi - newLo + 1).Fill(float.MaxValue);
                    }
                    else
                    {
                        if (newLo < touchedLo)
                            dist.AsSpan(newLo, touchedLo - newLo).Fill(float.MaxValue);
                        if (newHi > touchedHi)
                            dist.AsSpan(touchedHi + 1, newHi - touchedHi).Fill(float.MaxValue);
                    }
                    touchedLo = newLo;
                    touchedHi = newHi;
                }
                for (var x = first; x <= last; x++)
                {
                    var d2 = (float)SegmentDistanceSquared(new Vec2d(x + 0.5, cy), a, b);
                    if (d2 < dist[x])
                        dist[x] = d2;
                }
            }
            var r2 = r * r;
            for (var x = Math.Max(0, touchedLo); x <= touchedHi; x++)
            {
                if (dist[x] >= r2)
                    continue;
                var d = Math.Sqrt(dist[x]);
                mask[x] = (float)Ramp(mask[x] > 0.5f ? d : -d);
            }
            if (_invert)
                for (var x = lo; x < hi; x++)
                    mask[x] = 1f - mask[x];
        }

        /// <summary>The x-interval where the horizontal line y = cy passes within r of segment a–b (the
        /// capsule's chord): the union of the two end discs' chords and the slab's.</summary>
        private static bool CapsuleRowSpan(Vec2d a, Vec2d b, double cy, double r, out double x0, out double x1)
        {
            var lo = double.MaxValue;
            var hi = double.MinValue;
            DiscSpan(a, cy, r, ref lo, ref hi);
            DiscSpan(b, cy, r, ref lo, ref hi);
            var d = b - a;
            var len = d.Length;
            if (len > 1e-12 && Math.Abs(d.Y) > 1e-12)
            {
                // The slab |cross(d̂, p − a)| ≤ r, cut by the row: with p = (x, cy) the cross product is
                // ux(cy − a.Y) − uy(x − a.X), linear in x.
                var ux = d.X / len;
                var uy = d.Y / len;
                var c0 = ux * (cy - a.Y);
                var xa = a.X + (c0 - r) / uy;
                var xb = a.X + (c0 + r) / uy;
                var sx0 = Math.Min(xa, xb);
                var sx1 = Math.Max(xa, xb);
                // Between the end caps: 0 ≤ dot(p − a, d̂) = ux(x − a.X) + uy(cy − a.Y) ≤ len.
                if (Math.Abs(ux) > 1e-12)
                {
                    var p0 = a.X + (0 - (cy - a.Y) * uy) / ux;
                    var p1 = a.X + (len - (cy - a.Y) * uy) / ux;
                    sx0 = Math.Max(sx0, Math.Min(p0, p1));
                    sx1 = Math.Min(sx1, Math.Max(p0, p1));
                }
                else
                {
                    var along = (cy - a.Y) * uy;
                    if (along < 0 || along > len)
                        sx1 = double.MinValue;
                }
                if (sx0 <= sx1)
                {
                    lo = Math.Min(lo, sx0);
                    hi = Math.Max(hi, sx1);
                }
            }
            else if (len > 1e-12 && Math.Abs(cy - a.Y) <= r)
            {
                // A horizontal edge: the whole run between its ends is within r of the row.
                lo = Math.Min(lo, Math.Min(a.X, b.X));
                hi = Math.Max(hi, Math.Max(a.X, b.X));
            }
            x0 = lo;
            x1 = hi;
            return lo <= hi;
        }

        /// <summary>Widens [lo, hi] by the chord the row cuts from the disc of radius r about c.</summary>
        private static void DiscSpan(Vec2d c, double cy, double r, ref double lo, ref double hi)
        {
            var dy = cy - c.Y;
            var h2 = r * r - dy * dy;
            if (h2 < 0)
                return;
            var h = Math.Sqrt(h2);
            lo = Math.Min(lo, c.X - h);
            hi = Math.Max(hi, c.X + h);
        }

        private static double SegmentDistanceSquared(Vec2d p, Vec2d a, Vec2d b)
        {
            var ab = b - a;
            var l2 = ab.LengthSquared;
            var t = l2 > 1e-300 ? Math.Clamp(Vec2d.Dot(p - a, ab) / l2, 0, 1) : 0;
            return (p - (a + ab * t)).LengthSquared;
        }
    }
}
