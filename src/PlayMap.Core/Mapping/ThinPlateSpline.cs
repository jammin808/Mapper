using System.Collections.Immutable;
using PlayMap.Core.Numerics;

namespace PlayMap.Core.Mapping;

/// <summary>A 2D → 2D thin-plate spline: f(p) = a₀ + a₁x + a₂y + Σ wᵢ·U(|p − cᵢ|), U(r) = r²·ln r, the
/// smoothest (least bending energy) map that takes every centre to its value. With
/// <paramref name="regularization"/> 0 it interpolates exactly; above 0 it trades exactness for smoothness
/// (λ on the kernel's diagonal). Immutable and thread-safe after construction.</summary>
public sealed class ThinPlateSpline
{
    private readonly Vec2d[] _centres;
    private readonly Vec2d[] _weights;
    private Vec2d _a0;
    private Vec2d _ax;
    private Vec2d _ay;

    public int Count => _centres.Length;

    public ThinPlateSpline(IReadOnlyList<Vec2d> centres, IReadOnlyList<Vec2d> values, double regularization = 0)
    {
        ArgumentNullException.ThrowIfNull(centres);
        ArgumentNullException.ThrowIfNull(values);
        if (centres.Count != values.Count)
            throw new ArgumentException("One value per centre.", nameof(values));
        var n = centres.Count;
        _centres = [.. centres];
        _weights = new Vec2d[n];
        if (n == 0)
            return;
        // Coincident centres (or all on one line) leave the exact system singular: a touch of smoothing
        // makes it definite. Fewer than three centres, or a system still singular, cannot pin an affine
        // part: the field is then the values' mean, a constant.
        if (n >= 3 && (TryFit(values, Math.Max(0, regularization)) || TryFit(values, Math.Max(regularization, 1e-6))))
            return;
        var mean = Vec2d.Zero;
        foreach (var v in values)
            mean += v;
        _a0 = mean / n;
    }

    /// <summary>Solves [K + λI  P; Pᵀ 0]·[w; a] = [v; 0] (P = [1 x y]) for both components at once.</summary>
    private bool TryFit(IReadOnlyList<Vec2d> values, double lambda)
    {
        var n = _centres.Length;
        var m = n + 3;
        var a = new double[m * m];
        var rhs = new double[m * 2];
        for (var i = 0; i < n; i++)
        {
            for (var j = 0; j < n; j++)
                a[i * m + j] = Kernel((_centres[i] - _centres[j]).LengthSquared) + (i == j ? lambda : 0);
            a[i * m + n] = 1;
            a[i * m + n + 1] = _centres[i].X;
            a[i * m + n + 2] = _centres[i].Y;
            a[n * m + i] = 1;
            a[(n + 1) * m + i] = _centres[i].X;
            a[(n + 2) * m + i] = _centres[i].Y;
            rhs[i * 2] = values[i].X;
            rhs[i * 2 + 1] = values[i].Y;
        }
        if (!SolveInPlace(a, rhs, m))
            return false;
        for (var i = 0; i < n; i++)
            _weights[i] = new Vec2d(rhs[i * 2], rhs[i * 2 + 1]);
        _a0 = new Vec2d(rhs[n * 2], rhs[n * 2 + 1]);
        _ax = new Vec2d(rhs[(n + 1) * 2], rhs[(n + 1) * 2 + 1]);
        _ay = new Vec2d(rhs[(n + 2) * 2], rhs[(n + 2) * 2 + 1]);
        return true;
    }

    /// <summary>The spline's value at a point.</summary>
    public Vec2d Evaluate(Vec2d p)
    {
        var x = _a0.X + _ax.X * p.X + _ay.X * p.Y;
        var y = _a0.Y + _ax.Y * p.X + _ay.Y * p.Y;
        for (var i = 0; i < _centres.Length; i++)
        {
            var c = _centres[i];
            var dx = p.X - c.X;
            var dy = p.Y - c.Y;
            var k = Kernel(dx * dx + dy * dy);
            x += _weights[i].X * k;
            y += _weights[i].Y * k;
        }
        return new Vec2d(x, y);
    }

