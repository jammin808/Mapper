using System.Globalization;
using PlayMap.Core.Mapping;
using PlayMap.Core.Model;
using PlayMap.Core.Numerics;

namespace PlayMap.Core.Calibration;

/// <summary>What a pose solve may assume, as residual rows beside the pins': r_f = σ_px·(f − f₀)/σ_f for the focal length (pixels)
/// and r_C = σ_px·(C − C₀)/σ_C for the centre (metres, stage), each with a constant derivative. σ_px is the noise the pins' rows
/// are measured against (1 px for hand pins). <see cref="None"/> adds no row, so a solve without priors is unchanged.</summary>
public sealed record PosePriors(double? Focal, double FocalSigma, Vec3d? Centre, double CentreSigma, double PixelSigma)
{
    public static readonly PosePriors None = new(null, 0, null, 0, 1);

    public bool IsNone => Focal is null && Centre is null;

    public double FocalWeight => FocalSigma > 0 ? PixelSigma / FocalSigma : 0;

    public double CentreWeight => CentreSigma > 0 ? PixelSigma / CentreSigma : 0;

    public double FocalRow(double fx) => Focal is { } f ? (fx - f) * FocalWeight : 0;

    public Vec3d CentreRows(Vec3d centre) => Centre is { } c ? (centre - c) * CentreWeight : Vec3d.Zero;

    /// <summary>The same priors without the focal row (a solve that locks the focal has nothing to weigh it against).</summary>
    public PosePriors WithoutFocal => this with { Focal = null, FocalSigma = 0 };

    /// <summary>The default σ of a throw ratio prior when none is given (a datasheet's ±2 %).</summary>
    public const double DefaultThrowSigmaPct = 2;

    /// <summary>The default σ of a position prior when none is given (a total-station shot of the lens, ±5 cm).</summary>
    public const double DefaultPositionSigmaM = 0.05;

    /// <summary>A projector's priors in solver terms: f₀ = throw ratio × raster width, σ_f = f₀·pct/100; the position as given (a
    /// control point's is resolved to stage coordinates by the caller).</summary>
    public static PosePriors From(SolvePriors priors, Intrinsics k, Vec3d? position, double pixelSigma = 1)
    {
        ArgumentNullException.ThrowIfNull(priors);
        ArgumentNullException.ThrowIfNull(k);
        double? f0 = priors.ThrowRatio > 0 ? priors.ThrowRatio * k.Width : null;
        var pct = priors.ThrowRatioSigmaPct > 0 ? priors.ThrowRatioSigmaPct : DefaultThrowSigmaPct;
        var sigmaC = priors.PositionSigmaM > 0 ? priors.PositionSigmaM : DefaultPositionSigmaM;
        return new PosePriors(f0, f0 is { } f ? f * pct / 100 : 0, position, position is null ? 0 : sigmaC, pixelSigma);
    }
}

/// <summary>A long-throw solve: the solution, whether the focal was locked from the lens because the pins could not carry it, the
/// uncertainty (null when the pins cannot carry one), the weak direction in words and the solve's words.</summary>
public sealed record LongThrowResult(PoseSolution Solution, bool FocalLocked, SolveUncertainty? Uncertainty, string Weak, string Words,
    SolveLocks LocksUsed);

/// <summary>Solves a far projector with what the pins cannot tell it. First the pins alone (the position prior as rows); if the
/// solve's information cannot separate zoom from distance — the weakest direction of its normalised information matrix lies
/// mostly along the focal and the along-axis centre (combined share above 0.7) and the implied focal σ is above 10 % — the focal
/// is locked at the lens prior when there is one ("zoom locked from the lens"), or the solve is kept and named weak when there is
/// not. Otherwise the focal prior joins as a row. The uncertainty is the solve's own, with the priors' information added.</summary>
public static class LongThrowSolve
{
    /// <summary>The share of the weakest direction on focal and along-axis centre above which zoom and distance are one direction.</summary>
    public const double WeakShare = 0.7;

    /// <summary>The implied focal σ (%) above which the pins cannot carry the focal.</summary>
    public const double WeakFocalPct = 10;

    /// <summary>The residual floor of the uncertainty estimate for hand pins (px): an exact fit still has picking noise.</summary>
    public const double PinFloorPx = 0.5;

    public const string LockedWords = "zoom locked from the lens: the pins cannot tell zoom from distance";

    public const string WeakWords = "the pins cannot tell zoom from distance — set the lens (PROJECTOR PRIOR … THROW) or add pins at different depths or a night scan";

