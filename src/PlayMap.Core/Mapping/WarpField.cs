using PlayMap.Core.Model;
using PlayMap.Core.Numerics;

namespace PlayMap.Core.Mapping;

/// <summary>An output's whole 2D warp compiled once: content point (u, v) in the pre-warp picture (0..1,
/// y down) ↦ output point (normalised output units, y down), as three layers in order:
/// <list type="number">
/// <item>the lattice (<see cref="LatticeSurface"/>), in the unit square's frame;</item>
/// <item>the corner pin, the homography taking the unit square onto the pinned quad — so the lattice rides
/// inside the pin and straight lines of a lattice at rest stay straight;</item>
/// <item>the pin warp (<see cref="PinWarpField"/>), a residual in output space.</item>
/// </list>
/// A disabled warp, or one whose layers all rest, is the identity. Immutable and thread-safe.</summary>
public sealed class WarpField
{
    private readonly LatticeSurface? _lattice;
    private readonly Mat3d _pin;
    private readonly bool _hasPin;
    private readonly PinWarpField? _pins;
    private readonly Lazy<Vec2d[]> _seeds;
    private const int SeedGrid = 24;

    public static readonly WarpField Identity = new(OutputWarp.None);

    public WarpField(OutputWarp warp, PinWarp? pins = null)
    {
        ArgumentNullException.ThrowIfNull(warp);
        if (warp.Enabled)
        {
            if (warp.Lattice is { } l && !l.IsAtRest)
                _lattice = new LatticeSurface(l);
            if (!warp.Pin.IsNone)
            {
                _pin = QuadHomography.ForPin(warp.Pin);
                _hasPin = _pin != Mat3d.Identity;
                PinIsValid = QuadHomography.IsValid(warp.Pin);
            }
            _pins = pins?.Compile();
        }
        if (!_hasPin)
            _pin = Mat3d.Identity;
        _seeds = new Lazy<Vec2d[]>(BuildSeeds, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>True when every layer rests: drawing through the warp changes nothing.</summary>
    public bool IsIdentity => _lattice is null && !_hasPin && _pins is null;

    /// <summary>False when the corner pin's quad is not convex (it folds or flips through infinity).</summary>
    public bool PinIsValid { get; } = true;

    /// <summary>The lattice layer, when it moves anything.</summary>
    public LatticeSurface? Lattice => _lattice;

    /// <summary>The corner pin's homography (identity when it rests).</summary>
    public Mat3d PinHomography => _pin;

    /// <summary>The output point a content point lands on.</summary>
    public Vec2d Evaluate(double u, double v)
    {
        var p = _lattice is null ? new Vec2d(u, v) : _lattice.Evaluate(u, v);
        if (_hasPin)
            p = QuadHomography.Apply(_pin, p);
        if (_pins is not null)
            p = _pins.Apply(p);
        return p;
    }

    /// <summary>The output point of a lattice point already evaluated (the layers after the lattice).</summary>
    public Vec2d AfterLattice(Vec2d p)
    {
        if (_hasPin)
            p = QuadHomography.Apply(_pin, p);
        if (_pins is not null)
            p = _pins.Apply(p);
        return p;
    }

    /// <summary>The content point (u, v) that lands on output point (x, y) — a click on the output taken
    /// back into the picture. Seeds from a coarse sample of the warp (the cell of the sample grid that
    /// contains the point, or the nearest sample), then Newton with backtracking. Null when no preimage
    /// converges (the point is off a folded warp). The answer may lie outside 0..1 when the click is outside
    /// the picture but the layers extend there (a pin's perspective does; a lattice extends its border cells).</summary>
    public Vec2d? Invert(double x, double y)
    {
        var target = new Vec2d(x, y);
        if (IsIdentity)
            return target;
        if (_lattice is null && _pins is null)
            return _pin.TryInverse(out var inv) ? QuadHomography.Apply(inv, target) : null;
        var uv = Seed(target);
        var f = Evaluate(uv.X, uv.Y) - target;
        var err = f.LengthSquared;
        for (var it = 0; it < 60 && err > 1e-26; it++)
        {
            var (du, dv) = Jacobian(uv);
            var det = du.X * dv.Y - dv.X * du.Y;
            if (Math.Abs(det) < 1e-18)
                break;
            var step = new Vec2d((dv.Y * f.X - dv.X * f.Y) / det, (-du.Y * f.X + du.X * f.Y) / det);
            var lambda = 1.0;
            var improved = false;
            for (var ls = 0; ls < 20; ls++)
            {
                var trial = uv - step * lambda;
                var ft = Evaluate(trial.X, trial.Y) - target;
                var et = ft.LengthSquared;
                if (et < err)
                {
                    uv = trial;
                    f = ft;
                    err = et;
                    improved = true;
                    break;
                }
                lambda *= 0.5;
            }
            if (!improved)
                break;
        }
        return err < 1e-18 ? uv : null;
    }

    /// <summary>The Jacobian of the whole warp at a content point, as its columns (∂/∂u, ∂/∂v).</summary>
    public (Vec2d Du, Vec2d Dv) Jacobian(Vec2d uv)
    {
        Vec2d p, du, dv;
        if (_lattice is null)
        {
            p = uv;
            du = new Vec2d(1, 0);
            dv = new Vec2d(0, 1);
        }
        else
        {
            (p, du, dv) = _lattice.EvaluateWithDerivatives(uv.X, uv.Y);
        }
        if (_hasPin)
        {
            var (hx, hy) = QuadHomography.Jacobian(_pin, p);
            du = hx * du.X + hy * du.Y;
            dv = hx * dv.X + hy * dv.Y;
            p = QuadHomography.Apply(_pin, p);
        }
        if (_pins is not null)
        {
            const double h = 1e-6;
            var tx = (_pins.Apply(new Vec2d(p.X + h, p.Y)) - _pins.Apply(new Vec2d(p.X - h, p.Y))) / (2 * h);
            var ty = (_pins.Apply(new Vec2d(p.X, p.Y + h)) - _pins.Apply(new Vec2d(p.X, p.Y - h))) / (2 * h);
            du = tx * du.X + ty * du.Y;
            dv = tx * dv.X + ty * dv.Y;
        }
        return (du, dv);
    }

    private Vec2d[] BuildSeeds()
    {
        var seeds = new Vec2d[(SeedGrid + 1) * (SeedGrid + 1)];
        for (var j = 0; j <= SeedGrid; j++)
            for (var i = 0; i <= SeedGrid; i++)
                seeds[j * (SeedGrid + 1) + i] = Evaluate(i / (double)SeedGrid, j / (double)SeedGrid);
        return seeds;
    }

    private Vec2d Seed(Vec2d target)
    {
        var s = _seeds.Value;
        const int n = SeedGrid + 1;
        // A sample cell whose quad contains the target gives a bilinear-inverse seed inside the right fold.
        for (var j = 0; j < SeedGrid; j++)
            for (var i = 0; i < SeedGrid; i++)
            {
                var a = s[j * n + i];
                var b = s[j * n + i + 1];
                var c = s[(j + 1) * n + i + 1];
                var d = s[(j + 1) * n + i];
                if (InQuad(target, a, b, c, d))
                {
                    var local = BilinearInverse(target, a, b, c, d);
                    return new Vec2d((i + local.X) / SeedGrid, (j + local.Y) / SeedGrid);
                }
            }
        var best = 0;
        var bestD = double.MaxValue;
        for (var k = 0; k < s.Length; k++)
        {
            var d = (s[k] - target).LengthSquared;
            if (d < bestD)
            {
                bestD = d;
                best = k;
            }
        }
        return new Vec2d(best % n / (double)SeedGrid, best / n / (double)SeedGrid);
    }

    private static bool InQuad(Vec2d p, Vec2d a, Vec2d b, Vec2d c, Vec2d d)
    {
        var s1 = Math.Sign(Vec2d.Cross(b - a, p - a));
        var s2 = Math.Sign(Vec2d.Cross(c - b, p - b));
        var s3 = Math.Sign(Vec2d.Cross(d - c, p - c));
        var s4 = Math.Sign(Vec2d.Cross(a - d, p - d));
        var pos = (s1 >= 0 ? 1 : 0) + (s2 >= 0 ? 1 : 0) + (s3 >= 0 ? 1 : 0) + (s4 >= 0 ? 1 : 0);
        var neg = (s1 <= 0 ? 1 : 0) + (s2 <= 0 ? 1 : 0) + (s3 <= 0 ? 1 : 0) + (s4 <= 0 ? 1 : 0);
        return pos == 4 || neg == 4;
    }

    /// <summary>(s, t) in the unit square with bilinear(a, b, c, d; s, t) ≈ p, by a few Newton steps.</summary>
    private static Vec2d BilinearInverse(Vec2d p, Vec2d a, Vec2d b, Vec2d c, Vec2d d)
    {
        double s = 0.5, t = 0.5;
        for (var it = 0; it < 8; it++)
        {
            var q = (1 - s) * (1 - t) * a + s * (1 - t) * b + s * t * c + (1 - s) * t * d;
            var qs = (1 - t) * (b - a) + t * (c - d);
            var qt = (1 - s) * (d - a) + s * (c - b);
            var f = q - p;
            var det = qs.X * qt.Y - qt.X * qs.Y;
            if (Math.Abs(det) < 1e-20)
                break;
            s -= (qt.Y * f.X - qt.X * f.Y) / det;
            t -= (-qs.Y * f.X + qs.X * f.Y) / det;
        }
        return new Vec2d(Math.Clamp(s, 0, 1), Math.Clamp(t, 0, 1));
    }
}
