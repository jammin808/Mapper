using PlayMap.Core.Model;
using PlayMap.Core.Numerics;

namespace PlayMap.Core.Calibration;

/// <summary>A robustly fitted fundamental matrix (x_bᵀ·F·x_a = 0), the pairs within the Sampson threshold,
/// and their RMS Sampson distance in pixels.</summary>
public sealed record FundamentalFit(Mat3d F, IReadOnlyList<bool> Inliers, int InlierCount, double RmsPx, int Iterations)
{
    public bool Succeeded => InlierCount >= 8;
}

/// <summary>A relative pose between two views: x_b = R·x_a + t (a point in view a's frame to view b's), with
/// |t| = 1 when it comes from an essential matrix (the scale is not observable from two views alone).</summary>
public readonly record struct RelativePose(Mat3d Rotation, Vec3d Translation);

/// <summary>Two-view epipolar geometry: the normalised eight-point fundamental matrix with RANSAC on the
/// Sampson distance, the essential matrix from guessed intrinsics, and its four decompositions with the
/// cheirality test that picks the one with the scene in front of both views.</summary>
public static class Epipolar
{
    /// <summary>F from eight or more pairs by the conditioned eight-point algorithm, rank two enforced; null
    /// when degenerate.</summary>
    public static Mat3d? EightPoint(IReadOnlyList<Vec2d> a, IReadOnlyList<Vec2d> b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        if (a.Count != b.Count)
            throw new ArgumentException("The lists differ in length.", nameof(b));
        return EightPoint(a.ToArray(), b.ToArray());
    }

    internal static Mat3d? EightPoint(ReadOnlySpan<Vec2d> a, ReadOnlySpan<Vec2d> b)
    {
        var n = a.Length;
        if (n < 8)
            return null;
        var ta = Conditioning.Of(a);
        var tb = Conditioning.Of(b);
        var m = new DenseMatrix(n, 9);
        for (var i = 0; i < n; i++)
        {
            var p = ta.TransformPoint(a[i]);
            var q = tb.TransformPoint(b[i]);
            var row = m.RowSpan(i);
            row[0] = q.X * p.X; row[1] = q.X * p.Y; row[2] = q.X;
            row[3] = q.Y * p.X; row[4] = q.Y * p.Y; row[5] = q.Y;
            row[6] = p.X; row[7] = p.Y; row[8] = 1;
        }
        var f = Mat3d.FromArray(SolverMath.NullVector(m));
        var (u, s, v) = Svd3(f);
        var rank2 = u * new Mat3d(s.X, 0, 0, 0, s.Y, 0, 0, 0, 0) * v.Transposed();
        var full = tb.Transposed() * rank2 * ta;
        var norm = Math.Sqrt(full.ToArray().Sum(x => x * x));
        if (!(norm > 1e-300) || !double.IsFinite(norm))
            return null;
        return full * (1 / norm);
    }

