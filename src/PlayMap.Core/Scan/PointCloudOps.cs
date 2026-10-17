using PlayMap.Core.Geometry;
using PlayMap.Core.Numerics;
using PlayMap.Core.Threading;

namespace PlayMap.Core.Scan;

/// <summary>A plane n·p + d = 0 with a unit normal n.</summary>
public readonly record struct PlaneEquation(Vec3d Normal, double Offset)
{
    public double SignedDistance(Vec3d p) => Vec3d.Dot(Normal, p) + Offset;

    public Vec3d Project(Vec3d p) => p - Normal * SignedDistance(p);

    public static PlaneEquation Through(Vec3d point, Vec3d normal)
    {
        var n = normal.Normalized();
        return new PlaneEquation(n, -Vec3d.Dot(n, point));
    }

    /// <summary>The plane through three points, or null when they are (nearly) collinear.</summary>
    public static PlaneEquation? FromPoints(Vec3d a, Vec3d b, Vec3d c)
    {
        var n = Vec3d.Cross(b - a, c - a);
        var length = n.Length;
        var scale = Math.Max((b - a).LengthSquared, (c - a).LengthSquared);
        if (length < 1e-12 || length * length < 1e-12 * scale * scale)
            return null;
        return Through(a, n / length);
    }

    /// <summary>The same plane with its normal toward <paramref name="viewpoint"/>.</summary>
    public PlaneEquation FacingToward(Vec3d viewpoint) => SignedDistance(viewpoint) >= 0 ? this : new PlaneEquation(-Normal, -Offset);
}

/// <summary>A principal-component fit of points: the centroid, the normal (the direction of least spread),
/// the RMS distance from the fitted plane and the surface variation λ₀/(λ₀+λ₁+λ₂) — 0 on a plane, 1/3 for
/// an isotropic blob.</summary>
public readonly record struct PcaFit(Vec3d Centroid, Vec3d Normal, double RmsDistance, double Curvature, int Count)
{
    public PlaneEquation Plane => PlaneEquation.Through(Centroid, Normal);

    /// <summary>The fit of the points at <paramref name="indices"/>, or a fit with Count &lt; 3 and a zero normal
    /// when there are too few. One pass, about the first point (so far-from-origin clouds keep their precision).</summary>
    public static PcaFit Of(Vec3d[] points, ReadOnlySpan<int> indices)
    {
        ArgumentNullException.ThrowIfNull(points);
        var n = indices.Length;
        if (n < 3)
            return new PcaFit(n > 0 ? points[indices[0]] : Vec3d.Zero, Vec3d.Zero, 0, 0, n);
        var o = points[indices[0]];
        double sx = 0, sy = 0, sz = 0, xx = 0, xy = 0, xz = 0, yy = 0, yz = 0, zz = 0;
        foreach (var i in indices)
        {
            var p = points[i];
            double dx = p.X - o.X, dy = p.Y - o.Y, dz = p.Z - o.Z;
            sx += dx;
            sy += dy;
            sz += dz;
            xx += dx * dx;
            xy += dx * dy;
            xz += dx * dz;
            yy += dy * dy;
            yz += dy * dz;
            zz += dz * dz;
        }
        double mx = sx / n, my = sy / n, mz = sz / n;
        var (values, normal) = Sym3.Smallest(
            xx / n - mx * mx, xy / n - mx * my, xz / n - mx * mz,
            yy / n - my * my, yz / n - my * mz, zz / n - mz * mz);
        var total = values.X + values.Y + values.Z;
        var smallest = Math.Max(0, values.X);
        return new PcaFit(new Vec3d(o.X + mx, o.Y + my, o.Z + mz), normal, Math.Sqrt(smallest), total > 0 ? smallest / total : 0, n);
    }
}

