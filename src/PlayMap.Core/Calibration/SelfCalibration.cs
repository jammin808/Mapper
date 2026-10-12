using System.Globalization;
using System.Text;
using PlayMap.Core.Geometry;
using PlayMap.Core.Model;
using PlayMap.Core.Numerics;

namespace PlayMap.Core.Calibration;

/// <summary>One projector's dense pairs with the camera: camera pixel ↔ the projector pixel decoded there,
/// and the prior optics (datasheet throw ratio and lens shift; a lens model if known — it is held).</summary>
public sealed record ProjectorObservations(Intrinsics Prior, LensDistortion Lens, IReadOnlyList<Vec2d> CameraPixels,
    IReadOnlyList<Vec2d> ProjectorPixels, string Name = "");

/// <summary>How the self-calibration runs.</summary>
public sealed record SelfCalibrationOptions
{
    public static readonly SelfCalibrationOptions Default = new();

    /// <summary>Hold every principal point at its prior (lens shift measured, as the field advises).</summary>
    public bool LockPrincipalPoints { get; init; } = true;

    /// <summary>Hold the camera's focal length (a camera calibrated before).</summary>
    public bool LockCameraFocal { get; init; }

    /// <summary>Hold the projectors' focal lengths (zoom known).</summary>
    public bool LockProjectorFocal { get; init; }

    /// <summary>A weak prior on each free focal length, as a fraction of the guess (≤ 0: none). It decides
    /// only what the pairs cannot — the near-degenerate case where both optical axes meet — and is otherwise
    /// outweighed by thousands of residuals.</summary>
    public double FocalPriorSigma { get; init; } = 0.3;

    /// <summary>When principal points are free, a prior holding each near its guess, as a fraction of the
    /// raster width (≤ 0: none). Two views cannot see lens shift: freeing it trades it for focal length.</summary>
    public double PrincipalPointPriorSigma { get; init; } = 0.02;

    /// <summary>A focal length known worse than this (relative 1σ from the pairs alone) is called out.</summary>
    public double FocalWarningSigma { get; init; } = 0.01;

    /// <summary>A pair farther than this from its epipolar line or its bundle-adjusted reprojection (pixels)
    /// is an outlier.</summary>
    public double OutlierThresholdPx { get; init; } = 2;

    /// <summary>At most this many pairs per projector enter the adjustment (a seeded subset); every pair
    /// still gets its point and residual.</summary>
    public int MaxPairsPerProjector { get; init; } = 20000;

    public int MaxIterations { get; init; } = 100;

    public ulong Seed { get; init; } = 1;
}

/// <summary>One projector after self-calibration, in the camera's frame (the camera at the origin looking
/// down +z): optics, pose, and per pair its 3D point, reprojection residual and inlier flag.</summary>
public sealed record ProjectorCalibration(
    string Name,
    Intrinsics Intrinsics,
    LensDistortion Lens,
    Pose Pose,
    IReadOnlyList<Vec2d> CameraPixels,
    IReadOnlyList<Vec2d> ProjectorPixels,
    Vec3d[] Points,
    double[] ResidualsPx,
    bool[] Inliers,
    double RmsPx,
    bool Solved,
    /// <summary>True when this projector's scale is tied to the first's through shared camera pixels.</summary>
    bool ScaleLinked,
    /// <summary>The focal length's relative 1σ from the pairs alone (NaN when locked).</summary>
    double FocalUncertainty,
    string Words)
{
    public int InlierCount => Inliers.Count(b => b);

    public DeviceView View => new(Intrinsics, Lens, Pose);
}

/// <summary>A camera and its projectors calibrated without a target. The world is the camera's frame; lengths
/// are in units of the first projector's baseline until <see cref="SetScale(Vec3d, Vec3d, double)"/> fixes
/// them from one known distance.</summary>
public sealed record SelfCalibrationResult(
    Intrinsics Camera,
    LensDistortion CameraLens,
    IReadOnlyList<ProjectorCalibration> Projectors,
    /// <summary>World units per baseline unit (1 until scaled).</summary>
    double Scale,
    bool Metric,
    int Iterations,
    /// <summary>The camera focal length's relative 1σ from the pairs alone (NaN when locked).</summary>
    double CameraFocalUncertainty,
    string Words)
{
    public DeviceView CameraView => new(Camera, CameraLens, Pose.Identity);

    /// <summary>Every length multiplied by <paramref name="factor"/> (points, projector positions).</summary>
    public SelfCalibrationResult Scaled(double factor, bool metric)
    {
        if (!(factor > 0) || !double.IsFinite(factor))
            throw new ArgumentOutOfRangeException(nameof(factor), "A scale is positive and finite.");
        var projectors = Projectors.Select(p => p with
        {
            Pose = p.Pose with { Position = p.Pose.Position * factor },
            Points = p.Points.Select(x => x * factor).ToArray(),
        }).ToArray();
        return this with { Projectors = projectors, Scale = Scale * factor, Metric = metric };
    }

    /// <summary>Scaled so the two points (in this result's frame) are <paramref name="distance"/> apart.</summary>
    public SelfCalibrationResult SetScale(Vec3d a, Vec3d b, double distance)
    {
        var current = Vec3d.Distance(a, b);
        if (!(current > 0))
            throw new ArgumentException("The two points coincide.");
        return Scaled(distance / current, true);
    }

    /// <summary>Scaled so the 3D points seen at two camera pixels are <paramref name="distance"/> apart.
    /// Throws when either pixel has no reconstructed point within two pixels.</summary>
    public SelfCalibrationResult SetScale(Vec2d cameraPixelA, Vec2d cameraPixelB, double distance)
    {
        var a = PointAt(cameraPixelA) ?? throw new ArgumentException("No point near the first pixel.", nameof(cameraPixelA));
        var b = PointAt(cameraPixelB) ?? throw new ArgumentException("No point near the second pixel.", nameof(cameraPixelB));
        return SetScale(a, b, distance);
    }

    /// <summary>The reconstructed point of the inlier pair nearest a camera pixel (within the reach).</summary>
    public Vec3d? PointAt(Vec2d cameraPixel, double reachPx = 2)
    {
        Vec3d? best = null;
        var bestD = reachPx * reachPx;
        foreach (var p in Projectors)
        {
            for (var i = 0; i < p.Points.Length; i++)
            {
                if (!p.Inliers[i])
                    continue;
                var d = (p.CameraPixels[i] - cameraPixel).LengthSquared;
                if (d <= bestD)
                {
                    bestD = d;
                    best = p.Points[i];
                }
            }
        }
        return best;
    }

    /// <summary>The rig re-expressed in another world (after registering it to a model or the floor).</summary>
    public (DeviceView Camera, IReadOnlyList<DeviceView> Projectors) In(Similarity3 world) =>
        (CameraView.Transformed(world), Projectors.Select(p => p.View.Transformed(world)).ToArray());
}

