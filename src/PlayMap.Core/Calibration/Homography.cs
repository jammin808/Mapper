using PlayMap.Core.Numerics;

namespace PlayMap.Core.Calibration;

/// <summary>A robustly fitted homography: the matrix (from → to, scaled so H₂₂ = 1 when it can be), which
/// pairs it explains within the threshold, and the RMS transfer error over those in pixels.</summary>
public sealed record HomographyFit(Mat3d H, IReadOnlyList<bool> Inliers, int InlierCount, double RmsPx, int Iterations)
{
    public bool Succeeded => InlierCount >= 4 && double.IsFinite(RmsPx);
}

/// <summary>Plane-to-plane projective maps: the normalised DLT from four or more pairs, RANSAC around it,
/// Levenberg–Marquardt on the symmetric transfer error, and Heckbert's closed form for a unit square to a
/// quad (the corner pin). Points are (x, y) in any consistent unit; a homography acts on (x, y, 1).</summary>
public static class Homography
{
    /// <summary>The homography taking each <paramref name="from"/> point to its <paramref name="to"/>
    /// point, by the normalised DLT (exact for four pairs in general position, least squares beyond).
    /// Null when the pairs are degenerate (three collinear among four, fewer than four).</summary>
    public static Mat3d? Fit(IReadOnlyList<Vec2d> from, IReadOnlyList<Vec2d> to)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        if (from.Count != to.Count)
            throw new ArgumentException("The point lists differ in length.", nameof(to));
        return Fit(from.ToArray(), to.ToArray());
    }

    internal static Mat3d? Fit(ReadOnlySpan<Vec2d> from, ReadOnlySpan<Vec2d> to)
    {
        var n = from.Length;
        if (n < 4)
            return null;
        var t1 = Conditioning.Of(from);
        var t2 = Conditioning.Of(to);
        var a = new DenseMatrix(2 * n, 9);
        for (var i = 0; i < n; i++)
        {
            var p = t1.TransformPoint(from[i]);
            var q = t2.TransformPoint(to[i]);
            var r0 = a.RowSpan(2 * i);
            r0[0] = -p.X; r0[1] = -p.Y; r0[2] = -1;
            r0[6] = q.X * p.X; r0[7] = q.X * p.Y; r0[8] = q.X;
            var r1 = a.RowSpan(2 * i + 1);
            r1[3] = -p.X; r1[4] = -p.Y; r1[5] = -1;
            r1[6] = q.Y * p.X; r1[7] = q.Y * p.Y; r1[8] = q.Y;
        }
        var h = SolverMath.NullVector(a);
        var hn = Mat3d.FromArray(h);
        if (!t2.TryInverse(out var t2Inv))
            return null;
        var full = t2Inv * hn * t1;
        if (Math.Abs(full.Determinant) < 1e-300 || !IsFinite(full))
            return null;
        // A rank-deficient system (collinear points) leaves a near-singular map: refuse it.
        var hs = Normalise(hn, frobenius: true);
        if (Math.Abs(hs.Determinant) < 1e-9)
            return null;
        return Normalise(full);
    }

    /// <summary>RANSAC over four-point samples (seeded, the draw count adapting to the inlier ratio seen),
    /// then a least-squares refit and Levenberg–Marquardt refinement on the inliers. A pair is an inlier
    /// when its forward transfer error is within <paramref name="thresholdPx"/>.</summary>
    public static HomographyFit FitRobust(IReadOnlyList<Vec2d> from, IReadOnlyList<Vec2d> to, double thresholdPx = 3,
        ulong seed = 1, int maxIterations = 2000, double confidence = 0.999)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        if (from.Count != to.Count)
            throw new ArgumentException("The point lists differ in length.", nameof(to));
        var src = from.ToArray();
        var dst = to.ToArray();
        var n = src.Length;
        var none = new bool[n];
        if (n < 4)
            return new HomographyFit(Mat3d.Identity, none, 0, double.PositiveInfinity, 0);

        var rng = new Pcg32(seed);
        Span<int> sample = stackalloc int[4];
        var s4 = new Vec2d[4];
        var d4 = new Vec2d[4];
        var t2 = thresholdPx * thresholdPx;
        Mat3d? best = null;
        var bestScore = double.PositiveInfinity;
        var bestCount = 0;
        var needed = maxIterations;
        var iter = 0;
        for (; iter < needed && iter < maxIterations; iter++)
        {
            Ransac.Sample(ref rng, n, sample);
            for (var k = 0; k < 4; k++)
            {
                s4[k] = src[sample[k]];
                d4[k] = dst[sample[k]];
            }
            if (HasCollinearTriple(s4) || HasCollinearTriple(d4) || Fit(s4, d4) is not { } h)
                continue;
            // MSAC: inliers cost their squared error, outliers the threshold — ties break toward the tighter fit.
            double score = 0;
            var count = 0;
            for (var i = 0; i < n; i++)
            {
                var e = TransferError2(h, src[i], dst[i]);
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
                best = h;
                bestCount = count;
                needed = Ransac.RequiredIterations(count / (double)n, 4, confidence, maxIterations);
            }
        }
        if (best is not { } model || bestCount < 4)
            return new HomographyFit(Mat3d.Identity, none, 0, double.PositiveInfinity, iter);

        // Local optimisation: refit on the consensus, refine, re-read the consensus until it settles.
        var inliers = Classify(model, src, dst, t2);
        for (var round = 0; round < 4; round++)
        {
            var (fs, fd) = Select(src, dst, inliers);
            if (fs.Length < 4)
                break;
            var refit = Fit(fs, fd) ?? model;
            model = Refine(refit, fs, fd);
            var next = Classify(model, src, dst, t2);
            if (next.AsSpan().SequenceEqual(inliers))
                break;
            inliers = next;
        }
        var finalCount = inliers.Count(b => b);
        return new HomographyFit(model, inliers, finalCount, Rms(model, src, dst, inliers), iter);
    }

    /// <summary>Levenberg–Marquardt on the symmetric transfer error (forward and backward, both in their
    /// own plane's units) from an initial homography.</summary>
    public static Mat3d Refine(Mat3d initial, IReadOnlyList<Vec2d> from, IReadOnlyList<Vec2d> to)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        return Refine(initial, from.ToArray(), to.ToArray());
    }

    internal static Mat3d Refine(Mat3d initial, Vec2d[] from, Vec2d[] to)
    {
        if (from.Length < 4)
            return initial;
        var t1 = Conditioning.Of(from);
        var t2 = Conditioning.Of(to);
        if (!t1.TryInverse(out var t1Inv) || !t2.TryInverse(out var t2Inv))
            return initial;
        // Refine in conditioned coordinates (well-scaled parameters), measure in the original ones.
        var hn = Normalise(t2 * initial * t1Inv, frobenius: true);
        var problem = new SymmetricTransfer(from, to, t1, t2Inv);
        var result = LevenbergMarquardt.Solve(problem, hn.ToArray(), maxIterations: 50, tolerance: 1e-14);
        var refined = Normalise(t2Inv * Mat3d.FromArray(result.Parameters) * t1);
        return IsFinite(refined) && result.FinalRms <= result.InitialRms ? refined : initial;
    }

    /// <summary>Applies the homography to a point.</summary>
    public static Vec2d Apply(Mat3d h, Vec2d p) => h.TransformPoint(p);

    /// <summary>The inverse map, scaled like any homography here.</summary>
    public static Mat3d Inverse(Mat3d h) => Normalise(h.Inverse());

    /// <summary>Heckbert's closed form: the homography taking the unit square's corners (0,0), (1,0), (1,1),
    /// (0,1) to p0, p1, p2, p3 — the corner pin. Affine when the quad is a parallelogram.</summary>
    public static Mat3d UnitSquareToQuad(Vec2d p0, Vec2d p1, Vec2d p2, Vec2d p3)
    {
        var sx = p0.X - p1.X + p2.X - p3.X;
        var sy = p0.Y - p1.Y + p2.Y - p3.Y;
        if (Math.Abs(sx) < 1e-15 && Math.Abs(sy) < 1e-15)
        {
            return new Mat3d(
                p1.X - p0.X, p2.X - p1.X, p0.X,
                p1.Y - p0.Y, p2.Y - p1.Y, p0.Y,
                0, 0, 1);
        }
        var dx1 = p1.X - p2.X;
        var dx2 = p3.X - p2.X;
        var dy1 = p1.Y - p2.Y;
        var dy2 = p3.Y - p2.Y;
        var den = dx1 * dy2 - dx2 * dy1;
        if (Math.Abs(den) < 1e-300)
            throw new ArgumentException("The quad is degenerate (three corners on a line).");
        var g = (sx * dy2 - sy * dx2) / den;
        var h = (dx1 * sy - dy1 * sx) / den;
        return new Mat3d(
            p1.X - p0.X + g * p1.X, p3.X - p0.X + h * p3.X, p0.X,
            p1.Y - p0.Y + g * p1.Y, p3.Y - p0.Y + h * p3.Y, p0.Y,
            g, h, 1);
    }

    /// <summary>The homography taking quad a's corners to quad b's (both in unit-square corner order).</summary>
    public static Mat3d QuadToQuad(ReadOnlySpan<Vec2d> a, ReadOnlySpan<Vec2d> b)
    {
        if (a.Length != 4 || b.Length != 4)
            throw new ArgumentException("A quad has four corners.");
        var sa = UnitSquareToQuad(a[0], a[1], a[2], a[3]);
        var sb = UnitSquareToQuad(b[0], b[1], b[2], b[3]);
        return Normalise(sb * sa.Inverse());
    }

    /// <summary>The forward transfer error |H·p − q| of one pair.</summary>
    public static double TransferError(Mat3d h, Vec2d from, Vec2d to) => Math.Sqrt(TransferError2(h, from, to));

    internal static double TransferError2(Mat3d h, Vec2d p, Vec2d q)
    {
        var w = h.M20 * p.X + h.M21 * p.Y + h.M22;
        if (Math.Abs(w) < 1e-300)
            return double.PositiveInfinity;
        var x = (h.M00 * p.X + h.M01 * p.Y + h.M02) / w - q.X;
        var y = (h.M10 * p.X + h.M11 * p.Y + h.M12) / w - q.Y;
        var e = x * x + y * y;
        return double.IsNaN(e) ? double.PositiveInfinity : e;
    }

    /// <summary>Scaled so H₂₂ = 1 when that element is not tiny, else to unit Frobenius norm; the sign is
    /// fixed so the largest-magnitude element is positive in the Frobenius case.</summary>
    internal static Mat3d Normalise(Mat3d h, bool frobenius = false)
    {
        if (!frobenius && Math.Abs(h.M22) > 1e-12 * Frob(h))
            return h * (1 / h.M22);
        var f = Frob(h);
        if (f < 1e-300)
            return h;
        var s = h.M22 < 0 ? -1 / f : 1 / f;
        return h * s;
    }

    /// <summary>True when any three of four points are (nearly) on one line — a sample that fixes no homography.</summary>
    internal static bool HasCollinearTriple(ReadOnlySpan<Vec2d> q)
    {
        var scale = 0.0;
        for (var i = 1; i < q.Length; i++)
            scale = Math.Max(scale, (q[i] - q[0]).LengthSquared);
        if (scale < 1e-300)
            return true;
        for (var a = 0; a < q.Length; a++)
            for (var b = a + 1; b < q.Length; b++)
                for (var c = b + 1; c < q.Length; c++)
                    if (Math.Abs(Vec2d.Cross(q[b] - q[a], q[c] - q[a])) < 1e-6 * scale)
                        return true;
        return false;
    }

    private static double Frob(Mat3d h) => Math.Sqrt(h.ToArray().Sum(v => v * v));

    private static bool IsFinite(Mat3d h) => h.ToArray().All(double.IsFinite);

    private static bool[] Classify(Mat3d h, Vec2d[] src, Vec2d[] dst, double t2)
    {
        var inl = new bool[src.Length];
        for (var i = 0; i < src.Length; i++)
            inl[i] = TransferError2(h, src[i], dst[i]) < t2;
        return inl;
    }

    private static (Vec2d[] From, Vec2d[] To) Select(Vec2d[] src, Vec2d[] dst, bool[] keep)
    {
        var count = keep.Count(b => b);
        var fs = new Vec2d[count];
        var fd = new Vec2d[count];
        var k = 0;
        for (var i = 0; i < src.Length; i++)
        {
            if (!keep[i])
                continue;
            fs[k] = src[i];
            fd[k] = dst[i];
            k++;
        }
        return (fs, fd);
    }

    private static double Rms(Mat3d h, Vec2d[] src, Vec2d[] dst, bool[] keep)
    {
        double s = 0;
        var c = 0;
        for (var i = 0; i < src.Length; i++)
        {
            if (!keep[i])
                continue;
            s += TransferError2(h, src[i], dst[i]);
            c++;
        }
        return c > 0 ? Math.Sqrt(s / c) : double.PositiveInfinity;
    }

    /// <summary>Residuals: forward (H·p − q) and backward (H⁻¹·q − p) for every pair, four per pair. The
    /// parameters are the nine entries of the conditioned homography (scale is free; the damping holds it).</summary>
    private sealed class SymmetricTransfer(Vec2d[] from, Vec2d[] to, Mat3d t1, Mat3d t2Inv) : ILeastSquaresProblem
    {
        public int ParameterCount => 9;
        public int ResidualCount => from.Length * 4;

        public void Residuals(ReadOnlySpan<double> parameters, Span<double> residuals)
        {
            var h = t2Inv * Mat3d.FromArray(parameters) * t1;
            if (!h.TryInverse(out var hi))
            {
                residuals.Fill(1e6);
                return;
            }
            for (var i = 0; i < from.Length; i++)
            {
                var f = h.TransformPoint(from[i]) - to[i];
                var b = hi.TransformPoint(to[i]) - from[i];
                residuals[4 * i] = Finite(f.X);
                residuals[4 * i + 1] = Finite(f.Y);
                residuals[4 * i + 2] = Finite(b.X);
                residuals[4 * i + 3] = Finite(b.Y);
            }
        }

        public bool Jacobian(ReadOnlySpan<double> parameters, DenseMatrix jacobian) => false;

        private static double Finite(double v) => double.IsFinite(v) ? v : 1e6;
    }
}
