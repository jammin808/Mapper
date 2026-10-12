using System.Globalization;
using System.Text;
using PlayMap.Core.Geometry;
using PlayMap.Core.Mapping;
using PlayMap.Core.Model;
using PlayMap.Core.Numerics;

namespace PlayMap.Core.Calibration;

/// <summary>How a pose solve runs: what it may not change, how far off a pair may be before it is called a
/// bad pick, the seed of its sampling, and how its words name the pairs.</summary>
public sealed record PoseSolveOptions
{
    public static readonly PoseSolveOptions Default = new();

    /// <summary>Parameters kept at the prior's values exactly.</summary>
    public SolveLocks Locks { get; init; }

    /// <summary>Fit fx and fy separately; otherwise one focal length with the prior's aspect.</summary>
    public bool FreeAspect { get; init; }

    /// <summary>A pair whose reprojection error exceeds this (pixels) is an outlier: left out of the fit and
    /// named in the words.</summary>
    public double OutlierThresholdPx { get; init; } = 6;

    /// <summary>Find the consensus by RANSAC before fitting (off: every pair is trusted).</summary>
    public bool Robust { get; init; } = true;

    public ulong Seed { get; init; } = 1;

    public int MaxRansacIterations { get; init; } = 1000;

    /// <summary>Dense sets are fitted on a seeded subset of at most this many pairs; residuals and inlier
    /// flags still cover every pair.</summary>
    public int MaxFitPairs { get; init; } = 4000;

    /// <summary>A pose to also start from (the projector's current pose while a pin is dragged).</summary>
    public Pose? InitialPose { get; init; }

    /// <summary>What a pair is called in the words ("pin", "point").</summary>
    public string Noun { get; init; } = "pin";

    /// <summary>Names for the pairs in the words, in input order; default 1, 2, 3…</summary>
    public IReadOnlyList<string>? Labels { get; init; }
}

/// <summary>Solves a camera's or projector's optics and pose from world↔pixel pairs — pins picked by hand or
/// thousands from a structured-light bridge. Closed forms start it (the DLT and RQ for six or more pairs off
/// one plane; a plane homography, with Zhang's constraint for an unknown focal length, for pairs on one
/// plane), RANSAC flags bad picks, and Levenberg–Marquardt refines pose, focal, lens shift and k1/k2 — each
/// only when its lock is off and the pairs can carry it.</summary>
public static class PoseSolver
{
    /// <summary>Points flatter than this (smallest over middle principal spread) count as one plane: the
    /// DLT is ill-conditioned there and lens shift cannot be separated from pose.</summary>
    internal const double CoplanarFlatness = 0.05;

    private const double NearPlanarFlatness = 0.2;

    /// <summary>Solves a projector from its enabled pins with its locks. Residuals and flags follow the
    /// projector's pin order; a disabled pin gets its residual but is never an inlier.</summary>
    public static PoseSolution Solve(Projector projector, PoseSolveOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(projector);
        options ??= PoseSolveOptions.Default;
        var pins = projector.Pins;
        var enabled = Enumerable.Range(0, pins.Length).Where(i => pins[i].Enabled).ToArray();
        var pairs = enabled.Select(i => new Correspondence(pins[i].World, pins[i].Pixel)).ToArray();
        var labels = enabled.Select(i => (i + 1).ToString(CultureInfo.InvariantCulture)).ToArray();
        var solved = Solve(pairs, projector.Intrinsics, projector.Lens, options with
        {
            Locks = projector.Locks,
            Labels = labels,
            InitialPose = options.InitialPose ?? (projector.SolveResidualPx > 0 ? projector.Pose : null),
        });
        var residuals = new double[pins.Length];
        var inliers = new bool[pins.Length];
        for (var i = 0; i < pins.Length; i++)
            residuals[i] = ReprojectionError(new Correspondence(pins[i].World, pins[i].Pixel), solved.Intrinsics, solved.Lens, solved.Pose);
        for (var k = 0; k < enabled.Length; k++)
            inliers[enabled[k]] = solved.Inliers[k];
        return solved with { ResidualsPx = residuals, Inliers = inliers };
    }