/// <summary>Self-calibration of a camera and N projectors from dense structured-light pairs, no checkerboard:
/// per projector the eight-point fundamental matrix with RANSAC, the essential matrix from the guessed
/// intrinsics, its decomposition by cheirality (relative pose, unit baseline); then one bundle adjustment of
/// the camera's focal length, every projector's focal length and pose, and one inverse depth per pair along
/// its camera ray (so the camera reprojects exactly and each pair adds two projector residuals). The depth
/// block is diagonal and is eliminated by the Schur complement, leaving a system the size of the optics and
/// poses — thousands of pairs adjust in milliseconds.</summary>
public static class SelfCalibration
{
    public static SelfCalibrationResult Solve(Intrinsics cameraPrior, LensDistortion cameraLens,
        IReadOnlyList<ProjectorObservations> projectors, SelfCalibrationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(cameraPrior);
        ArgumentNullException.ThrowIfNull(cameraLens);
        ArgumentNullException.ThrowIfNull(projectors);
        options ??= SelfCalibrationOptions.Default;
        var bundle = new Bundle(cameraPrior, cameraLens, options);
        var failures = new string?[projectors.Count];
        for (var j = 0; j < projectors.Count; j++)
            failures[j] = bundle.AddProjector(projectors[j], j);
        bundle.Begin();
        var iterations = bundle.Adjust();
        // A second pass without the pairs the first one could not explain.
        if (bundle.Trim())
            iterations += bundle.Adjust();
        return bundle.Result(projectors, failures, iterations);
    }

    /// <summary>The adjustment: shared camera optics, per-projector optics and pose, one inverse depth per
    /// pair. Steps are increments: a rotation multiplies on the left, a baseline moves on its tangent plane
    /// and stays unit length (the scale two views cannot see).</summary>
    private sealed class Bundle
    {
        /// <summary>The residual a pair behind either device costs per axis: no descent step accepts it.</summary>
        private const double Behind = 1e3;

        private readonly SelfCalibrationOptions _o;
        private readonly Intrinsics _camPrior;
        private readonly LensDistortion _camLens;
        private readonly double _camAspect;
        private readonly int _nc;
        private readonly List<Proj> _proj = [];
        private readonly List<Pair> _pairs = [];
        private readonly List<Mat3d> _r0 = [];
        private readonly List<Vec3d> _t0 = [];
        private readonly List<double> _rho0 = [];
        private readonly List<bool> _active0 = [];
        private State _s = State.Empty;
        private bool[] _active = [];

        /// <summary>A projector's constants: its input index, prior, lens and where its parameters sit.</summary>
        private sealed record Proj(int Index, Intrinsics Prior, LensDistortion Lens, double Aspect, int Offset, int First, int[] Subset);

        /// <summary>One pair: its projector, its index in that projector's input, the camera pixel and the
        /// projector pixel decoded there.</summary>
        private readonly record struct Pair(int Owner, int Source, Vec2d Cam, Vec2d Obs);

        /// <summary>Everything the adjustment moves.</summary>
        private sealed record State(double Fc, double Ccx, double Ccy, Mat3d[] R, Vec3d[] T, double[] F, double[] Cx, double[] Cy, double[] Rho)
        {
            public static readonly State Empty = new(0, 0, 0, [], [], [], [], [], []);
        }

        public Bundle(Intrinsics camPrior, LensDistortion camLens, SelfCalibrationOptions o)
        {
            _o = o;
            _camPrior = camPrior;
            _camLens = camLens;
            _camAspect = camPrior.Fx > 0 ? camPrior.Fy / camPrior.Fx : 1;
            _nc = (o.LockCameraFocal ? 0 : 1) + (o.LockPrincipalPoints ? 0 : 2);
        }

        private int ProjectorBlock => (_o.LockProjectorFocal ? 0 : 1) + (_o.LockPrincipalPoints ? 0 : 2) + 5;

        // ---- initialisation ----