    /// <summary>U(r) = r² ln r written in r² (½ r² ln r²), 0 at r = 0.</summary>
    private static double Kernel(double r2) => r2 < 1e-300 ? 0 : 0.5 * r2 * Math.Log(r2);

    /// <summary>Gaussian elimination with partial pivoting on an m×m system with two right-hand sides
    /// (rhs interleaved, 2 per row); false when a pivot vanishes.</summary>
    private static bool SolveInPlace(double[] a, double[] rhs, int m)
    {
        var scale = 0.0;
        foreach (var v in a)
            scale = Math.Max(scale, Math.Abs(v));
        var tiny = Math.Max(scale, 1) * 1e-13;
        for (var col = 0; col < m; col++)
        {
            var pivot = col;
            var best = Math.Abs(a[col * m + col]);
            for (var r = col + 1; r < m; r++)
            {
                var v = Math.Abs(a[r * m + col]);
                if (v > best)
                {
                    best = v;
                    pivot = r;
                }
            }
            if (best < tiny)
                return false;
            if (pivot != col)
            {
                for (var c = 0; c < m; c++)
                    (a[col * m + c], a[pivot * m + c]) = (a[pivot * m + c], a[col * m + c]);
                (rhs[col * 2], rhs[pivot * 2]) = (rhs[pivot * 2], rhs[col * 2]);
                (rhs[col * 2 + 1], rhs[pivot * 2 + 1]) = (rhs[pivot * 2 + 1], rhs[col * 2 + 1]);
            }
            var inv = 1 / a[col * m + col];
            for (var r = col + 1; r < m; r++)
            {
                var f = a[r * m + col] * inv;
                if (f == 0)
                    continue;
                for (var c = col; c < m; c++)
                    a[r * m + c] -= f * a[col * m + c];
                rhs[r * 2] -= f * rhs[col * 2];
                rhs[r * 2 + 1] -= f * rhs[col * 2 + 1];
            }
        }
        for (var r = m - 1; r >= 0; r--)
        {
            double sx = rhs[r * 2], sy = rhs[r * 2 + 1];
            for (var c = r + 1; c < m; c++)
            {
                sx -= a[r * m + c] * rhs[c * 2];
                sy -= a[r * m + c] * rhs[c * 2 + 1];
            }
            var d = a[r * m + r];
            rhs[r * 2] = sx / d;
            rhs[r * 2 + 1] = sy / d;
        }
        return true;
    }
}

/// <summary>One pin of a pin warp: the output point (normalised output units, after the lattice and the
/// corner pin) that must move, and where it must land.</summary>
public readonly record struct WarpPin(Vec2d From, Vec2d To, bool Enabled = true)
{
    public Vec2d Displacement => To - From;
}

/// <summary>A sparse residual warp for one-off fixes without lattice density: a handful of pins, each
/// dragging the picture point at <see cref="WarpPin.From"/> onto <see cref="WarpPin.To"/>, smoothly
/// interpolated between them by a thin-plate spline of the displacements. It is the last layer of an
/// output's warp — lattice, corner pin, then the pins — in output space, so a fix stays on the bump it
/// fixes when the pin or the lattice is retouched. With <see cref="AnchorEdges"/> the raster's border is
/// held still (zero displacement at points spaced along it), so a pin's pull decays to nothing at the
/// frame; without, a single pin translates the whole picture. <see cref="Stiffness"/> 0 lands every pin
/// exactly; above 0 the field is smoother and the pins land near.</summary>
public sealed record PinWarp(ImmutableArray<WarpPin> Pins, bool AnchorEdges, double Stiffness)
{
    /// <summary>Anchors per border side when <see cref="AnchorEdges"/> is set.</summary>
    public const int AnchorsPerSide = 8;

    public static readonly PinWarp None = new([], true, 0);

    public bool IsNone => Pins.IsDefaultOrEmpty || Pins.All(p => !p.Enabled || p.From == p.To);

    /// <summary>The compiled field, or null when no enabled pin moves anything.</summary>
    public PinWarpField? Compile() => IsNone ? null : new PinWarpField(this);