    /// <summary>Solves optics and pose from the pairs, starting from the prior optics (a projector's
    /// datasheet throw ratio and lens shift, a camera's field of view).</summary>
    public static PoseSolution Solve(IReadOnlyList<Correspondence> pairs, Intrinsics prior, LensDistortion priorLens,
        PoseSolveOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(pairs);
        ArgumentNullException.ThrowIfNull(prior);
        ArgumentNullException.ThrowIfNull(priorLens);
        options ??= PoseSolveOptions.Default;
        var all = pairs.ToArray();
        var n = all.Length;
        var world = all.Select(c => c.World).ToArray();
        var pixels = all.Select(c => c.Pixel).ToArray();
        var noun = options.Noun;
        var flatness = n >= 3 ? SolverMath.FitPlane(world).Flatness : 0;
        var coplanar = flatness < CoplanarFlatness;
        if (n < 4)
        {
            return Failure(all, prior, priorLens, options, string.Create(CultureInfo.InvariantCulture,
                $"need at least 6 {noun}s off one plane, or 4 on one plane: have {n}"));
        }

        // The closed forms fit ideal pinhole pixels: the observations with the prior lens taken out.
        var ideal = pixels.Select(p => IdealPixel(p, prior, priorLens)).ToArray();
        var notes = new List<string>();
        var layout = Layout.For(options, prior, coplanar, n, notes, noun);

        // The fit set: all pairs, or a seeded subset of a dense set; the consensus is found within it.
        var fit = Ransac.Subset(n, Math.Max(16, options.MaxFitPairs), options.Seed);
        var use = fit;
        if (options.Robust && n >= (coplanar ? 6 : 8))
        {
            var consensus = Consensus(world, ideal, fit, prior, layout, coplanar, options);
            if (consensus.Length >= (coplanar ? 4 : 6))
                use = consensus;
        }

        var best = BestStart(world, pixels, ideal, use, prior, priorLens, layout, coplanar, flatness, options);
        if (best is null)
            return Failure(all, prior, priorLens, options, "no closed form fits these " + noun + "s: spread them out, add more off one plane");
        var model = best.Value;

        // Re-read the consensus with the refined model until it settles (at most three rounds).
        var t = options.OutlierThresholdPx;
        if (options.Robust)
        {
            for (var round = 0; round < 3; round++)
            {
                var next = fit.Where(i => Error(model, world[i], pixels[i]) <= t).ToArray();
                if (next.Length < (coplanar ? 4 : 6) || next.AsSpan().SequenceEqual(use))
                    break;
                use = next;
                model = Refine(world, pixels, use, layout, model).Model;
            }
        }

        var residuals = new double[n];
        var inliers = new bool[n];
        double sum = 0;
        var count = 0;
        for (var i = 0; i < n; i++)
        {
            residuals[i] = Error(model, world[i], pixels[i]);
            inliers[i] = !options.Robust || residuals[i] <= t;
            if (inliers[i])
            {
                sum += residuals[i] * residuals[i];
                count++;
            }
        }
        var rms = count > 0 ? Math.Sqrt(sum / count) : double.PositiveInfinity;
        var intr = new Intrinsics(model.Fx, model.Fy, model.Cx, model.Cy, prior.Width, prior.Height);
        var pose = new Pose(model.Centre, model.WorldToCamera.Transposed().ToRotationVector());
        var words = Words(rms, count, n, residuals, inliers, options, notes, prior, intr, layout);
        return new PoseSolution(intr, model.Lens, pose, rms, residuals, inliers, words);
    }

    /// <summary>The reprojection error of one pair under the given optics and pose, in pixels (infinity when
    /// the point is behind the device).</summary>
    public static double ReprojectionError(Correspondence pair, Intrinsics k, LensDistortion lens, Pose pose)
    {
        var p = CameraModel.Project(pair.World, k, lens, pose);
        return p is { } q ? Vec2d.Distance(q, pair.Pixel) : double.PositiveInfinity;
    }

    // ---- the model and its parameters ----

    /// <summary>One candidate: world→camera rotation, centre, intrinsics, lens.</summary>
    internal readonly record struct Model(Mat3d WorldToCamera, Vec3d Centre, double Fx, double Fy, double Cx, double Cy, LensDistortion Lens)
    {
        public Vec2d? Project(Vec3d world)
        {
            var x = WorldToCamera * (world - Centre);
            if (x.Z <= 1e-12)
                return null;
            var d = CameraModel.Distort(new Vec2d(x.X / x.Z, x.Y / x.Z), Lens);
            return new Vec2d(Fx * d.X + Cx, Fy * d.Y + Cy);
        }
    }