        /// <summary>Adds a projector from its pairs: epipolar geometry, relative pose, start depths. Returns
        /// why it could not be added, or null.</summary>
        public string? AddProjector(ProjectorObservations obs, int index)
        {
            if (obs.CameraPixels.Count != obs.ProjectorPixels.Count)
                throw new ArgumentException("A projector's pixel lists differ in length.", nameof(obs));
            var subset = Ransac.Subset(obs.CameraPixels.Count, Math.Max(16, _o.MaxPairsPerProjector), _o.Seed + (ulong)index);
            var cam = subset.Select(i => obs.CameraPixels[i]).ToArray();
            var prj = subset.Select(i => obs.ProjectorPixels[i]).ToArray();
            if (cam.Length < 16)
                return "too few pairs";
            var fit = Epipolar.FitRobust(cam, prj, _o.OutlierThresholdPx, _o.Seed + 17 * (ulong)index);
            if (!fit.Succeeded)
                return "no epipolar geometry fits these pairs";
            var e = Epipolar.EssentialFromFundamental(fit.F, _camPrior, obs.Prior);
            var na = new Vec2d[cam.Length];
            var nb = new Vec2d[cam.Length];
            for (var i = 0; i < cam.Length; i++)
            {
                na[i] = Normalised(cam[i], _camPrior, _camLens);
                nb[i] = Normalised(prj[i], obs.Prior, obs.Lens);
            }
            var inlierA = Enumerable.Range(0, cam.Length).Where(i => fit.Inliers[i]).Select(i => na[i]).ToArray();
            var inlierB = Enumerable.Range(0, cam.Length).Where(i => fit.Inliers[i]).Select(i => nb[i]).ToArray();
            if (Epipolar.SelectByCheirality(e, inlierA, inlierB) is not { } rel)
                return "the pairs do not put the scene in front of both devices";

            var owner = _proj.Count;
            _proj.Add(new Proj(index, obs.Prior, obs.Lens, obs.Prior.Fx > 0 ? obs.Prior.Fy / obs.Prior.Fx : 1,
                _nc + owner * ProjectorBlock, _pairs.Count, subset));
            _r0.Add(rel.Rotation.NearestRotation());
            _t0.Add(rel.Translation.Normalized());
            var depths = new List<double>();
            var start = _rho0.Count;
            for (var i = 0; i < cam.Length; i++)
            {
                var z = Epipolar.DepthInA(rel, na[i], nb[i]);
                var ok = fit.Inliers[i] && z > 0 && double.IsFinite(z);
                _pairs.Add(new Pair(owner, subset[i], cam[i], prj[i]));
                _rho0.Add(ok ? 1 / z : double.NaN);
                _active0.Add(ok);
                if (ok)
                    depths.Add(z);
            }
            // Pairs without a usable start depth begin at the median, inactive until re-read.
            var median = depths.Count > 0 ? SolverMath.Median(depths.ToArray()) : 1;
            for (var i = start; i < _rho0.Count; i++)
                if (double.IsNaN(_rho0[i]))
                    _rho0[i] = 1 / median;
            return null;
        }

        /// <summary>Freezes the start into the state the adjustment moves.</summary>
        public void Begin()
        {
            _s = new State(_camPrior.Fx, _camPrior.Cx, _camPrior.Cy, [.. _r0], [.. _t0],
                _proj.Select(p => p.Prior.Fx).ToArray(), _proj.Select(p => p.Prior.Cx).ToArray(),
                _proj.Select(p => p.Prior.Cy).ToArray(), [.. _rho0]);
            _active = [.. _active0];
        }

        private static Vec2d Normalised(Vec2d pixel, Intrinsics k, LensDistortion lens)
        {
            var d = new Vec2d((pixel.X - k.Cx) / k.Fx, (pixel.Y - k.Cy) / k.Fy);
            return CameraModel.Undistort(d, lens);
        }

        private static (Vec3d E1, Vec3d E2) Basis(Vec3d t)
        {
            var helper = Math.Abs(t.X) < 0.9 ? Vec3d.UnitX : Vec3d.UnitY;
            var e1 = (helper - t * Vec3d.Dot(helper, t)).Normalized();
            return (e1, Vec3d.Cross(t, e1));
        }

        // ---- the model ----

        /// <summary>The camera ray of a pixel scaled to z = 1.</summary>
        private Vec3d Ray(Vec2d c, State s)
        {
            var q = new Vec2d((c.X - s.Ccx) / s.Fc, (c.Y - s.Ccy) / (s.Fc * _camAspect));
            var m = CameraModel.Undistort(q, _camLens);
            return new Vec3d(m.X, m.Y, 1);
        }

        /// <summary>The camera ray with its derivatives by fc, ccx and ccy (through the lens inverse when
        /// there is one: m = D⁻¹(q), so dm = J(m)⁻¹·dq).</summary>
        private Vec3d Ray(Vec2d c, State s, out Vec3d dFc, out Vec3d dCx, out Vec3d dCy)
        {
            var fy = s.Fc * _camAspect;
            var q = new Vec2d((c.X - s.Ccx) / s.Fc, (c.Y - s.Ccy) / fy);
            var dqFc = -q / s.Fc;
            var dqCx = new Vec2d(-1 / s.Fc, 0);
            var dqCy = new Vec2d(0, -1 / fy);
            if (_camLens.IsNone)
            {
                dFc = new Vec3d(dqFc.X, dqFc.Y, 0);
                dCx = new Vec3d(dqCx.X, 0, 0);
                dCy = new Vec3d(0, dqCy.Y, 0);
                return new Vec3d(q.X, q.Y, 1);
            }
            SolverMath.TryUndistort(q, _camLens, out var m);
            var (a, b, cc, d) = SolverMath.DistortJacobian(m, _camLens);
            var det = a * d - b * cc;
            Vec3d Through(Vec2d dq) => Math.Abs(det) > 1e-300
                ? new Vec3d((d * dq.X - b * dq.Y) / det, (-cc * dq.X + a * dq.Y) / det, 0)
                : Vec3d.Zero;
            dFc = Through(dqFc);
            dCx = Through(dqCx);
            dCy = Through(dqCy);
            return new Vec3d(m.X, m.Y, 1);
        }

        /// <summary>The projector residual of a pair at a state and inverse depth; false behind either device.</summary>
        private bool Residual(Pair pr, State s, double rho, out Vec2d res)
        {
            res = default;
            var j = pr.Owner;
            var y = s.R[j] * Ray(pr.Cam, s) + s.T[j] * rho;
            if (!(rho > 0) || y.Z <= 1e-9 * y.Length)
                return false;
            var p = _proj[j];
            var d = CameraModel.Distort(new Vec2d(y.X / y.Z, y.Y / y.Z), p.Lens);
            res = new Vec2d(s.F[j] * d.X + s.Cx[j], s.F[j] * p.Aspect * d.Y + s.Cy[j]) - pr.Obs;
            return double.IsFinite(res.X) && double.IsFinite(res.Y);
        }

