using System.Collections.Immutable;
using PlayMap.Core.Model;
using PlayMap.Core.Numerics;

namespace PlayMap.Core.Mapping;

/// <summary>Where a point lies nearest on a path: the segment, the parameter on it, the point, the distance.</summary>
public readonly record struct PathLocation(int Segment, double T, Vec2d Point, double Distance);

/// <summary>The pen tool's maths on a <see cref="BezierPath"/>: flattening to a polyline within a tolerance,
/// inside tests (non-zero winding), signed distance, exact bounds, the node edits (add, delete, corner ↔
/// smooth, split) and offset outlines for a feather's inner and outer edges. Segment k runs from node k to
/// node k + 1 (the last to the first when closed) with control points P, P + Out, Q + In, Q. Pure.</summary>
public static class BezierTools
{
    /// <summary>The tolerance a mask is flattened with when none is given (in the path's units).</summary>
    public const double DefaultTolerance = 1e-3;

    private const int MaxDepth = 18;

    /// <summary>How many segments the path has: n − 1 open, n closed (0 for fewer than two nodes).</summary>
    public static int SegmentCount(BezierPath path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var n = path.Nodes.IsDefault ? 0 : path.Nodes.Length;
        return n < 2 ? 0 : path.Closed ? n : n - 1;
    }

    /// <summary>Segment k's four control points.</summary>
    public static (Vec2d P0, Vec2d P1, Vec2d P2, Vec2d P3) Segment(BezierPath path, int k)
    {
        ArgumentNullException.ThrowIfNull(path);
        var a = path.Nodes[k];
        var b = path.Nodes[(k + 1) % path.Nodes.Length];
        return (a.Point, a.Point + a.Out, b.Point + b.In, b.Point);
    }

    /// <summary>A cubic's point at t.</summary>
    public static Vec2d PointAt(Vec2d p0, Vec2d p1, Vec2d p2, Vec2d p3, double t)
    {
        var s = 1 - t;
        return s * s * s * p0 + 3 * s * s * t * p1 + 3 * s * t * t * p2 + t * t * t * p3;
    }

    /// <summary>The point on segment k at t.</summary>
    public static Vec2d PointAt(BezierPath path, int segment, double t)
    {
        var (p0, p1, p2, p3) = Segment(path, segment);
        return PointAt(p0, p1, p2, p3, t);
    }

    /// <summary>The path as a polyline whose distance from the true curve is at most
    /// <paramref name="tolerance"/> everywhere (adaptive de Casteljau subdivision: a piece is flat enough
    /// when both inner control points lie within the tolerance of its chord, and the curve lies inside the
    /// hull of its control points). A closed path's first point is not repeated at the end.</summary>
    public static Vec2d[] Flatten(BezierPath path, double tolerance = DefaultTolerance)
    {
        var list = new List<Vec2d>();
        FlattenInto(path, tolerance, list);
        return [.. list];
    }