    private static double Error(Model m, Vec3d world, Vec2d pixel) =>
        m.Project(world) is { } p ? Vec2d.Distance(p, pixel) : double.PositiveInfinity;

    /// <summary>Which parameters the refinement moves, after the locks and what the pairs can carry.</summary>
    internal sealed class Layout
    {
        public bool Focal { get; private init; }
        public bool FreeAspect { get; private init; }
        public bool Shift { get; private init; }
        public bool Distortion { get; private init; }
        public double Aspect { get; private init; }

        public int Count => 6 + (Focal ? (FreeAspect ? 2 : 1) : 0) + (Shift ? 2 : 0) + (Distortion ? 2 : 0);

        public static Layout For(PoseSolveOptions o, Intrinsics prior, bool coplanar, int n, List<string> notes, string noun)
        {
            var focal = !o.Locks.HasFlag(SolveLocks.Focal);
            var shift = !o.Locks.HasFlag(SolveLocks.Shift);
            var dist = !o.Locks.HasFlag(SolveLocks.Distortion);
            if (coplanar && shift)
            {
                shift = false;
                notes.Add("lens shift held: every " + noun + " is on one plane");
            }
            if (dist && (coplanar || n < 10))
            {
                dist = false;
                notes.Add(coplanar ? "distortion held: one plane cannot separate it from focal length"
                    : "distortion held: fit it with 10 or more " + noun + "s");
            }
            // Two residuals a pair; keep a margin of two over the unknowns, dropping the least-determined first.
            var layout = new Layout
            {
                Focal = focal,
                FreeAspect = focal && o.FreeAspect,
                Shift = shift,
                Distortion = dist,
                Aspect = prior.Fx > 0 ? prior.Fy / prior.Fx : 1,
            };
            if (2 * n < layout.Count + 2 && layout.FreeAspect)
                layout = layout.With(freeAspect: false);
            if (2 * n < layout.Count + 2 && layout.Shift)
            {
                layout = layout.With(shift: false);
                notes.Add("lens shift held: too few " + noun + "s");
            }
            if (2 * n < layout.Count + 2 && layout.Focal)
            {
                layout = layout.With(focal: false);
                notes.Add("focal length held: too few " + noun + "s");
            }
            return layout;
        }

        private Layout With(bool? focal = null, bool? freeAspect = null, bool? shift = null) => new()
        {
            Focal = focal ?? Focal,
            FreeAspect = (freeAspect ?? FreeAspect) && (focal ?? Focal),
            Shift = shift ?? Shift,
            Distortion = Distortion,
            Aspect = Aspect,
        };

        public double[] Pack(Model m)
        {
            var p = new double[Count];
            p[3] = m.Centre.X;
            p[4] = m.Centre.Y;
            p[5] = m.Centre.Z;
            var k = 6;
            if (Focal)
            {
                if (FreeAspect)
                {
                    p[k++] = m.Fx;
                    p[k++] = m.Fy;
                }
                else
                {
                    p[k++] = m.Fx;
                }
            }
            if (Shift)
            {
                p[k++] = m.Cx;
                p[k++] = m.Cy;
            }
            if (Distortion)
            {
                p[k++] = m.Lens.K1;
                p[k] = m.Lens.K2;
            }
            return p;
        }

        /// <summary>The model at parameters p: the rotation is a small rotation vector on top of the
        /// start's (no singularity anywhere near), the rest replace the start's values.</summary>
        public Model Unpack(ReadOnlySpan<double> p, Model start)
        {
            var r = Mat3d.FromRotationVector(new Vec3d(p[0], p[1], p[2])) * start.WorldToCamera;
            var c = new Vec3d(p[3], p[4], p[5]);
            double fx = start.Fx, fy = start.Fy, cx = start.Cx, cy = start.Cy;
            var lens = start.Lens;
            var k = 6;
            if (Focal)
            {
                if (FreeAspect)
                {
                    fx = p[k++];
                    fy = p[k++];
                }
                else
                {
                    fx = p[k++];
                    fy = fx * Aspect;
                }
            }
            if (Shift)
            {
                cx = p[k++];
                cy = p[k++];
            }
            if (Distortion)
            {
                lens = lens with { K1 = p[k], K2 = p[k + 1] };
            }
            return new Model(r, c, fx, fy, cx, cy, lens);
        }
    }