        /// <summary>Pairs are processed in this many fixed blocks, in parallel, and the blocks' partial results
        /// are combined in block order — the same sums on any machine and any thread timing.</summary>
        private const int Blocks = 64;

        private static (int Start, int End) Block(int b, int n) => ((int)((long)n * b / Blocks), (int)((long)n * (b + 1) / Blocks));

        private double Cost(State s)
        {
            var n = _pairs.Count;
            var partial = new double[Blocks];
            Parallel.For(0, Blocks, b =>
            {
                var (start, end) = Block(b, n);
                double sum = 0;
                for (var i = start; i < end; i++)
                {
                    if (!_active[i])
                        continue;
                    sum += Residual(_pairs[i], s, s.Rho[i], out var res) ? res.LengthSquared : 2 * Behind * Behind;
                }
                partial[b] = sum;
            });
            double cost = 0;
            foreach (var x in partial)
                cost += x;
            if (_o.FocalPriorSigma <= 0)
                return cost;
            if (!_o.LockCameraFocal)
                cost += Sq((s.Fc - _camPrior.Fx) / (_o.FocalPriorSigma * _camPrior.Fx));
            if (!_o.LockProjectorFocal)
                for (var j = 0; j < _proj.Count; j++)
                    cost += Sq((s.F[j] - _proj[j].Prior.Fx) / (_o.FocalPriorSigma * _proj[j].Prior.Fx));
            if (_o.LockPrincipalPoints || _o.PrincipalPointPriorSigma <= 0)
                return cost;
            var sc = _o.PrincipalPointPriorSigma * _camPrior.Width;
            cost += Sq((s.Ccx - _camPrior.Cx) / sc) + Sq((s.Ccy - _camPrior.Cy) / sc);
            for (var j = 0; j < _proj.Count; j++)
            {
                var sp = _o.PrincipalPointPriorSigma * _proj[j].Prior.Width;
                cost += Sq((s.Cx[j] - _proj[j].Prior.Cx) / sp) + Sq((s.Cy[j] - _proj[j].Prior.Cy) / sp);
            }
            return cost;
        }

        private static double Sq(double v) => v * v;

        // ---- Levenberg–Marquardt with the depth block eliminated ----

        /// <summary>Runs the adjustment from the current state; returns the iterations taken.</summary>
        public int Adjust()
        {
            var n = _pairs.Count;
            if (_proj.Count == 0 || n == 0)
                return 0;
            var g = _nc + _proj.Count * ProjectorBlock;
            var q = _nc + ProjectorBlock;
            var w = new double[n * q];
            var v = new double[n];
            var gd = new double[n];
            var u = new DenseMatrix(g, g);
            var gg = new double[g];
            var jg = new double[n * 2 * q];
            var idx = new int[q];
            // The damping scales each diagonal (Marquardt), so it starts as a plain fraction: units differ by
            // orders of magnitude between focal lengths, rotations and inverse depths.
            var lambda = 1e-3;
            var nu = 2.0;
            var cost = Cost(_s);
            var iter = 0;
            while (iter < _o.MaxIterations)
            {
                iter++;
                Accumulate(u, gg, w, v, gd, jg, idx);
                AddPriors(u, gg);
                var gmax = 0.0;
                foreach (var x in gg)
                    gmax = Math.Max(gmax, Math.Abs(x));
                foreach (var x in gd)
                    gmax = Math.Max(gmax, Math.Abs(x));
                if (gmax < 1e-12)
                    break;

                var accepted = false;
                var settled = false;
                for (var attempt = 0; attempt < 20 && !accepted; attempt++)
                {
                    if (!Step(u, gg, w, v, gd, lambda, q, idx, out var dg, out var dd, out var predicted))
                    {
                        lambda *= nu;
                        nu *= 2;
                        continue;
                    }
                    var trial = Stepped(_s, dg, dd);
                    var trialCost = Cost(trial);
                    var gain = predicted > 0 ? (cost - trialCost) / predicted : -1;
                    if (gain > 0 && double.IsFinite(trialCost))
                    {
                        settled = (cost - trialCost) / Math.Max(cost, 1e-300) < 1e-10;
                        _s = trial;
                        cost = trialCost;
                        lambda *= Math.Max(1.0 / 3, 1 - Math.Pow(2 * gain - 1, 3));
                        nu = 2;
                        accepted = true;
                    }
                    else
                    {
                        lambda *= nu;
                        nu *= 2;
                    }
                }
                if (!accepted || settled)
                    break;
            }
            return iter;
        }

