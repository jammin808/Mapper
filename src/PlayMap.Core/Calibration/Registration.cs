using System.Globalization;
using PlayMap.Core.Geometry;
using PlayMap.Core.Model;
using PlayMap.Core.Numerics;
using PlayMap.Core.Threading;

namespace PlayMap.Core.Calibration;

/// <summary>A similarity of 3D space: p ↦ s·R·p + t (rotation proper, scale positive). Registration answers
/// in this shape: a scan into a LiDAR model's frame, a self-calibrated rig into the world.</summary>
public readonly record struct Similarity3(Mat3d Rotation, Vec3d Translation, double Scale)
{
    public static readonly Similarity3 Identity = new(Mat3d.Identity, Vec3d.Zero, 1);

    public Vec3d Apply(Vec3d p) => Rotation * p * Scale + Translation;

    /// <summary>A direction (no translation, no scale).</summary>
    public Vec3d ApplyDirection(Vec3d d) => Rotation * d;

    /// <summary>A device pose re-expressed: the centre moves with the similarity, the device turns with it.</summary>
    public Pose Apply(Pose pose)
    {
        ArgumentNullException.ThrowIfNull(pose);
        return new Pose(Apply(pose.Position), (Rotation * pose.CameraToWorld).NearestRotation().ToRotationVector());
    }

    public Similarity3 Inverse()
    {
        var rt = Rotation.Transposed();
        var inv = 1 / Scale;
        return new Similarity3(rt, rt * Translation * -inv, inv);
    }

    /// <summary>This, then <paramref name="next"/>.</summary>
    public Similarity3 Then(Similarity3 next) =>
        new(next.Rotation * Rotation, next.Apply(Translation), next.Scale * Scale);

    /// <summary>As a placement of an object in the world (uniform scale).</summary>
    public Transform3 ToTransform3() => new(Translation, Rotation.ToRotationVector(), new Vec3d(Scale, Scale, Scale));

    public static Similarity3 FromTransform3(Transform3 t)
    {
        ArgumentNullException.ThrowIfNull(t);
        return new Similarity3(Mat3d.FromRotationVector(t.Rotation), t.Position, (t.Scale.X + t.Scale.Y + t.Scale.Z) / 3);
    }

    /// <summary>The rotation angle in degrees (how far this turns anything).</summary>
    public double AngleDegrees => Rotation.ToRotationVector().Length * 180 / Math.PI;
}

/// <summary>Umeyama's closed form: the similarity (or rigid motion) that best maps source points onto target
/// points in least squares — three picked pairs register a LiDAR model to a scan.</summary>
public static class Umeyama
{
    /// <summary>The best similarity source → target from three or more non-collinear pairs (scale fixed to 1
    /// when <paramref name="allowScale"/> is false). Throws when there are fewer than three.</summary>
    public static Similarity3 Fit(IReadOnlyList<Vec3d> source, IReadOnlyList<Vec3d> target, bool allowScale = true)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        if (source.Count != target.Count)
            throw new ArgumentException("The lists differ in length.", nameof(target));
        var n = source.Count;
        if (n < 3)
            throw new ArgumentException("Three pairs or more are needed.", nameof(source));
        var ms = Vec3d.Zero;
        var mt = Vec3d.Zero;
        for (var i = 0; i < n; i++)
        {
            ms += source[i];
            mt += target[i];
        }
        ms /= n;
        mt /= n;
        double varS = 0;
        var cov = new DenseMatrix(3, 3);
        for (var i = 0; i < n; i++)
        {
            var a = source[i] - ms;
            var b = target[i] - mt;
            varS += a.LengthSquared;
            for (var r = 0; r < 3; r++)
                for (var c = 0; c < 3; c++)
                    cov[r, c] += b[r] * a[c];
        }
        varS /= n;
        for (var k = 0; k < 9; k++)
            cov.Data[k] /= n;
        var svd = Svd.Decompose(cov);
        var u = svd.U.ToMat3();
        var v = svd.V.ToMat3();
        // Three pairs (or any coplanar set) leave the smallest singular value at zero and that column of U
        // empty: complete it as the cross product so U is orthonormal; the reflection fix below still holds.
        if (svd.S[2] <= 1e-12 * Math.Max(svd.S[0], 1e-300))
            u = Mat3d.FromColumns(u.Column(0), u.Column(1), Vec3d.Cross(u.Column(0), u.Column(1)));
        var d = u.Determinant * v.Determinant < 0 ? -1.0 : 1.0;
        var sDiag = new Mat3d(1, 0, 0, 0, 1, 0, 0, 0, d);
        var rot = u * sDiag * v.Transposed();
        var scale = allowScale && varS > 1e-300 ? (svd.S[0] + svd.S[1] + d * svd.S[2]) / varS : 1;
        var t = mt - rot * ms * scale;
        return new Similarity3(rot, t, scale);
    }

    /// <summary>The RMS distance between the mapped source points and the targets.</summary>
    public static double Rms(Similarity3 s, IReadOnlyList<Vec3d> source, IReadOnlyList<Vec3d> target)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        double sum = 0;
        for (var i = 0; i < source.Count; i++)
            sum += (s.Apply(source[i]) - target[i]).LengthSquared;
        return source.Count > 0 ? Math.Sqrt(sum / source.Count) : 0;
    }
}

