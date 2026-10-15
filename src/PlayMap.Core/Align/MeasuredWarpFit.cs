using System.Globalization;
using PlayMap.Core.Numerics;

namespace PlayMap.Core.Align;

/// <summary>
/// A measured warp: canvas uv (0–1, y down) at a regular grid of output raster positions — node (i, k) at raster point
/// (i·W/(C−1), k·H/(R−1)) — read between nodes bilinearly. A node the camera did not see is invalid: it still holds the smooth
/// extension of its neighbours, and a point is shown only where the validity, read bilinearly too, is at least one half.
/// </summary>
public sealed class WarpGrid
{
    public WarpGrid(int columns, int rows, int rasterWidth, int rasterHeight, float[] u, float[] v, bool[] valid)
    {
        ArgumentNullException.ThrowIfNull(u);
        ArgumentNullException.ThrowIfNull(v);
        ArgumentNullException.ThrowIfNull(valid);
        if (columns < 2 || rows < 2 || u.Length != columns * rows || v.Length != u.Length || valid.Length != u.Length)
            throw new ArgumentException("A grid is at least 2 × 2 with one value per node.");
        Columns = columns;
        Rows = rows;
        RasterWidth = rasterWidth;
        RasterHeight = rasterHeight;
        U = u;
        V = v;
        Valid = valid;
    }

    public int Columns { get; }
    public int Rows { get; }
    public int RasterWidth { get; }
    public int RasterHeight { get; }
    public float[] U { get; }
    public float[] V { get; }
    public bool[] Valid { get; }

    public int ValidNodes => Valid.Count(v => v);

    /// <summary>The raster point of node (i, k).</summary>
    public Vec2d Node(int i, int k) => new((double)i * RasterWidth / (Columns - 1), (double)k * RasterHeight / (Rows - 1));

    /// <summary>The canvas uv at raster point <paramref name="q"/>, or null where the grid is not valid.</summary>
    public Vec2d? Eval(Vec2d q)
    {
        var (uv, valid) = Sample(q);
        return valid >= 0.5 ? uv : null;
    }

    /// <summary>The canvas uv at raster point <paramref name="q"/> and the validity there (0–1), both bilinear.</summary>
    public (Vec2d Uv, double Valid) Sample(Vec2d q)
    {
        var gx = Math.Clamp(q.X / RasterWidth * (Columns - 1), 0, Columns - 1);
        var gy = Math.Clamp(q.Y / RasterHeight * (Rows - 1), 0, Rows - 1);
        var i = Math.Min((int)gx, Columns - 2);
        var k = Math.Min((int)gy, Rows - 2);
        var fx = gx - i;
        var fy = gy - k;
        var a = k * Columns + i;
        var b = a + 1;
        var c = a + Columns;
        var d = c + 1;
        var w0 = (1 - fx) * (1 - fy);
        var w1 = fx * (1 - fy);
        var w2 = (1 - fx) * fy;
        var w3 = fx * fy;
        var u = w0 * U[a] + w1 * U[b] + w2 * U[c] + w3 * U[d];
        var v = w0 * V[a] + w1 * V[b] + w2 * V[c] + w3 * V[d];
        var ok = w0 * (Valid[a] ? 1 : 0) + w1 * (Valid[b] ? 1 : 0) + w2 * (Valid[c] ? 1 : 0) + w3 * (Valid[d] ? 1 : 0);
        return (new Vec2d(u, v), ok);
    }

    /// <summary>The raster point whose canvas uv is <paramref name="uv"/>, by Newton's method on the bilinear grid from
    /// <paramref name="start"/>; null when it does not settle within the raster, or lands where the grid is not valid.</summary>
    public Vec2d? Invert(Vec2d uv, Vec2d start)
    {
        var q = start;
        const double h = 0.25;
        for (var i = 0; i < 30; i++)
        {
            var e = Sample(q).Uv - uv;
            var dx = (Sample(q + new Vec2d(h, 0)).Uv - Sample(q - new Vec2d(h, 0)).Uv) / (2 * h);
            var dy = (Sample(q + new Vec2d(0, h)).Uv - Sample(q - new Vec2d(0, h)).Uv) / (2 * h);
            var det = dx.X * dy.Y - dy.X * dx.Y;
            if (Math.Abs(det) < 1e-18)
                return null;
            var step = new Vec2d((dy.Y * e.X - dy.X * e.Y) / det, (-dx.Y * e.X + dx.X * e.Y) / det);
            q -= step;
            if (step.Length < 1e-7)
                break;
        }
        if (q.X < 0 || q.Y < 0 || q.X > RasterWidth || q.Y > RasterHeight)
            return null;
        var (at, ok) = Sample(q);
        return ok >= 0.5 && (at - uv).Length < 1e-6 ? q : null;
    }