        /// <summary>The normal equations at the current state, data only: U += Jgᵀ·Jg, g += Jgᵀ·r, and per pair
        /// W = Jgᵀ·Jd, V = Jdᵀ·Jd, gd = Jdᵀ·r. Returns the data cost and the pairs that contributed.</summary>
        private (double Cost, int Pairs) Accumulate(DenseMatrix u, double[] gg, double[] w, double[] v, double[] gd, double[] jgAll, int[] idx)
        {
            var g = gg.Length;
            var q = idx.Length;
            var n = _pairs.Count;
            Array.Clear(u.Data);
            Array.Clear(gg);
            Array.Clear(w);
            Array.Clear(v);
            Array.Clear(gd);
            // The per-pair derivatives in parallel (the costly part), then the sums in pair order.
            var rs = new Vec2d[n];
            var jds = new Vec2d[n];
            var ok = new bool[n];
            Parallel.For(0, Blocks, b =>
            {
                var (start, end) = Block(b, n);
                for (var i = start; i < end; i++)
                    ok[i] = _active[i] && Linearise(_pairs[i], _s.Rho[i], jgAll.AsSpan(i * 2 * q, 2 * q), q, out rs[i], out jds[i]);
            });
            double cost = 0;
            var used = 0;
            for (var i = 0; i < n; i++)
            {
                if (!ok[i])
                    continue;
                var r = rs[i];
                var jd = jds[i];
                var jg = jgAll.AsSpan(i * 2 * q, 2 * q);
                cost += r.LengthSquared;
                used++;
                Indices(_pairs[i].Owner, idx);
                for (var a = 0; a < q; a++)
                {
                    var ja0 = jg[a];
                    var ja1 = jg[q + a];
                    gg[idx[a]] += ja0 * r.X + ja1 * r.Y;
                    w[i * q + a] = ja0 * jd.X + ja1 * jd.Y;
                    var row = idx[a] * g;
                    for (var b = 0; b < q; b++)
                        u.Data[row + idx[b]] += ja0 * jg[b] + ja1 * jg[q + b];
                }
                v[i] = jd.LengthSquared;
                gd[i] = jd.X * r.X + jd.Y * r.Y;
            }
            return (cost, used);
        }

        /// <summary>How well the pairs alone fix each free focal length: the relative 1σ from σ²·(S⁻¹)ₖₖ, S the
        /// reduced system without the priors. Large when the geometry is near the degenerate case where the
        /// camera's and the projector's optical axes meet. NaN for a locked focal length.</summary>
        public (double Camera, double[] Projectors) FocalUncertainty()
        {
            var n = _pairs.Count;
            var g = _nc + _proj.Count * ProjectorBlock;
            var q = _nc + ProjectorBlock;
            var projectors = Enumerable.Repeat(double.NaN, _proj.Count).ToArray();
            if (n == 0 || _proj.Count == 0)
                return (double.NaN, projectors);
            var w = new double[n * q];
            var v = new double[n];
            var gd = new double[n];
            var u = new DenseMatrix(g, g);
            var gg = new double[g];
            var (cost, used) = Accumulate(u, gg, w, v, gd, new double[n * 2 * q], new int[q]);
            // Two residuals a pair against one depth a pair and the globals.
            var dof = used - g;
            if (dof <= 0)
                return (double.PositiveInfinity, projectors.Select(_ => double.PositiveInfinity).ToArray());
            var sigma2 = cost / dof;
            var s = u.Clone();
            var idx = new int[q];
            for (var i = 0; i < n; i++)
            {
                if (!_active[i] || v[i] <= 0)
                    continue;
                Indices(_pairs[i].Owner, idx);
                for (var a = 0; a < q; a++)
                {
                    var wa = w[i * q + a] / v[i];
                    var row = idx[a] * g;
                    for (var c = 0; c < q; c++)
                        s.Data[row + idx[c]] -= wa * w[i * q + c];
                }
            }
            // A ridge relative to each diagonal: the parameters' units differ by orders of magnitude (pixels of
            // focal length against radians), so one absolute ridge would cap exactly the variances sought.
            for (var k = 0; k < g; k++)
                s[k, k] += 1e-10 * Math.Abs(s[k, k]) + 1e-300;
            double Relative(int k, double f)
            {
                var e = new double[g];
                e[k] = 1;
                var x = new double[g];
                return LinearSolve.TryCholesky(s, e, x) && x[k] > 0 ? Math.Sqrt(sigma2 * x[k]) / f : double.PositiveInfinity;
            }
            var camera = _o.LockCameraFocal ? double.NaN : Relative(0, _s.Fc);
            if (!_o.LockProjectorFocal)
                for (var j = 0; j < _proj.Count; j++)
                    projectors[j] = Relative(_proj[j].Offset, _s.F[j]);
            return (camera, projectors);
        }

        /// <summary>One damped step: the reduced system (U + λD − Σ W·V⁻¹·Wᵀ)·δg = −g + Σ W·V⁻¹·gd solved by
        /// Cholesky, then each depth back-substituted, δd = V⁻¹·(−gd − Wᵀ·δg). Also the predicted reduction
        /// δᵀ(λ·D·δ − g) for the gain ratio.</summary>
        private bool Step(DenseMatrix u, double[] gg, double[] w, double[] v, double[] gd, double lambda, int q, int[] idx,
            out double[] dg, out double[] dd, out double predicted)
        {
            var g = gg.Length;
            var n = v.Length;
            var s = u.Clone();
            var b = new double[g];
            for (var k = 0; k < g; k++)
            {
                s[k, k] += lambda * Math.Max(u[k, k], 1e-12);
                b[k] = -gg[k];
            }
            var vd = new double[n];
            for (var i = 0; i < n; i++)
            {
                if (!_active[i] || v[i] <= 0)
                    continue;
                vd[i] = v[i] + lambda * Math.Max(v[i], 1e-12);
                Indices(_pairs[i].Owner, idx);
                var inv = 1 / vd[i];
                for (var a = 0; a < q; a++)
                {
                    var wa = w[i * q + a] * inv;
                    if (wa == 0)
                        continue;
                    b[idx[a]] += wa * gd[i];
                    var row = idx[a] * g;
                    for (var c = 0; c < q; c++)
                        s.Data[row + idx[c]] -= wa * w[i * q + c];
                }
            }
            dg = new double[g];
            dd = new double[n];
            predicted = 0;
            if (!LinearSolve.TryCholesky(s, b, dg))
                return false;
            for (var k = 0; k < g; k++)
                predicted += dg[k] * (lambda * Math.Max(u[k, k], 1e-12) * dg[k] - gg[k]);
            for (var i = 0; i < n; i++)
            {
                if (vd[i] <= 0)
                    continue;
                Indices(_pairs[i].Owner, idx);
                double dot = 0;
                for (var a = 0; a < q; a++)
                    dot += w[i * q + a] * dg[idx[a]];
                dd[i] = (-gd[i] - dot) / vd[i];
                predicted += dd[i] * (lambda * Math.Max(v[i], 1e-12) * dd[i] - gd[i]);
            }
            return true;
        }