/// <summary>Eigen-decomposition of a symmetric 3 × 3 by cyclic Jacobi rotations, on the stack.</summary>
internal static class Sym3
{
    /// <summary>The eigenvalues ascending (as X ≤ Y ≤ Z) and the unit eigenvector of the smallest.</summary>
    public static (Vec3d Values, Vec3d Smallest) Smallest(double a00, double a01, double a02, double a11, double a12, double a22)
    {
        Span<double> a = [a00, a01, a02, a01, a11, a12, a02, a12, a22];
        Span<double> v = [1, 0, 0, 0, 1, 0, 0, 0, 1];
        var scale = Math.Abs(a00) + Math.Abs(a11) + Math.Abs(a22) + 1e-300;
        for (var sweep = 0; sweep < 32; sweep++)
        {
            var off = a[1] * a[1] + a[2] * a[2] + a[5] * a[5];
            if (off <= 1e-30 * scale * scale)
                break;
            Rotate(a, v, 0, 1);
            Rotate(a, v, 0, 2);
            Rotate(a, v, 1, 2);
        }
        var i0 = 0;
        if (a[4] < a[i0 * 4])
            i0 = 1;
        if (a[8] < a[i0 * 4])
            i0 = 2;
        Span<double> d = [a[0], a[4], a[8]];
        d.Sort();
        var vector = new Vec3d(v[i0], v[3 + i0], v[6 + i0]).Normalized();
        return (new Vec3d(d[0], d[1], d[2]), vector);
    }

    private static void Rotate(Span<double> a, Span<double> v, int p, int q)
    {
        var apq = a[p * 3 + q];
        if (Math.Abs(apq) < 1e-300)
            return;
        var theta = (a[q * 3 + q] - a[p * 3 + p]) / (2 * apq);
        var t = Math.Sign(theta) / (Math.Abs(theta) + Math.Sqrt(theta * theta + 1));
        if (theta == 0)
            t = 1;
        var c = 1 / Math.Sqrt(t * t + 1);
        var s = t * c;
        for (var k = 0; k < 3; k++)
        {
            var akp = a[k * 3 + p];
            var akq = a[k * 3 + q];
            a[k * 3 + p] = c * akp - s * akq;
            a[k * 3 + q] = s * akp + c * akq;
        }
        for (var k = 0; k < 3; k++)
        {
            var apk = a[p * 3 + k];
            var aqk = a[q * 3 + k];
            a[p * 3 + k] = c * apk - s * aqk;
            a[q * 3 + k] = s * apk + c * aqk;
        }
        for (var k = 0; k < 3; k++)
        {
            var vkp = v[k * 3 + p];
            var vkq = v[k * 3 + q];
            v[k * 3 + p] = c * vkp - s * vkq;
            v[k * 3 + q] = s * vkp + c * vkq;
        }
    }
}

/// <summary>Options for normal estimation.</summary>
public sealed record NormalOptions
{
    /// <summary>Neighbours of an unorganised cloud's point (k nearest).</summary>
    public int K { get; init; } = 16;

    /// <summary>An organised cloud's neighbourhood: the (2r+1)² grid window.</summary>
    public int GridRadius { get; init; } = 2;

    /// <summary>An organised neighbour further than this many local grid steps (per cell of grid distance) lies
    /// across a depth jump and is left out, so normals do not bleed over an occluding edge.</summary>
    public double JumpFactor { get; init; } = 3;
}

/// <summary>Clean-up and normals for point clouds: PCA normals (k nearest, or the grid window of an organised
/// cloud) turned toward the sensor, voxel down-sampling, statistical outlier removal, and the local grid step
/// the depth-jump tests measure against.</summary>
public static class PointCloudOps
{
    /// <summary>The cloud with a unit normal per valid point (zero for an invalid or isolated one), each turned
    /// toward <paramref name="sensor"/>.</summary>
    public static PointCloud EstimateNormals(PointCloud cloud, Vec3d sensor, NormalOptions? options = null) =>
        EstimateNormals(cloud, sensor, out _, options);

