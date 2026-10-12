using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using PlayMap.Core.Model;
using PlayMap.Core.Numerics;
using PlayMap.Core.Vision;
using PlayMap.Vision.Segmentation;

namespace PlayMap.Vision.Vectorise;

/// <summary>How a mask becomes pen-tool paths. Distances are in pixels of the mask.</summary>
public sealed record MaskToPathOptions(
    /// <summary>The largest distance between the fitted curve and the traced edge.</summary>
    double Tolerance = 0.75,
    /// <summary>A turn of at least this many degrees, concentrated in a few pixels, is a corner node.</summary>
    double CornerDegrees = 35,
    /// <summary>Pieces and holes smaller than this many pixels are dropped.</summary>
    int MinArea = 12)
{
    public static readonly MaskToPathOptions Default = new();
}

/// <summary>A mask as paths: outlines (clockwise on screen) and holes (anticlockwise), largest first, in
/// normalised units of the mask's grid (x ÷ width, y ÷ height, y down) — what an output's or a surface's
/// <see cref="Mask"/> holds. A hole becomes a subtracting mask on the desk.</summary>
public sealed record VectorisedMask(ImmutableArray<BezierPath> Outlines, ImmutableArray<BezierPath> Holes);

/// <summary>Vectorises a mask so the desk's pen tool can edit it: the edge is traced with marching squares on
/// the coverage (sub-pixel, through soft edges), corners are found where the edge turns sharply within a few
/// pixels (and placed where the straight edges either side meet), runs that Douglas–Peucker reduces to their
/// ends become straight segments, and the rest is fitted with cubic Béziers (least squares with the end
/// tangents fixed, Newton reparameterisation, split at the worst point with a smooth node) to within the
/// tolerance.</summary>
public static class MaskToPath
{
    public static VectorisedMask Vectorise(SegmentationMask mask, MaskToPathOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(mask);
        options ??= MaskToPathOptions.Default;
        int w = mask.Width, h = mask.Height;
        var inside = MaskOps.FromCoverage(mask.Coverage);
        var labels = MaskOps.Components(inside, w, h, true, out var count);
        var area = new int[count + 1];
        foreach (var l in labels)
            area[l]++;
        var outlines = new List<(double Area, BezierPath Path)>();
        var holes = new List<(double Area, BezierPath Path)>();
        for (var c = 1; c <= count; c++)
        {
            if (area[c] < options.MinArea)
                continue;
            foreach (var loop in Trace(mask.Coverage, labels, c, w, h))
            {
                var signed = SignedArea(loop);
                if (Math.Abs(signed) < options.MinArea)
                    continue;
                var path = Fit(loop, w, h, options);
                if (signed > 0)
                    outlines.Add((signed, path));
                else
                    holes.Add((-signed, path));
            }
        }
        return new VectorisedMask(
            [.. outlines.OrderByDescending(o => o.Area).Select(o => o.Path)],
            [.. holes.OrderByDescending(o => o.Area).Select(o => o.Path)]);
    }

    // ---- tracing ----

    /// <summary>Marching squares on the coverage of one component (other pixels read as 0), sampled at pixel
    /// centres with a ring of zeros around the grid so every loop closes. Saddles keep diagonal pixels apart
    /// (4-connected, as the components are). Loops are oriented with the inside on the right as they run (in y-down
    /// coordinates), so an outline's shoelace area is positive and a hole's negative.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal static List<List<Vec2d>> Trace(byte[] coverage, int[] labels, int component, int w, int h)
    {
        int fw = w + 2, fh = h + 2;
        var f = new float[fw * fh];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var i = y * w + x;
                // Pixels of other pieces read as empty; this piece and the soft edge around it keep their values.
                f[(y + 1) * fw + x + 1] = labels[i] == component || labels[i] == 0 ? coverage[i] : 0;
            }
        // A [1 2 1] blur each way: a hard staircase becomes a sloped edge whose half-level line runs between the
        // stairs (a straight edge on the pixel grid stays exactly where it was), so the trace follows the shape,
        // not the pixels.
        Blur121(f, fw, fh);
        const float iso = 127.5f;
        var next = new Dictionary<long, long>();
        var point = new Dictionary<long, Vec2d>();
        for (var j = 0; j < fh - 1; j++)
        {
            for (var i = 0; i < fw - 1; i++)
            {
                float v0 = f[j * fw + i], v1 = f[j * fw + i + 1], v2 = f[(j + 1) * fw + i + 1], v3 = f[(j + 1) * fw + i];
                var code = (v0 > iso ? 1 : 0) | (v1 > iso ? 2 : 0) | (v2 > iso ? 4 : 0) | (v3 > iso ? 8 : 0);
                if (code is 0 or 15)
                    continue;
                foreach (var (e1, e2) in Segments(code))
                    AddSegment(i, j, e1, e2, code, v0, v1, v2, v3);
            }
        }