    /// <summary>The grid as a three-channel float map (u, v, valid), one pixel per node.</summary>
    public FloatMap ToMap()
    {
        var values = new float[Columns * Rows * 3];
        for (var n = 0; n < U.Length; n++)
        {
            values[3 * n] = U[n];
            values[3 * n + 1] = V[n];
            values[3 * n + 2] = Valid[n] ? 1 : 0;
        }
        return new FloatMap(Columns, Rows, 3, values);
    }

    /// <summary>A grid back from its float map, for an output raster.</summary>
    public static WarpGrid FromMap(FloatMap map, int rasterWidth, int rasterHeight)
    {
        ArgumentNullException.ThrowIfNull(map);
        if (map.Channels != 3)
            throw new InvalidDataException("a warp grid carries u, v and validity");
        var n = map.Width * map.Height;
        var u = new float[n];
        var v = new float[n];
        var valid = new bool[n];
        for (var i = 0; i < n; i++)
        {
            u[i] = map.Values[3 * i];
            v[i] = map.Values[3 * i + 1];
            valid[i] = map.Values[3 * i + 2] >= 0.5f;
        }
        return new WarpGrid(map.Width, map.Height, rasterWidth, rasterHeight, u, v, valid);
    }
}

/// <summary>A fitted grid with its residual on the pairs held out of the fit (output pixels), the pairs it was fitted to, and the scale
/// of the canvas against the raster (uv per output pixel).</summary>
public sealed record WarpFitResult(WarpGrid Grid, double HeldOutRmsPx, double HeldOutP95Px, int Pairs, double UvPerPx)
{
    public string Words => string.Create(CultureInfo.InvariantCulture,
        $"{Grid.Columns}×{Grid.Rows} grid from {Pairs:N0} pairs, {HeldOutRmsPx:0.00} px RMS on the pairs held out ({Grid.ValidNodes} of {Grid.U.Length} nodes seen)");
}

/// <summary>
/// The measured warp fitted from scattered pairs (raster point → canvas uv): bilinear basis per cell, least squares with a graph
/// Laplacian smoothness (λ = 10⁻³ · pairs per node) so nodes without pairs take their neighbours' smooth extension, and Huber
/// re-weighting (δ 0.5 output px) over three passes; each pass solves the banded normal equations by Jacobi-preconditioned conjugate
/// gradients from the affine fit of the pairs. Every tenth pair is held out and measures the residual. A node is valid when at least
/// four pairs fall in the cells around it. Pure.
/// </summary>
public static class MeasuredWarpFit
{
    private const int Band = 5;
    private const double HuberPx = 0.5;