    /// <summary>As <see cref="EstimateNormals(PointCloud, Vec3d, NormalOptions?)"/>, also giving each point's surface
    /// variation (0 flat, larger at edges and corners; NaN where no normal).</summary>
    public static PointCloud EstimateNormals(PointCloud cloud, Vec3d sensor, out float[] curvature, NormalOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(cloud);
        options ??= new NormalOptions();
        var points = cloud.Points;
        var normals = new Vec3d[points.Length];
        var variation = new float[points.Length];
        Array.Fill(variation, float.NaN);
        if (cloud.IsOrganised)
        {
            var spacing = GridSpacing(cloud);
            var w = cloud.GridWidth;
            var h = cloud.GridHeight;
            var r = Math.Max(1, options.GridRadius);
            Parallel.For(0, h, Compute.Options, y =>
            {
                var window = new int[(2 * r + 1) * (2 * r + 1)];
                for (var x = 0; x < w; x++)
                {
                    var k = y * w + x;
                    if (!PointCloud.IsValid(points[k]) || !(spacing[k] > 0))
                        continue;
                    var n = 0;
                    for (var dy = -r; dy <= r; dy++)
                    {
                        var yy = y + dy;
                        if (yy < 0 || yy >= h)
                            continue;
                        for (var dx = -r; dx <= r; dx++)
                        {
                            var xx = x + dx;
                            if (xx < 0 || xx >= w)
                                continue;
                            var q = yy * w + xx;
                            var reach = options.JumpFactor * spacing[k] * Math.Max(Math.Abs(dx), Math.Abs(dy));
                            if (PointCloud.IsValid(points[q]) && (q == k || (points[q] - points[k]).LengthSquared <= reach * reach))
                                window[n++] = q;
                        }
                    }
                    Fit(points, window.AsSpan(0, n), k, sensor, normals, variation);
                }
            });
        }
        else
        {
            var tree = new KdTree3(points);
            Parallel.For(0, points.Length, Compute.Options, k =>
            {
                if (!PointCloud.IsValid(points[k]))
                    return;
                Fit(points, tree.KNearest(points[k], Math.Max(3, options.K)), k, sensor, normals, variation);
            });
        }
        curvature = variation;
        return new PointCloud(points, normals, cloud.HasColors ? cloud.Colors : null, cloud.GridWidth, cloud.GridHeight);
    }

    private static void Fit(Vec3d[] points, ReadOnlySpan<int> neighbours, int k, Vec3d sensor, Vec3d[] normals, float[] variation)
    {
        if (neighbours.Length < 3)
            return;
        var fit = PcaFit.Of(points, neighbours);
        if (fit.Normal == Vec3d.Zero)
            return;
        var n = fit.Normal;
        if (Vec3d.Dot(n, sensor - points[k]) < 0)
            n = -n;
        normals[k] = n;
        variation[k] = (float)fit.Curvature;
    }

    /// <summary>Per point of an organised cloud, the median distance to its valid 8-neighbours — the local grid
    /// step (NaN where invalid or alone). A neighbour across a depth jump is far, but it is a minority of the
    /// eight on any straight edge, so the median stays the surface's own step.</summary>
    public static float[] GridSpacing(PointCloud organised)
    {
        ArgumentNullException.ThrowIfNull(organised);
        if (!organised.IsOrganised)
            throw new ArgumentException("The cloud has no grid.", nameof(organised));
        var w = organised.GridWidth;
        var h = organised.GridHeight;
        var points = organised.Points;
        var spacing = new float[points.Length];
        Parallel.For(0, h, Compute.Options, y =>
        {
            Span<float> d = stackalloc float[8];
            for (var x = 0; x < w; x++)
            {
                var k = y * w + x;
                spacing[k] = float.NaN;
                if (!PointCloud.IsValid(points[k]))
                    continue;
                var n = 0;
                for (var dy = -1; dy <= 1; dy++)
                {
                    for (var dx = -1; dx <= 1; dx++)
                    {
                        var xx = x + dx;
                        var yy = y + dy;
                        if ((dx == 0 && dy == 0) || xx < 0 || yy < 0 || xx >= w || yy >= h)
                            continue;
                        var q = points[yy * w + xx];
                        if (!PointCloud.IsValid(q))
                            continue;
                        // A diagonal neighbour is √2 steps away.
                        var dist = (q - points[k]).Length / (dx != 0 && dy != 0 ? Math.Sqrt(2) : 1);
                        d[n++] = (float)dist;
                    }
                }
                if (n == 0)
                    continue;
                var near = d[..n];
                near.Sort();
                spacing[k] = n % 2 == 1 ? near[n / 2] : 0.5f * (near[n / 2 - 1] + near[n / 2]);
            }
        });
        return spacing;
    }