/// <summary>Surface normals of a point set by principal components of each point's neighbourhood.</summary>
public static class PointNormals
{
    /// <summary>One unit normal per point (zero for an invalid point or a degenerate neighbourhood), from
    /// its <paramref name="neighbours"/> nearest; oriented towards <paramref name="viewpoint"/> when given.</summary>
    public static Vec3d[] Estimate(IReadOnlyList<Vec3d> points, int neighbours = 12, Vec3d? viewpoint = null, KdTree3? tree = null)
    {
        ArgumentNullException.ThrowIfNull(points);
        tree ??= new KdTree3(points);
        var normals = new Vec3d[points.Count];
        Parallel.For(0, points.Count, Compute.Options, i =>
        {
            var p = points[i];
            if (!PointCloud.IsValid(p))
                return;
            var nn = tree.KNearest(p, Math.Max(3, neighbours));
            if (nn.Length < 3)
                return;
            var near = new Vec3d[nn.Length];
            for (var k = 0; k < nn.Length; k++)
                near[k] = points[nn[k]];
            var (_, normal, _, _, _) = SolverMath.FitPlane(near);
            if (viewpoint is { } vp && Vec3d.Dot(normal, vp - p) < 0)
                normal = -normal;
            normals[i] = normal;
        });
        return normals;
    }
}

/// <summary>How point-to-plane ICP runs.</summary>
public sealed record IcpOptions
{
    public static readonly IcpOptions Default = new();

    public int MaxIterations { get; init; } = 60;

    /// <summary>The fraction of closest pairs kept each iteration (the rest are partial overlap).</summary>
    public double TrimFraction { get; init; } = 0.9;

    /// <summary>Pairs farther apart than this (world units) are never used.</summary>
    public double MaxPairDistance { get; init; } = double.PositiveInfinity;

    /// <summary>A source point counts toward the overlap when its final nearest target is within this;
    /// zero means three times the final RMS.</summary>
    public double OverlapDistance { get; init; }

    /// <summary>Stop when an iteration turns less than this (radians) and moves less than
    /// <see cref="TranslationTolerance"/>.</summary>
    public double RotationTolerance { get; init; } = 1e-7;

    public double TranslationTolerance { get; init; } = 1e-7;

    /// <summary>The neighbourhood for target normals when the target has none.</summary>
    public int NormalNeighbours { get; init; } = 12;

    /// <summary>At most this many source points are used (a seeded subset).</summary>
    public int MaxSourcePoints { get; init; } = 20000;

    public ulong Seed { get; init; } = 1;
}

/// <summary>The outcome of an ICP run: the refined similarity (scale as started), the point-to-plane RMS of the
/// kept pairs, how much of the source overlaps the target, and plain words.</summary>
public sealed record IcpResult(Similarity3 Transform, double RmsDistance, double OverlapFraction, int Iterations, bool Converged, string Words);