    /// <summary>Reprojection residuals over the chosen pairs.</summary>
    private sealed class Problem(Vec3d[] world, Vec2d[] pixels, int[] use, Layout layout, Model start) : ILeastSquaresProblem
    {
        public int ParameterCount => layout.Count;
        public int ResidualCount => use.Length * 2;

        public void Residuals(ReadOnlySpan<double> parameters, Span<double> residuals)
        {
            var m = layout.Unpack(parameters, start);
            for (var k = 0; k < use.Length; k++)
            {
                var i = use[k];
                if (m.Project(world[i]) is { } p)
                {
                    residuals[2 * k] = p.X - pixels[i].X;
                    residuals[2 * k + 1] = p.Y - pixels[i].Y;
                }
                else
                {
                    // Behind the device: a cost no descent step accepts.
                    residuals[2 * k] = 1e4;
                    residuals[2 * k + 1] = 1e4;
                }
            }
        }

        public bool Jacobian(ReadOnlySpan<double> parameters, DenseMatrix jacobian) => false;
    }

    private static (Model Model, double Cost) Refine(Vec3d[] world, Vec2d[] pixels, int[] use, Layout layout, Model start)
    {
        var problem = new Problem(world, pixels, use, layout, start);
        var result = LevenbergMarquardt.Solve(problem, layout.Pack(start), maxIterations: 100, tolerance: 1e-12);
        var model = layout.Unpack(result.Parameters, start);
        var cost = result.FinalRms * result.FinalRms * problem.ResidualCount;
        return (model with { WorldToCamera = model.WorldToCamera.NearestRotation() }, cost);
    }

    // ---- starts ----

    private static Model? BestStart(Vec3d[] world, Vec2d[] pixels, Vec2d[] ideal, int[] use, Intrinsics prior, LensDistortion lens,
        Layout layout, bool coplanar, double flatness, PoseSolveOptions o)
    {
        var w = use.Select(i => world[i]).ToArray();
        var q = use.Select(i => ideal[i]).ToArray();
        var starts = new List<Model>();
        if (!coplanar && use.Length >= 6 && CameraDlt.Solve(w, q) is { } p)
        {
            if (CameraDlt.Decompose(p) is { } dec)
            {
                var fx = prior.Fx;
                var fy = prior.Fy;
                if (layout.Focal)
                {
                    if (layout.FreeAspect)
                    {
                        fx = dec.Fx;
                        fy = dec.Fy;
                    }
                    else
                    {
                        fx = 0.5 * (dec.Fx + dec.Fy / layout.Aspect);
                        fy = fx * layout.Aspect;
                    }
                }
                var cx = layout.Shift ? dec.Cx : prior.Cx;
                var cy = layout.Shift ? dec.Cy : prior.Cy;
                starts.Add(new Model(dec.WorldToCamera, dec.Centre, fx, fy, cx, cy, lens));
            }
            if (CameraDlt.PoseWithKnownIntrinsics(p, prior) is { } kp)
                starts.Add(FromPose(kp, prior, lens));
        }
        if (coplanar || flatness < NearPlanarFlatness || use.Length < 6)
        {
            if (PlaneStart(w, q, prior, lens, layout) is { } ps)
                starts.Add(ps);
        }
        if (o.InitialPose is { } init)
            starts.Add(FromPose(init, prior, lens));

        Model? best = null;
        var bestCost = double.PositiveInfinity;
        foreach (var s in starts)
        {
            // A start with most points behind it is the mirror solution: skip it.
            var front = w.Count(x => (s.WorldToCamera * (x - s.Centre)).Z > 0);
            if (front * 2 < w.Length)
                continue;
            var (m, cost) = Refine(world, pixels, use, layout, s);
            if (cost < bestCost)
            {
                bestCost = cost;
                best = m;
            }
        }
        return best;
    }

    private static Model FromPose(Pose pose, Intrinsics k, LensDistortion lens) =>
        new(pose.CameraToWorld.Transposed(), pose.Position, k.Fx, k.Fy, k.Cx, k.Cy, lens);