        var loops = new List<List<Vec2d>>();
        var used = new HashSet<long>();
        foreach (var start in next.Keys)
        {
            if (used.Contains(start))
                continue;
            var loop = new List<Vec2d>();
            var k = start;
            while (used.Add(k))
            {
                loop.Add(point[k]);
                if (!next.TryGetValue(k, out k))
                    break;
            }
            if (loop.Count >= 3)
                loops.Add(loop);
        }
        return loops;

        void AddSegment(int i, int j, int e1, int e2, int code, float v0, float v1, float v2, float v3)
        {
            var (k1, p1) = EdgePoint(i, j, e1, v0, v1, v2, v3);
            var (k2, p2) = EdgePoint(i, j, e2, v0, v1, v2, v3);
            // Orient so an inside corner touching the segment's edges lies to the right of the direction of travel.
            var c = InsideCorner(code, e1, e2);
            var cp = new Vec2d(i + (c is 1 or 2 ? 1 : 0) - 0.5, j + (c is 2 or 3 ? 1 : 0) - 0.5);
            if (Vec2d.Cross(p2 - p1, cp - p1) < 0)
            {
                (k1, k2) = (k2, k1);
                (p1, p2) = (p2, p1);
            }
            next[k1] = k2;
            point[k1] = p1;
            point[k2] = p2;
        }

        (long Key, Vec2d P) EdgePoint(int i, int j, int edge, float v0, float v1, float v2, float v3)
        {
            // Field index (i, j) is the centre of picture pixel (i − 1, j − 1), at (i − 0.5, j − 0.5).
            double x0 = i - 0.5, y0 = j - 0.5;
            switch (edge)
            {
                case 0: // top: corner 0 → corner 1
                    return (((long)j * fw + i) * 2, new Vec2d(x0 + Lerp(v0, v1), y0));
                case 1: // right: corner 1 → corner 2
                    return (((long)j * fw + i + 1) * 2 + 1, new Vec2d(x0 + 1, y0 + Lerp(v1, v2)));
                case 2: // bottom: corner 3 → corner 2
                    return (((long)(j + 1) * fw + i) * 2, new Vec2d(x0 + Lerp(v3, v2), y0 + 1));
                default: // left: corner 0 → corner 3
                    return (((long)j * fw + i) * 2 + 1, new Vec2d(x0, y0 + Lerp(v0, v3)));
            }
        }