        private void Indices(int owner, int[] idx)
        {
            for (var a = 0; a < _nc; a++)
                idx[a] = a;
            var off = _proj[owner].Offset;
            for (var a = 0; a < ProjectorBlock; a++)
                idx[_nc + a] = off + a;
        }

        /// <summary>Residual and Jacobian of a pair at the current state: the global columns [camera block |
        /// projector block] into jg (row-major 2 × q), the inverse-depth column into jd.</summary>
        private bool Linearise(Pair pr, double rho, Span<double> jg, int q, out Vec2d r, out Vec2d jd)
        {
            jg.Clear();
            var j = pr.Owner;
            var p = _proj[j];
            var rot = _s.R[j];
            var t = _s.T[j];
            var f = _s.F[j];
            var m = Ray(pr.Cam, _s, out var mFc, out var mCx, out var mCy);
            var rm = rot * m;
            var y = rm + t * rho;
            r = default;
            jd = default;
            if (!(rho > 0) || !Projection.ProjectCamera(y, f, f * p.Aspect, _s.Cx[j], _s.Cy[j], p.Lens, out var pix, out var du, out var dv))
                return false;
            r = pix - pr.Obs;
            jd = new Vec2d(Vec3d.Dot(du, t), Vec3d.Dot(dv, t));
            var k = 0;
            if (!_o.LockCameraFocal)
            {
                var dy = rot * mFc;
                jg[k] = Vec3d.Dot(du, dy);
                jg[q + k] = Vec3d.Dot(dv, dy);
                k++;
            }
            if (!_o.LockPrincipalPoints)
            {
                var dyx = rot * mCx;
                var dyy = rot * mCy;
                jg[k] = Vec3d.Dot(du, dyx);
                jg[q + k] = Vec3d.Dot(dv, dyx);
                jg[k + 1] = Vec3d.Dot(du, dyy);
                jg[q + k + 1] = Vec3d.Dot(dv, dyy);
                k += 2;
            }
            if (!_o.LockProjectorFocal)
            {
                var d = CameraModel.Distort(new Vec2d(y.X / y.Z, y.Y / y.Z), p.Lens);
                jg[k] = d.X;
                jg[q + k] = d.Y * p.Aspect;
                k++;
            }
            if (!_o.LockPrincipalPoints)
            {
                jg[k] = 1;
                jg[q + k + 1] = 1;
                k += 2;
            }
            // ∂Y/∂ω_k = e_k × (R·m), so ∂u/∂ω = (R·m) × ∂u/∂Y.
            var wu = Vec3d.Cross(rm, du);
            var wv = Vec3d.Cross(rm, dv);
            jg[k] = wu.X;
            jg[k + 1] = wu.Y;
            jg[k + 2] = wu.Z;
            jg[q + k] = wv.X;
            jg[q + k + 1] = wv.Y;
            jg[q + k + 2] = wv.Z;
            k += 3;
            var (e1, e2) = Basis(t);
            jg[k] = rho * Vec3d.Dot(du, e1);
            jg[k + 1] = rho * Vec3d.Dot(du, e2);
            jg[q + k] = rho * Vec3d.Dot(dv, e1);
            jg[q + k + 1] = rho * Vec3d.Dot(dv, e2);
            return true;
        }

        private void AddPriors(DenseMatrix u, double[] gg)
        {
            AddPrincipalPointPriors(u, gg);
            if (_o.FocalPriorSigma <= 0)
                return;
            if (!_o.LockCameraFocal)
            {
                var s = _o.FocalPriorSigma * _camPrior.Fx;
                u[0, 0] += 1 / (s * s);
                gg[0] += (_s.Fc - _camPrior.Fx) / (s * s);
            }
            if (_o.LockProjectorFocal)
                return;
            for (var j = 0; j < _proj.Count; j++)
            {
                var p = _proj[j];
                var s = _o.FocalPriorSigma * p.Prior.Fx;
                u[p.Offset, p.Offset] += 1 / (s * s);
                gg[p.Offset] += (_s.F[j] - p.Prior.Fx) / (s * s);
            }
        }

        /// <summary>A free principal point is held near its prior: two views fix seven numbers, and focal lengths
        /// and pose already take all of them.</summary>
        private void AddPrincipalPointPriors(DenseMatrix u, double[] gg)
        {
            if (_o.LockPrincipalPoints || _o.PrincipalPointPriorSigma <= 0)
                return;
            void Pull(int k, double value, double prior, double sigma)
            {
                u[k, k] += 1 / (sigma * sigma);
                gg[k] += (value - prior) / (sigma * sigma);
            }
            var cam = _o.LockCameraFocal ? 0 : 1;
            var sc = _o.PrincipalPointPriorSigma * _camPrior.Width;
            Pull(cam, _s.Ccx, _camPrior.Cx, sc);
            Pull(cam + 1, _s.Ccy, _camPrior.Cy, sc);
            var off = _o.LockProjectorFocal ? 0 : 1;
            for (var j = 0; j < _proj.Count; j++)
            {
                var p = _proj[j];
                var sp = _o.PrincipalPointPriorSigma * p.Prior.Width;
                Pull(p.Offset + off, _s.Cx[j], p.Prior.Cx, sp);
                Pull(p.Offset + off + 1, _s.Cy[j], p.Prior.Cy, sp);
            }
        }