    /// <summary>RANSAC over eight-pair samples scored by the Sampson distance (MSAC, seeded, adaptive draw
    /// count), then refits on the consensus until it settles.</summary>
    public static FundamentalFit FitRobust(IReadOnlyList<Vec2d> a, IReadOnlyList<Vec2d> b, double thresholdPx = 1.5,
        ulong seed = 1, int maxIterations = 2000, double confidence = 0.999)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        if (a.Count != b.Count)
            throw new ArgumentException("The lists differ in length.", nameof(b));
        var pa = a.ToArray();
        var pb = b.ToArray();
        var n = pa.Length;
        if (n < 8)
            return new FundamentalFit(default, new bool[n], 0, double.PositiveInfinity, 0);
        var rng = new Pcg32(seed, 0xf00d);
        Span<int> sample = stackalloc int[8];
        var sa = new Vec2d[8];
        var sb = new Vec2d[8];
        var t2 = thresholdPx * thresholdPx;
        Mat3d? best = null;
        var bestScore = double.PositiveInfinity;
        var needed = maxIterations;
        var iter = 0;
        for (; iter < needed; iter++)
        {
            Ransac.Sample(ref rng, n, sample);
            for (var k = 0; k < 8; k++)
            {
                sa[k] = pa[sample[k]];
                sb[k] = pb[sample[k]];
            }
            if (EightPoint(sa, sb) is not { } f)
                continue;
            double score = 0;
            var count = 0;
            for (var i = 0; i < n; i++)
            {
                var e = Sampson2(f, pa[i], pb[i]);
                if (e < t2)
                {
                    score += e;
                    count++;
                }
                else
                {
                    score += t2;
                }
            }
            if (score < bestScore)
            {
                bestScore = score;
                best = f;
                needed = Ransac.RequiredIterations(count / (double)n, 8, confidence, maxIterations);
            }
        }
        if (best is not { } model)
            return new FundamentalFit(default, new bool[n], 0, double.PositiveInfinity, iter);
        var inliers = Classify(model, pa, pb, t2);
        for (var round = 0; round < 4; round++)
        {
            var keep = Enumerable.Range(0, n).Where(i => inliers[i]).ToArray();
            if (keep.Length < 8)
                break;
            if (EightPoint(keep.Select(i => pa[i]).ToArray(), keep.Select(i => pb[i]).ToArray()) is not { } refit)
                break;
            model = refit;
            var next = Classify(model, pa, pb, t2);
            if (next.AsSpan().SequenceEqual(inliers))
                break;
            inliers = next;
        }
        double sum = 0;
        var c = 0;
        for (var i = 0; i < n; i++)
        {
            if (!inliers[i])
                continue;
            sum += Sampson2(model, pa[i], pb[i]);
            c++;
        }
        return new FundamentalFit(model, inliers, c, c > 0 ? Math.Sqrt(sum / c) : double.PositiveInfinity, iter);
    }

    /// <summary>The Sampson distance of a pair to F: the first-order distance to the nearest exact
    /// correspondence, in pixels.</summary>
    public static double SampsonDistance(Mat3d f, Vec2d a, Vec2d b) => Math.Sqrt(Sampson2(f, a, b));

    internal static double Sampson2(Mat3d f, Vec2d a, Vec2d b)
    {
        var fa = f * new Vec3d(a.X, a.Y, 1);
        var ftb = f.Transposed() * new Vec3d(b.X, b.Y, 1);
        var e = b.X * fa.X + b.Y * fa.Y + fa.Z;
        var den = fa.X * fa.X + fa.Y * fa.Y + ftb.X * ftb.X + ftb.Y * ftb.Y;
        return den > 1e-300 ? e * e / den : double.PositiveInfinity;
    }

    /// <summary>E = K_bᵀ·F·K_a projected onto the essential manifold (singular values 1, 1, 0).</summary>
    public static Mat3d EssentialFromFundamental(Mat3d f, Intrinsics ka, Intrinsics kb)
    {
        ArgumentNullException.ThrowIfNull(ka);
        ArgumentNullException.ThrowIfNull(kb);
        var e = kb.K.Transposed() * f * ka.K;
        var (u, _, v) = Svd3(e);
        return u * new Mat3d(1, 0, 0, 0, 1, 0, 0, 0, 0) * v.Transposed();
    }

    /// <summary>The four (R, t) an essential matrix allows: R ∈ {U·W·Vᵀ, U·Wᵀ·Vᵀ}, t = ±u₃.</summary>
    public static RelativePose[] Decompose(Mat3d e)
    {
        var (u, _, v) = Svd3(e);
        if (u.Determinant < 0)
            u *= -1;
        if (v.Determinant < 0)
            v *= -1;
        var w = new Mat3d(0, -1, 0, 1, 0, 0, 0, 0, 1);
        var r1 = u * w * v.Transposed();
        var r2 = u * w.Transposed() * v.Transposed();
        var t = u.Column(2).Normalized();
        return [new(r1, t), new(r1, -t), new(r2, t), new(r2, -t)];
    }

    /// <summary>Of E's four decompositions, the one that puts the most of the given pairs (normalised image
    /// coordinates, z = 1) in front of both views; null when none puts a majority there.</summary>
    public static RelativePose? SelectByCheirality(Mat3d e, IReadOnlyList<Vec2d> normalisedA, IReadOnlyList<Vec2d> normalisedB)
    {
        ArgumentNullException.ThrowIfNull(normalisedA);
        ArgumentNullException.ThrowIfNull(normalisedB);
        RelativePose? best = null;
        var bestCount = 0;
        foreach (var cand in Decompose(e))
        {
            var count = 0;
            for (var i = 0; i < normalisedA.Count; i++)
            {
                var za = DepthInA(cand, normalisedA[i], normalisedB[i]);
                if (za <= 0 || !double.IsFinite(za))
                    continue;
                var xb = cand.Rotation * new Vec3d(normalisedA[i].X, normalisedA[i].Y, 1) * za + cand.Translation;
                if (xb.Z > 0)
                    count++;
            }
            if (count > bestCount)
            {
                bestCount = count;
                best = cand;
            }
        }
        return bestCount * 2 > normalisedA.Count ? best : null;
    }

    /// <summary>The depth along view a's ray (z of the point in a's frame) that best meets view b's ray
    /// under x_b = R·x_a + t, from normalised coordinates.</summary>
    public static double DepthInA(RelativePose pose, Vec2d a, Vec2d b)
    {
        var xb = new Vec3d(b.X, b.Y, 1);
        var ra = pose.Rotation * new Vec3d(a.X, a.Y, 1);
        var c1 = Vec3d.Cross(xb, ra);
        var c2 = Vec3d.Cross(xb, pose.Translation);
        var den = c1.LengthSquared;
        return den > 1e-300 ? -Vec3d.Dot(c1, c2) / den : double.NaN;
    }

    /// <summary>The SVD of a 3×3 matrix with U completed to an orthonormal basis when a singular value is
    /// zero (the third column of a rank-two matrix's U is otherwise empty).</summary>
    internal static (Mat3d U, Vec3d S, Mat3d V) Svd3(Mat3d m)
    {
        var svd = Svd.Decompose(DenseMatrix.FromMat3(m));
        var u = svd.U.ToMat3();
        var v = svd.V.ToMat3();
        var c0 = u.Column(0);
        var c1 = u.Column(1);
        if (svd.S[1] <= 1e-14 * Math.Max(svd.S[0], 1e-300))
        {
            var helper = Math.Abs(c0.X) < 0.9 ? Vec3d.UnitX : Vec3d.UnitY;
            c1 = Vec3d.Cross(c0, helper).Normalized();
        }
        if (svd.S[2] <= 1e-14 * Math.Max(svd.S[0], 1e-300))
            u = Mat3d.FromColumns(c0, c1, Vec3d.Cross(c0, c1));
        else
            u = Mat3d.FromColumns(c0, c1, u.Column(2));
        return (u, new Vec3d(svd.S[0], svd.S[1], svd.S[2]), v);
    }

    private static bool[] Classify(Mat3d f, Vec2d[] a, Vec2d[] b, double t2)
    {
        var r = new bool[a.Length];
        for (var i = 0; i < a.Length; i++)
            r[i] = Sampson2(f, a[i], b[i]) < t2;
        return r;
    }
}
