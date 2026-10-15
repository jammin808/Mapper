using PlayMap.Core.Calibration;
using PlayMap.Core.Model;
using PlayMap.Core.Numerics;

namespace PlayMap.Core.Scan.MultiView;

/// <summary>
/// The projector-anchored bundle adjustment. Unknowns: each free view's pose (rotation increment on the left, centre),
/// each free rig's f, cx, cy, k1, k2 (fy = f·aspect), each free projector's pose, f, cx, cy, and one depth per track
/// along its projector's ray, X_t = C_p + d_t·R_p·K_p⁻¹[u, 1]ᵀ — the projector's own observation is exact by
/// construction, which is the anchor. Residuals are camera reprojections π(K_c, D_c, T_v, X_t) − x_{t,v} under
/// Huber's loss, plus the priors. The depth block is diagonal and is eliminated by the Schur complement, so each
/// Levenberg–Marquardt step solves a dense system the size of the poses and optics. A centre on a sphere (the gauge's
/// scale) moves on its tangent plane at a fixed distance from its anchor. Sums run in a fixed order: the same input
/// gives the same result on any machine.
/// </summary>
internal sealed class MultiViewBundle
{
    /// <summary>The cost an observation behind a device counts: no step that puts a point there is taken.</summary>
    private const double Behind = 1e4;

    internal sealed record RigSpec(Intrinsics Prior, LensDistortion Lens, bool Held);

    internal sealed record ViewSpec(int Rig, bool Fixed, bool Sphere, Vec3d Anchor, double Radius);

    internal sealed record ProjSpec(Intrinsics Prior, LensDistortion Lens, bool Held, bool LockFocal, bool LockShift, bool Sphere, Vec3d Anchor, double Radius);

    internal readonly record struct TrackSpec(int Projector, Vec2d U);

    internal readonly record struct ObsSpec(int Track, int View, Vec2d X);

    /// <summary>Everything the adjustment moves.</summary>
    internal sealed class State
    {
        public Mat3d[] VR = [];
        public Vec3d[] VC = [];
        public double[] RF = [], RCx = [], RCy = [], RK1 = [], RK2 = [];
        public Mat3d[] PR = [];
        public Vec3d[] PC = [];
        public double[] PF = [], PCx = [], PCy = [];
        public double[] D = [];

        public State Clone() => new()
        {
            VR = (Mat3d[])VR.Clone(), VC = (Vec3d[])VC.Clone(),
            RF = (double[])RF.Clone(), RCx = (double[])RCx.Clone(), RCy = (double[])RCy.Clone(),
            RK1 = (double[])RK1.Clone(), RK2 = (double[])RK2.Clone(),
            PR = (Mat3d[])PR.Clone(), PC = (Vec3d[])PC.Clone(),
            PF = (double[])PF.Clone(), PCx = (double[])PCx.Clone(), PCy = (double[])PCy.Clone(),
            D = (double[])D.Clone(),
        };
    }

    // Local slots of one observation: the view's 6, the rig's 5, the projector's 9.
    private const int ViewSlots = 6, RigSlots = 5, ProjSlots = 9, Slots = ViewSlots + RigSlots + ProjSlots;

    private readonly MultiViewOptions _o;
    private readonly RigSpec[] _rigs;
    private readonly ViewSpec[] _views;
    private readonly ProjSpec[] _proj;
    private readonly TrackSpec[] _tracks;
    private readonly ObsSpec[] _obs;
    private readonly int[] _trackStart;
    private readonly int[][] _viewIdx;
    private readonly int[][] _rigIdx;
    private readonly int[][] _projIdx;
    private readonly bool[] _active;
    private readonly int _g;