    /// <summary>The warp with one more pin: the point currently shown at <paramref name="shownAt"/>
    /// (through the existing pins) is dragged to <paramref name="to"/>.</summary>
    public PinWarp WithPin(Vec2d shownAt, Vec2d to)
    {
        var from = Compile() is { } field ? field.Inverse(shownAt) : shownAt;
        return this with { Pins = (Pins.IsDefault ? [] : Pins).Add(new WarpPin(from, to)) };
    }

    /// <summary>The warp with pin <paramref name="index"/> landing somewhere else.</summary>
    public PinWarp WithPinMoved(int index, Vec2d to) =>
        index < 0 || index >= Pins.Length ? this : this with { Pins = Pins.SetItem(index, Pins[index] with { To = to }) };

    public PinWarp WithoutPin(int index) =>
        index < 0 || index >= Pins.Length ? this : this with { Pins = Pins.RemoveAt(index) };

    /// <summary>The pin whose landing point is nearest <paramref name="at"/> within <paramref name="radius"/>
    /// (normalised units), or −1.</summary>
    public int NearestPin(Vec2d at, double radius)
    {
        var best = -1;
        var bestD = radius * radius;
        for (var i = 0; i < Pins.Length; i++)
        {
            var d = (Pins[i].To - at).LengthSquared;
            if (d <= bestD)
            {
                bestD = d;
                best = i;
            }
        }
        return best;
    }
}

/// <summary>A compiled <see cref="PinWarp"/>: y ↦ y + d(y), d the thin-plate spline of the pins'
/// displacements (plus the border's zero anchors). Thread-safe.</summary>
public sealed class PinWarpField
{
    private readonly ThinPlateSpline _spline;

    public PinWarpField(PinWarp warp)
    {
        ArgumentNullException.ThrowIfNull(warp);
        var centres = new List<Vec2d>();
        var values = new List<Vec2d>();
        foreach (var p in warp.Pins)
        {
            if (!p.Enabled)
                continue;
            centres.Add(p.From);
            values.Add(p.Displacement);
        }
        if (warp.AnchorEdges)
        {
            const int k = PinWarp.AnchorsPerSide;
            for (var s = 0; s < 4 * k; s++)
            {
                var side = s / k;
                var t = (s % k) / (double)k;
                var anchor = side switch
                {
                    0 => new Vec2d(t, 0),
                    1 => new Vec2d(1, t),
                    2 => new Vec2d(1 - t, 1),
                    _ => new Vec2d(0, 1 - t),
                };
                // A pin on the border wins over the anchor it sits on.
                if (centres.Exists(c => (c - anchor).LengthSquared < 1e-8))
                    continue;
                centres.Add(anchor);
                values.Add(Vec2d.Zero);
            }
        }
        _spline = new ThinPlateSpline(centres, values, Math.Max(0, warp.Stiffness));
    }

    /// <summary>The displacement at an output point.</summary>
    public Vec2d Displacement(Vec2d y) => _spline.Evaluate(y);

    /// <summary>The pinned point: y + d(y).</summary>
    public Vec2d Apply(Vec2d y) => y + _spline.Evaluate(y);

    /// <summary>The point that lands on <paramref name="target"/> (Newton on y + d(y) = target, numeric
    /// Jacobian); the best estimate when the field folds there.</summary>
    public Vec2d Inverse(Vec2d target)
    {
        var y = target - _spline.Evaluate(target);
        const double h = 1e-6;
        for (var it = 0; it < 30; it++)
        {
            var f = Apply(y) - target;
            if (f.LengthSquared < 1e-26)
                break;
            var fx = (Apply(new Vec2d(y.X + h, y.Y)) - Apply(new Vec2d(y.X - h, y.Y))) / (2 * h);
            var fy = (Apply(new Vec2d(y.X, y.Y + h)) - Apply(new Vec2d(y.X, y.Y - h))) / (2 * h);
            var det = fx.X * fy.Y - fy.X * fx.Y;
            if (Math.Abs(det) < 1e-14)
                break;
            var dx = (fy.Y * f.X - fy.X * f.Y) / det;
            var dy = (-fx.Y * f.X + fx.X * f.Y) / det;
            y = new Vec2d(y.X - dx, y.Y - dy);
        }
        return y;
    }
}
