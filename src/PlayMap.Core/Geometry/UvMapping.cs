using PlayMap.Core.Numerics;

namespace PlayMap.Core.Geometry;

/// <summary>How projected UVs fill the square.</summary>
public enum UvFit
{
    /// <summary>One scale on both axes: the longer side spans 0..1, the shorter is centred — a square of
    /// content lands as a square on the surface.</summary>
    Contain,
    /// <summary>Each axis spans 0..1: the content fills the region, stretched to its aspect.</summary>
    Stretch,
}

/// <summary>A face set with projected UVs: the re-indexed mesh, and the region's extent along u and v in
/// metres (before fitting), so the content's aspect can be matched to the surface's.</summary>
public sealed record UvProjection(MeshData Mesh, double Width, double Height)
{
    public double Aspect => Height > 0 ? Width / Height : 0;
}

/// <summary>UVs for a face set that has none — what a scanned region gets when it becomes a surface. Each
/// projection keeps the region's aspect (unless asked to stretch) and reads the picture upright: v runs
/// down the world (−Y) on anything standing, and toward the front (+Z) on a floor or ceiling. UVs have
/// (0, 0) at the content's top-left with v growing downward, as the primitives'.</summary>
public static class UvMapping
{
    /// <summary>A plane frame: an origin, the in-plane right and down axes, and the normal (down × right ·
    /// the normal = right-handed, the normal toward the side the faces face).</summary>
    public readonly record struct Frame(Vec3d Origin, Vec3d Right, Vec3d Down, Vec3d Normal);

    /// <summary>Planar UVs: the best-fit plane (area-weighted principal axes), the in-plane rectangle of
    /// least area around the region (rotating calipers on its hull, so a square region aligns with its
    /// sides, not its diagonal), turned upright.</summary>
    public static UvProjection Planar(MeshData mesh, IReadOnlyCollection<int>? faces = null, UvFit fit = UvFit.Contain)
    {
        var sub = SubsetOf(mesh, faces);
        var frame = PlaneFrame(sub);
        var n = sub.VertexCount;
        var u = new double[n];
        var v = new double[n];
        for (var i = 0; i < n; i++)
        {
            var d = sub.Position(i) - frame.Origin;
            u[i] = Vec3d.Dot(d, frame.Right);
            v[i] = Vec3d.Dot(d, frame.Down);
        }
        var (uvs, w, h) = Fit(u, v, fit);
        return new UvProjection(new MeshData(sub.Positions, sub.Normals, uvs, sub.Indices), w, h);
    }

    /// <summary>The upright plane frame of a face set (see <see cref="Planar"/>).</summary>
    public static Frame PlaneFrame(MeshData mesh)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        // Area-weighted moments of the triangles (each vertex carries a third of its triangle's area).
        var total = 0.0;
        var mean = Vec3d.Zero;
        var faceNormal = Vec3d.Zero;
        for (var t = 0; t < mesh.TriangleCount; t++)
        {
            var (a, b, c) = mesh.Triangle(t);
            var pa = mesh.Position(a);
            var pb = mesh.Position(b);
            var pc = mesh.Position(c);
            var cross = Vec3d.Cross(pb - pa, pc - pa);
            var area = cross.Length / 2;
            faceNormal += cross;
            total += area;
            mean += (pa + pb + pc) * (area / 3);
        }
        if (total <= 0)
            return new Frame(Vec3d.Zero, Vec3d.UnitX, -Vec3d.UnitY, Vec3d.UnitZ);
        mean /= total;
        var cov = new DenseMatrix(3, 3);
        for (var t = 0; t < mesh.TriangleCount; t++)
        {
            var (a, b, c) = mesh.Triangle(t);
            var pa = mesh.Position(a);
            var pb = mesh.Position(b);
            var pc = mesh.Position(c);
            var area = Vec3d.Cross(pb - pa, pc - pa).Length / 2;
            foreach (var p in (ReadOnlySpan<Vec3d>)[pa, pb, pc])
            {
                var d = p - mean;
                for (var r = 0; r < 3; r++)
                    for (var k = 0; k < 3; k++)
                        cov[r, k] += area / 3 * d[r] * d[k];
            }
        }
        var (_, vectors) = SymmetricEigen.Decompose(cov);
        var normal = new Vec3d(vectors[0, 0], vectors[1, 0], vectors[2, 0]).Normalized();
        if (Vec3d.Dot(normal, faceNormal) < 0)
            normal = -normal;
        var e1 = new Vec3d(vectors[0, 2], vectors[1, 2], vectors[2, 2]).Normalized();
        var e2 = Vec3d.Cross(normal, e1).Normalized();

