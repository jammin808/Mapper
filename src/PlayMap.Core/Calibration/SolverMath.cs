using PlayMap.Core.Model;
using PlayMap.Core.Numerics;

namespace PlayMap.Core.Calibration;

/// <summary>Sampling arithmetic shared by every robust estimator here: the adaptive iteration count and
/// distinct random indices from a seeded generator (a solve is a pure function of its input and seed).</summary>
internal static class Ransac
{
    /// <summary>The draws needed so that, with probability <paramref name="confidence"/>, at least one
    /// sample of <paramref name="sampleSize"/> is all inliers when a fraction <paramref name="inlierRatio"/>
    /// of the data are; clamped to [1, <paramref name="maxIterations"/>].</summary>
    public static int RequiredIterations(double inlierRatio, int sampleSize, double confidence, int maxIterations)
    {
        if (inlierRatio >= 1)
            return 1;
        if (inlierRatio <= 0)
            return maxIterations;
        var allGood = Math.Pow(inlierRatio, sampleSize);
        if (allGood <= 1e-12)
            return maxIterations;
        var n = Math.Log(1 - confidence) / Math.Log(1 - allGood);
        if (double.IsNaN(n) || n > maxIterations)
            return maxIterations;
        return Math.Max(1, (int)Math.Ceiling(n));
    }

    /// <summary>Fills <paramref name="into"/> with distinct indices in [0, count).</summary>
    public static void Sample(ref Pcg32 rng, int count, Span<int> into)
    {
        for (var i = 0; i < into.Length; i++)
        {
            int k;
            do
                k = rng.NextInt(count);
            while (into[..i].Contains(k));
            into[i] = k;
        }
    }

    /// <summary>A deterministic subset of at most <paramref name="max"/> indices of [0, count), in order.</summary>
    public static int[] Subset(int count, int max, ulong seed)
    {
        if (count <= max)
            return Enumerable.Range(0, count).ToArray();
        // Partial Fisher–Yates over the index range, then sorted so the subset keeps the input's order.
        var all = Enumerable.Range(0, count).ToArray();
        var rng = new Pcg32(seed, 0x5eed);
        for (var i = 0; i < max; i++)
        {
            var j = i + rng.NextInt(count - i);
            (all[i], all[j]) = (all[j], all[i]);
        }
        var pick = all[..max];
        Array.Sort(pick);
        return pick;
    }
}

/// <summary>Hartley's conditioning: translate to the centroid and scale so the mean distance from it is
/// √2 (2D) or √3 (3D). Every DLT here runs on conditioned coordinates and maps back.</summary>
internal static class Conditioning
{
    /// <summary>T with T·(x, y, 1) conditioned.</summary>
    public static Mat3d Of(ReadOnlySpan<Vec2d> pts)
    {
        double cx = 0, cy = 0;
        foreach (var p in pts)
        {
            cx += p.X;
            cy += p.Y;
        }
        cx /= pts.Length;
        cy /= pts.Length;
        double mean = 0;
        foreach (var p in pts)
            mean += Math.Sqrt((p.X - cx) * (p.X - cx) + (p.Y - cy) * (p.Y - cy));
        mean /= pts.Length;
        var s = mean > 1e-300 ? Math.Sqrt(2) / mean : 1;
        return new Mat3d(s, 0, -s * cx, 0, s, -s * cy, 0, 0, 1);
    }

    /// <summary>The centroid and scale with (X − c)·s conditioned.</summary>
    public static (Vec3d Centroid, double Scale) Of(ReadOnlySpan<Vec3d> pts)
    {
        var c = Vec3d.Zero;
        foreach (var p in pts)
            c += p;
        c /= pts.Length;
        double mean = 0;
        foreach (var p in pts)
            mean += (p - c).Length;
        mean /= pts.Length;
        return (c, mean > 1e-300 ? Math.Sqrt(3) / mean : 1);
    }
}

/// <summary>Small dense-algebra helpers the estimators share.</summary>
internal static class SolverMath
{
    /// <summary>The unit x minimising |A·x|: the SVD's null vector for small systems, the smallest
    /// eigenvector of AᵀA for tall ones (conditioned input keeps AᵀA well within double precision).</summary>
    public static double[] NullVector(DenseMatrix a)
    {
        if (a.Rows <= 64)
            return Svd.Decompose(a).NullVector();
        var (_, vectors) = SymmetricEigen.Decompose(a.NormalMatrix());
        return vectors.Column(0);
    }

