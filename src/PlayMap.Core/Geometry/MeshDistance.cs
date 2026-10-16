using PlayMap.Core.Numerics;

namespace PlayMap.Core.Geometry;

/// <summary>The distance from a point to a triangle mesh's surface: a bounding-volume hierarchy over the triangles (ordered along a
/// Morton curve of their centroids, halved down to leaves of four), searched nearest-first with the boxes' distances as the bound.
/// Exact (the closest point on each candidate triangle), thread-safe after construction.</summary>
public sealed class MeshDistance
{
    private const int Leaf = 4;
    private readonly Vec3d[] _a, _b, _c;
    private readonly int[] _order;
    private readonly double[] _box; // per node: min xyz, max xyz
    private readonly int[] _node;   // per node: left child (or −1 for a leaf), right child, start, count

    public MeshDistance(MeshData mesh)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        var n = mesh.TriangleCount;
        _a = new Vec3d[n];
        _b = new Vec3d[n];
        _c = new Vec3d[n];
        var keys = new ulong[n];
        var (min, max) = mesh.Bounds();
        var ext = max - min;
        var span = Math.Max(Math.Max(ext.X, ext.Y), Math.Max(ext.Z, 1e-12));
        for (var t = 0; t < n; t++)
        {
            var (i, j, k) = mesh.Triangle(t);
            _a[t] = mesh.Position(i);
            _b[t] = mesh.Position(j);
            _c[t] = mesh.Position(k);
            var m = (_a[t] + _b[t] + _c[t]) / 3 - min;
            keys[t] = Morton(m.X / span, m.Y / span, m.Z / span);
        }
        _order = Enumerable.Range(0, n).ToArray();
        Array.Sort(keys, _order);
        var nodes = NodesFor(n);
        _box = new double[nodes * 6];
        _node = new int[nodes * 4];
        var count = 0;
        if (n > 0)
            Build(0, n, ref count);
    }

    /// <summary>The nodes a tree over n triangles has: one, or one and its two halves'.</summary>
    private static int NodesFor(int n) => n <= Leaf ? 1 : 1 + NodesFor(n / 2) + NodesFor(n - n / 2);

    private static ulong Spread(uint v)
    {
        ulong x = v & 0x1fffff;
        x = (x | (x << 32)) & 0x1f00000000ffffUL;
        x = (x | (x << 16)) & 0x1f0000ff0000ffUL;
        x = (x | (x << 8)) & 0x100f00f00f00f00fUL;
        x = (x | (x << 4)) & 0x10c30c30c30c30c3UL;
        x = (x | (x << 2)) & 0x1249249249249249UL;
        return x;
    }

    private static ulong Morton(double x, double y, double z)
    {
        static uint Q(double v) => (uint)Math.Clamp(v * 2097151, 0, 2097151);
        return Spread(Q(x)) | (Spread(Q(y)) << 1) | (Spread(Q(z)) << 2);
    }

    private int Build(int start, int count, ref int used)
    {
        var index = used++;
        _node[index * 4 + 2] = start;
        _node[index * 4 + 3] = count;
        var o = index * 6;
        if (count <= Leaf)
        {
            _node[index * 4] = -1;
            double x0 = double.MaxValue, y0 = double.MaxValue, z0 = double.MaxValue;
            double x1 = double.MinValue, y1 = double.MinValue, z1 = double.MinValue;
            for (var i = start; i < start + count; i++)
            {
                var t = _order[i];
                foreach (var v in (ReadOnlySpan<Vec3d>)[_a[t], _b[t], _c[t]])
                {
                    x0 = Math.Min(x0, v.X);
                    y0 = Math.Min(y0, v.Y);
                    z0 = Math.Min(z0, v.Z);
                    x1 = Math.Max(x1, v.X);
                    y1 = Math.Max(y1, v.Y);
                    z1 = Math.Max(z1, v.Z);
                }
            }
            (_box[o], _box[o + 1], _box[o + 2], _box[o + 3], _box[o + 4], _box[o + 5]) = (x0, y0, z0, x1, y1, z1);
            return index;
        }
        var half = count / 2;
        var l = Build(start, half, ref used) * 6;
        var r = Build(start + half, count - half, ref used) * 6;
        _node[index * 4] = l / 6;
        _node[index * 4 + 1] = r / 6;
        // A node's box is its children's together: built bottom-up, each triangle is read once.
        for (var k = 0; k < 3; k++)
        {
            _box[o + k] = Math.Min(_box[l + k], _box[r + k]);
            _box[o + 3 + k] = Math.Max(_box[l + 3 + k], _box[r + 3 + k]);
        }
        return index;
    }

    private double BoxDistance2(int node, Vec3d p)
    {
        var o = node * 6;
        var dx = Math.Max(Math.Max(_box[o] - p.X, 0), p.X - _box[o + 3]);
        var dy = Math.Max(Math.Max(_box[o + 1] - p.Y, 0), p.Y - _box[o + 4]);
        var dz = Math.Max(Math.Max(_box[o + 2] - p.Z, 0), p.Z - _box[o + 5]);
        return dx * dx + dy * dy + dz * dz;
    }

    /// <summary>The distance from <paramref name="p"/> to the nearest point of the mesh (infinity for an empty mesh).</summary>
    public double Distance(Vec3d p) => Closest(p).Distance;

    /// <summary>The nearest point of the mesh to <paramref name="p"/> and its distance (p itself and infinity for an empty mesh).</summary>
    public (Vec3d Point, double Distance) Closest(Vec3d p) => Closest(p, 0);

    /// <summary>The nearest point of the mesh to <paramref name="p"/>, or — once one is found within <paramref name="enough"/> — that
    /// one (a search for a maximum of minima needs no more: the point cannot raise it).</summary>
    public (Vec3d Point, double Distance) Closest(Vec3d p, double enough)
    {
        var stop = enough > 0 ? enough * enough : 0;
        if (_a.Length == 0)
            return (p, double.PositiveInfinity);
        var best = double.PositiveInfinity;
        var at = p;
        Span<int> stack = stackalloc int[128];
        var top = 0;
        stack[top++] = 0;
        while (top > 0)
        {
            var node = stack[--top];
            if (BoxDistance2(node, p) >= best)
                continue;
            var left = _node[node * 4];
            if (left < 0)
            {
                var start = _node[node * 4 + 2];
                var end = start + _node[node * 4 + 3];
                for (var i = start; i < end; i++)
                {
                    var t = _order[i];
                    var q = ClosestOnTriangle(p, _a[t], _b[t], _c[t]);
                    var d = (q - p).LengthSquared;
                    if (d < best)
                    {
                        best = d;
                        at = q;
                        if (best <= stop)
                            return (at, Math.Sqrt(best));
                    }
                }
                continue;
            }
            // The nearer child is searched first.
            var right = _node[node * 4 + 1];
            var dl = BoxDistance2(left, p);
            var dr = BoxDistance2(right, p);
            if (top + 2 > stack.Length)
                throw new InvalidOperationException("the distance tree is deeper than its stack");
            if (dl < dr)
            {
                stack[top++] = right;
                stack[top++] = left;
            }
            else
            {
                stack[top++] = left;
                stack[top++] = right;
            }
        }
        return (at, Math.Sqrt(best));
    }

    /// <summary>The point of triangle abc nearest to p (Ericson, Real-Time Collision Detection §5.1.5).</summary>
    public static Vec3d ClosestOnTriangle(Vec3d p, Vec3d a, Vec3d b, Vec3d c)
    {
        var ab = b - a;
        var ac = c - a;
        var ap = p - a;
        var d1 = Vec3d.Dot(ab, ap);
        var d2 = Vec3d.Dot(ac, ap);
        if (d1 <= 0 && d2 <= 0)
            return a;
        var bp = p - b;
        var d3 = Vec3d.Dot(ab, bp);
        var d4 = Vec3d.Dot(ac, bp);
        if (d3 >= 0 && d4 <= d3)
            return b;
        var vc = d1 * d4 - d3 * d2;
        if (vc <= 0 && d1 >= 0 && d3 <= 0)
            return a + ab * (d1 / (d1 - d3));
        var cp = p - c;
        var d5 = Vec3d.Dot(ab, cp);
        var d6 = Vec3d.Dot(ac, cp);
        if (d6 >= 0 && d5 <= d6)
            return c;
        var vb = d5 * d2 - d1 * d6;
        if (vb <= 0 && d2 >= 0 && d6 <= 0)
            return a + ac * (d2 / (d2 - d6));
        var va = d3 * d6 - d5 * d4;
        if (va <= 0 && d4 - d3 >= 0 && d5 - d6 >= 0)
            return b + (c - b) * ((d4 - d3) / (d4 - d3 + (d5 - d6)));
        var denom = va + vb + vc;
        if (Math.Abs(denom) < 1e-300)
            return a;
        var v = vb / denom;
        var w = vc / denom;
        return a + ab * v + ac * w;
    }
}