        // The least-area rectangle in the plane: one of its sides lies along an edge of the hull.
        var pts = new List<Vec2d>(mesh.VertexCount);
        for (var i = 0; i < mesh.VertexCount; i++)
        {
            var d = mesh.Position(i) - mean;
            pts.Add(new Vec2d(Vec3d.Dot(d, e1), Vec3d.Dot(d, e2)));
        }
        var hull = ConvexHull(pts);
        var bestAngle = 0.0;
        var bestArea = double.MaxValue;
        for (var i = 0; i < hull.Count; i++)
        {
            var edge = hull[(i + 1) % hull.Count] - hull[i];
            if (edge.LengthSquared < 1e-24)
                continue;
            var angle = Math.Atan2(edge.Y, edge.X);
            var (s, c) = Math.SinCos(angle);
            double minA = double.MaxValue, maxA = double.MinValue, minB = double.MaxValue, maxB = double.MinValue;
            foreach (var p in hull)
            {
                var a = p.X * c + p.Y * s;
                var b = -p.X * s + p.Y * c;
                minA = Math.Min(minA, a);
                maxA = Math.Max(maxA, a);
                minB = Math.Min(minB, b);
                maxB = Math.Max(maxB, b);
            }
            var area = (maxA - minA) * (maxB - minB);
            if (area < bestArea - 1e-12)
            {
                bestArea = area;
                bestAngle = angle;
            }
        }
        var (sa, ca) = Math.SinCos(bestAngle);
        var axisA = (e1 * ca + e2 * sa).Normalized();
        var axisB = Vec3d.Cross(normal, axisA).Normalized();