    /// <summary>One point per occupied voxel of side <paramref name="voxel"/>: the mean of its points (normals and
    /// colours averaged too). The result is unorganised, in first-seen voxel order.</summary>
    public static PointCloud VoxelDownsample(PointCloud cloud, double voxel)
    {
        ArgumentNullException.ThrowIfNull(cloud);
        if (!(voxel > 0))
            throw new ArgumentOutOfRangeException(nameof(voxel), "The voxel size must be positive.");
        var cells = new Dictionary<(long, long, long), int>();
        var sums = new List<(Vec3d Point, Vec3d Normal, double R, double G, double B, int Count)>();
        for (var i = 0; i < cloud.Count; i++)
        {
            var p = cloud.Points[i];
            if (!PointCloud.IsValid(p))
                continue;
            var key = ((long)Math.Floor(p.X / voxel), (long)Math.Floor(p.Y / voxel), (long)Math.Floor(p.Z / voxel));
            if (!cells.TryGetValue(key, out var slot))
            {
                slot = sums.Count;
                cells[key] = slot;
                sums.Add((Vec3d.Zero, Vec3d.Zero, 0, 0, 0, 0));
            }
            var s = sums[slot];
            var colour = cloud.HasColors ? cloud.Colors[i] : 0;
            sums[slot] = (s.Point + p, s.Normal + (cloud.HasNormals ? cloud.Normals[i] : Vec3d.Zero),
                s.R + ((colour >> 16) & 0xFF), s.G + ((colour >> 8) & 0xFF), s.B + (colour & 0xFF), s.Count + 1);
        }
        var points = new Vec3d[sums.Count];
        var normals = cloud.HasNormals ? new Vec3d[sums.Count] : null;
        var colours = cloud.HasColors ? new int[sums.Count] : null;
        for (var i = 0; i < sums.Count; i++)
        {
            var s = sums[i];
            points[i] = s.Point / s.Count;
            if (normals is not null)
                normals[i] = s.Normal.Normalized();
            if (colours is not null)
                colours[i] = ((int)Math.Round(s.R / s.Count) << 16) | ((int)Math.Round(s.G / s.Count) << 8) | (int)Math.Round(s.B / s.Count);
        }
        return new PointCloud(points, normals, colours);
    }

    /// <summary>Drops points whose mean distance to their <paramref name="k"/> nearest neighbours is more than
    /// <paramref name="stdRatio"/> standard deviations above the cloud's mean of that distance. An organised
    /// cloud keeps its grid (dropped points become invalid); an unorganised one is compacted. Kept lists the
    /// surviving input indices.</summary>
    public static (PointCloud Cloud, int[] Kept) RemoveStatisticalOutliers(PointCloud cloud, int k = 16, double stdRatio = 2.0)
    {
        ArgumentNullException.ThrowIfNull(cloud);
        ArgumentOutOfRangeException.ThrowIfLessThan(k, 1);
        var points = cloud.Points;
        var tree = new KdTree3(points);
        var meanDistance = new double[points.Length];
        Array.Fill(meanDistance, double.NaN);
        Parallel.For(0, points.Length, Compute.Options, i =>
        {
            if (!PointCloud.IsValid(points[i]))
                return;
            var near = tree.KNearest(points[i], k + 1);
            double sum = 0;
            var n = 0;
            foreach (var j in near)
            {
                if (j == i)
                    continue;
                sum += (points[j] - points[i]).Length;
                n++;
            }
            meanDistance[i] = n > 0 ? sum / n : double.PositiveInfinity;
        });
        double total = 0, totalSq = 0;
        var count = 0;
        foreach (var d in meanDistance)
        {
            if (!double.IsFinite(d))
                continue;
            total += d;
            totalSq += d * d;
            count++;
        }
        var mean = count > 0 ? total / count : 0;
        var std = count > 1 ? Math.Sqrt(Math.Max(0, totalSq / count - mean * mean)) : 0;
        var limit = mean + stdRatio * std;
        var kept = new List<int>();
        for (var i = 0; i < points.Length; i++)
        {
            if (double.IsFinite(meanDistance[i]) && meanDistance[i] <= limit)
                kept.Add(i);
        }
        if (cloud.IsOrganised)
        {
            var grid = new Vec3d[points.Length];
            Array.Fill(grid, PointCloud.Invalid);
            foreach (var i in kept)
                grid[i] = points[i];
            return (new PointCloud(grid, cloud.HasNormals ? cloud.Normals : null, cloud.HasColors ? cloud.Colors : null, cloud.GridWidth, cloud.GridHeight), [.. kept]);
        }
        return (new PointCloud(
            kept.Select(i => points[i]).ToArray(),
            cloud.HasNormals ? kept.Select(i => cloud.Normals[i]).ToArray() : null,
            cloud.HasColors ? kept.Select(i => cloud.Colors[i]).ToArray() : null), [.. kept]);
    }
}