    public MultiViewBundle(MultiViewOptions options, RigSpec[] rigs, ViewSpec[] views, ProjSpec[] projectors, TrackSpec[] tracks,
        ObsSpec[] observations, State start)
    {
        _o = options;
        _rigs = rigs;
        _views = views;
        _proj = projectors;
        _tracks = tracks;
        _obs = [.. observations.OrderBy(o => o.Track).ThenBy(o => o.View)];
        S = start;
        _trackStart = new int[tracks.Length + 1];
        foreach (var o in _obs)
            _trackStart[o.Track + 1]++;
        for (var t = 0; t < tracks.Length; t++)
            _trackStart[t + 1] += _trackStart[t];
        _active = Enumerable.Repeat(true, _obs.Length).ToArray();

        var g = 0;
        _viewIdx = new int[views.Length][];
        for (var v = 0; v < views.Length; v++)
        {
            var idx = Enumerable.Repeat(-1, ViewSlots).ToArray();
            if (!views[v].Fixed)
            {
                for (var a = 0; a < 3; a++)
                    idx[a] = g++;
                for (var a = 0; a < (views[v].Sphere ? 2 : 3); a++)
                    idx[3 + a] = g++;
            }
            _viewIdx[v] = idx;
        }
        _rigIdx = new int[rigs.Length][];
        for (var r = 0; r < rigs.Length; r++)
        {
            var idx = Enumerable.Repeat(-1, RigSlots).ToArray();
            if (!rigs[r].Held)
                for (var a = 0; a < RigSlots; a++)
                    idx[a] = g++;
            _rigIdx[r] = idx;
        }
        _projIdx = new int[projectors.Length][];
        for (var p = 0; p < projectors.Length; p++)
        {
            var idx = Enumerable.Repeat(-1, ProjSlots).ToArray();
            var spec = projectors[p];
            if (!spec.Held)
            {
                for (var a = 0; a < 3; a++)
                    idx[a] = g++;
                for (var a = 0; a < (spec.Sphere ? 2 : 3); a++)
                    idx[3 + a] = g++;
                if (!spec.LockFocal)
                    idx[6] = g++;
                if (!spec.LockShift)
                {
                    idx[7] = g++;
                    idx[8] = g++;
                }
            }
            _projIdx[p] = idx;
        }
        _g = g;
    }

    public State S { get; private set; }

    public int Parameters => _g;

    public int Iterations { get; private set; }

    public IReadOnlyList<ObsSpec> Observations => _obs;

    public bool IsActive(int observation) => _active[observation];

    // ---- the model ----

    private static (Vec3d E1, Vec3d E2) Basis(Vec3d b)
    {
        var helper = Math.Abs(b.X) < 0.9 ? Vec3d.UnitX : Vec3d.UnitY;
        var e1 = (helper - b * Vec3d.Dot(helper, b)).Normalized();
        return (e1, Vec3d.Cross(b, e1));
    }

    private LensDistortion RigLens(State s, int r)
    {
        var l = _rigs[r].Lens;
        return new LensDistortion(s.RK1[r], s.RK2[r], l.P1, l.P2, l.K3);
    }

    /// <summary>The projector's ray for pixel u in its own frame, scaled to z = 1, with the inverse lens Jacobian.</summary>
    private Vec3d ProjectorRay(State s, int p, Vec2d u, out Vec2d q, out (double A, double B, double C, double D) inv)
    {
        var spec = _proj[p];
        var aspect = spec.Prior.Fx > 0 ? spec.Prior.Fy / spec.Prior.Fx : 1;
        q = new Vec2d((u.X - s.PCx[p]) / s.PF[p], (u.Y - s.PCy[p]) / (s.PF[p] * aspect));
        if (spec.Lens.IsNone)
        {
            inv = (1, 0, 0, 1);
            return new Vec3d(q.X, q.Y, 1);
        }
        SolverMath.TryUndistort(q, spec.Lens, out var m);
        var (a, b, c, d) = SolverMath.DistortJacobian(m, spec.Lens);
        var det = a * d - b * c;
        inv = Math.Abs(det) > 1e-300 ? (d / det, -b / det, -c / det, a / det) : (0, 0, 0, 0);
        return new Vec3d(m.X, m.Y, 1);
    }

    /// <summary>The world point of a track at a depth.</summary>
    public Vec3d Point(State s, int t, double depth)
    {
        var tr = _tracks[t];
        var m = ProjectorRay(s, tr.Projector, tr.U, out _, out _);
        return s.PC[tr.Projector] + s.PR[tr.Projector] * m * depth;
    }

    /// <summary>An observation's residual (camera pixels); false behind either device.</summary>
    public bool Residual(State s, int i, out Vec2d res) => Linearise(s, i, null, null, out res, out _);

