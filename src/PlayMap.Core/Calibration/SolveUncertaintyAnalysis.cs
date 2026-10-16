using System.Globalization;
using PlayMap.Core.Mapping;
using PlayMap.Core.Model;
using PlayMap.Core.Numerics;

namespace PlayMap.Core.Calibration;

/// <summary>A solve's uncertainty from its own information matrix, Σ = σ̂²(JᵀJ)⁻¹, with J the reprojection's Jacobian at the
/// inlier pins over the parameters the solve was free to move (the pose in the device's own axes, and the focal and the lens
/// shift unless locked) and σ̂² the residuals' variance per coordinate. Propagated to a grid over the raster at the pins'
/// median depth, it says how far the picture could be off on the wall (p95 and worst, px), along the axis and across it (m),
/// in focal (%), how ill-conditioned the solve is, and its weakest direction in words. Lens distortion is held at the solve's.</summary>
public static class SolveUncertaintyAnalysis
{
    /// <summary>The raster samples the wall error is taken over (at most this many).</summary>
    public const int MaxSamples = 2000;

    // Parameter order: rotation (x, y, z, device axes), translation (x, y, z, device axes), focal, cx, cy.
    private const int Rx = 0, Tz = 5, F = 6, Cx = 7, Cy = 8;

    /// <summary>The uncertainty of a solved device whose inlier pairs are given, or null when the pairs cannot carry it (fewer
    /// residuals than parameters, or a point behind the device).</summary>
    public static SolveUncertainty? Of(Intrinsics k, LensDistortion lens, Pose pose, IReadOnlyList<Correspondence> inliers, SolveLocks locks,
        PosePriors? priors = null)
    {
        return Analyse(k, lens, pose, inliers, locks, priors)?.Uncertainty;
    }

    /// <summary>The uncertainty and every raster sample's predicted error (px), for a confidence share.</summary>
    public static (SolveUncertainty Uncertainty, double[] SamplesPx)? Analyse(Intrinsics k, LensDistortion lens, Pose pose,
        IReadOnlyList<Correspondence> inliers, SolveLocks locks, PosePriors? priors = null)
    {
        ArgumentNullException.ThrowIfNull(k);
        ArgumentNullException.ThrowIfNull(lens);
        ArgumentNullException.ThrowIfNull(pose);
        ArgumentNullException.ThrowIfNull(inliers);
        var free = new List<int> { 0, 1, 2, 3, 4, 5 };
        if ((locks & SolveLocks.Focal) == 0)
            free.Add(F);
        if ((locks & SolveLocks.Shift) == 0)
        {
            free.Add(Cx);
            free.Add(Cy);
        }
        var n = inliers.Count;
        var m = free.Count;
        if (2 * n <= m)
            return null;
        var model = new Model(k, lens, pose);
        var j = new DenseMatrix(2 * n, m);
        var ssr = 0.0;
        Span<double> row = stackalloc double[2 * 9];
        for (var i = 0; i < n; i++)
        {
            if (!model.Derivatives(inliers[i].World, row, out var at))
                return null;
            var d = at - inliers[i].Pixel;
            ssr += d.X * d.X + d.Y * d.Y;
            for (var c = 0; c < m; c++)
            {
                j[2 * i, c] = row[free[c]];
                j[2 * i + 1, c] = row[9 + free[c]];
            }
        }
        var sigma2 = ssr / (2 * n - m);
        // Columns scaled to unit norm, so the condition and the weakest direction do not depend on the parameters' units.
        var info = j.NormalMatrix();
        AddPriors(info, free, priors);
        var scale = new double[m];
        for (var c = 0; c < m; c++)
            scale[c] = info[c, c] > 0 ? 1 / Math.Sqrt(info[c, c]) : 0;
        var normed = new DenseMatrix(m, m);
        for (var r = 0; r < m; r++)
            for (var c = 0; c < m; c++)
                normed[r, c] = info[r, c] * scale[r] * scale[c];
        var (values, vectors) = SymmetricEigen.Decompose(normed);
        var lo = Math.Max(values[0], 1e-15);
        var condition = values[^1] / lo;
        // Σ = σ̂² · S · N⁻¹ · S, with N⁻¹ from the eigen-decomposition.
        var cov = new double[9, 9];
        for (var r = 0; r < m; r++)
            for (var c = 0; c < m; c++)
            {
                var s = 0.0;
                for (var e = 0; e < m; e++)
                    s += vectors[r, e] * vectors[c, e] / Math.Max(values[e], 1e-15);
                cov[free[r], free[c]] = sigma2 * s * scale[r] * scale[c];
            }

        // The raster at the pins' median depth: where the picture lands if the stage were there.
        var depths = new double[n];
        for (var i = 0; i < n; i++)
            depths[i] = pose.WorldToCamera(inliers[i].World).Z;
        var depth = SolverMath.Median(depths);
        const int Cols = 40, Rows = 24;
        var samples = new double[Cols * Rows];
        Span<double> jx = stackalloc double[2 * 9];
        var count = 0;
        for (var y = 0; y < Rows; y++)
            for (var x = 0; x < Cols; x++)
            {
                var px = new Vec2d((x + 0.5) / Cols * k.Width, (y + 0.5) / Rows * k.Height);
                var dir = Geometry.CameraModel.PixelRayCamera(px, k, lens);
                var world = pose.CameraToWorldPoint(dir * (depth / dir.Z));
                if (!model.Derivatives(world, jx, out _))
                    continue;
                // The 2 × 2 covariance J Σ Jᵀ and its larger eigenvalue.
                double a = 0, b = 0, d = 0;
                for (var p = 0; p < 9; p++)
                    for (var q = 0; q < 9; q++)
                    {
                        var s = cov[p, q];
                        if (s == 0)
                            continue;
                        a += jx[p] * s * jx[q];
                        b += jx[p] * s * jx[9 + q];
                        d += jx[9 + p] * s * jx[9 + q];
                    }
                var half = (a + d) / 2;
                var disc = Math.Sqrt(Math.Max(0, (a - d) * (a - d) / 4 + b * b));
                samples[count++] = Math.Sqrt(Math.Max(0, half + disc));
            }
        if (count == 0)
            return null;
        var used = samples.AsSpan(0, count).ToArray();
        var wall = SolverMath.Quantile(used, 0.95);
        var worst = used.Max();
        var weakest = new double[m];
        for (var r = 0; r < m; r++)
            weakest[r] = vectors[r, 0];
        var focalPct = cov[F, F] > 0 ? Math.Sqrt(cov[F, F]) / k.Fx * 100 : 0;
        var u = new SolveUncertainty(wall, worst, focalPct, Math.Sqrt(cov[Tz, Tz]), Math.Sqrt(cov[3, 3] + cov[4, 4]), condition,
            Weak(free, weakest, condition));
        return (u, used);
    }