    /// <summary>The start for pairs on (or near) one plane: the homography from plane coordinates to ideal
    /// pixels, the focal length from Zhang's two constraints with the principal point known (when the focal
    /// is free and the view is not head-on), then the pose from K⁻¹·H.</summary>
    private static Model? PlaneStart(Vec3d[] w, Vec2d[] q, Intrinsics prior, LensDistortion lens, Layout layout)
    {
        if (w.Length < 4)
            return null;
        var (c, _, u, v, _) = SolverMath.FitPlane(w);
        var uv = w.Select(x => new Vec2d(Vec3d.Dot(x - c, u), Vec3d.Dot(x - c, v))).ToArray();
        if (Homography.Fit(uv, q) is not { } h)
            return null;
        var fx = prior.Fx;
        var fy = prior.Fy;
        if (layout.Focal && FocalFromHomography(h, prior.Cx, prior.Cy, layout.Aspect) is { } f && f > 0.2 * prior.Fx && f < 5 * prior.Fx)
        {
            fx = f;
            fy = f * layout.Aspect;
        }
        var k = new Intrinsics(fx, fy, prior.Cx, prior.Cy, prior.Width, prior.Height);
        if (PoseFromPlaneHomography(h, k, c, u, v) is not { } pose)
            return null;
        return FromPose(pose, k, lens);
    }

    /// <summary>Zhang's constraints on one plane's homography with a known principal point and aspect:
    /// h₁ᵀωh₂ = 0 and h₁ᵀωh₁ = h₂ᵀωh₂ with ω = diag(1/f², 1/(a·f)², 1), solved for 1/f² in least squares.
    /// Null when the plane is seen head-on (the constraints vanish) or the answer is not a real focal.</summary>
    public static double? FocalFromHomography(Mat3d h, double cx, double cy, double aspect = 1)
    {
        var shifted = new Mat3d(1, 0, -cx, 0, 1, -cy, 0, 0, 1) * h;
        var hn = Homography.Normalise(shifted, frobenius: true);
        double h11 = hn.M00, h12 = hn.M01, h21 = hn.M10, h22 = hn.M11, h31 = hn.M20, h32 = hn.M21;
        var a2 = aspect * aspect;
        var c1 = h11 * h12 + h21 * h22 / a2;
        var k1 = h31 * h32;
        var c2 = h11 * h11 - h12 * h12 + (h21 * h21 - h22 * h22) / a2;
        var k2 = h31 * h31 - h32 * h32;
        var den = c1 * c1 + c2 * c2;
        // Head-on: the perspective row vanishes and so does all information about f.
        var persp = Math.Sqrt(h31 * h31 + h32 * h32);
        if (den < 1e-24 || persp < 1e-12)
            return null;
        var w = -(c1 * k1 + c2 * k2) / den;
        if (!(w > 0) || !double.IsFinite(w))
            return null;
        return 1 / Math.Sqrt(w);
    }

    /// <summary>The pose from a homography taking plane coordinates (along u, v from origin c) to pixels of a
    /// device with intrinsics k: [r₁ r₂ t] = λ·K⁻¹·H, the sign chosen so the plane is in front.</summary>
    public static Pose? PoseFromPlaneHomography(Mat3d h, Intrinsics k, Vec3d origin, Vec3d axisU, Vec3d axisV)
    {
        ArgumentNullException.ThrowIfNull(k);
        var a = k.K.Inverse() * h;
        var a1 = a.Column(0);
        var a2 = a.Column(1);
        var a3 = a.Column(2);
        var norm = 0.5 * (a1.Length + a2.Length);
        if (norm < 1e-300)
            return null;
        var lambda = 1 / norm;
        if (a3.Z * lambda < 0)
            lambda = -lambda;
        var r1 = a1 * lambda;
        var r2 = a2 * lambda;
        var t = a3 * lambda;
        var rp = Mat3d.FromColumns(r1, r2, Vec3d.Cross(r1, r2)).NearestRotation();
        var normal = Vec3d.Cross(axisU, axisV);
        var b = Mat3d.FromColumns(axisU, axisV, normal);
        // camera = Rp·Bᵀ·(X − origin) + t.
        var rwc = rp * b.Transposed();
        var centre = origin - rwc.Transposed() * t;
        return new Pose(centre, rwc.Transposed().ToRotationVector());
    }

    // ---- consensus ----