    /// <summary>The residual and, when spans are given, its derivatives by the local slots (jx, jy) and by the depth.</summary>
    private bool Linearise(State s, int i, double[]? jx, double[]? jy, out Vec2d res, out Vec2d jd)
    {
        res = default;
        jd = default;
        var o = _obs[i];
        var tr = _tracks[o.Track];
        var p = tr.Projector;
        var v = o.View;
        var r = _views[v].Rig;
        var depth = s.D[o.Track];
        if (!(depth > 0))
            return false;
        var m = ProjectorRay(s, p, tr.U, out var q, out var inv);
        var ray = s.PR[p] * m;
        var x = s.PC[p] + ray * depth;
        var w = x - s.VC[v];
        var rv = s.VR[v];
        var y = rv.Transposed() * w;
        if (y.Z <= 1e-9 * y.Length)
            return false;
        var iz = 1 / y.Z;
        var n = new Vec2d(y.X * iz, y.Y * iz);
        var lens = RigLens(s, r);
        var dn = Geometry.CameraModel.Distort(n, lens);
        var f = s.RF[r];
        var rigPrior = _rigs[r].Prior;
        var aspect = rigPrior.Fx > 0 ? rigPrior.Fy / rigPrior.Fx : 1;
        res = new Vec2d(f * dn.X + s.RCx[r] - o.X.X, f * aspect * dn.Y + s.RCy[r] - o.X.Y);
        if (!double.IsFinite(res.X) || !double.IsFinite(res.Y))
            return false;
        if (jx is null || jy is null)
            return true;

        Array.Clear(jx);
        Array.Clear(jy);
        var (a, b, c, d) = SolverMath.DistortJacobian(n, lens);
        // ∂pixel/∂Y, then ∂pixel/∂X = ∂pixel/∂Y · Rᵀ (as row vectors: R·row).
        var jy0 = new Vec3d(a * iz, b * iz, -(a * n.X + b * n.Y) * iz) * f;
        var jy1 = new Vec3d(c * iz, d * iz, -(c * n.X + d * n.Y) * iz) * (f * aspect);
        var jx0 = rv * jy0;
        var jx1 = rv * jy1;
        jd = new Vec2d(Vec3d.Dot(jx0, ray), Vec3d.Dot(jx1, ray));

        // The view: a left rotation increment ω moves Y by Rᵀ·[w]×·ω; the centre moves Y by −Rᵀ.
        var vs = _views[v];
        Put(jx, jy, 0, Vec3d.Cross(jx0, w), Vec3d.Cross(jx1, w));
        if (vs.Sphere)
        {
            var (e1, e2) = Basis((s.VC[v] - vs.Anchor).Normalized());
            jx[3] = -Vec3d.Dot(jx0, e1) * vs.Radius;
            jy[3] = -Vec3d.Dot(jx1, e1) * vs.Radius;
            jx[4] = -Vec3d.Dot(jx0, e2) * vs.Radius;
            jy[4] = -Vec3d.Dot(jx1, e2) * vs.Radius;
        }
        else
        {
            Put(jx, jy, 3, -jx0, -jx1);
        }

        // The rig.
        var r2 = n.LengthSquared;
        const int R0 = ViewSlots;
        jx[R0] = dn.X;
        jy[R0] = aspect * dn.Y;
        jx[R0 + 1] = 1;
        jy[R0 + 2] = 1;
        jx[R0 + 3] = f * n.X * r2;
        jy[R0 + 3] = f * aspect * n.Y * r2;
        jx[R0 + 4] = f * n.X * r2 * r2;
        jy[R0 + 4] = f * aspect * n.Y * r2 * r2;

        // The projector: X moves with its centre, turns with its rotation (dX/dω = −d·[ray]×), and slides along the
        // raster as its focal length and principal point change the ray.
        const int P0 = ViewSlots + RigSlots;
        var ps = _proj[p];
        Put(jx, jy, P0, Vec3d.Cross(jx0, ray) * -depth, Vec3d.Cross(jx1, ray) * -depth);
        if (ps.Sphere)
        {
            var (e1, e2) = Basis((s.PC[p] - ps.Anchor).Normalized());
            jx[P0 + 3] = Vec3d.Dot(jx0, e1) * ps.Radius;
            jy[P0 + 3] = Vec3d.Dot(jx1, e1) * ps.Radius;
            jx[P0 + 4] = Vec3d.Dot(jx0, e2) * ps.Radius;
            jy[P0 + 4] = Vec3d.Dot(jx1, e2) * ps.Radius;
        }
        else
        {
            Put(jx, jy, P0 + 3, jx0, jx1);
        }
        var pAspect = ps.Prior.Fx > 0 ? ps.Prior.Fy / ps.Prior.Fx : 1;
        var pf = s.PF[p];
        Vec3d Through(Vec2d dq) => s.PR[p] * new Vec3d(inv.A * dq.X + inv.B * dq.Y, inv.C * dq.X + inv.D * dq.Y, 0) * depth;
        var dF = Through(new Vec2d(-q.X / pf, -q.Y / pf));
        var dCx = Through(new Vec2d(-1 / pf, 0));
        var dCy = Through(new Vec2d(0, -1 / (pf * pAspect)));
        jx[P0 + 6] = Vec3d.Dot(jx0, dF);
        jy[P0 + 6] = Vec3d.Dot(jx1, dF);
        jx[P0 + 7] = Vec3d.Dot(jx0, dCx);
        jy[P0 + 7] = Vec3d.Dot(jx1, dCx);
        jx[P0 + 8] = Vec3d.Dot(jx0, dCy);
        jy[P0 + 8] = Vec3d.Dot(jx1, dCy);
        return true;
    }