    /// <summary>The priors' information beside the pins' (wp2-facade): a focal prior adds (σ_px/σ_f)² on the focal, a position prior
    /// (σ_px/σ_C)² on each centre axis (isotropic, so the device's axes serve as well as the stage's).</summary>
    private static void AddPriors(DenseMatrix info, List<int> free, PosePriors? priors)
    {
        if (priors is null || priors.IsNone)
            return;
        var f = free.IndexOf(F);
        if (priors.Focal is not null && f >= 0)
            info[f, f] += priors.FocalWeight * priors.FocalWeight;
        if (priors.Centre is not null)
            for (var t = 3; t < 6; t++)
                info[t, t] += priors.CentreWeight * priors.CentreWeight;
    }

    /// <summary>The weakest direction in words when the solve is ill-conditioned there ("" when it is not).</summary>
    private static string Weak(List<int> free, double[] v, double condition)
    {
        if (condition < 1e4)
            return "";
        double At(int p)
        {
            var i = free.IndexOf(p);
            return i < 0 ? 0 : Math.Abs(v[i]);
        }
        if (At(F) > 0.3 && At(Tz) > 0.3)
            return "zoom↔distance";
        if (At(Cx) > 0.3 && At(Rx + 1) > 0.3)
            return "shift↔pan";
        if (At(Cy) > 0.3 && At(Rx) > 0.3)
            return "shift↔tilt";
        var top = 0;
        for (var i = 1; i < v.Length; i++)
            if (Math.Abs(v[i]) > Math.Abs(v[top]))
                top = i;
        return free[top] switch
        {
            < 3 => "rotation",
            < 6 => "position",
            F => "zoom",
            _ => "lens shift",
        };
    }

    /// <summary>A device at a pose with small moves in its own axes: the pixel of a world point and its derivatives by the nine
    /// parameters (row 0: x, row 1: y), by central differences.</summary>
    private sealed class Model(Intrinsics k, LensDistortion lens, Pose pose)
    {
        private readonly Mat3d _c2w = pose.CameraToWorld;

        private Vec2d? Project(Vec3d world, ReadOnlySpan<double> d)
        {
            var rot = Mat3d.FromRotationVector(new Vec3d(d[0], d[1], d[2]));
            var centre = pose.Position + _c2w * new Vec3d(d[3], d[4], d[5]);
            var cam = rot.Transposed() * (_c2w.Transposed() * (world - centre));
            var fx = k.Fx + d[F];
            var intr = k with { Fx = fx, Fy = k.Fy * fx / k.Fx, Cx = k.Cx + d[Cx], Cy = k.Cy + d[Cy] };
            return Geometry.CameraModel.ProjectCamera(cam, intr, lens);
        }

        public bool Derivatives(Vec3d world, Span<double> rows, out Vec2d at)
        {
            Span<double> d = stackalloc double[9];
            if (Project(world, d) is not { } p0)
            {
                at = default;
                return false;
            }
            at = p0;
            var dist = (world - pose.Position).Length;
            for (var i = 0; i < 9; i++)
            {
                var h = i < 3 ? 1e-6 : i < 6 ? 1e-6 * Math.Max(1, dist) : 1e-3;
                d[i] = h;
                var plus = Project(world, d);
                d[i] = -h;
                var minus = Project(world, d);
                d[i] = 0;
                if (plus is not { } a || minus is not { } b)
                    return false;
                rows[i] = (a.X - b.X) / (2 * h);
                rows[9 + i] = (a.Y - b.Y) / (2 * h);
            }
            return true;
        }
    }

    /// <summary>The uncertainty in words: "could be 1.8 px off at the edges (zoom↔distance)".</summary>
    public static string Words(SolveUncertainty u)
    {
        ArgumentNullException.ThrowIfNull(u);
        return string.Create(CultureInfo.InvariantCulture, $"could be {u.WallPx:0.0} px off (p95), {u.ProudPx:0.0} px at worst")
            + (u.Weak.Length > 0 ? $"; weakest: {u.Weak}" : "");
    }
}
