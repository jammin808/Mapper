using System.Globalization;
using PlayMap.Core.Model;
using PlayMap.Core.Numerics;

namespace PlayMap.Core.Stacks;

/// <summary>One measured pair of a stack member, in its raster's pixels: where its render puts a picture point (the target's
/// position) and where the light that reaches that point really leaves it.</summary>
public readonly record struct StackPair(Vec2d Render, Vec2d Emit);

/// <summary>How a member's residual lattice is fitted.</summary>
public sealed record StackFitOptions
{
    public static readonly StackFitOptions Default = new();

    public int Columns { get; init; } = 33;

    public int Rows { get; init; } = 19;

    /// <summary>The bending penalty, relative to the data's mean weight on a node: a straight field costs nothing, so a zoom or a
    /// shift is free and only curvature the pairs do not ask for is held back.</summary>
    public double Smoothing { get; init; } = 1e-3;

    /// <summary>Pairs farther than this from the fit (pixels) count linearly (Huber): a stray decode moves the lattice little.</summary>
    public double HuberPx { get; init; } = 0.5;

    public int Reweights { get; init; } = 4;

    /// <summary>The most pairs fitted (taken evenly from those given); the held-out pairs come from the same set.</summary>
    public int MaxPairs { get; init; } = 60_000;

    /// <summary>One pair in this many is held out and never fitted: the residual is measured on them.</summary>
    public int HoldOutEvery { get; init; } = 10;
}

/// <summary>A member's fit: its residual lattice (raster-normalised offsets, Catmull-Rom), the held-out misfit after it (RMS and
/// p95, pixels) and before it, the pairs used and held out, and words.</summary>
public sealed record StackFit(WarpLattice Lattice, double ResidualPx, double P95Px, double BeforePx, int Pairs, int HeldOut, string Words);

/// <summary>
/// A stack member's measured residual (facade §3.9): a Catmull-Rom <see cref="WarpLattice"/> L with Render + L(Render) = Emit, so the
/// content its render puts at a point leaves from where its light really reaches that point. The lattice is linear in its node
/// offsets (<see cref="LatticeBasis"/>), so the fit is linear least squares in them: the pairs' squared misfit, reweighted by Huber
/// against stray decodes, plus a bending penalty along every row and column of nodes (second differences, so a shift or a zoom is
/// free). The normal equations are banded (a node meets the nodes three around it) and solved by Jacobi-preconditioned conjugate
/// gradients, x and y with the same matrix. Every tenth pair is held out and the residual is measured on those (neighbours of
/// fitted pairs, so the residual says how well the lattice follows the decodes, not how far they can be trusted). Deterministic: the
/// pairs are taken in order and every sum runs in one order.
/// </summary>
public static class StackAlignment
{
    private const int Reach = 3;
    private const int Band = 2 * Reach + 1;
    private const int BandSize = Band * Band;