    private static void Put(double[] jx, double[] jy, int at, Vec3d rowX, Vec3d rowY)
    {
        jx[at] = rowX.X;
        jx[at + 1] = rowX.Y;
        jx[at + 2] = rowX.Z;
        jy[at] = rowY.X;
        jy[at + 1] = rowY.Y;
        jy[at + 2] = rowY.Z;
    }

    private void SlotIndices(int i, int[] idx)
    {
        var o = _obs[i];
        var v = o.View;
        var r = _views[v].Rig;
        var p = _tracks[o.Track].Projector;
        Array.Copy(_viewIdx[v], 0, idx, 0, ViewSlots);
        Array.Copy(_rigIdx[r], 0, idx, ViewSlots, RigSlots);
        Array.Copy(_projIdx[p], 0, idx, ViewSlots + RigSlots, ProjSlots);
    }

    private double Huber(double e) => e <= _o.HuberPx ? e * e : 2 * _o.HuberPx * e - _o.HuberPx * _o.HuberPx;

    /// <summary>The weight IRLS gives a residual of length e (so the weighted square matches Huber's slope).</summary>
    private double HuberWeight(double e) => e <= _o.HuberPx ? 1 : _o.HuberPx / e;

    /// <summary>Huber cost of the active observations plus the priors.</summary>
    public double Cost(State s)
    {
        double cost = 0;
        for (var i = 0; i < _obs.Length; i++)
        {
            if (!_active[i])
                continue;
            cost += Residual(s, i, out var res) ? Huber(res.Length) : Behind * Behind;
        }
        return cost + PriorCost(s);
    }

    private double PriorCost(State s)
    {
        double cost = 0;
        for (var r = 0; r < _rigs.Length; r++)
        {
            if (_rigs[r].Held)
                continue;
            var k = _rigs[r].Prior;
            var l = _rigs[r].Lens;
            cost += Sq((s.RF[r] - k.Fx) / (_o.CameraFocalPrior * k.Fx));
            var sp = _o.PrincipalPointPrior * k.Width;
            cost += Sq((s.RCx[r] - k.Cx) / sp) + Sq((s.RCy[r] - k.Cy) / sp);
            cost += Sq((s.RK1[r] - l.K1) / _o.DistortionPrior) + Sq((s.RK2[r] - l.K2) / _o.DistortionPrior);
        }
        for (var p = 0; p < _proj.Length; p++)
        {
            var spec = _proj[p];
            if (spec.Held)
                continue;
            if (!spec.LockFocal)
                cost += Sq((s.PF[p] - spec.Prior.Fx) / (_o.ProjectorThrowPrior * spec.Prior.Fx));
            if (!spec.LockShift)
            {
                var sp = _o.PrincipalPointPrior * spec.Prior.Width;
                cost += Sq((s.PCx[p] - spec.Prior.Cx) / sp) + Sq((s.PCy[p] - spec.Prior.Cy) / sp);
            }
        }
        return cost;
    }