    private static int[] Consensus(Vec3d[] world, Vec2d[] ideal, int[] fit, Intrinsics prior, Layout layout, bool coplanar, PoseSolveOptions o)
    {
        var size = coplanar ? 4 : 6;
        var n = fit.Length;
        var rng = new Pcg32(o.Seed, 0xca1);
        Span<int> sample = stackalloc int[size];
        var w = new Vec3d[size];
        var q = new Vec2d[size];
        var t2 = o.OutlierThresholdPx * o.OutlierThresholdPx;
        var bestScore = double.PositiveInfinity;
        int[] best = [];
        var needed = o.MaxRansacIterations;
        for (var iter = 0; iter < needed; iter++)
        {
            Ransac.Sample(ref rng, n, sample);
            for (var k = 0; k < size; k++)
            {
                w[k] = world[fit[sample[k]]];
                q[k] = ideal[fit[sample[k]]];
            }
            Func<Vec3d, Vec2d?>? project = null;
            if (!coplanar)
            {
                if (CameraDlt.Solve(w, q) is { } p)
                    project = x => CameraDlt.Project(p, x);
            }
            else if (PlaneStart(w, q, prior, LensDistortion.None, layout) is { } m)
            {
                project = m.Project;
            }
            if (project is null)
                continue;
            double score = 0;
            var count = 0;
            foreach (var i in fit)
            {
                var e = project(world[i]) is { } p2 ? (p2 - ideal[i]).LengthSquared : double.PositiveInfinity;
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
                best = fit.Where(i => project(world[i]) is { } p3 && (p3 - ideal[i]).LengthSquared < t2).ToArray();
                needed = Ransac.RequiredIterations(count / (double)n, size, 0.999, o.MaxRansacIterations);
            }
        }
        return best;
    }

    private static Vec2d IdealPixel(Vec2d p, Intrinsics k, LensDistortion lens)
    {
        if (lens.IsNone)
            return p;
        var d = new Vec2d((p.X - k.Cx) / k.Fx, (p.Y - k.Cy) / k.Fy);
        var n = CameraModel.Undistort(d, lens);
        return new Vec2d(k.Fx * n.X + k.Cx, k.Fy * n.Y + k.Cy);
    }

    // ---- words ----

    private static PoseSolution Failure(Correspondence[] all, Intrinsics prior, LensDistortion lens, PoseSolveOptions o, string words)
    {
        var pose = o.InitialPose ?? Pose.Identity;
        var residuals = all.Select(c => ReprojectionError(c, prior, lens, pose)).ToArray();
        return new PoseSolution(prior, lens, pose, double.PositiveInfinity, residuals, new bool[all.Length], "not solved: " + words);
    }

    private static string Plural(string noun, int n, out string word)
    {
        word = n == 1 ? noun : noun + "s";
        return word;
    }

    private static string Words(double rms, int count, int n, double[] residuals, bool[] inliers, PoseSolveOptions o,
        List<string> notes, Intrinsics prior, Intrinsics solved, Layout layout)
    {
        var inv = CultureInfo.InvariantCulture;
        var noun = o.Noun;
        var sb = new StringBuilder();
        sb.Append(string.Create(inv, $"{rms:0.0#} px RMS over "));
        sb.Append(count == n
            ? string.Create(inv, $"{n:N0} {Plural(noun, n, out _)}")
            : string.Create(inv, $"{count:N0} of {n:N0} {Plural(noun, n, out _)}"));
        var bad = Enumerable.Range(0, n).Where(i => !inliers[i]).OrderByDescending(i => residuals[i]).ToArray();
        string Name(int i) => o.Labels is { } l && i < l.Count ? l[i] : (i + 1).ToString(inv);
        string Px(int i) => double.IsFinite(residuals[i]) ? string.Create(inv, $"{residuals[i]:0.0}") : "∞";
        if (bad.Length == 1)
        {
            sb.Append(string.Create(inv, $"; {noun} {Name(bad[0])} is {Px(bad[0])} px off — check it"));
        }
        else if (bad.Length is 2 or 3)
        {
            var names = string.Join(bad.Length == 2 ? " and " : ", ", bad.Select(Name));
            var pxs = string.Join(bad.Length == 2 ? " and " : ", ", bad.Select(Px));
            sb.Append(string.Create(inv, $"; {noun}s {names} are {pxs} px off — check them"));
        }
        else if (bad.Length > 3)
        {
            sb.Append(string.Create(inv,
                $"; {bad.Length:N0} {noun}s are beyond {o.OutlierThresholdPx:0.#} px (worst: {noun} {Name(bad[0])}, {Px(bad[0])} px)"));
        }
        if (layout.Focal && prior.Width > 0)
            sb.Append(string.Create(inv, $"; throw ratio {solved.ThrowRatio:0.000} (prior {prior.ThrowRatio:0.000})"));
        foreach (var note in notes)
            sb.Append("; ").Append(note);
        return sb.ToString();
    }
}