        static double Lerp(float a, float b) => Math.Abs(b - a) < 1e-6 ? 0.5 : Math.Clamp((iso - a) / (b - a), 0.0, 1.0);
    }

    private static void Blur121(float[] f, int w, int h)
    {
        var t = new float[f.Length];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var i = y * w + x;
                var l = x > 0 ? f[i - 1] : f[i];
                var r = x + 1 < w ? f[i + 1] : f[i];
                t[i] = (l + 2 * f[i] + r) * 0.25f;
            }
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var i = y * w + x;
                var u = y > 0 ? t[i - w] : t[i];
                var d = y + 1 < h ? t[i + w] : t[i];
                f[i] = (u + 2 * t[i] + d) * 0.25f;
            }
    }

    // Edges: 0 top, 1 right, 2 bottom, 3 left. Corners: 0 top-left, 1 top-right, 2 bottom-right, 3 bottom-left.
    private static readonly (int, int)[][] SegmentTable =
    [
        [], [(3, 0)], [(0, 1)], [(3, 1)], [(1, 2)], [(3, 0), (1, 2)], [(0, 2)], [(3, 2)],
        [(2, 3)], [(0, 2)], [(0, 1), (2, 3)], [(1, 2)], [(3, 1)], [(0, 1)], [(3, 0)], [],
    ];

    private static (int, int)[] Segments(int code) => SegmentTable[code];

    private static readonly int[][] EdgeCorners = [[0, 1], [1, 2], [3, 2], [0, 3]];

    private static int InsideCorner(int code, int e1, int e2)
    {
        foreach (var c in EdgeCorners[e1])
            if ((code & (1 << c)) != 0)
                return c;
        foreach (var c in EdgeCorners[e2])
            if ((code & (1 << c)) != 0)
                return c;
        return 0;
    }

    internal static double SignedArea(IReadOnlyList<Vec2d> loop)
    {
        double a = 0;
        for (var i = 0; i < loop.Count; i++)
        {
            var p = loop[i];
            var q = loop[(i + 1) % loop.Count];
            a += p.X * q.Y - q.X * p.Y;
        }
        return a / 2;
    }

    // ---- simplification and corners ----

    /// <summary>Douglas–Peucker on an open polyline: the indices kept, ends included.</summary>
    internal static List<int> DouglasPeucker(IReadOnlyList<Vec2d> pts, int first, int last, double tolerance)
    {
        var keep = new List<int> { first };
        var stack = new Stack<(int, int)>();
        stack.Push((first, last));
        var marks = new SortedSet<int> { first, last };
        while (stack.Count > 0)
        {
            var (a, b) = stack.Pop();
            var worst = -1;
            var worstD = tolerance;
            for (var i = a + 1; i < b; i++)
            {
                var d = DistanceToSegment(pts[i], pts[a], pts[b]);
                if (d > worstD)
                {
                    worstD = d;
                    worst = i;
                }
            }
            if (worst < 0)
                continue;
            marks.Add(worst);
            stack.Push((a, worst));
            stack.Push((worst, b));
        }
        keep.Clear();
        keep.AddRange(marks);
        return keep;
    }

    private static double DistanceToSegment(Vec2d p, Vec2d a, Vec2d b)
    {
        var ab = b - a;
        var l2 = ab.LengthSquared;
        if (l2 < 1e-18)
            return (p - a).Length;
        var t = Math.Clamp(Vec2d.Dot(p - a, ab) / l2, 0, 1);
        return (p - (a + ab * t)).Length;
    }

    /// <summary>The point an arc length <paramref name="s"/> away along the closed loop from vertex i (s may be
    /// negative), using cumulative lengths.</summary>
    private static Vec2d Along(IReadOnlyList<Vec2d> loop, double[] cum, double total, int i, double s)
    {
        var target = cum[i] + s;
        target = ((target % total) + total) % total;
        var lo = 0;
        var hi = loop.Count;
        while (hi - lo > 1)
        {
            var mid = (lo + hi) / 2;
            if (cum[mid] <= target)
                lo = mid;
            else
                hi = mid;
        }
        var a = loop[lo];
        var b = loop[(lo + 1) % loop.Count];
        var seg = cum[lo + 1] - cum[lo];
        return seg < 1e-12 ? a : Vec2d.Lerp(a, b, (target - cum[lo]) / seg);
    }

    private static double Turn(Vec2d before, Vec2d at, Vec2d after)
    {
        var u = at - before;
        var v = after - at;
        var lu = u.Length;
        var lv = v.Length;
        if (lu < 1e-9 || lv < 1e-9)
            return 0;
        return Math.Acos(Math.Clamp(Vec2d.Dot(u, v) / (lu * lv), -1, 1)) * 180 / Math.PI;
    }

    /// <summary>Corner vertices: where the edge turns by at least the corner angle over a few pixels and most of
    /// that turn happens within the nearest pixel or two (a circle's turn grows with the span; a corner's does
    /// not). One vertex per corner (the strongest within the span).</summary>
    internal static List<int> Corners(IReadOnlyList<Vec2d> loop, double cornerDegrees)
    {
        var n = loop.Count;
        var cum = new double[n + 1];
        for (var i = 0; i < n; i++)
            cum[i + 1] = cum[i] + (loop[(i + 1) % n] - loop[i]).Length;
        var total = cum[n];

        // Measured over ±2.5 and ±6 pixels: a 90° corner (rounded by the blur over a pixel or two) turns most of
        // its angle within the near span; a circle of radius r turns 2.5/r there and 6/r over the far span.
        const double near = 2.5;
        const double far = 6.0;
        var strength = new double[n];
        for (var i = 0; i < n; i++)
        {
            var tFar = Turn(Along(loop, cum, total, i, -far), loop[i], Along(loop, cum, total, i, far));
            var tNear = Turn(Along(loop, cum, total, i, -near), loop[i], Along(loop, cum, total, i, near));
            if (tFar >= cornerDegrees && tNear >= 0.5 * tFar)
                strength[i] = tFar + tNear;
        }
        var corners = new List<int>();
        for (var i = 0; i < n; i++)
        {
            if (strength[i] <= 0)
                continue;
            // The strongest within the far span on either side (ties to the earlier vertex) is the corner.
            var best = true;
            foreach (var dir in (ReadOnlySpan<int>)[1, -1])
            {
                var travelled = 0.0;
                for (var j = 1; j < n && best; j++)
                {
                    var k = ((i + dir * j) % n + n) % n;
                    var prevK = ((i + dir * (j - 1)) % n + n) % n;
                    travelled += (loop[k] - loop[prevK]).Length;
                    if (travelled > far)
                        break;
                    if (strength[k] > strength[i] || (strength[k] == strength[i] && k < i))
                        best = false;
                }
            }
            if (best)
                corners.Add(i);
        }
        return corners;
    }

    /// <summary>The meeting point of the straight edges either side of a corner (each fitted by least squares over
    /// the 2–8 pixels of the edge beside it); the vertex itself when the edges are nearly parallel or meet far off.</summary>
    private static Vec2d RefineCorner(IReadOnlyList<Vec2d> loop, double[] cum, double total, int i)
    {
        var before = new List<Vec2d>();
        var after = new List<Vec2d>();
        for (var s = 2.0; s <= 8.0; s += 0.5)
        {
            before.Add(Along(loop, cum, total, i, -s));
            after.Add(Along(loop, cum, total, i, s));
        }
        var (c1, d1) = FitLine(before);
        var (c2, d2) = FitLine(after);
        var denom = Vec2d.Cross(d1, d2);
        if (Math.Abs(denom) < 0.2)
            return loop[i];
        var t = Vec2d.Cross(c2 - c1, d2) / denom;
        var p = c1 + d1 * t;
        return (p - loop[i]).Length <= 2.5 ? p : loop[i];
    }

    private static (Vec2d Centre, Vec2d Direction) FitLine(List<Vec2d> pts)
    {
        var c = Vec2d.Zero;
        foreach (var p in pts)
            c += p;
        c /= pts.Count;
        double sxx = 0, sxy = 0, syy = 0;
        foreach (var p in pts)
        {
            var d = p - c;
            sxx += d.X * d.X;
            sxy += d.X * d.Y;
            syy += d.Y * d.Y;
        }
        var angle = 0.5 * Math.Atan2(2 * sxy, sxx - syy);
        return (c, new Vec2d(Math.Cos(angle), Math.Sin(angle)));
    }

    // ---- fitting ----

    private static BezierPath Fit(List<Vec2d> loop, int w, int h, MaskToPathOptions o)
    {
        var n = loop.Count;
        var cum = new double[n + 1];
        for (var i = 0; i < n; i++)
            cum[i + 1] = cum[i] + (loop[(i + 1) % n] - loop[i]).Length;
        var total = cum[n];
        var corners = Corners(loop, o.CornerDegrees);
        var segments = new List<(Vec2d P0, Vec2d C1, Vec2d C2, Vec2d P3, bool StartsAtCorner)>();
        if (corners.Count == 0)
        {
            // A smooth loop: start anywhere and close with the same tangent.
            var run = new List<Vec2d>(loop) { loop[0] };
            var t0 = (Along(loop, cum, total, 0, 2) - Along(loop, cum, total, 0, -2)).Normalized();
            FitRun(run, t0, -t0, o.Tolerance, segments, startsAtCorner: false);
        }
        else
        {
            var refined = corners.Select(c => RefineCorner(loop, cum, total, c)).ToList();
            for (var k = 0; k < corners.Count; k++)
            {
                var a = corners[k];
                var b = corners[(k + 1) % corners.Count];
                var run = new List<Vec2d> { refined[k] };
                for (var i = (a + 1) % n; i != b; i = (i + 1) % n)
                    run.Add(loop[i]);
                run.Add(refined[(k + 1) % corners.Count]);
                if (IsStraight(run, o.Tolerance))
                {
                    // Straight between its corners: a line, handles at the anchors.
                    segments.Add((run[0], run[0], run[^1], run[^1], true));
                    continue;
                }
                var tStart = Tangent(run, 0, +1);
                var tEnd = Tangent(run, run.Count - 1, -1);
                FitRun(run, tStart, tEnd, o.Tolerance, segments, startsAtCorner: true);
            }
        }

        // Nodes: each segment's start; In from the previous segment's second handle.
        var nodes = ImmutableArray.CreateBuilder<BezierNode>(segments.Count);
        for (var s = 0; s < segments.Count; s++)
        {
            var seg = segments[s];
            var prev = segments[(s - 1 + segments.Count) % segments.Count];
            var p = seg.P0;
            nodes.Add(new BezierNode(
                new Vec2d(p.X / w, p.Y / h),
                new Vec2d((prev.C2.X - p.X) / w, (prev.C2.Y - p.Y) / h),
                new Vec2d((seg.C1.X - p.X) / w, (seg.C1.Y - p.Y) / h),
                !seg.StartsAtCorner));
        }
        return new BezierPath(nodes.ToImmutable(), true);
    }

    /// <summary>A run between corners is a straight side when Douglas–Peucker at twice the tolerance (at least a
    /// pixel and a half: a segmented edge wavers by a pixel) keeps only its ends and its points sit on the chord
    /// on average within the tolerance — a curve bows away systematically, a ragged straight edge does not.</summary>
    internal static bool IsStraight(List<Vec2d> run, double tolerance)
    {
        if (DouglasPeucker(run, 0, run.Count - 1, Math.Max(2 * tolerance, 1.5)).Count != 2)
            return false;
        double sum = 0;
        for (var i = 1; i < run.Count - 1; i++)
            sum += DistanceToSegment(run[i], run[0], run[^1]);
        return run.Count <= 2 || sum / (run.Count - 2) <= tolerance;
    }

    /// <summary>The unit tangent at an end of a run, from the point about three pixels in.</summary>
    private static Vec2d Tangent(List<Vec2d> run, int at, int dir)
    {
        var p = run[at];
        var i = at;
        var travelled = 0.0;
        while (i + dir >= 0 && i + dir < run.Count && travelled < 3)
        {
            travelled += (run[i + dir] - run[i]).Length;
            i += dir;
        }
        var t = (run[i] - p).Normalized();
        return t.LengthSquared > 0 ? t : (run[at + dir] - p).Normalized();
    }

    /// <summary>Schneider's fit of one run (an open polyline) with fixed end tangents: chord-length parameters, a
    /// least-squares cubic, Newton reparameterisation while close, and a split at the worst point with a smooth
    /// node (centred tangent) when the error stays above the tolerance.</summary>
    private static void FitRun(List<Vec2d> d, Vec2d tHat1, Vec2d tHat2, double tolerance,
        List<(Vec2d, Vec2d, Vec2d, Vec2d, bool)> output, bool startsAtCorner)
    {
        var stack = new Stack<(int First, int Last, Vec2d T1, Vec2d T2, bool Corner)>();
        // Pushed in reverse so the segments come out in order.
        var pending = new List<(int, int, Vec2d, Vec2d, bool)>();
        stack.Push((0, d.Count - 1, tHat1, tHat2, startsAtCorner));
        while (stack.Count > 0)
        {
            var (first, last, t1, t2, corner) = stack.Pop();
            if (last - first <= 1)
            {
                var dist = (d[last] - d[first]).Length / 3;
                pending.Add((first, last, d[first] + t1 * dist, d[last] + t2 * dist, corner));
                continue;
            }
            var u = ChordParameters(d, first, last);
            var bez = Generate(d, first, last, u, t1, t2);
            var (err, split) = MaxError(d, first, last, bez, u);
            if (err > tolerance && err < tolerance * 4)
            {
                for (var it = 0; it < 4 && err > tolerance; it++)
                {
                    u = Reparameterise(d, first, last, u, bez);
                    bez = Generate(d, first, last, u, t1, t2);
                    (err, split) = MaxError(d, first, last, bez, u);
                }
            }
            if (err <= tolerance || last - first < 4)
            {
                pending.Add((first, last, bez[1], bez[2], corner));
                continue;
            }
            split = Math.Clamp(split, first + 2, last - 2);
            var centre = (d[split - 1] - d[split + 1]).Normalized();
            // Right half first on the stack so the left half pops (and lands) first.
            stack.Push((split, last, -centre, t2, false));
            stack.Push((first, split, t1, centre, corner));
        }
        // The stack visits left halves before right halves, so pending is already in path order.
        foreach (var (first, last, c1, c2, corner) in pending)
            output.Add((d[first], c1, c2, d[last], corner));
    }

    private static double[] ChordParameters(List<Vec2d> d, int first, int last)
    {
        var u = new double[last - first + 1];
        for (var i = first + 1; i <= last; i++)
            u[i - first] = u[i - first - 1] + (d[i] - d[i - 1]).Length;
        var total = u[^1];
        for (var i = 1; i < u.Length; i++)
            u[i] = total > 0 ? u[i] / total : i / (double)(u.Length - 1);
        return u;
    }

    private static Vec2d[] Generate(List<Vec2d> d, int first, int last, double[] u, Vec2d t1, Vec2d t2)
    {
        double c00 = 0, c01 = 0, c11 = 0, x0 = 0, x1 = 0;
        var p0 = d[first];
        var p3 = d[last];
        for (var i = 0; i < u.Length; i++)
        {
            var t = u[i];
            var mt = 1 - t;
            var b0 = mt * mt * mt;
            var b1 = 3 * t * mt * mt;
            var b2 = 3 * t * t * mt;
            var b3 = t * t * t;
            var a1 = t1 * b1;
            var a2 = t2 * b2;
            c00 += Vec2d.Dot(a1, a1);
            c01 += Vec2d.Dot(a1, a2);
            c11 += Vec2d.Dot(a2, a2);
            var tmp = d[first + i] - (p0 * (b0 + b1) + p3 * (b2 + b3));
            x0 += Vec2d.Dot(a1, tmp);
            x1 += Vec2d.Dot(a2, tmp);
        }
        var det = c00 * c11 - c01 * c01;
        var segLen = (p3 - p0).Length;
        double alpha1 = 0, alpha2 = 0;
        if (Math.Abs(det) > 1e-12)
        {
            alpha1 = (x0 * c11 - c01 * x1) / det;
            alpha2 = (c00 * x1 - x0 * c01) / det;
        }
        var eps = 1e-6 * segLen;
        if (alpha1 < eps || alpha2 < eps)
        {
            // The least-squares handles collapse or flip: fall back to a third of the chord (Wu–Barsky).
            alpha1 = alpha2 = segLen / 3;
        }
        return [p0, p0 + t1 * alpha1, p3 + t2 * alpha2, p3];
    }

    private static Vec2d Eval(Vec2d[] b, double t)
    {
        var mt = 1 - t;
        return b[0] * (mt * mt * mt) + b[1] * (3 * t * mt * mt) + b[2] * (3 * t * t * mt) + b[3] * (t * t * t);
    }

    private static (double Error, int Split) MaxError(List<Vec2d> d, int first, int last, Vec2d[] bez, double[] u)
    {
        double worst = 0;
        var split = (first + last) / 2;
        for (var i = first + 1; i < last; i++)
        {
            var e = (Eval(bez, u[i - first]) - d[i]).Length;
            if (e > worst)
            {
                worst = e;
                split = i;
            }
        }
        return (worst, split);
    }

    private static double[] Reparameterise(List<Vec2d> d, int first, int last, double[] u, Vec2d[] b)
    {
        var o = new double[u.Length];
        for (var i = 0; i < u.Length; i++)
        {
            var t = u[i];
            var mt = 1 - t;
            var q = Eval(b, t);
            var q1 = (b[1] - b[0]) * (3 * mt * mt) + (b[2] - b[1]) * (6 * mt * t) + (b[3] - b[2]) * (3 * t * t);
            var q2 = (b[2] - b[1] * 2 + b[0]) * (6 * mt) + (b[3] - b[2] * 2 + b[1]) * (6 * t);
            var diff = q - d[first + i];
            var num = Vec2d.Dot(diff, q1);
            var den = Vec2d.Dot(q1, q1) + Vec2d.Dot(diff, q2);
            o[i] = Math.Abs(den) < 1e-12 ? t : Math.Clamp(t - num / den, 0, 1);
        }
        return o;
    }

    /// <summary>Points along a path's curves (for tests and previews), in the path's own units.</summary>
    public static List<Vec2d> Sample(BezierPath path, int perSegment)
    {
        ArgumentNullException.ThrowIfNull(path);
        var pts = new List<Vec2d>();
        var nodes = path.Nodes;
        var count = path.Closed ? nodes.Length : nodes.Length - 1;
        for (var s = 0; s < count; s++)
        {
            var a = nodes[s];
            var b = nodes[(s + 1) % nodes.Length];
            Vec2d[] bez = [a.Point, a.Point + a.Out, b.Point + b.In, b.Point];
            for (var k = 0; k < perSegment; k++)
                pts.Add(Eval(bez, k / (double)perSegment));
        }
        return pts;
    }
}