    private static double Sq(double v) => v * v;

    /// <summary>Adds the priors' normal equations (each a residual (x − x₀)/σ with derivative 1/σ).</summary>
    private void AddPriors(State s, DenseMatrix u, double[] gg)
    {
        void Add(int index, double value, double prior, double sigma)
        {
            if (index < 0)
                return;
            u[index, index] += 1 / (sigma * sigma);
            gg[index] += (value - prior) / (sigma * sigma);
        }
        for (var r = 0; r < _rigs.Length; r++)
        {
            if (_rigs[r].Held)
                continue;
            var k = _rigs[r].Prior;
            var l = _rigs[r].Lens;
            var idx = _rigIdx[r];
            var sp = _o.PrincipalPointPrior * k.Width;
            Add(idx[0], s.RF[r], k.Fx, _o.CameraFocalPrior * k.Fx);
            Add(idx[1], s.RCx[r], k.Cx, sp);
            Add(idx[2], s.RCy[r], k.Cy, sp);
            Add(idx[3], s.RK1[r], l.K1, _o.DistortionPrior);
            Add(idx[4], s.RK2[r], l.K2, _o.DistortionPrior);
        }
        for (var p = 0; p < _proj.Length; p++)
        {
            var spec = _proj[p];
            if (spec.Held)
                continue;
            var idx = _projIdx[p];
            var sp = _o.PrincipalPointPrior * spec.Prior.Width;
            Add(idx[6], s.PF[p], spec.Prior.Fx, _o.ProjectorThrowPrior * spec.Prior.Fx);
            Add(idx[7], s.PCx[p], spec.Prior.Cx, sp);
            Add(idx[8], s.PCy[p], spec.Prior.Cy, sp);
        }
    }

    // ---- the normal equations, the depth block eliminated ----

    /// <summary>One track's coupling to the global parameters: Σ Jgᵀ·Jd over its observations, sparse.</summary>
    private readonly record struct Coupling(int[] Index, double[] Value);

    private sealed class Normal(int g, int tracks)
    {
        public readonly DenseMatrix U = new(g, g);
        public readonly double[] G = new double[g];
        public readonly double[] V = new double[tracks];
        public readonly double[] Gd = new double[tracks];
        public readonly Coupling[] W = new Coupling[tracks];
    }

    /// <summary>The weighted normal equations at a state, data and priors.</summary>
    private Normal Accumulate(State s)
    {
        var n = new Normal(_g, _tracks.Length);
        var jx = new double[Slots];
        var jy = new double[Slots];
        var idx = new int[Slots];
        var w = new double[_g];
        var touched = new List<int>();
        var mark = new bool[_g];
        for (var t = 0; t < _tracks.Length; t++)
        {
            for (var i = _trackStart[t]; i < _trackStart[t + 1]; i++)
            {
                if (!_active[i] || !Linearise(s, i, jx, jy, out var res, out var jd))
                    continue;
                var sw = Math.Sqrt(HuberWeight(res.Length));
                var rx = res.X * sw;
                var ry = res.Y * sw;
                var jdx = jd.X * sw;
                var jdy = jd.Y * sw;
                SlotIndices(i, idx);
                for (var a = 0; a < Slots; a++)
                {
                    var ia = idx[a];
                    if (ia < 0)
                        continue;
                    var ja0 = jx[a] * sw;
                    var ja1 = jy[a] * sw;
                    n.G[ia] += ja0 * rx + ja1 * ry;
                    w[ia] += ja0 * jdx + ja1 * jdy;
                    if (!mark[ia])
                    {
                        mark[ia] = true;
                        touched.Add(ia);
                    }
                    var row = ia * _g;
                    for (var b2 = 0; b2 < Slots; b2++)
                    {
                        var ib = idx[b2];
                        if (ib >= 0)
                            n.U.Data[row + ib] += ja0 * jx[b2] * sw + ja1 * jy[b2] * sw;
                    }
                }
                n.V[t] += jdx * jdx + jdy * jdy;
                n.Gd[t] += jdx * rx + jdy * ry;
            }
            touched.Sort();
            n.W[t] = new Coupling([.. touched], touched.Select(k => w[k]).ToArray());
            foreach (var k in touched)
            {
                w[k] = 0;
                mark[k] = false;
            }
            touched.Clear();
        }
        AddPriors(s, n.U, n.G);
        return n;
    }