    /// <summary>Solves a projector from its enabled pins with its locks and its own priors (<see cref="Projector.Priors"/>, the
    /// position already in stage coordinates). Residuals and flags follow the projector's pin order, as <see cref="PoseSolver"/>'s.</summary>
    public static LongThrowResult Solve(Projector projector, PoseSolveOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(projector);
        options ??= PoseSolveOptions.Default;
        var pins = projector.Pins;
        var enabled = Enumerable.Range(0, pins.Length).Where(i => pins[i].Enabled).ToArray();
        var pairs = enabled.Select(i => new Correspondence(pins[i].World, pins[i].Pixel)).ToArray();
        var labels = enabled.Select(i => (i + 1).ToString(CultureInfo.InvariantCulture)).ToArray();
        var priors = PosePriors.From(projector.Priors, projector.Intrinsics, projector.Priors.Position);
        var r = Solve(pairs, projector.Intrinsics, projector.Lens, options with
        {
            Locks = projector.Locks,
            Labels = labels,
            InitialPose = options.InitialPose ?? (projector.SolveResidualPx > 0 ? projector.Pose : null),
        }, priors);
        var s = r.Solution;
        var residuals = new double[pins.Length];
        var inliers = new bool[pins.Length];
        for (var i = 0; i < pins.Length; i++)
            residuals[i] = PoseSolver.ReprojectionError(new Correspondence(pins[i].World, pins[i].Pixel), s.Intrinsics, s.Lens, s.Pose);
        for (var k = 0; k < enabled.Length; k++)
            inliers[enabled[k]] = k < s.Inliers.Count && s.Inliers[k];
        return r with { Solution = s with { ResidualsPx = residuals, Inliers = inliers } };
    }

    public static LongThrowResult Solve(IReadOnlyList<Correspondence> pairs, Intrinsics prior, LensDistortion lens, PoseSolveOptions options, PosePriors priors)
    {
        ArgumentNullException.ThrowIfNull(pairs);
        ArgumentNullException.ThrowIfNull(prior);
        ArgumentNullException.ThrowIfNull(lens);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(priors);
        // A lens prior is also where the closed forms and a locked focal start.
        var start = priors.Focal is { } f0 ? prior with { Fx = f0, Fy = f0 * (prior.Fx > 0 ? prior.Fy / prior.Fx : 1) } : prior;
        var locks = options.Locks;
        if ((locks & SolveLocks.Focal) != 0)
        {
            var held = PoseSolver.Solve(pairs, start, lens, options with { Priors = priors.WithoutFocal });
            return Finish(held, pairs, locks, priors.WithoutFocal, false, "", "");
        }
        var first = PoseSolver.Solve(pairs, start, lens, options with { Priors = priors.WithoutFocal });
        if (!double.IsFinite(first.RmsPx))
            return new LongThrowResult(first, false, null, "", first.Words, locks);
        var weak = IsZoomDistanceWeak(first, pairs, locks);
        if (weak && priors.Focal is not null)
        {
            var lockedLocks = locks | SolveLocks.Focal;
            var held = PoseSolver.Solve(pairs, start, lens, options with { Locks = lockedLocks, Priors = priors.WithoutFocal });
            return Finish(held, pairs, lockedLocks, priors.WithoutFocal, true, "", LockedWords);
        }
        if (weak)
            return Finish(first, pairs, locks, priors.WithoutFocal, false, "zoom↔distance", WeakWords);
        if (priors.Focal is null)
            return Finish(first, pairs, locks, priors, false, "", "");
        var withPrior = PoseSolver.Solve(pairs, start, lens, options with { Priors = priors });
        return Finish(withPrior, pairs, locks, priors, false, "", "");
    }

    private static LongThrowResult Finish(PoseSolution s, IReadOnlyList<Correspondence> pairs, SolveLocks locks, PosePriors priors, bool locked,
        string weak, string words)
    {
        var inliers = Inliers(s, pairs);
        var u = double.IsFinite(s.RmsPx) ? SolveUncertaintyAnalysis.Of(s.Intrinsics, s.Lens, s.Pose, inliers, locks, priors) : null;
        if (u is not null && weak.Length > 0)
            u = u with { Weak = weak };
        else if (u is not null && locked)
            u = u with { Weak = "" };
        var all = words.Length > 0 ? s.Words + "; " + words : s.Words;
        return new LongThrowResult(s with { Words = all }, locked, u, u?.Weak ?? weak, all, locks);
    }