        // Upright: on anything standing, "up" is the rectangle axis nearer the world's up; on a floor or a
        // ceiling, "down" is the axis nearer the front (+Z).
        Vec3d down;
        if (Math.Abs(normal.Y) < 0.9)
        {
            var up = Math.Abs(axisA.Y) >= Math.Abs(axisB.Y) ? axisA : axisB;
            if (up.Y < 0)
                up = -up;
            down = -up;
        }
        else
        {
            down = Math.Abs(axisA.Z) >= Math.Abs(axisB.Z) ? axisA : axisB;
            if (down.Z < 0)
                down = -down;
        }
        // right = up × normal = normal × down: for a wall facing +Z, (+X, −Y, +Z).
        var right = Vec3d.Cross(normal, down).Normalized();
        return new Frame(mean, right, down, normal);
    }

    /// <summary>Box UVs: each triangle projected along the axis its normal is nearest (faces seen from
    /// outside as a box primitive's are), all faces at one scale so aspect holds across them; vertices
    /// shared by triangles of different faces are split.</summary>
    public static UvProjection Box(MeshData mesh, IReadOnlyCollection<int>? faces = null, UvFit fit = UvFit.Contain)
    {
        var sub = SubsetOf(mesh, faces);
        var (min, max) = sub.Bounds();
        // Right and down for the six sides (+X −X +Y −Y +Z −Z), as on the box primitive.
        ReadOnlySpan<(Vec3d Right, Vec3d Down)> axes =
        [
            (new Vec3d(0, 0, -1), new Vec3d(0, -1, 0)),
            (new Vec3d(0, 0, 1), new Vec3d(0, -1, 0)),
            (new Vec3d(1, 0, 0), new Vec3d(0, 0, 1)),
            (new Vec3d(1, 0, 0), new Vec3d(0, 0, -1)),
            (new Vec3d(1, 0, 0), new Vec3d(0, -1, 0)),
            (new Vec3d(-1, 0, 0), new Vec3d(0, -1, 0)),
        ];
        var size = max - min;
        var scale = Math.Max(size.X, Math.Max(size.Y, size.Z));
        if (scale <= 0)
            scale = 1;
        var map = new Dictionary<(uint Vertex, int Side), uint>();
        var pos = new List<float>();
        var nor = new List<float>();
        var uv = new List<float>();
        var idx = new List<uint>(sub.Indices.Length);
        for (var t = 0; t < sub.TriangleCount; t++)
        {
            var (a, b, c) = sub.Triangle(t);
            var n = Vec3d.Cross(sub.Position(b) - sub.Position(a), sub.Position(c) - sub.Position(a));
            var ax = Math.Abs(n.X);
            var ay = Math.Abs(n.Y);
            var az = Math.Abs(n.Z);
            var side = ax >= ay && ax >= az ? (n.X >= 0 ? 0 : 1) : ay >= az ? (n.Y >= 0 ? 2 : 3) : (n.Z >= 0 ? 4 : 5);
            var (right, down) = axes[side];
            // Each side's own corner: where its right and down coordinates are least over the bounds.
            var corner = new Vec3d(right.X + down.X < 0 ? max.X : min.X, right.Y + down.Y < 0 ? max.Y : min.Y, right.Z + down.Z < 0 ? max.Z : min.Z);
            foreach (var vi in (ReadOnlySpan<int>)[a, b, c])
            {
                if (!map.TryGetValue(((uint)vi, side), out var ni))
                {
                    ni = (uint)(pos.Count / 3);
                    map[((uint)vi, side)] = ni;
                    var p = sub.Position(vi);
                    pos.Add((float)p.X);
                    pos.Add((float)p.Y);
                    pos.Add((float)p.Z);
                    if (sub.HasNormals)
                    {
                        var vn = sub.Normal(vi);
                        nor.Add((float)vn.X);
                        nor.Add((float)vn.Y);
                        nor.Add((float)vn.Z);
                    }
                    var d = p - corner;
                    // Contain: one scale for every side; Stretch: each side's own extent fills 0..1.
                    var su = fit == UvFit.Stretch ? Extent(size, right) : scale;
                    var sv = fit == UvFit.Stretch ? Extent(size, down) : scale;
                    uv.Add((float)(Vec3d.Dot(d, right) / su));
                    uv.Add((float)(Vec3d.Dot(d, down) / sv));
                }
                idx.Add(ni);
            }
        }
        return new UvProjection(new MeshData([.. pos], [.. nor], [.. uv], [.. idx]), scale, scale);
    }

    /// <summary>The box's extent along an axis-aligned unit direction (never zero).</summary>
    private static double Extent(Vec3d size, Vec3d axis)
    {
        var e = Math.Abs(axis.X) * size.X + Math.Abs(axis.Y) * size.Y + Math.Abs(axis.Z) * size.Z;
        return e > 0 ? e : 1;
    }

    /// <summary>Cylindrical UVs about an axis (default the world's up): the centre found by a least-squares
    /// circle through the region seen down the axis, u the arc length round it (starting after the widest
    /// empty gap of angle, so a curved wall is not cut and a full column is cut at its back), v down the
    /// axis — both in metres before fitting, so the aspect is the surface's own.</summary>
    public static UvProjection Cylindrical(MeshData mesh, IReadOnlyCollection<int>? faces = null, Vec3d? axis = null, UvFit fit = UvFit.Contain)
    {
        var sub = SubsetOf(mesh, faces);
        var up = (axis ?? Vec3d.UnitY).Normalized();
        if (up == Vec3d.Zero)
            up = Vec3d.UnitY;
        // A frame across the axis: x and z as a cylinder primitive's (the front +Z when the axis is +Y).
        var refFront = Math.Abs(Vec3d.Dot(up, Vec3d.UnitZ)) < 0.9 ? Vec3d.UnitZ : Vec3d.UnitX;
        var fz = (refFront - up * Vec3d.Dot(refFront, up)).Normalized();
        var fx = Vec3d.Cross(up, fz).Normalized();
        var n = sub.VertexCount;
        var px = new double[n];
        var pz = new double[n];
        var h = new double[n];
        for (var i = 0; i < n; i++)
        {
            var p = sub.Position(i);
            px[i] = Vec3d.Dot(p, fx);
            pz[i] = Vec3d.Dot(p, fz);
            h[i] = Vec3d.Dot(p, up);
        }
        var (cx, cz, radius) = FitCircle(px, pz);
        var theta = new double[n];
        for (var i = 0; i < n; i++)
            theta[i] = Math.Atan2(px[i] - cx, pz[i] - cz); // 0 at the front, growing toward +x (to the right)
        // Start the angle after the widest gap so the region's own span is contiguous.
        var sorted = (double[])theta.Clone();
        Array.Sort(sorted);
        var start = sorted.Length > 0 ? sorted[0] : 0;
        var widest = sorted.Length > 0 ? sorted[0] + 2 * Math.PI - sorted[^1] : 0;
        for (var i = 1; i < sorted.Length; i++)
        {
            var gap = sorted[i] - sorted[i - 1];
            if (gap > widest)
            {
                widest = gap;
                start = sorted[i];
            }
        }
        var arc = new double[n];
        for (var i = 0; i < n; i++)
        {
            var a = theta[i] - start;
            while (a < 0)
                a += 2 * Math.PI;
            arc[i] = a;
        }
        // A triangle that straddles the cut (its angles more than half a turn apart) gets its own copies of
        // the vertices on the low side, a turn further on.
        var pos = new List<float>(sub.Positions);
        var nor = new List<float>(sub.Normals);
        var uList = new List<double>(arc.Select(a => a * radius));
        var vList = new List<double>(h.Select(x => -x));
        var idx = new List<uint>(sub.Indices.Length);
        var wrapped = new Dictionary<int, uint>();
        for (var t = 0; t < sub.TriangleCount; t++)
        {
            var (a, b, c) = sub.Triangle(t);
            ReadOnlySpan<int> tri = [a, b, c];
            var lo = Math.Min(arc[a], Math.Min(arc[b], arc[c]));
            var hi = Math.Max(arc[a], Math.Max(arc[b], arc[c]));
            var straddles = hi - lo > Math.PI;
            foreach (var vi in tri)
            {
                if (!straddles || arc[vi] >= Math.PI)
                {
                    idx.Add((uint)vi);
                    continue;
                }
                if (!wrapped.TryGetValue(vi, out var copy))
                {
                    copy = (uint)(pos.Count / 3);
                    wrapped[vi] = copy;
                    var p = sub.Position(vi);
                    pos.Add((float)p.X);
                    pos.Add((float)p.Y);
                    pos.Add((float)p.Z);
                    if (sub.HasNormals)
                    {
                        var vn = sub.Normal(vi);
                        nor.Add((float)vn.X);
                        nor.Add((float)vn.Y);
                        nor.Add((float)vn.Z);
                    }
                    uList.Add((arc[vi] + 2 * Math.PI) * radius);
                    vList.Add(-h[vi]);
                }
                idx.Add(copy);
            }
        }
        var (uvs, w, hh) = Fit([.. uList], [.. vList], fit);
        return new UvProjection(new MeshData([.. pos], [.. nor], uvs, [.. idx]), w, hh);
    }

    private static MeshData SubsetOf(MeshData mesh, IReadOnlyCollection<int>? faces)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        return faces is null || faces.Count == 0 ? mesh : mesh.Subset(faces);
    }

    /// <summary>Normalises raw coordinates (metres) into UVs; returns the extents.</summary>
    private static (float[] Uvs, double Width, double Height) Fit(double[] u, double[] v, UvFit fit)
    {
        var n = u.Length;
        var uvs = new float[n * 2];
        if (n == 0)
            return (uvs, 0, 0);
        double umin = u.Min(), umax = u.Max(), vmin = v.Min(), vmax = v.Max();
        var w = umax - umin;
        var h = vmax - vmin;
        double su, sv, ou, ov;
        if (fit == UvFit.Stretch)
        {
            su = w > 0 ? 1 / w : 0;
            sv = h > 0 ? 1 / h : 0;
            ou = ov = 0;
        }
        else
        {
            var m = Math.Max(w, h);
            su = sv = m > 0 ? 1 / m : 0;
            ou = (1 - w * su) / 2;
            ov = (1 - h * sv) / 2;
        }
        for (var i = 0; i < n; i++)
        {
            uvs[i * 2] = (float)((u[i] - umin) * su + ou);
            uvs[i * 2 + 1] = (float)((v[i] - vmin) * sv + ov);
        }
        return (uvs, w, h);
    }

    /// <summary>Andrew's monotone chain; counter-clockwise, no repeated end point.</summary>
    private static List<Vec2d> ConvexHull(List<Vec2d> points)
    {
        var pts = points.Distinct().OrderBy(p => p.X).ThenBy(p => p.Y).ToList();
        if (pts.Count < 3)
            return pts;
        var hull = new List<Vec2d>(pts.Count * 2);
        foreach (var p in pts)
        {
            while (hull.Count >= 2 && Vec2d.Cross(hull[^1] - hull[^2], p - hull[^2]) <= 0)
                hull.RemoveAt(hull.Count - 1);
            hull.Add(p);
        }
        var lower = hull.Count + 1;
        for (var i = pts.Count - 2; i >= 0; i--)
        {
            var p = pts[i];
            while (hull.Count >= lower && Vec2d.Cross(hull[^1] - hull[^2], p - hull[^2]) <= 0)
                hull.RemoveAt(hull.Count - 1);
            hull.Add(p);
        }
        hull.RemoveAt(hull.Count - 1);
        return hull;
    }

    /// <summary>The algebraic least-squares circle x² + z² + Dx + Ez + F = 0 through the points; for fewer
    /// than three or collinear points, the centroid and the mean distance to it.</summary>
    private static (double Cx, double Cz, double Radius) FitCircle(double[] x, double[] z)
    {
        var n = x.Length;
        if (n == 0)
            return (0, 0, 1);
        var mx = x.Average();
        var mz = z.Average();
        if (n >= 3)
        {
            var a = new DenseMatrix(n, 3);
            var b = new double[n];
            for (var i = 0; i < n; i++)
            {
                var dx = x[i] - mx;
                var dz = z[i] - mz;
                a[i, 0] = dx;
                a[i, 1] = dz;
                a[i, 2] = 1;
                b[i] = -(dx * dx + dz * dz);
            }
            var s = LinearSolve.LeastSquares(a, b);
            var cx = -s[0] / 2;
            var cz = -s[1] / 2;
            var r2 = cx * cx + cz * cz - s[2];
            if (r2 > 0 && double.IsFinite(r2))
            {
                // A nearly straight region fits a huge circle; the arc length is still right.
                return (cx + mx, cz + mz, Math.Sqrt(r2));
            }
        }
        var meanR = 0.0;
        for (var i = 0; i < n; i++)
            meanR += Math.Sqrt((x[i] - mx) * (x[i] - mx) + (z[i] - mz) * (z[i] - mz));
        return (mx, mz, Math.Max(meanR / n, 1e-9));
    }
}