    /// <summary>The reduced system S = U − W·V⁻¹·Wᵀ (damped by λ on every diagonal) and its right-hand side.</summary>
    private (DenseMatrix S, double[] Rhs) Reduce(Normal n, double lambda)
    {
        var s = n.U.Clone();
        for (var k = 0; k < _g; k++)
            s[k, k] += lambda * Math.Max(n.U[k, k], 1e-12);
        var rhs = new double[_g];
        for (var k = 0; k < _g; k++)
            rhs[k] = -n.G[k];
        for (var t = 0; t < _tracks.Length; t++)
        {
            var vt = n.V[t] * (1 + lambda);
            if (!(vt > 0))
                continue;
            var c = n.W[t];
            for (var a = 0; a < c.Index.Length; a++)
            {
                var ia = c.Index[a];
                var wa = c.Value[a] / vt;
                rhs[ia] += wa * n.Gd[t];
                var row = ia * _g;
                for (var b = 0; b < c.Index.Length; b++)
                    s.Data[row + c.Index[b]] -= wa * c.Value[b];
            }
        }
        return (s, rhs);
    }

    private bool Step(Normal n, double lambda, out double[] dg, out double[] dd)
    {
        var (s, rhs) = Reduce(n, lambda);
        dg = new double[_g];
        dd = new double[_tracks.Length];
        if (_g > 0 && !LinearSolve.TryCholesky(s, rhs, dg))
            return false;
        for (var t = 0; t < _tracks.Length; t++)
        {
            var vt = n.V[t] * (1 + lambda);
            if (!(vt > 0))
                continue;
            var c = n.W[t];
            var sum = n.Gd[t];
            for (var a = 0; a < c.Index.Length; a++)
                sum += c.Value[a] * dg[c.Index[a]];
            dd[t] = -sum / vt;
        }
        return dg.All(double.IsFinite) && dd.All(double.IsFinite);
    }

    private State Stepped(State s, double[] dg, double[] dd)
    {
        var next = s.Clone();
        double At(int index) => index >= 0 ? dg[index] : 0;
        for (var v = 0; v < _views.Length; v++)
        {
            var idx = _viewIdx[v];
            if (_views[v].Fixed)
                continue;
            next.VR[v] = (Mat3d.FromRotationVector(new Vec3d(At(idx[0]), At(idx[1]), At(idx[2]))) * s.VR[v]).NearestRotation();
            next.VC[v] = Moved(s.VC[v], _views[v].Sphere, _views[v].Anchor, _views[v].Radius, At(idx[3]), At(idx[4]), At(idx[5]));
        }
        for (var r = 0; r < _rigs.Length; r++)
        {
            var idx = _rigIdx[r];
            next.RF[r] += At(idx[0]);
            next.RCx[r] += At(idx[1]);
            next.RCy[r] += At(idx[2]);
            next.RK1[r] += At(idx[3]);
            next.RK2[r] += At(idx[4]);
        }
        for (var p = 0; p < _proj.Length; p++)
        {
            var idx = _projIdx[p];
            var spec = _proj[p];
            if (spec.Held)
                continue;
            next.PR[p] = (Mat3d.FromRotationVector(new Vec3d(At(idx[0]), At(idx[1]), At(idx[2]))) * s.PR[p]).NearestRotation();
            next.PC[p] = Moved(s.PC[p], spec.Sphere, spec.Anchor, spec.Radius, At(idx[3]), At(idx[4]), At(idx[5]));
            next.PF[p] += At(idx[6]);
            next.PCx[p] += At(idx[7]);
            next.PCy[p] += At(idx[8]);
        }
        for (var t = 0; t < _tracks.Length; t++)
            next.D[t] += dd[t];
        return next;
    }

    private static Vec3d Moved(Vec3d c, bool sphere, Vec3d anchor, double radius, double a, double b, double z)
    {
        if (!sphere)
            return c + new Vec3d(a, b, z);
        var dir = (c - anchor).Normalized();
        var (e1, e2) = Basis(dir);
        return anchor + (dir + e1 * a + e2 * b).Normalized() * radius;
    }