        private State Stepped(State s, double[] dg, double[] dd)
        {
            var k = 0;
            var fc = s.Fc;
            var ccx = s.Ccx;
            var ccy = s.Ccy;
            if (!_o.LockCameraFocal)
                fc += dg[k++];
            if (!_o.LockPrincipalPoints)
            {
                ccx += dg[k++];
                ccy += dg[k];
            }
            var count = _proj.Count;
            var rs = new Mat3d[count];
            var ts = new Vec3d[count];
            var fs = (double[])s.F.Clone();
            var px = (double[])s.Cx.Clone();
            var py = (double[])s.Cy.Clone();
            for (var j = 0; j < count; j++)
            {
                var o = _proj[j].Offset;
                if (!_o.LockProjectorFocal)
                    fs[j] += dg[o++];
                if (!_o.LockPrincipalPoints)
                {
                    px[j] += dg[o++];
                    py[j] += dg[o++];
                }
                rs[j] = (Mat3d.FromRotationVector(new Vec3d(dg[o], dg[o + 1], dg[o + 2])) * s.R[j]).NearestRotation();
                var (e1, e2) = Basis(s.T[j]);
                ts[j] = (s.T[j] + e1 * dg[o + 3] + e2 * dg[o + 4]).Normalized();
            }
            var rho = new double[s.Rho.Length];
            for (var i = 0; i < rho.Length; i++)
                rho[i] = s.Rho[i] + dd[i];
            return new State(fc, ccx, ccy, rs, ts, fs, px, py, rho);
        }

        // ---- after the adjustment ----

        /// <summary>The inverse depth along the camera ray that best explains the projector pixel at the
        /// current state (Gauss–Newton in one unknown, kept in front of the camera).</summary>
        private double BestDepth(Pair pr, double rho)
        {
            var j = pr.Owner;
            var p = _proj[j];
            var m = Ray(pr.Cam, _s);
            var rm = _s.R[j] * m;
            var t = _s.T[j];
            for (var it = 0; it < 12; it++)
            {
                if (!(rho > 0) || !Projection.ProjectCamera(rm + t * rho, _s.F[j], _s.F[j] * p.Aspect, _s.Cx[j], _s.Cy[j], p.Lens,
                        out var pix, out var du, out var dv))
                    return rho;
                var r = pix - pr.Obs;
                var jd = new Vec2d(Vec3d.Dot(du, t), Vec3d.Dot(dv, t));
                var jj = jd.LengthSquared;
                if (jj < 1e-300)
                    return rho;
                var step = -(jd.X * r.X + jd.Y * r.Y) / jj;
                var next = rho + step;
                rho = next > 0 ? next : rho * 0.5;
                if (Math.Abs(step) < 1e-12 * rho)
                    break;
            }
            return rho;
        }

        /// <summary>Re-reads every pair at the adjusted state: an inactive pair gets its best depth; a pair
        /// beyond the threshold leaves the adjustment, one within it joins. True when the set changed.</summary>
        public bool Trim()
        {
            var residuals = new double[_pairs.Count];
            Parallel.For(0, _pairs.Count, i =>
            {
                if (!_active[i])
                    _s.Rho[i] = BestDepth(_pairs[i], _s.Rho[i]);
                residuals[i] = Residual(_pairs[i], _s, _s.Rho[i], out var res) ? res.Length : double.PositiveInfinity;
            });
            // Never tighter than three robust sigmas of what the adjustment reached: an adjustment that stopped
            // short must not shed the pairs it was still fitting.
            var activeResiduals = residuals.Where((r, i) => _active[i] && double.IsFinite(r)).ToArray();
            var sigma = activeResiduals.Length > 0 ? 1.4826 * SolverMath.Median(activeResiduals) : 0;
            var limit = Math.Max(_o.OutlierThresholdPx, 3 * sigma);
            var changed = false;
            for (var i = 0; i < _pairs.Count; i++)
            {
                var ok = residuals[i] <= limit;
                if (ok != _active[i])
                {
                    _active[i] = ok;
                    changed = true;
                }
            }
            return changed;
        }

        private Intrinsics ProjectorIntrinsics(int j) =>
            new(_s.F[j], _s.F[j] * _proj[j].Aspect, _s.Cx[j], _s.Cy[j], _proj[j].Prior.Width, _proj[j].Prior.Height);