    public static WarpFitResult Fit(int rasterWidth, int rasterHeight, int columns, int rows, IReadOnlyList<Vec2d> q, IReadOnlyList<Vec2d> uv,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(q);
        ArgumentNullException.ThrowIfNull(uv);
        if (q.Count != uv.Count)
            throw new ArgumentException("One uv per raster point.", nameof(uv));
        columns = Math.Clamp(columns, 2, 65);
        rows = Math.Clamp(rows, 2, 65);
        var nodes = columns * rows;
        var cellW = (double)rasterWidth / (columns - 1);
        var cellH = (double)rasterHeight / (rows - 1);
        var training = new List<int>(q.Count);
        var held = new List<int>(q.Count / 10 + 1);
        for (var p = 0; p < q.Count; p++)
            (p % 10 == 7 ? held : training).Add(p);
        if (training.Count < 3)
            throw new ArgumentException("A warp needs at least three pairs.", nameof(q));

        var (affine, uvPerPx) = Affine(q, uv, training);
        var gu = new double[nodes];
        var gv = new double[nodes];
        for (var k = 0; k < rows; k++)
        {
            for (var i = 0; i < columns; i++)
            {
                var a = affine(new Vec2d(i * cellW, k * cellH));
                gu[k * columns + i] = a.X;
                gv[k * columns + i] = a.Y;
            }
        }

        var lambda = 1e-3 * training.Count / nodes;
        var weights = new double[q.Count];
        Array.Fill(weights, 1.0);
        var delta = HuberPx * uvPerPx;
        var band = new double[nodes * Band * Band];
        var bu = new double[nodes];
        var bv = new double[nodes];
        Span<int> idx = stackalloc int[4];
        Span<double> bw = stackalloc double[4];
        for (var pass = 0; pass < 3; pass++)
        {
            ct.ThrowIfCancellationRequested();
            Array.Clear(band);
            Array.Clear(bu);
            Array.Clear(bv);
            foreach (var p in training)
            {
                Basis(q[p], columns, rows, cellW, cellH, idx, bw);
                var w = weights[p];
                for (var a = 0; a < 4; a++)
                {
                    bu[idx[a]] += w * bw[a] * uv[p].X;
                    bv[idx[a]] += w * bw[a] * uv[p].Y;
                    for (var b = 0; b < 4; b++)
                        band[Slot(idx[a], idx[b], columns)] += w * bw[a] * bw[b];
                }
            }
            AddLaplacian(band, columns, rows, lambda);
            Solve(band, bu, gu, columns, rows);
            Solve(band, bv, gv, columns, rows);
            foreach (var p in training)
            {
                var r = (Eval(gu, gv, q[p], columns, rows, cellW, cellH) - uv[p]).Length;
                weights[p] = r <= delta ? 1 : delta / r;
            }
        }

        var support = new int[nodes];
        for (var p = 0; p < q.Count; p++)
        {
            Basis(q[p], columns, rows, cellW, cellH, idx, bw);
            for (var a = 0; a < 4; a++)
                support[idx[a]]++;
        }
        var valid = new bool[nodes];
        for (var n = 0; n < nodes; n++)
            valid[n] = support[n] >= 4;
        var grid = new WarpGrid(columns, rows, rasterWidth, rasterHeight, Array.ConvertAll(gu, x => (float)x), Array.ConvertAll(gv, x => (float)x), valid);

        var residuals = new List<double>(held.Count);
        foreach (var p in held)
        {
            var (at, ok) = grid.Sample(q[p]);
            if (ok >= 0.5)
                residuals.Add((at - uv[p]).Length / uvPerPx);
        }
        residuals.Sort();
        var rms = residuals.Count > 0 ? Math.Sqrt(residuals.Sum(r => r * r) / residuals.Count) : double.NaN;
        var p95 = residuals.Count > 0 ? residuals[Math.Min(residuals.Count - 1, (int)(0.95 * residuals.Count))] : double.NaN;
        return new WarpFitResult(grid, rms, p95, training.Count, uvPerPx);
    }

    /// <summary>The least-squares affine map raster → uv over the given pairs, and its scale (uv per raster pixel, the root of its
    /// determinant).</summary>
    private static (Func<Vec2d, Vec2d> Map, double Scale) Affine(IReadOnlyList<Vec2d> q, IReadOnlyList<Vec2d> uv, List<int> use)
    {
        var a = new DenseMatrix(use.Count, 3);
        var bx = new double[use.Count];
        var by = new double[use.Count];
        for (var r = 0; r < use.Count; r++)
        {
            var p = use[r];
            a[r, 0] = q[p].X;
            a[r, 1] = q[p].Y;
            a[r, 2] = 1;
            bx[r] = uv[p].X;
            by[r] = uv[p].Y;
        }
        var cx = LinearSolve.LeastSquares(a, bx);
        var cy = LinearSolve.LeastSquares(a, by);
        var det = Math.Abs(cx[0] * cy[1] - cx[1] * cy[0]);
        var scale = det > 0 ? Math.Sqrt(det) : 1e-3;
        return (p => new Vec2d(cx[0] * p.X + cx[1] * p.Y + cx[2], cy[0] * p.X + cy[1] * p.Y + cy[2]), scale);
    }

    private static void Basis(Vec2d q, int columns, int rows, double cellW, double cellH, Span<int> idx, Span<double> w)
    {
        var gx = Math.Clamp(q.X / cellW, 0, columns - 1);
        var gy = Math.Clamp(q.Y / cellH, 0, rows - 1);
        var i = Math.Min((int)gx, columns - 2);
        var k = Math.Min((int)gy, rows - 2);
        var fx = gx - i;
        var fy = gy - k;
        idx[0] = k * columns + i;
        idx[1] = idx[0] + 1;
        idx[2] = idx[0] + columns;
        idx[3] = idx[2] + 1;
        w[0] = (1 - fx) * (1 - fy);
        w[1] = fx * (1 - fy);
        w[2] = (1 - fx) * fy;
        w[3] = fx * fy;
    }