    /// <summary>Levenberg–Marquardt from the current state; returns the iterations taken.</summary>
    public int Adjust(CancellationToken ct)
    {
        var lambda = 1e-3;
        var cost = Cost(S);
        var iter = 0;
        while (iter < _o.MaxIterations)
        {
            ct.ThrowIfCancellationRequested();
            iter++;
            var n = Accumulate(S);
            var accepted = false;
            var settled = false;
            for (var attempt = 0; attempt < 16 && !accepted; attempt++)
            {
                if (!Step(n, lambda, out var dg, out var dd))
                {
                    lambda *= 8;
                    continue;
                }
                var trial = Stepped(S, dg, dd);
                var trialCost = Cost(trial);
                if (double.IsFinite(trialCost) && trialCost < cost)
                {
                    settled = (cost - trialCost) / Math.Max(cost, 1e-300) < 1e-10;
                    S = trial;
                    cost = trialCost;
                    lambda = Math.Max(lambda / 4, 1e-9);
                    accepted = true;
                }
                else
                {
                    lambda *= 8;
                }
            }
            if (!accepted || settled)
                break;
        }
        Iterations += iter;
        return iter;
    }

    /// <summary>Drops the active observations whose residual is beyond the outlier distance (or that fell behind a
    /// device); true when any was dropped.</summary>
    public bool Trim()
    {
        var any = false;
        for (var i = 0; i < _obs.Length; i++)
        {
            if (!_active[i])
                continue;
            if (!Residual(S, i, out var res) || res.Length > _o.OutlierPx)
            {
                _active[i] = false;
                any = true;
            }
        }
        return any;
    }

    /// <summary>The posterior covariance of the global parameters, σ̂²·S⁻¹ with S the reduced system (priors
    /// included, no damping) and σ̂² the data's own residual variance per degree of freedom; null when singular.</summary>
    public Func<int, int, double>? Covariance()
    {
        if (_g == 0)
            return null;
        var n = Accumulate(S);
        var (s, _) = Reduce(n, 0);
        double data = 0;
        var used = 0;
        for (var i = 0; i < _obs.Length; i++)
        {
            if (_active[i] && Residual(S, i, out var res))
            {
                data += res.LengthSquared;
                used++;
            }
        }
        var dof = 2 * used - _g - _tracks.Length;
        var sigma2 = dof > 0 ? data / dof : 1;
        var columns = new Dictionary<int, double[]>();
        return (a, b) =>
        {
            if (a < 0 || b < 0)
                return 0;
            if (!columns.TryGetValue(b, out var col))
            {
                var e = new double[_g];
                e[b] = 1;
                col = new double[_g];
                if (!LinearSolve.TryCholesky(s, e, col))
                    col = Enumerable.Repeat(double.NaN, _g).ToArray();
                columns[b] = col;
            }
            return sigma2 * col[a];
        };
    }

    /// <summary>The 3 × 3 covariance of a projector's centre (zero for a held one).</summary>
    public Mat3d CentreCovariance(Func<int, int, double> cov, int p)
    {
        var spec = _proj[p];
        var idx = _projIdx[p];
        if (spec.Held)
            return new Mat3d(0, 0, 0, 0, 0, 0, 0, 0, 0);
        if (!spec.Sphere)
            return Mat3d.FromArray([
                cov(idx[3], idx[3]), cov(idx[3], idx[4]), cov(idx[3], idx[5]),
                cov(idx[4], idx[3]), cov(idx[4], idx[4]), cov(idx[4], idx[5]),
                cov(idx[5], idx[3]), cov(idx[5], idx[4]), cov(idx[5], idx[5])]);
        var (e1, e2) = Basis((S.PC[p] - spec.Anchor).Normalized());
        var a = e1 * spec.Radius;
        var b = e2 * spec.Radius;
        double caa = cov(idx[3], idx[3]), cab = cov(idx[3], idx[4]), cbb = cov(idx[4], idx[4]);
        Vec3d Row(int k) => a * (a[k] * caa + b[k] * cab) + b * (a[k] * cab + b[k] * cbb);
        return Mat3d.FromRows(Row(0), Row(1), Row(2));
    }

    public int RigFocalIndex(int r) => _rigIdx[r][0];
}