    private static List<Correspondence> Inliers(PoseSolution s, IReadOnlyList<Correspondence> pairs) =>
        [.. Enumerable.Range(0, pairs.Count).Where(i => i < s.Inliers.Count && s.Inliers[i]).Select(i => pairs[i])];

    /// <summary>True when the solve's weakest direction is zoom against distance: the smallest eigenvector of the normalised
    /// information matrix over rotation, centre (device axes), focal and the unlocked shift has its focal and along-axis components
    /// together above <see cref="WeakShare"/>, and the focal's σ (σ̂ from the inlier residuals, floored at <see cref="PinFloorPx"/>)
    /// is above <see cref="WeakFocalPct"/> % — or the matrix is singular there.</summary>
    public static bool IsZoomDistanceWeak(PoseSolution s, IReadOnlyList<Correspondence> pairs, SolveLocks locks)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(pairs);
        var inliers = Inliers(s, pairs);
        var shift = (locks & SolveLocks.Shift) == 0;
        var m = shift ? 9 : 7;
        var n = inliers.Count;
        if (2 * n <= m)
            return true;
        var k = s.Intrinsics;
        var pose = s.Pose;
        var c2w = pose.CameraToWorld;
        var j = new DenseMatrix(2 * n, m);
        Span<double> d = stackalloc double[9];
        Vec2d? Project(Vec3d world, ReadOnlySpan<double> delta)
        {
            var rot = Mat3d.FromRotationVector(new Vec3d(delta[0], delta[1], delta[2]));
            var centre = pose.Position + c2w * new Vec3d(delta[3], delta[4], delta[5]);
            var cam = rot.Transposed() * (c2w.Transposed() * (world - centre));
            var fx = k.Fx + delta[6];
            var intr = k with { Fx = fx, Fy = k.Fy * fx / k.Fx, Cx = k.Cx + delta[7], Cy = k.Cy + delta[8] };
            return Geometry.CameraModel.ProjectCamera(cam, intr, s.Lens);
        }
        for (var i = 0; i < n; i++)
        {
            var dist = (inliers[i].World - pose.Position).Length;
            for (var p = 0; p < m; p++)
            {
                var h = p < 3 ? 1e-6 : p < 6 ? 1e-6 * Math.Max(1, dist) : 1e-3;
                d.Clear();
                d[p] = h;
                var plus = Project(inliers[i].World, d);
                d[p] = -h;
                var minus = Project(inliers[i].World, d);
                if (plus is not { } a || minus is not { } b)
                    return true;
                j[2 * i, p] = (a.X - b.X) / (2 * h);
                j[2 * i + 1, p] = (a.Y - b.Y) / (2 * h);
            }
        }
        var info = j.NormalMatrix();
        var scale = new double[m];
        for (var c = 0; c < m; c++)
            scale[c] = info[c, c] > 0 ? 1 / Math.Sqrt(info[c, c]) : 0;
        var normed = new DenseMatrix(m, m);
        for (var r = 0; r < m; r++)
            for (var c = 0; c < m; c++)
                normed[r, c] = info[r, c] * scale[r] * scale[c];
        var (values, vectors) = SymmetricEigen.Decompose(normed);
        var share = Math.Sqrt(vectors[6, 0] * vectors[6, 0] + vectors[5, 0] * vectors[5, 0]);
        if (share <= WeakShare)
            return false;
        var rmsSq = 0.0;
        foreach (var pair in inliers)
            rmsSq += Math.Pow(PoseSolver.ReprojectionError(pair, k, s.Lens, pose), 2);
        var sigma = Math.Max(Math.Sqrt(rmsSq / Math.Max(1, 2 * n - m)), PinFloorPx);
        var sff = 0.0;
        for (var e = 0; e < m; e++)
            sff += vectors[6, e] * vectors[6, e] / Math.Max(values[e], 1e-300);
        var focalPct = sigma * Math.Sqrt(sff) * scale[6] / k.Fx * 100;
        return !double.IsFinite(focalPct) || focalPct > WeakFocalPct;
    }

    /// <summary>The solve's uncertainty in words for a reply: the wall first, then the metres.</summary>
    public static string Words(SolveUncertainty u)
    {
        ArgumentNullException.ThrowIfNull(u);
        return string.Create(CultureInfo.InvariantCulture,
            $"on the wall ±{u.WallPx:0.0} px · distance ±{u.AxisM:0.0#} m · zoom ±{u.FocalPct:0.0#} %")
            + (u.Weak.Length > 0 ? $" ({u.Weak})" : "");
    }
}