    private static Vec2d Eval(double[] gu, double[] gv, Vec2d q, int columns, int rows, double cellW, double cellH)
    {
        Span<int> idx = stackalloc int[4];
        Span<double> w = stackalloc double[4];
        Basis(q, columns, rows, cellW, cellH, idx, w);
        double u = 0, v = 0;
        for (var a = 0; a < 4; a++)
        {
            u += w[a] * gu[idx[a]];
            v += w[a] * gv[idx[a]];
        }
        return new Vec2d(u, v);
    }

    /// <summary>Where node <paramref name="b"/>'s coefficient sits in node <paramref name="a"/>'s row of the 5 × 5 band.</summary>
    private static int Slot(int a, int b, int columns)
    {
        var dx = b % columns - a % columns;
        var dy = b / columns - a / columns;
        return a * Band * Band + (dy + 2) * Band + dx + 2;
    }

    /// <summary>λ·LᵀL, L the grid's graph Laplacian ((Lg)ₙ = Σ neighbours − degree·gₙ).</summary>
    private static void AddLaplacian(double[] band, int columns, int rows, double lambda)
    {
        Span<int> set = stackalloc int[5];
        Span<double> coef = stackalloc double[5];
        for (var k = 0; k < rows; k++)
        {
            for (var i = 0; i < columns; i++)
            {
                var n = k * columns + i;
                var count = 0;
                var degree = 0;
                if (i > 0) { set[++count] = n - 1; coef[count] = 1; degree++; }
                if (i < columns - 1) { set[++count] = n + 1; coef[count] = 1; degree++; }
                if (k > 0) { set[++count] = n - columns; coef[count] = 1; degree++; }
                if (k < rows - 1) { set[++count] = n + columns; coef[count] = 1; degree++; }
                set[0] = n;
                coef[0] = -degree;
                for (var a = 0; a <= count; a++)
                    for (var b = 0; b <= count; b++)
                        band[Slot(set[a], set[b], columns)] += lambda * coef[a] * coef[b];
            }
        }
    }

    private static void Multiply(double[] band, double[] x, double[] y, int columns, int rows)
    {
        for (var k = 0; k < rows; k++)
        {
            for (var i = 0; i < columns; i++)
            {
                var n = k * columns + i;
                double s = 0;
                var row = n * Band * Band;
                for (var dy = -2; dy <= 2; dy++)
                {
                    var kk = k + dy;
                    if (kk < 0 || kk >= rows)
                        continue;
                    for (var dx = -2; dx <= 2; dx++)
                    {
                        var ii = i + dx;
                        if (ii < 0 || ii >= columns)
                            continue;
                        s += band[row + (dy + 2) * Band + dx + 2] * x[kk * columns + ii];
                    }
                }
                y[n] = s;
            }
        }
    }

    /// <summary>Jacobi-preconditioned conjugate gradients on the band, from <paramref name="x"/>'s values.</summary>
    private static void Solve(double[] band, double[] b, double[] x, int columns, int rows)
    {
        var n = b.Length;
        var r = new double[n];
        var z = new double[n];
        var p = new double[n];
        var ap = new double[n];
        var inv = new double[n];
        for (var i = 0; i < n; i++)
        {
            var d = band[i * Band * Band + 2 * Band + 2];
            inv[i] = d > 0 ? 1 / d : 1;
        }
        Multiply(band, x, ap, columns, rows);
        double bnorm = 0;
        for (var i = 0; i < n; i++)
        {
            r[i] = b[i] - ap[i];
            z[i] = inv[i] * r[i];
            p[i] = z[i];
            bnorm += b[i] * b[i];
        }
        var rz = Dot(r, z);
        var tol = 1e-24 * Math.Max(bnorm, 1e-30);
        for (var iter = 0; iter < 4 * n + 200 && Dot(r, r) > tol; iter++)
        {
            Multiply(band, p, ap, columns, rows);
            var pap = Dot(p, ap);
            if (!(pap > 0))
                break;
            var alpha = rz / pap;
            for (var i = 0; i < n; i++)
            {
                x[i] += alpha * p[i];
                r[i] -= alpha * ap[i];
                z[i] = inv[i] * r[i];
            }
            var next = Dot(r, z);
            var beta = next / rz;
            rz = next;
            for (var i = 0; i < n; i++)
                p[i] = z[i] + beta * p[i];
        }
    }

    private static double Dot(double[] a, double[] b)
    {
        double s = 0;
        for (var i = 0; i < a.Length; i++)
            s += a[i] * b[i];
        return s;
    }
}