    public static StackFit Fit(IReadOnlyList<StackPair> pairs, int width, int height, StackFitOptions? options = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(pairs);
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        options ??= StackFitOptions.Default;
        var basis = new LatticeBasis(options.Columns, options.Rows);
        var n = basis.Nodes;
        var every = Math.Max(2, options.HoldOutEvery);
        var step = Math.Max(1, (pairs.Count + options.MaxPairs - 1) / Math.Max(1, options.MaxPairs));
        var train = new List<StackPair>();
        var held = new List<StackPair>();
        for (var i = 0; i < pairs.Count; i += step)
        {
            var pair = pairs[i];
            if (!double.IsFinite(pair.Render.X) || !double.IsFinite(pair.Render.Y) || !double.IsFinite(pair.Emit.X) || !double.IsFinite(pair.Emit.Y))
                continue;
            if ((train.Count + held.Count) % every == every - 1)
                held.Add(pair);
            else
                train.Add(pair);
        }
        var rest = WarpLattice.AtRest(basis.Columns, basis.Rows);
        if (train.Count < 8)
            return new StackFit(rest, double.NaN, double.NaN, double.NaN, train.Count, held.Count, $"{train.Count} pairs: too few to fit a lattice");

        // Each training pair's cell and weights, once.
        var cells = new int[train.Count];
        var weights = new double[train.Count * LatticeBasis.MaxSupport];
        for (var p = 0; p < train.Count; p++)
            cells[p] = basis.Weights(train[p].Render.X / width, train[p].Render.Y / height, weights.AsSpan(p * LatticeBasis.MaxSupport, LatticeBasis.MaxSupport));

        var dx = new double[n];
        var dy = new double[n];
        var h = new double[train.Count];
        Array.Fill(h, 1.0);
        var matrix = new double[n * BandSize];
        var bx = new double[n];
        var by = new double[n];
        for (var round = 0; round <= Math.Max(0, options.Reweights); round++)
        {
            ct.ThrowIfCancellationRequested();
            Array.Clear(matrix);
            Array.Clear(bx);
            Array.Clear(by);
            for (var p = 0; p < train.Count; p++)
            {
                var nodes = basis.NodesOf(cells[p]);
                var w = weights.AsSpan(p * LatticeBasis.MaxSupport, nodes.Count);
                var hp = h[p];
                var rx = train[p].Emit.X - train[p].Render.X;
                var ry = train[p].Emit.Y - train[p].Render.Y;
                for (var a = 0; a < nodes.Count; a++)
                {
                    var wa = w[a] * hp;
                    if (wa == 0)
                        continue;
                    var ka = nodes[a];
                    bx[ka] += wa * rx;
                    by[ka] += wa * ry;
                    for (var b = 0; b < nodes.Count; b++)
                        matrix[ka * BandSize + Slot(basis, ka, nodes[b])] += wa * w[b];
                }
            }
            var diagonal = 0.0;
            for (var k = 0; k < n; k++)
                diagonal += matrix[k * BandSize + Slot(basis, k, k)];
            var lambda = Math.Max(options.Smoothing, 0) * Math.Max(diagonal / n, 1e-12);
            AddBending(matrix, basis, lambda);
            // A whisper of ridge keeps a node no pair and no bend reaches at rest.
            for (var k = 0; k < n; k++)
                matrix[k * BandSize + Slot(basis, k, k)] += 1e-9 * Math.Max(diagonal / n, 1e-12);
            Solve(matrix, basis, bx, dx, ct);
            Solve(matrix, basis, by, dy, ct);
            if (round == options.Reweights)
                break;
            for (var p = 0; p < train.Count; p++)
            {
                var r = Misfit(basis, train[p], width, height, dx, dy, weights.AsSpan(p * LatticeBasis.MaxSupport, LatticeBasis.MaxSupport));
                h[p] = r <= options.HuberPx ? 1 : options.HuberPx / r;
            }
        }

        var offsets = new Vec2d[n];
        for (var k = 0; k < n; k++)
            offsets[k] = new Vec2d(dx[k] / width, dy[k] / height);
        var lattice = rest with { Offsets = [.. offsets] };
        var after = new List<double>(held.Count);
        var before = new List<double>(held.Count);
        Span<double> hw = stackalloc double[LatticeBasis.MaxSupport];
        foreach (var pair in held)
        {
            after.Add(Misfit(basis, pair, width, height, dx, dy, hw));
            before.Add((pair.Emit - pair.Render).Length);
        }
        var rms = Rms(after);
        var p95 = Quantile(after, 0.95);
        var was = Rms(before);
        var words = string.Create(CultureInfo.InvariantCulture,
            $"{train.Count} pairs fitted on a {basis.Columns}×{basis.Rows} lattice; held out {held.Count}: {was:0.00} px apart before, {rms:0.00} px RMS after (p95 {p95:0.00} px)");
        return new StackFit(lattice, rms, p95, was, train.Count, held.Count, words);
    }

    /// <summary>The distance (pixels) between where the lattice sends a pair's render point and where its light leaves.</summary>
    public static double Misfit(WarpLattice lattice, StackPair pair, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(lattice);
        var basis = new LatticeBasis(lattice.Columns, lattice.Rows);
        var dx = lattice.Offsets.Select(o => o.X * width).ToArray();
        var dy = lattice.Offsets.Select(o => o.Y * height).ToArray();
        Span<double> w = stackalloc double[LatticeBasis.MaxSupport];
        return Misfit(basis, pair, width, height, dx, dy, w);
    }

    private static double Misfit(LatticeBasis basis, StackPair pair, int width, int height, double[] dx, double[] dy, Span<double> w)
    {
        var cell = basis.Weights(pair.Render.X / width, pair.Render.Y / height, w);
        var nodes = basis.NodesOf(cell);
        double x = pair.Render.X, y = pair.Render.Y;
        for (var a = 0; a < nodes.Count; a++)
        {
            x += w[a] * dx[nodes[a]];
            y += w[a] * dy[nodes[a]];
        }
        return Math.Sqrt((x - pair.Emit.X) * (x - pair.Emit.X) + (y - pair.Emit.Y) * (y - pair.Emit.Y));
    }

    /// <summary>Where node b sits in node a's band row.</summary>
    private static int Slot(LatticeBasis basis, int a, int b)
    {
        var di = b % basis.Columns - a % basis.Columns;
        var dj = b / basis.Columns - a / basis.Columns;
        return di + Reach + Band * (dj + Reach);
    }

