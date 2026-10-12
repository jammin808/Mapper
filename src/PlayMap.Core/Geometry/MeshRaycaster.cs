using PlayMap.Core.Numerics;

namespace PlayMap.Core.Geometry;

/// <summary>A ray: origin and unit direction.</summary>
public readonly record struct Ray(Vec3d Origin, Vec3d Direction)
{
    public Vec3d At(double t) => Origin + Direction * t;
}

/// <summary>Where a ray met a mesh: the distance, the triangle, the barycentric weights of its second and
/// third vertices, and the point.</summary>
public readonly record struct RayHit(double Distance, int Triangle, double U, double V, Vec3d Point);

/// <summary>Ray casting against a mesh through a bounding-volume hierarchy (median split on the longest
/// axis, 4 triangles a leaf). Used to pick a surface under the cursor, to lift a decoded camera pixel onto a
/// LiDAR mesh, to test visibility for the 3D blend and to simulate scans. Thread-safe after construction.</summary>
public sealed class MeshRaycaster
{
    private const int LeafSize = 4;
    private readonly MeshData _mesh;
    private readonly int[] _tris;
    private readonly List<Node> _nodes = [];

    private readonly record struct Node(Vec3d Min, Vec3d Max, int Left, int Right, int Start, int Count);

    public MeshRaycaster(MeshData mesh)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        _mesh = mesh;
        _tris = Enumerable.Range(0, mesh.TriangleCount).ToArray();
        if (_tris.Length > 0)
            Build(0, _tris.Length);
    }

    public MeshData Mesh => _mesh;

    private int Build(int start, int count)
    {
        var (min, max) = BoundsOf(start, count);
        var index = _nodes.Count;
        _nodes.Add(default);
        if (count <= LeafSize)
        {
            _nodes[index] = new Node(min, max, -1, -1, start, count);
            return index;
        }
        var ext = max - min;
        var axis = ext.X >= ext.Y && ext.X >= ext.Z ? 0 : ext.Y >= ext.Z ? 1 : 2;
        Array.Sort(_tris, start, count, Comparer<int>.Create((a, b) => Centroid(a)[axis].CompareTo(Centroid(b)[axis])));
        var half = count / 2;
        var left = Build(start, half);
        var right = Build(start + half, count - half);
        _nodes[index] = new Node(min, max, left, right, start, count);
        return index;
    }

    private Vec3d Centroid(int t)
    {
        var (a, b, c) = _mesh.Triangle(t);
        return (_mesh.Position(a) + _mesh.Position(b) + _mesh.Position(c)) / 3;
    }

    private (Vec3d Min, Vec3d Max) BoundsOf(int start, int count)
    {
        var min = new Vec3d(double.MaxValue, double.MaxValue, double.MaxValue);
        var max = new Vec3d(double.MinValue, double.MinValue, double.MinValue);
        for (var i = start; i < start + count; i++)
        {
            var (a, b, c) = _mesh.Triangle(_tris[i]);
            foreach (var v in (ReadOnlySpan<int>)[a, b, c])
            {
                var p = _mesh.Position(v);
                min = Vec3d.Min(min, p);
                max = Vec3d.Max(max, p);
            }
        }
        return (min, max);
    }

    /// <summary>The nearest hit within <paramref name="maxDistance"/>, optionally only on the triangles
    /// the filter accepts (a surface's face selection).</summary>
    public RayHit? Cast(Ray ray, double maxDistance = double.PositiveInfinity, Func<int, bool>? triangleFilter = null)
    {
        if (_nodes.Count == 0)
            return null;
        var inv = new Vec3d(1 / ray.Direction.X, 1 / ray.Direction.Y, 1 / ray.Direction.Z);
        RayHit? best = null;
        var bestT = maxDistance;
        Span<int> stack = stackalloc int[128];
        var sp = 0;
        stack[sp++] = 0;
        while (sp > 0)
        {
            var node = _nodes[stack[--sp]];
            if (!HitsBox(ray.Origin, inv, node.Min, node.Max, bestT))
                continue;
            if (node.Left < 0)
            {
                for (var i = node.Start; i < node.Start + node.Count; i++)
                {
                    var t = _tris[i];
                    if (triangleFilter is not null && !triangleFilter(t))
                        continue;
                    if (IntersectTriangle(ray, t, out var dist, out var u, out var v) && dist < bestT)
                    {
                        bestT = dist;
                        best = new RayHit(dist, t, u, v, ray.At(dist));
                    }
                }
            }
            else if (sp + 2 <= stack.Length)
            {
                stack[sp++] = node.Left;
                stack[sp++] = node.Right;
            }
        }
        return best;
    }

    /// <summary>True when anything lies on the ray before <paramref name="distance"/> (a shadow test).</summary>
    public bool Occluded(Ray ray, double distance) => Cast(ray, distance) is not null;

    private static bool HitsBox(Vec3d o, Vec3d inv, Vec3d min, Vec3d max, double tMax)
    {
        var tx1 = (min.X - o.X) * inv.X;
        var tx2 = (max.X - o.X) * inv.X;
        var tmin = Math.Min(tx1, tx2);
        var tmax = Math.Max(tx1, tx2);
        var ty1 = (min.Y - o.Y) * inv.Y;
        var ty2 = (max.Y - o.Y) * inv.Y;
        tmin = Math.Max(tmin, Math.Min(ty1, ty2));
        tmax = Math.Min(tmax, Math.Max(ty1, ty2));
        var tz1 = (min.Z - o.Z) * inv.Z;
        var tz2 = (max.Z - o.Z) * inv.Z;
        tmin = Math.Max(tmin, Math.Min(tz1, tz2));
        tmax = Math.Min(tmax, Math.Max(tz1, tz2));
        // NaN from 0 × ∞ on an axis-parallel ray falls through the comparisons as "no reject".
        return !(tmax < Math.Max(tmin, 0)) && !(tmin > tMax);
    }

    /// <summary>Möller–Trumbore, both faces.</summary>
    private bool IntersectTriangle(Ray ray, int t, out double dist, out double u, out double v)
    {
        var (ia, ib, ic) = _mesh.Triangle(t);
        var a = _mesh.Position(ia);
        var e1 = _mesh.Position(ib) - a;
        var e2 = _mesh.Position(ic) - a;
        var p = Vec3d.Cross(ray.Direction, e2);
        var det = Vec3d.Dot(e1, p);
        dist = u = v = 0;
        if (Math.Abs(det) < 1e-14)
            return false;
        var invDet = 1 / det;
        var s = ray.Origin - a;
        u = Vec3d.Dot(s, p) * invDet;
        if (u < 0 || u > 1)
            return false;
        var q = Vec3d.Cross(s, e1);
        v = Vec3d.Dot(ray.Direction, q) * invDet;
        if (v < 0 || u + v > 1)
            return false;
        dist = Vec3d.Dot(e2, q) * invDet;
        return dist > 1e-9;
    }
}