        public SelfCalibrationResult Result(IReadOnlyList<ProjectorObservations> inputs, string?[] failures, int iterations)
        {
            var inv = CultureInfo.InvariantCulture;
            var camera = new Intrinsics(_s.Fc, _s.Fc * _camAspect, _s.Ccx, _s.Ccy, _camPrior.Width, _camPrior.Height);
            var (cameraSigma, projectorSigma) = FocalUncertainty();
            var outputs = new ProjectorCalibration?[inputs.Count];
            var depthByPixel = new List<Dictionary<long, double>>();
            for (var j = 0; j < _proj.Count; j++)
            {
                var p = _proj[j];
                var obs = inputs[p.Index];
                var count = obs.CameraPixels.Count;
                var points = new Vec3d[count];
                var residuals = new double[count];
                var inliers = new bool[count];
                Array.Fill(points, PointCloud.Invalid);
                Array.Fill(residuals, double.PositiveInfinity);
                var adjusted = new bool[count];
                for (var k = 0; k < p.Subset.Length; k++)
                    adjusted[_pairs[p.First + k].Source] = true;
                Parallel.For(0, p.Subset.Length, k =>
                {
                    var i = p.First + k;
                    Fill(_pairs[i], _s.Rho[i], _active[i], points, residuals, inliers);
                });
                // Pairs outside the adjusted subset get their own best depth at the final state.
                var rel = new RelativePose(_s.R[j], _s.T[j]);
                var k2 = ProjectorIntrinsics(j);
                var owner = j;
                Parallel.For(0, count, src =>
                {
                    if (adjusted[src])
                        return;
                    var pr = new Pair(owner, src, obs.CameraPixels[src], obs.ProjectorPixels[src]);
                    var m = Ray(pr.Cam, _s);
                    var z = Epipolar.DepthInA(rel, new Vec2d(m.X, m.Y), Normalised(pr.Obs, k2, p.Lens));
                    var rho = BestDepth(pr, z > 0 && double.IsFinite(z) ? 1 / z : 1);
                    Fill(pr, rho, true, points, residuals, inliers);
                });
                double sum = 0;
                var nIn = 0;
                var depth = new Dictionary<long, double>();
                for (var i = 0; i < count; i++)
                {
                    if (!inliers[i])
                        continue;
                    sum += residuals[i] * residuals[i];
                    nIn++;
                    depth.TryAdd(PixelKey(obs.CameraPixels[i]), points[i].Z);
                }
                depthByPixel.Add(depth);
                var rms = nIn > 0 ? Math.Sqrt(sum / nIn) : double.PositiveInfinity;
                // Projector frame → camera frame: X_c = Rᵀ·(X_p − t).
                var rt = _s.R[j].Transposed();
                var pose = new Pose(rt * _s.T[j] * -1, rt.ToRotationVector());
                var name = string.IsNullOrEmpty(obs.Name) ? (p.Index + 1).ToString(inv) : obs.Name;
                var words = string.Create(inv,
                    $"projector {name}: {rms:0.00} px RMS over {nIn:N0} of {count:N0} pairs, throw ratio {k2.ThrowRatio:0.000}{Plus(projectorSigma[j])} (guess {obs.Prior.ThrowRatio:0.000})");
                outputs[p.Index] = new ProjectorCalibration(name, k2, p.Lens, pose, obs.CameraPixels, obs.ProjectorPixels,
                    points, residuals, inliers, rms, true, j == 0, projectorSigma[j], words);
            }
            LinkScales(outputs, depthByPixel);

            var list = new List<ProjectorCalibration>(inputs.Count);
            for (var j = 0; j < inputs.Count; j++)
            {
                if (outputs[j] is { } o)
                {
                    list.Add(o);
                    continue;
                }
                var obs = inputs[j];
                var name = string.IsNullOrEmpty(obs.Name) ? (j + 1).ToString(inv) : obs.Name;
                var count = obs.CameraPixels.Count;
                list.Add(new ProjectorCalibration(name, obs.Prior, obs.Lens, Pose.Identity, obs.CameraPixels, obs.ProjectorPixels,
                    Enumerable.Repeat(PointCloud.Invalid, count).ToArray(), Enumerable.Repeat(double.PositiveInfinity, count).ToArray(),
                    new bool[count], double.PositiveInfinity, false, false, double.NaN, $"projector {name}: not solved: {failures[j] ?? "no pairs"}"));
            }
            var sb = new StringBuilder();
            sb.Append(string.Create(inv,
                $"camera {camera.HorizontalFovDegrees:0.0}° horizontal field of view{Plus(cameraSigma)} (guess {_camPrior.HorizontalFovDegrees:0.0}°)"));
            foreach (var p in list)
                sb.Append("; ").Append(p.Words);
            var weak = cameraSigma > _o.FocalWarningSigma || projectorSigma.Any(x => x > _o.FocalWarningSigma);
            if (weak)
            {
                sb.Append(_o.LockCameraFocal
                    ? "; focal lengths poorly determined by these pairs — add depth to the scene or move the camera"
                    : "; focal lengths poorly determined: the camera and projector look along nearly crossing axes — lock the camera's focal length or move the camera off the projector's axis");
            }
            sb.Append("; lengths in baselines — set one known distance for metres");
            return new SelfCalibrationResult(camera, _camLens, list, 1, false, iterations, cameraSigma, sb.ToString());
        }

        /// <summary>" ±x%" for a relative uncertainty, nothing for a locked value.</summary>
        private static string Plus(double sigma) => double.IsNaN(sigma) ? ""
            : double.IsFinite(sigma) ? string.Create(CultureInfo.InvariantCulture, $" ±{sigma * 100:0.0#}%") : " (undetermined)";

        /// <summary>Ties each further projector's scale to the first's: the median ratio of the depths both
        /// give the same camera pixels.</summary>
        private void LinkScales(ProjectorCalibration?[] outputs, List<Dictionary<long, double>> depthByPixel)
        {
            for (var j = 1; j < _proj.Count; j++)
            {
                var ratios = new List<double>();
                foreach (var (key, z) in depthByPixel[j])
                    if (depthByPixel[0].TryGetValue(key, out var z0) && z > 0)
                        ratios.Add(z0 / z);
                var index = _proj[j].Index;
                var output = outputs[index]!;
                if (ratios.Count < 20)
                {
                    outputs[index] = output with { Words = output.Words + "; scale not linked: too few camera pixels shared with the first projector" };
                    continue;
                }
                var s = SolverMath.Median(ratios.ToArray());
                outputs[index] = output with
                {
                    Pose = output.Pose with { Position = output.Pose.Position * s },
                    Points = output.Points.Select(x => x * s).ToArray(),
                    ScaleLinked = true,
                };
            }
        }

        private void Fill(Pair pr, double rho, bool active, Vec3d[] points, double[] residuals, bool[] inliers)
        {
            if (!(rho > 0))
                return;
            points[pr.Source] = Ray(pr.Cam, _s) / rho;
            if (!Residual(pr, _s, rho, out var res))
                return;
            residuals[pr.Source] = res.Length;
            inliers[pr.Source] = active && res.Length <= _o.OutlierThresholdPx;
        }

        /// <summary>A camera pixel's half-pixel bin, for finding the same pixel decoded by two projectors.</summary>
        private static long PixelKey(Vec2d c) => ((long)Math.Floor(c.X * 2) << 32) ^ (uint)(int)Math.Floor(c.Y * 2);
    }
}
