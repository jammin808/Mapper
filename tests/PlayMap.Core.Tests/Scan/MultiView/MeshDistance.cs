using PlayMap.Core.Geometry;
using PlayMap.Core.Numerics;

namespace PlayMap.Core.Tests.Scan.MultiView;

/// <summary>The distance from a point to the nearest triangle of a mesh (brute force: the test scenes have a few
/// hundred triangles).</summary>
internal static class MeshDistance
{
    public static double To(MeshData mesh, Vec3d p)
    {
        var best = double.PositiveInfinity;
        for (var t = 0; t < mesh.Indices.Length; t += 3)
        {
            var d = (Closest(p, mesh.Position((int)mesh.Indices[t]), mesh.Position((int)mesh.Indices[t + 1]), mesh.Position((int)mesh.Indices[t + 2])) - p).LengthSquared;
            if (d < best)
                best = d;
        }
        return Math.Sqrt(best);
    }

    /// <summary>The point of triangle abc nearest p (Ericson, Real-Time Collision Detection, §5.1.5).</summary>
    private static Vec3d Closest(Vec3d p, Vec3d a, Vec3d b, Vec3d c)
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
        var denom = 1 / (va + vb + vc);
        return a + ab * (vb * denom) + ac * (vc * denom);
    }
}