    /// <summary>Appends the flattened path to <paramref name="output"/> (see <see cref="Flatten"/>).</summary>
    public static void FlattenInto(BezierPath path, double tolerance, List<Vec2d> output)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(output);
        var n = path.Nodes.IsDefault ? 0 : path.Nodes.Length;
        if (n == 0)
            return;
        tolerance = Math.Max(tolerance, 1e-9);
        output.Add(path.Nodes[0].Point);
        var segments = SegmentCount(path);
        for (var k = 0; k < segments; k++)
        {
            var (p0, p1, p2, p3) = Segment(path, k);
            if (path.Nodes[k].Out == Vec2d.Zero && path.Nodes[(k + 1) % n].In == Vec2d.Zero)
                output.Add(p3);
            else
                Subdivide(p0, p1, p2, p3, tolerance * tolerance, 0, output);
        }
        if (path.Closed && output.Count > 1 && output[^1] == output[0])
            output.RemoveAt(output.Count - 1);
    }

    private static void Subdivide(Vec2d p0, Vec2d p1, Vec2d p2, Vec2d p3, double tol2, int depth, List<Vec2d> output)
    {
        if (depth >= MaxDepth || (DistanceToLineSquared(p1, p0, p3) <= tol2 && DistanceToLineSquared(p2, p0, p3) <= tol2))
        {
            output.Add(p3);
            return;
        }
        var (a, b) = Split(p0, p1, p2, p3, 0.5);
        Subdivide(a.P0, a.P1, a.P2, a.P3, tol2, depth + 1, output);
        Subdivide(b.P0, b.P1, b.P2, b.P3, tol2, depth + 1, output);
    }

    /// <summary>Squared distance from p to the segment a–b (to a when a = b).</summary>
    private static double DistanceToLineSquared(Vec2d p, Vec2d a, Vec2d b)
    {
        var ab = b - a;
        var l2 = ab.LengthSquared;
        if (l2 < 1e-300)
            return (p - a).LengthSquared;
        var t = Math.Clamp(Vec2d.Dot(p - a, ab) / l2, 0, 1);
        return (p - (a + ab * t)).LengthSquared;
    }

    /// <summary>De Casteljau: the cubic cut at t into two cubics that trace it exactly.</summary>
    public static ((Vec2d P0, Vec2d P1, Vec2d P2, Vec2d P3) Left, (Vec2d P0, Vec2d P1, Vec2d P2, Vec2d P3) Right) Split(
        Vec2d p0, Vec2d p1, Vec2d p2, Vec2d p3, double t)
    {
        var a = Vec2d.Lerp(p0, p1, t);
        var b = Vec2d.Lerp(p1, p2, t);
        var c = Vec2d.Lerp(p2, p3, t);
        var d = Vec2d.Lerp(a, b, t);
        var e = Vec2d.Lerp(b, c, t);
        var f = Vec2d.Lerp(d, e, t);
        return ((p0, a, d, f), (f, e, c, p3));
    }

    /// <summary>Non-zero winding inside test of a closed polygon (the last point joins the first).</summary>
    public static bool Contains(ReadOnlySpan<Vec2d> polygon, Vec2d p) => Winding(polygon, p) != 0;

    /// <summary>The winding number of a closed polygon around a point.</summary>
    public static int Winding(ReadOnlySpan<Vec2d> polygon, Vec2d p)
    {
        var w = 0;
        var n = polygon.Length;
        for (var i = 0; i < n; i++)
        {
            var a = polygon[i];
            var b = polygon[(i + 1) % n];
            if (a.Y <= p.Y)
            {
                if (b.Y > p.Y && Vec2d.Cross(b - a, p - a) > 0)
                    w++;
            }
            else if (b.Y <= p.Y && Vec2d.Cross(b - a, p - a) < 0)
            {
                w--;
            }
        }
        return w;
    }

    /// <summary>True when the point is inside the path (non-zero winding; an open path is closed by its
    /// chord), the curve flattened within <paramref name="tolerance"/>.</summary>
    public static bool Contains(BezierPath path, Vec2d p, double tolerance = DefaultTolerance) =>
        Contains(Flatten(path, tolerance), p);

    /// <summary>Distance from the point to the path's outline: negative inside, positive outside (the
    /// curve flattened within <paramref name="tolerance"/>, so the answer is exact to that).</summary>
    public static double SignedDistance(BezierPath path, Vec2d p, double tolerance = DefaultTolerance)
    {
        var poly = Flatten(path, tolerance);
        if (poly.Length == 0)
            return double.PositiveInfinity;
        var d2 = double.MaxValue;
        for (var i = 0; i < poly.Length; i++)
            d2 = Math.Min(d2, DistanceToLineSquared(p, poly[i], poly[(i + 1) % poly.Length]));
        var d = Math.Sqrt(d2);
        return Contains(poly, p) ? -d : d;
    }

    /// <summary>The tight bounds of the path (the extremes of every segment, where its derivative vanishes,
    /// not the control points' box).</summary>
    public static RectD Bounds(BezierPath path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (path.Nodes.IsDefaultOrEmpty)
            return default;
        var min = path.Nodes[0].Point;
        var max = min;
        void Take(Vec2d q)
        {
            min = new Vec2d(Math.Min(min.X, q.X), Math.Min(min.Y, q.Y));
            max = new Vec2d(Math.Max(max.X, q.X), Math.Max(max.Y, q.Y));
        }
        foreach (var node in path.Nodes)
            Take(node.Point);
        Span<double> roots = stackalloc double[4];
        for (var k = 0; k < SegmentCount(path); k++)
        {
            var (p0, p1, p2, p3) = Segment(path, k);
            var count = DerivativeRoots(p0.X, p1.X, p2.X, p3.X, roots);
            count += DerivativeRoots(p0.Y, p1.Y, p2.Y, p3.Y, roots[count..]);
            for (var r = 0; r < count; r++)
                Take(PointAt(p0, p1, p2, p3, roots[r]));
        }
        return new RectD(min.X, min.Y, max.X - min.X, max.Y - min.Y);
    }

    /// <summary>The t in (0, 1) where one coordinate of a cubic has zero derivative (at most two).</summary>
    private static int DerivativeRoots(double a, double b, double c, double d, Span<double> roots)
    {
        // B'(t)/3 = (b − a)(1 − t)² + 2(c − b)(1 − t)t + (d − c)t² = A t² + B t + C.
        var qa = (b - a) - 2 * (c - b) + (d - c);
        var qb = 2 * ((c - b) - (b - a));
        var qc = b - a;
        var count = 0;
        if (Math.Abs(qa) < 1e-14)
        {
            if (Math.Abs(qb) > 1e-14)
                count = AddRoot(-qc / qb, roots, count);
            return count;
        }
        var disc = qb * qb - 4 * qa * qc;
        if (disc < 0)
            return 0;
        var sq = Math.Sqrt(disc);
        count = AddRoot((-qb + sq) / (2 * qa), roots, count);
        return AddRoot((-qb - sq) / (2 * qa), roots, count);
    }

    private static int AddRoot(double t, Span<double> roots, int count)
    {
        if (t > 0 && t < 1)
            roots[count++] = t;
        return count;
    }

    /// <summary>The point of the path nearest to <paramref name="p"/> (coarse samples per segment, then
    /// refined by Newton on the squared distance), or null for an empty path.</summary>
    public static PathLocation? Nearest(BezierPath path, Vec2d p)
    {
        ArgumentNullException.ThrowIfNull(path);
        PathLocation? best = null;
        for (var k = 0; k < SegmentCount(path); k++)
        {
            var (p0, p1, p2, p3) = Segment(path, k);
            var bt = 0.0;
            var bd = double.MaxValue;
            const int samples = 32;
            for (var s = 0; s <= samples; s++)
            {
                var t = s / (double)samples;
                var d = (PointAt(p0, p1, p2, p3, t) - p).LengthSquared;
                if (d < bd)
                {
                    bd = d;
                    bt = t;
                }
            }
            for (var it = 0; it < 8; it++)
            {
                var q = PointAt(p0, p1, p2, p3, bt) - p;
                var d1 = Derivative(p0, p1, p2, p3, bt);
                var d2 = SecondDerivative(p0, p1, p2, p3, bt);
                var num = Vec2d.Dot(q, d1);
                var den = Vec2d.Dot(d1, d1) + Vec2d.Dot(q, d2);
                if (Math.Abs(den) < 1e-18)
                    break;
                bt = Math.Clamp(bt - num / den, 0, 1);
            }
            var pt = PointAt(p0, p1, p2, p3, bt);
            var dist = (pt - p).Length;
            if (best is null || dist < best.Value.Distance)
                best = new PathLocation(k, bt, pt, dist);
        }
        if (best is null && !path.Nodes.IsDefaultOrEmpty)
            best = new PathLocation(0, 0, path.Nodes[0].Point, (path.Nodes[0].Point - p).Length);
        return best;
    }

    private static Vec2d Derivative(Vec2d p0, Vec2d p1, Vec2d p2, Vec2d p3, double t)
    {
        var s = 1 - t;
        return 3 * s * s * (p1 - p0) + 6 * s * t * (p2 - p1) + 3 * t * t * (p3 - p2);
    }

    private static Vec2d SecondDerivative(Vec2d p0, Vec2d p1, Vec2d p2, Vec2d p3, double t) =>
        6 * (1 - t) * (p2 - 2 * p1 + p0) + 6 * t * (p3 - 2 * p2 + p1);

    /// <summary>The path with a node inserted on segment k at t, the shape unchanged (the segment is cut by
    /// de Casteljau: the neighbours' handles shorten, the new node takes the cut's handles and is smooth
    /// unless the segment was straight).</summary>
    public static BezierPath SplitSegment(BezierPath path, int segment, double t)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (segment < 0 || segment >= SegmentCount(path))
            return path;
        t = Math.Clamp(t, 1e-6, 1 - 1e-6);
        var n = path.Nodes.Length;
        var ia = segment;
        var ib = (segment + 1) % n;
        var a = path.Nodes[ia];
        var b = path.Nodes[ib];
        var straight = a.Out == Vec2d.Zero && b.In == Vec2d.Zero;
        var (l, r) = Split(a.Point, a.Point + a.Out, b.Point + b.In, b.Point, t);
        var mid = straight
            ? new BezierNode(l.P3, Vec2d.Zero, Vec2d.Zero, false)
            : new BezierNode(l.P3, l.P2 - l.P3, r.P1 - r.P0, true);
        var builder = path.Nodes.ToBuilder();
        if (!straight)
        {
            builder[ia] = a with { Out = l.P1 - l.P0 };
            builder[ib] = b with { In = r.P2 - r.P3 };
        }
        builder.Insert(ia + 1, mid);
        return path with { Nodes = builder.ToImmutable() };
    }

    /// <summary>The path with a node added on the outline nearest <paramref name="p"/>, and that node's
    /// index; the path unchanged and −1 when it has no segment.</summary>
    public static (BezierPath Path, int Index) AddNode(BezierPath path, Vec2d p)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (SegmentCount(path) == 0 || Nearest(path, p) is not { } at)
            return (path, -1);
        return (SplitSegment(path, at.Segment, at.T), at.Segment + 1);
    }

    /// <summary>The path without node <paramref name="index"/>. When either joining segment is curved, the
    /// two segments become one cubic fitted to them (end points kept, the inner control points by least
    /// squares on chord-length samples), so the outline keeps its sweep; two straight segments become one
    /// straight segment.</summary>
    public static BezierPath DeleteNode(BezierPath path, int index)
    {
        ArgumentNullException.ThrowIfNull(path);
        var n = path.Nodes.IsDefault ? 0 : path.Nodes.Length;
        if (index < 0 || index >= n)
            return path;
        var hasPrev = path.Closed || index > 0;
        var hasNext = path.Closed || index < n - 1;
        if (n <= 2 || !hasPrev || !hasNext)
            return path with { Nodes = path.Nodes.RemoveAt(index) };
        var ip = (index - 1 + n) % n;
        var inx = (index + 1) % n;
        var prev = path.Nodes[ip];
        var node = path.Nodes[index];
        var next = path.Nodes[inx];
        var builder = path.Nodes.ToBuilder();
        var straight = prev.Out == Vec2d.Zero && node.In == Vec2d.Zero && node.Out == Vec2d.Zero && next.In == Vec2d.Zero;
        if (!straight)
        {
            // Samples along both segments with their chord-length parameter over the merged span.
            const int per = 16;
            var pts = new List<Vec2d>(2 * per + 1);
            for (var s = 0; s <= per; s++)
                pts.Add(PointAt(prev.Point, prev.Point + prev.Out, node.Point + node.In, node.Point, s / (double)per));
            for (var s = 1; s <= per; s++)
                pts.Add(PointAt(node.Point, node.Point + node.Out, next.Point + next.In, next.Point, s / (double)per));
            var (c1, c2) = FitInner(pts, prev.Point, next.Point);
            builder[ip] = prev with { Out = c1 - prev.Point };
            builder[inx] = next with { In = c2 - next.Point };
        }
        builder.RemoveAt(index);
        return path with { Nodes = builder.ToImmutable() };
    }

    /// <summary>The two inner control points of the cubic from p0 to p3 that best fit the samples (least
    /// squares, chord-length parameters).</summary>
    private static (Vec2d C1, Vec2d C2) FitInner(List<Vec2d> pts, Vec2d p0, Vec2d p3)
    {
        var u = new double[pts.Count];
        for (var i = 1; i < pts.Count; i++)
            u[i] = u[i - 1] + (pts[i] - pts[i - 1]).Length;
        var total = u[^1];
        double a11 = 0, a12 = 0, a22 = 0;
        Vec2d r1 = Vec2d.Zero, r2 = Vec2d.Zero;
        for (var i = 0; i < pts.Count; i++)
        {
            var t = total > 0 ? u[i] / total : i / (double)(pts.Count - 1);
            var s = 1 - t;
            var b0 = s * s * s;
            var b1 = 3 * s * s * t;
            var b2 = 3 * s * t * t;
            var b3 = t * t * t;
            var rest = pts[i] - b0 * p0 - b3 * p3;
            a11 += b1 * b1;
            a12 += b1 * b2;
            a22 += b2 * b2;
            r1 += b1 * rest;
            r2 += b2 * rest;
        }
        var det = a11 * a22 - a12 * a12;
        if (Math.Abs(det) < 1e-18)
            return (Vec2d.Lerp(p0, p3, 1 / 3.0), Vec2d.Lerp(p0, p3, 2 / 3.0));
        return ((a22 * r1 - a12 * r2) / det, (a11 * r2 - a12 * r1) / det);
    }

    /// <summary>The path with node <paramref name="index"/> made smooth or corner. Smooth → corner keeps
    /// the shape exactly (only the handles' coupling changes). Corner → smooth turns both handles onto
    /// their mean direction, each keeping its length (the shape is kept when they were already in line);
    /// a node with no handles gets handles along its neighbours' chord, a third of the way to each.</summary>
    public static BezierPath ConvertNode(BezierPath path, int index, bool smooth)
    {
        ArgumentNullException.ThrowIfNull(path);
        var n = path.Nodes.IsDefault ? 0 : path.Nodes.Length;
        if (index < 0 || index >= n)
            return path;
        var node = path.Nodes[index];
        if (!smooth)
            return path with { Nodes = path.Nodes.SetItem(index, node with { Smooth = false }) };
        var hasPrev = path.Closed || index > 0;
        var hasNext = path.Closed || index < n - 1;
        var prev = hasPrev ? path.Nodes[(index - 1 + n) % n].Point : node.Point;
        var next = hasNext ? path.Nodes[(index + 1) % n].Point : node.Point;
        var inLen = node.In.Length;
        var outLen = node.Out.Length;
        Vec2d dir;
        if (inLen > 0 || outLen > 0)
        {
            dir = (node.Out.Normalized() - node.In.Normalized()).Normalized();
            if (dir == Vec2d.Zero)
                dir = (next - prev).Normalized();
        }
        else
        {
            dir = (next - prev).Normalized();
            inLen = hasPrev ? (node.Point - prev).Length / 3 : 0;
            outLen = hasNext ? (next - node.Point).Length / 3 : 0;
        }
        var converted = new BezierNode(node.Point, -dir * inLen, dir * outLen, true);
        return path with { Nodes = path.Nodes.SetItem(index, converted) };
    }

    /// <summary>The path with one handle of node <paramref name="index"/> moved to <paramref name="handle"/>
    /// (relative to the anchor); on a smooth node the opposite handle turns to stay in line, keeping its
    /// length.</summary>
    public static BezierPath MoveHandle(BezierPath path, int index, bool outHandle, Vec2d handle)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (index < 0 || index >= path.Nodes.Length)
            return path;
        var node = path.Nodes[index];
        BezierNode moved;
        if (outHandle)
            moved = node.Smooth && handle != Vec2d.Zero ? node with { Out = handle, In = -handle.Normalized() * node.In.Length } : node with { Out = handle };
        else
            moved = node.Smooth && handle != Vec2d.Zero ? node with { In = handle, Out = -handle.Normalized() * node.Out.Length } : node with { In = handle };
        return path with { Nodes = path.Nodes.SetItem(index, moved) };
    }

    /// <summary>The closed outline <paramref name="distance"/> outside the path (inside when negative), as a
    /// polygon: the flattened outline with each vertex pushed along its corner's bisector by the distance
    /// over the half-angle's cosine (mitred, capped at twice the distance). It draws a feather's inner and
    /// outer edges; on a deep concave notch an inward offset may cross itself.</summary>
    public static Vec2d[] Offset(BezierPath path, double distance, double tolerance = DefaultTolerance)
    {
        var poly = Flatten(path, tolerance);
        var n = poly.Length;
        if (n < 3 || distance == 0)
            return poly;
        // The shoelace sign says which side is outside: for positive area the outward normal of a→b is (dy, −dx).
        var area = 0.0;
        for (var i = 0; i < n; i++)
            area += Vec2d.Cross(poly[i], poly[(i + 1) % n]);
        var sign = area >= 0 ? 1.0 : -1.0;
        var result = new Vec2d[n];
        for (var i = 0; i < n; i++)
        {
            var a = poly[(i - 1 + n) % n];
            var b = poly[i];
            var c = poly[(i + 1) % n];
            var n1 = OutwardNormal(a, b, sign);
            var n2 = OutwardNormal(b, c, sign);
            var bis = (n1 + n2).Normalized();
            if (bis == Vec2d.Zero)
                bis = n1;
            var cos = Math.Max(Vec2d.Dot(bis, n1), 0.5);
            result[i] = b + bis * (distance / cos);
        }
        return result;
    }

    private static Vec2d OutwardNormal(Vec2d a, Vec2d b, double sign)
    {
        // The edge direction turned a quarter: (dx, dy) → (dy, −dx).
        var d = (b - a).Normalized();
        var nx = d.Y * sign;
        var ny = -d.X * sign;
        return new Vec2d(nx, ny);
    }

    /// <summary>The path moved and scaled into another space: every anchor through p ↦ (p·scale + offset),
    /// handles scaled — exact, since a cubic's shape follows its control points under an affine map.</summary>
    public static BezierPath Transform(BezierPath path, Vec2d scale, Vec2d offset)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (path.Nodes.IsDefaultOrEmpty)
            return path;
        var nodes = ImmutableArray.CreateBuilder<BezierNode>(path.Nodes.Length);
        foreach (var node in path.Nodes)
        {
            nodes.Add(new BezierNode(
                new Vec2d(node.Point.X * scale.X + offset.X, node.Point.Y * scale.Y + offset.Y),
                new Vec2d(node.In.X * scale.X, node.In.Y * scale.Y),
                new Vec2d(node.Out.X * scale.X, node.Out.Y * scale.Y),
                node.Smooth));
        }
        return path with { Nodes = nodes.MoveToImmutable() };
    }
}