    /// <summary>λ Σ (Dₖ₋₁ − 2Dₖ + Dₖ₊₁)² along every row and every column of nodes.</summary>
    private static void AddBending(double[] matrix, LatticeBasis basis, double lambda)
    {
        if (lambda <= 0)
            return;
        Span<int> k = stackalloc int[3];
        void Add(Span<int> nodes)
        {
            ReadOnlySpan<double> c = [1, -2, 1];
            for (var a = 0; a < 3; a++)
                for (var b = 0; b < 3; b++)
                    matrix[nodes[a] * BandSize + Slot(basis, nodes[a], nodes[b])] += lambda * c[a] * c[b];
        }
        for (var j = 0; j < basis.Rows; j++)
            for (var i = 1; i + 1 < basis.Columns; i++)
            {
                k[0] = j * basis.Columns + i - 1;
                k[1] = k[0] + 1;
                k[2] = k[0] + 2;
                Add(k);
            }
        for (var i = 0; i < basis.Columns; i++)
            for (var j = 1; j + 1 < basis.Rows; j++)
            {
                k[0] = (j - 1) * basis.Columns + i;
                k[1] = k[0] + basis.Columns;
                k[2] = k[1] + basis.Columns;
                Add(k);
            }
    }

    private static void Multiply(double[] matrix, LatticeBasis basis, double[] x, double[] y)
    {
        var cols = basis.Columns;
        var rows = basis.Rows;
        for (var a = 0; a < x.Length; a++)
        {
            var ai = a % cols;
            var aj = a / cols;
            double sum = 0;
            for (var dj = -Reach; dj <= Reach; dj++)
            {
                var j = aj + dj;
                if (j < 0 || j >= rows)
                    continue;
                for (var di = -Reach; di <= Reach; di++)
                {
                    var i = ai + di;
                    if (i < 0 || i >= cols)
                        continue;
                    sum += matrix[a * BandSize + di + Reach + Band * (dj + Reach)] * x[j * cols + i];
                }
            }
            y[a] = sum;
        }
    }

    /// <summary>Jacobi-preconditioned conjugate gradients from the current solution (warm start between reweights).</summary>
    private static void Solve(double[] matrix, LatticeBasis basis, double[] b, double[] x, CancellationToken ct)
    {
        var n = x.Length;
        var inv = new double[n];
        for (var k = 0; k < n; k++)
        {
            var d = matrix[k * BandSize + Slot(basis, k, k)];
            inv[k] = d > 0 ? 1 / d : 1;
        }
        var r = new double[n];
        var z = new double[n];
        var p = new double[n];
        var ap = new double[n];
        Multiply(matrix, basis, x, ap);
        double bb = 0;
        for (var k = 0; k < n; k++)
        {
            r[k] = b[k] - ap[k];
            z[k] = inv[k] * r[k];
            p[k] = z[k];
            bb += b[k] * b[k];
        }
        if (bb == 0)
        {
            Array.Clear(x);
            return;
        }
        var rz = Dot(r, z);
        for (var it = 0; it < 4 * n + 200; it++)
        {
            if ((it & 63) == 0)
                ct.ThrowIfCancellationRequested();
            Multiply(matrix, basis, p, ap);
            var pap = Dot(p, ap);
            if (pap <= 0)
                break;
            var alpha = rz / pap;
            double rr = 0;
            for (var k = 0; k < n; k++)
            {
                x[k] += alpha * p[k];
                r[k] -= alpha * ap[k];
                rr += r[k] * r[k];
            }
            if (rr <= 1e-24 * bb)
                break;
            for (var k = 0; k < n; k++)
                z[k] = inv[k] * r[k];
            var next = Dot(r, z);
            var beta = next / rz;
            rz = next;
            for (var k = 0; k < n; k++)
                p[k] = z[k] + beta * p[k];
        }
    }

    private static double Dot(double[] a, double[] b)
    {
        double s = 0;
        for (var k = 0; k < a.Length; k++)
            s += a[k] * b[k];
        return s;
    }

    private static double Rms(List<double> values) =>
        values.Count == 0 ? double.NaN : Math.Sqrt(values.Sum(v => v * v) / values.Count);

    /// <summary>The q-quantile (NaN for none).</summary>
    public static double Quantile(IReadOnlyList<double> values, double q)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count == 0)
            return double.NaN;
        var sorted = values.Order().ToArray();
        return sorted[Math.Clamp((int)Math.Ceiling(q * sorted.Length) - 1, 0, sorted.Length - 1)];
    }
}