    /// <summary>The best-fit plane of points: centroid, unit normal, and the ratio of the smallest to the
    /// middle principal spread (0 for exactly coplanar points).</summary>
    public static (Vec3d Centroid, Vec3d Normal, Vec3d AxisU, Vec3d AxisV, double Flatness) FitPlane(ReadOnlySpan<Vec3d> pts)
    {
        var c = Vec3d.Zero;
        foreach (var p in pts)
            c += p;
        c /= Math.Max(1, pts.Length);
        var cov = new DenseMatrix(3, 3);
        foreach (var p in pts)
        {
            var d = p - c;
            for (var i = 0; i < 3; i++)
                for (var j = 0; j < 3; j++)
                    cov[i, j] += d[i] * d[j];
        }
        var (values, vectors) = SymmetricEigen.Decompose(cov);
        var n = new Vec3d(vectors[0, 0], vectors[1, 0], vectors[2, 0]).Normalized();
        var v = new Vec3d(vectors[0, 1], vectors[1, 1], vectors[2, 1]).Normalized();
        var u = new Vec3d(vectors[0, 2], vectors[1, 2], vectors[2, 2]).Normalized();
        // Right-handed (u, v, n).
        if (Vec3d.Dot(Vec3d.Cross(u, v), n) < 0)
            n = -n;
        var flat = values[1] > 1e-300 ? Math.Sqrt(Math.Max(0, values[0]) / values[1]) : 0;
        return (c, n, u, v, flat);
    }

    /// <summary>The 2×2 Jacobian of <see cref="Geometry.CameraModel.Distort"/> at normalised point n,
    /// row-major (∂dx/∂x, ∂dx/∂y, ∂dy/∂x, ∂dy/∂y).</summary>
    public static (double A, double B, double C, double D) DistortJacobian(Vec2d n, LensDistortion lens)
    {
        if (lens.IsNone)
            return (1, 0, 0, 1);
        var x = n.X;
        var y = n.Y;
        var r2 = x * x + y * y;
        var radial = 1 + lens.K1 * r2 + lens.K2 * r2 * r2 + lens.K3 * r2 * r2 * r2;
        var dr = lens.K1 + 2 * lens.K2 * r2 + 3 * lens.K3 * r2 * r2;
        var drdx = dr * 2 * x;
        var drdy = dr * 2 * y;
        return (
            radial + x * drdx + 2 * lens.P1 * y + 6 * lens.P2 * x,
            x * drdy + 2 * lens.P1 * x + 2 * lens.P2 * y,
            y * drdx + 2 * lens.P1 * x + 2 * lens.P2 * y,
            radial + y * drdy + 6 * lens.P1 * y + 2 * lens.P2 * x);
    }

    /// <summary>Inverts the distortion at a distorted normalised point: the fixed-point inverse polished by
    /// Newton steps on the analytic Jacobian. Returns false when it does not reach 1e-12.</summary>
    public static bool TryUndistort(Vec2d distorted, LensDistortion lens, out Vec2d undistorted)
    {
        undistorted = Geometry.CameraModel.Undistort(distorted, lens);
        if (lens.IsNone)
            return true;
        for (var i = 0; i < 8; i++)
        {
            var e = Geometry.CameraModel.Distort(undistorted, lens) - distorted;
            if (e.LengthSquared < 1e-24)
                return true;
            var (a, b, c, d) = DistortJacobian(undistorted, lens);
            var det = a * d - b * c;
            if (Math.Abs(det) < 1e-12)
                return false;
            undistorted -= new Vec2d((d * e.X - b * e.Y) / det, (-c * e.X + a * e.Y) / det);
        }
        var final = Geometry.CameraModel.Distort(undistorted, lens) - distorted;
        return final.LengthSquared < 1e-18 && double.IsFinite(undistorted.X) && double.IsFinite(undistorted.Y);
    }

    /// <summary>The value at fraction q (0..1) of the sorted copy of the values.</summary>
    public static double Quantile(ReadOnlySpan<double> values, double q)
    {
        if (values.Length == 0)
            return double.NaN;
        var sorted = values.ToArray();
        Array.Sort(sorted);
        var pos = Math.Clamp(q, 0, 1) * (sorted.Length - 1);
        var lo = (int)Math.Floor(pos);
        var hi = Math.Min(sorted.Length - 1, lo + 1);
        return sorted[lo] + (sorted[hi] - sorted[lo]) * (pos - lo);
    }

    public static double Median(ReadOnlySpan<double> values) => Quantile(values, 0.5);
}