/// <summary>Point-to-plane ICP (Chen–Medioni, linearised): each source point pairs with its nearest target
/// point, the worst pairs are trimmed, and a 6-parameter small-motion step minimises the distances along the
/// target normals. Needs a start within a few degrees and centimetres (Umeyama from three picked points).</summary>
public static class Icp
{
    public static IcpResult PointToPlane(IReadOnlyList<Vec3d> source, PointCloud target, Similarity3 initial, IcpOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        options ??= IcpOptions.Default;
        var tree = new KdTree3(target.Points);
        var normals = target.HasNormals ? target.Normals : PointNormals.Estimate(target.Points, options.NormalNeighbours, null, tree);
        var pick = Ransac.Subset(source.Count, options.MaxSourcePoints, options.Seed)
            .Where(i => PointCloud.IsValid(source[i])).ToArray();
        var src = pick.Select(i => source[i]).ToArray();
        var inv = CultureInfo.InvariantCulture;
        if (src.Length < 6 || tree.Count < 3)
            return new IcpResult(initial, double.PositiveInfinity, 0, 0, false, "not registered: too few points");

        var current = initial;
        var moved = new Vec3d[src.Length];
        var nearest = new int[src.Length];
        var dist2 = new double[src.Length];
        var converged = false;
        var iterations = 0;
        var rms = double.PositiveInfinity;
        Span<double> row = stackalloc double[6];
        for (var iter = 0; iter < options.MaxIterations; iter++)
        {
            iterations = iter + 1;
            Pair(src, current, tree, moved, nearest, dist2);
            var keep = KeptPairs(dist2, nearest, normals, options);
            if (keep.Length < 6)
                break;
            // Normal equations of Σ((p + α×p + τ − q)·n)²: row [p×n, n], value −(p − q)·n.
            var a = new DenseMatrix(6, 6);
            var b = new double[6];
            double sum = 0;
            foreach (var k in keep)
            {
                var p = moved[k];
                var q = target.Points[nearest[k]];
                var n = normals[nearest[k]];
                var r = Vec3d.Dot(p - q, n);
                sum += r * r;
                var c = Vec3d.Cross(p, n);
                row[0] = c.X; row[1] = c.Y; row[2] = c.Z; row[3] = n.X; row[4] = n.Y; row[5] = n.Z;
                for (var i = 0; i < 6; i++)
                {
                    b[i] -= row[i] * r;
                    for (var j = 0; j < 6; j++)
                        a[i, j] += row[i] * row[j];
                }
            }
            rms = Math.Sqrt(sum / keep.Length);
            // A whisper of damping keeps directions the geometry does not fix (a lone plane's slide) still.
            var trace = 0.0;
            for (var i = 0; i < 6; i++)
                trace += a[i, i];
            for (var i = 0; i < 6; i++)
                a[i, i] += 1e-9 * trace + 1e-300;
            var x = new double[6];
            if (!LinearSolve.TryCholesky(a, b, x))
                break;
            var alpha = new Vec3d(x[0], x[1], x[2]);
            var tau = new Vec3d(x[3], x[4], x[5]);
            var step = new Similarity3(Mat3d.FromRotationVector(alpha), tau, 1);
            current = current.Then(step);
            current = current with { Rotation = current.Rotation.NearestRotation() };
            if (alpha.Length < options.RotationTolerance && tau.Length < options.TranslationTolerance)
            {
                converged = true;
                break;
            }
        }

        // Final statistics at the final transform.
        Pair(src, current, tree, moved, nearest, dist2);
        var final = KeptPairs(dist2, nearest, normals, options);
        if (final.Length > 0)
        {
            double s = 0;
            foreach (var k in final)
            {
                var r = Vec3d.Dot(moved[k] - target.Points[nearest[k]], normals[nearest[k]]);
                s += r * r;
            }
            rms = Math.Sqrt(s / final.Length);
        }
        // Overlap: the source point lies on the target's surface (within the plane tolerance) and among its
        // samples (within two sample spacings of one), so a scan hanging off the model's edge does not count.
        var spacing = SampleSpacing(target.Points, tree);
        var overlapDistance = options.OverlapDistance > 0 ? options.OverlapDistance : 3 * Math.Max(rms, 1e-9);
        var reach = 2 * spacing + overlapDistance;
        var overlapping = 0;
        for (var i = 0; i < src.Length; i++)
        {
            var plane = Math.Abs(Vec3d.Dot(moved[i] - target.Points[nearest[i]], normals[nearest[i]]));
            if (plane <= overlapDistance && dist2[i] <= reach * reach)
                overlapping++;
        }
        var overlap = overlapping / (double)src.Length;
        var words = string.Create(inv,
            $"{(converged ? "converged" : "stopped")} after {iterations} iterations: {rms * 1000:0.0} mm RMS, {overlap * 100:0}% overlap");
        return new IcpResult(current, rms, overlap, iterations, converged, words);
    }

    /// <summary>The median nearest-neighbour distance of (a sample of) the target: its sampling density.</summary>
    private static double SampleSpacing(Vec3d[] points, KdTree3 tree)
    {
        var step = Math.Max(1, points.Length / 500);
        var d = new List<double>();
        for (var i = 0; i < points.Length; i += step)
        {
            if (!PointCloud.IsValid(points[i]))
                continue;
            var nn = tree.KNearest(points[i], 2);
            if (nn.Length == 2)
                d.Add(Vec3d.Distance(points[nn[1]], points[i]));
        }
        return d.Count > 0 ? SolverMath.Median(d.ToArray()) : 0;
    }

    private static void Pair(Vec3d[] src, Similarity3 t, KdTree3 tree, Vec3d[] moved, int[] nearest, double[] dist2)
    {
        Parallel.For(0, src.Length, Compute.Options, i =>
        {
            var p = t.Apply(src[i]);
            moved[i] = p;
            nearest[i] = tree.Nearest(p, out var d2);
            dist2[i] = d2;
        });
    }

    private static int[] KeptPairs(double[] dist2, int[] nearest, Vec3d[] normals, IcpOptions o)
    {
        var cutoff = SolverMath.Quantile(dist2, Math.Clamp(o.TrimFraction, 0.05, 1));
        var max2 = o.MaxPairDistance * o.MaxPairDistance;
        var keep = new List<int>(dist2.Length);
        for (var i = 0; i < dist2.Length; i++)
        {
            if (nearest[i] < 0 || dist2[i] > cutoff || dist2[i] > max2)
                continue;
            if (normals[nearest[i]].LengthSquared < 0.5)
                continue;
            keep.Add(i);
        }
        return [.. keep];
    }
}
