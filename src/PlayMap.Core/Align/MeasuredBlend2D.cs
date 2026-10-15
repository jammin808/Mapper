using System.Globalization;
using PlayMap.Core.Mapping;
using PlayMap.Core.Model;
using PlayMap.Core.Numerics;

namespace PlayMap.Core.Align;

/// <summary>What the measured blend is made from: each output's warp, the light the camera measured (each output's
/// white less black, and the black with every output black, both in camera-linear units at a canvas uv; NaN where unseen), the curve
/// and edge width of the fades, how far to even out the white (0–1), whether to lift the black, and the canvas raster it works on.</summary>
public sealed record BlendInput(
    IReadOnlyList<WarpGrid> Grids,
    Func<int, Vec2d, double> WhiteLight,
    Func<Vec2d, double> BlackLight,
    BlendCurve Curve,
    double EdgeWidth,
    double Uniformity,
    bool BlackLevel,
    int CanvasWidth,
    int CanvasHeight);

/// <summary>One output's measured blend at a quarter of its raster, in light (shares of the output's own white): per pixel the weight
/// (the uniformity gain folded in), the black pedestal, and the uniformity gain alone (kept for the record), row-major, three floats a
/// pixel. The output stage draws ped + (1 − ped)·weight·L, L the picture's light through the output's own γ, then back to its signal.</summary>
public sealed record OutputBlend(int Width, int Height, float[] Values)
{
    public float Weight(int x, int y) => Values[(y * Width + x) * 3];

    public float Pedestal(int x, int y) => Values[(y * Width + x) * 3 + 1];

    public FloatMap ToMap() => new(Width, Height, 3, Values);
}

/// <summary>The blend, and what it predicts in light: the white's 5th over 95th percentile over the canvas after the uniformity
/// gain, and the black floor the pedestal lifts every point to (camera-linear).</summary>
public sealed record BlendResult(IReadOnlyList<OutputBlend> Maps, double WhiteP5OverP95, double BlackFloor, string Words);

/// <summary>
/// The measured blend on a flat canvas. Per canvas cell, each output that reaches it fades with the nearer of its raster edge (in
/// EdgeWidth × its raster side) and the edge of where it reaches on the canvas (the same width in canvas cells), through the curve;
/// the fades are normalised to one in light. The black every output leaks (measured with all of them black, modelled as the room's
/// light plus each output's leak where its raster reaches, so the steps fall exactly on the rasters' edges) is lifted everywhere to
/// the deepest overlap's, the lift shared by weight and turned into each output's pedestal as a share of its own white. The white the
/// weights make is evened out toward its 2nd percentile by the uniformity (IntensityCompensation's gain, low-passed over 2 % of the
/// canvas width), folded into the weights. Each output's map samples these at its own quarter-raster pixels through its warp. Pure.
/// </summary>
public static class MeasuredBlend2D
{
    public static BlendResult Solve(BlendInput input, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var n = input.Grids.Count;
        var cw = input.CanvasWidth;
        var ch = input.CanvasHeight;
        var cells = cw * ch;
        var inverses = new CanvasInverse[n];
        Parallel.For(0, n, k => inverses[k] = CanvasInverse.Build(input.Grids[k], cw, ch));
        ct.ThrowIfCancellationRequested();

        // Fades in light, per output, per cell (0 where it does not reach).
        var fade = new float[n][];
        for (var k = 0; k < n; k++)
        {
            var grid = input.Grids[k];
            var inv = inverses[k];
            var outside = new bool[cells];
            for (var c = 0; c < cells; c++)
                outside[c] = float.IsNaN(inv.X[c]);
            var dt = DistanceTransform.Euclidean(outside, cw, ch);
            var band = Math.Max(1, input.EdgeWidth * Math.Min(grid.RasterWidth, grid.RasterHeight) * UvPerPx(grid) * cw);
            var f = new float[cells];
            for (var c = 0; c < cells; c++)
            {
                if (outside[c])
                    continue;
                var qx = inv.X[c];
                var qy = inv.Y[c];
                var ex = Math.Min(qx, grid.RasterWidth - qx) / (input.EdgeWidth * grid.RasterWidth);
                var ey = Math.Min(qy, grid.RasterHeight - qy) / (input.EdgeWidth * grid.RasterHeight);
                var t = Math.Min(Math.Min(ex, ey), dt[c] / band);
                f[c] = (float)Math.Max(1e-6, BlendMath.Curve(input.Curve, t));
            }
            fade[k] = f;
        }
        var weight = new float[n][];
        for (var k = 0; k < n; k++)
            weight[k] = new float[cells];
        for (var c = 0; c < cells; c++)
        {
            double sum = 0;
            for (var k = 0; k < n; k++)
                sum += fade[k][c];
            if (sum <= 0)
                continue;
            for (var k = 0; k < n; k++)
                weight[k][c] = (float)(fade[k][c] / sum);
        }
        ct.ThrowIfCancellationRequested();

        // The white the weights make, its gain toward the dimmest, and the black lift.
        var white = new double[cells];
        var black = new double[cells];
        var covered = new bool[cells];
        Parallel.For(0, ch, y =>
        {
            for (var x = 0; x < cw; x++)
            {
                var c = y * cw + x;
                var uv = new Vec2d((x + 0.5) / cw, (y + 0.5) / ch);
                double s = 0;
                var any = false;
                for (var k = 0; k < n; k++)
                {
                    if (weight[k][c] <= 0)
                        continue;
                    var l = input.WhiteLight(k, uv);
                    if (!double.IsFinite(l))
                        continue;
                    s += weight[k][c] * l;
                    any = true;
                }
                covered[c] = any;
                white[c] = any ? s : double.NaN;
                black[c] = input.BlackLight(uv);
            }
        });
        var whites = new List<float>(cells);
        var blacks = new List<double>(cells);
        for (var c = 0; c < cells; c++)
        {
            if (!covered[c])
                continue;
            whites.Add((float)white[c]);
            if (double.IsFinite(black[c]))
                blacks.Add(black[c]);
        }
        var target = whites.Count > 0 ? IntensityCompensation.Target(whites, 0.02) : 0;
        var gain = new double[cells];
        for (var c = 0; c < cells; c++)
            gain[c] = covered[c] && target > 0 && white[c] > 0 ? IntensityCompensation.Gain(white[c], target, Math.Clamp(input.Uniformity, 0, 1)) : 1;
        gain = LowPass(gain, covered, cw, ch, Math.Max(1, 0.02 * cw));
        blacks.Sort();
        var model = input.BlackLevel ? BlackModel.Fit(inverses, black, cw, ch) : null;
        var floor = model?.Floor ?? (blacks.Count > 0 ? blacks[Math.Min(blacks.Count - 1, (int)(0.99 * blacks.Count))] : 0);
        ct.ThrowIfCancellationRequested();

        var maps = new OutputBlend[n];
        Parallel.For(0, n, j =>
        {
            var grid = input.Grids[j];
            var mw = (grid.RasterWidth + 3) / 4;
            var mh = (grid.RasterHeight + 3) / 4;
            var values = new float[mw * mh * 3];
            for (var my = 0; my < mh; my++)
            {
                for (var mx = 0; mx < mw; mx++)
                {
                    var o = (my * mw + mx) * 3;
                    values[o] = 1;
                    values[o + 2] = 1;
                    var q = new Vec2d((mx + 0.5) / mw * grid.RasterWidth, (my + 0.5) / mh * grid.RasterHeight);
                    var (uv, ok) = grid.Sample(q);
                    if (ok < 0.5 || uv.X < 0 || uv.Y < 0 || uv.X > 1 || uv.Y > 1)
                        continue;
                    var w = Bilinear(weight[j], cw, ch, uv);
                    var g = Math.Min(1, Bilinear(gain, cw, ch, uv));
                    values[o] = (float)Math.Clamp(w * g, 0, 1);
                    values[o + 2] = (float)g;
                    if (!input.BlackLevel)
                        continue;
                    var b = model is null ? input.BlackLight(uv) : model.At(uv, k => k == j || Reaches(input.Grids[k], inverses[k], uv));
                    var own = input.WhiteLight(j, uv);
                    if (!double.IsFinite(b) || !(own > 0))
                        continue;
                    var lift = Math.Max(0, floor - b) * w / own;
                    values[o + 1] = (float)Math.Clamp(lift, 0, 1);
                }
            }
            maps[j] = new OutputBlend(mw, mh, values);
        });

        var after = new List<double>(whites.Count);
        for (var c = 0; c < cells; c++)
            if (covered[c])
                after.Add(white[c] * Math.Min(1, gain[c]));
        after.Sort();
        var ratio = after.Count > 0 ? after[(int)(0.05 * (after.Count - 1))] / Math.Max(1e-12, after[(int)(0.95 * (after.Count - 1))]) : double.NaN;
        var lifted = blacks.Count > 0 ? blacks.Count(b => floor - b > 0.1 * floor) / (double)blacks.Count : 0;
        var words = string.Create(CultureInfo.InvariantCulture,
            $"blended in light; white p5/p95 {ratio:0.00} after uniformity {input.Uniformity:0.##}")
            + (input.BlackLevel && floor > 0
                ? string.Create(CultureInfo.InvariantCulture, $"; black lifted to the overlaps' floor over {lifted * 100:0} % of the canvas")
                : "");
        return new BlendResult(maps, ratio, floor, words);
    }

    /// <summary>Whether an output's raster reaches canvas <paramref name="uv"/>: its warp turned around exactly (Newton from the nearest
    /// cell of its inverse), so a raster's edge is where it is, not where a canvas cell is.</summary>
    private static bool Reaches(WarpGrid grid, CanvasInverse inverse, Vec2d uv)
    {
        var cx = (int)Math.Floor(uv.X * inverse.Width);
        var cy = (int)Math.Floor(uv.Y * inverse.Height);
        for (var r = 0; r <= 2; r++)
        {
            for (var dy = -r; dy <= r; dy++)
            {
                for (var dx = -r; dx <= r; dx++)
                {
                    if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != r || inverse.At(cx + dx, cy + dy) is not { } start)
                        continue;
                    return grid.Invert(uv, start) is not null;
                }
            }
        }
        return false;
    }

    /// <summary>The black with every output black, modelled so it is sharp at every raster's edge: the room's light, bilinear over the
    /// canvas, plus each output's own leak where it reaches — fitted by least squares to the camera's measure away from every edge
    /// (three cells), where the camera's blur cannot reach. Its floor is the deepest the model reaches over the canvas.</summary>
    private sealed class BlackModel
    {
        private readonly double[] _c;

        private BlackModel(double[] c, double floor)
        {
            _c = c;
            Floor = floor;
        }

        public double Floor { get; }

        public double At(Vec2d uv, Func<int, bool> reaches)
        {
            var b = _c[0] + _c[1] * uv.X + _c[2] * uv.Y + _c[3] * uv.X * uv.Y;
            for (var k = 4; k < _c.Length; k++)
                if (reaches(k - 4))
                    b += _c[k];
            return b;
        }

        public static BlackModel? Fit(CanvasInverse[] inverses, double[] measured, int w, int h)
        {
            const double guard = 3;
            var n = inverses.Length;
            var inside = new float[n][];
            var away = new float[n][];
            for (var k = 0; k < n; k++)
            {
                var cov = new bool[w * h];
                var outside = new bool[w * h];
                for (var c = 0; c < cov.Length; c++)
                {
                    cov[c] = !float.IsNaN(inverses[k].X[c]);
                    outside[c] = !cov[c];
                }
                inside[k] = DistanceTransform.Euclidean(outside, w, h);
                away[k] = DistanceTransform.Euclidean(cov, w, h);
            }
            var m = 4 + n;
            var ata = new DenseMatrix(m, m);
            var atb = new double[m];
            var row = new double[m];
            var rows = 0;
            var seen = new int[n];
            for (var y = 1; y < h; y += 3)
            {
                for (var x = 1; x < w; x += 3)
                {
                    var c = y * w + x;
                    if (!double.IsFinite(measured[c]))
                        continue;
                    var clear = true;
                    var any = false;
                    var set = 0;
                    for (var k = 0; k < n && clear; k++)
                    {
                        var reaches = !float.IsNaN(inverses[k].X[c]);
                        clear = reaches ? inside[k][c] >= guard : away[k][c] >= guard;
                        any |= reaches;
                        if (reaches)
                            set |= 1 << Math.Min(k, 30);
                    }
                    if (!clear || !any)
                        continue;
                    var u = (x + 0.5) / w;
                    var v = (y + 0.5) / h;
                    row[0] = 1;
                    row[1] = u;
                    row[2] = v;
                    row[3] = u * v;
                    for (var k = 0; k < n; k++)
                    {
                        row[4 + k] = (set >> Math.Min(k, 30) & 1) != 0 ? 1 : 0;
                        seen[k] += (int)row[4 + k];
                    }
                    for (var a = 0; a < m; a++)
                    {
                        atb[a] += row[a] * measured[c];
                        for (var b = 0; b < m; b++)
                            ata[a, b] += row[a] * row[b];
                    }
                    rows++;
                }
            }
            if (rows < 8 * m || seen.Any(s => s == 0))
                return null;
            for (var a = 0; a < m; a++)
                ata[a, a] += 1e-9 * rows;
            var coef = new double[m];
            if (!LinearSolve.TryCholesky(ata, atb, coef))
                return null;
            var model = new BlackModel(coef, 0);
            var floor = double.MinValue;
            for (var y = 0; y < h; y += 2)
            {
                for (var x = 0; x < w; x += 2)
                {
                    var c = y * w + x;
                    if (!inverses.Any(i => !float.IsNaN(i.X[c])))
                        continue;
                    var uv = new Vec2d((x + 0.5) / w, (y + 0.5) / h);
                    floor = Math.Max(floor, model.At(uv, k => !float.IsNaN(inverses[k].X[c])));
                }
            }
            return floor > double.MinValue ? new BlackModel(coef, floor) : null;
        }
    }

    /// <summary>The grid's canvas uv per raster pixel: the median node spacing over the valid nodes (both axes, geometric mean).</summary>
    public static double UvPerPx(WarpGrid grid)
    {
        ArgumentNullException.ThrowIfNull(grid);
        var sx = new List<double>();
        var sy = new List<double>();
        var cellW = (double)grid.RasterWidth / (grid.Columns - 1);
        var cellH = (double)grid.RasterHeight / (grid.Rows - 1);
        for (var k = 0; k < grid.Rows; k++)
        {
            for (var i = 0; i < grid.Columns; i++)
            {
                var a = k * grid.Columns + i;
                if (!grid.Valid[a])
                    continue;
                if (i + 1 < grid.Columns && grid.Valid[a + 1])
                    sx.Add(Math.Sqrt(Sq(grid.U[a + 1] - grid.U[a]) + Sq(grid.V[a + 1] - grid.V[a])) / cellW);
                if (k + 1 < grid.Rows && grid.Valid[a + grid.Columns])
                    sy.Add(Math.Sqrt(Sq(grid.U[a + grid.Columns] - grid.U[a]) + Sq(grid.V[a + grid.Columns] - grid.V[a])) / cellH);
            }
        }
        if (sx.Count == 0 || sy.Count == 0)
            return 1.0 / Math.Max(grid.RasterWidth, grid.RasterHeight);
        sx.Sort();
        sy.Sort();
        return Math.Sqrt(sx[sx.Count / 2] * sy[sy.Count / 2]);
    }

    private static double Sq(double v) => v * v;

    private static double Bilinear(float[] values, int w, int h, Vec2d uv)
    {
        var fx = Math.Clamp(uv.X * w - 0.5, 0, w - 1);
        var fy = Math.Clamp(uv.Y * h - 0.5, 0, h - 1);
        var x0 = Math.Min((int)fx, Math.Max(0, w - 2));
        var y0 = Math.Min((int)fy, Math.Max(0, h - 2));
        var x1 = Math.Min(x0 + 1, w - 1);
        var y1 = Math.Min(y0 + 1, h - 1);
        var tx = fx - x0;
        var ty = fy - y0;
        return (values[y0 * w + x0] * (1 - tx) + values[y0 * w + x1] * tx) * (1 - ty) + (values[y1 * w + x0] * (1 - tx) + values[y1 * w + x1] * tx) * ty;
    }

    private static double Bilinear(double[] values, int w, int h, Vec2d uv)
    {
        var fx = Math.Clamp(uv.X * w - 0.5, 0, w - 1);
        var fy = Math.Clamp(uv.Y * h - 0.5, 0, h - 1);
        var x0 = Math.Min((int)fx, Math.Max(0, w - 2));
        var y0 = Math.Min((int)fy, Math.Max(0, h - 2));
        var x1 = Math.Min(x0 + 1, w - 1);
        var y1 = Math.Min(y0 + 1, h - 1);
        var tx = fx - x0;
        var ty = fy - y0;
        return (values[y0 * w + x0] * (1 - tx) + values[y0 * w + x1] * tx) * (1 - ty) + (values[y1 * w + x0] * (1 - tx) + values[y1 * w + x1] * tx) * ty;
    }

    /// <summary>A Gaussian low-pass of the values over the covered cells only (normalised convolution, separable); uncovered cells
    /// keep their value.</summary>
    private static double[] LowPass(double[] values, bool[] covered, int w, int h, double sigma)
    {
        var radius = (int)Math.Ceiling(3 * sigma);
        var kernel = new double[2 * radius + 1];
        for (var i = -radius; i <= radius; i++)
            kernel[i + radius] = Math.Exp(-i * i / (2 * sigma * sigma));
        var num = new double[w * h];
        var den = new double[w * h];
        Parallel.For(0, h, y =>
        {
            for (var x = 0; x < w; x++)
            {
                double s = 0, d = 0;
                for (var i = -radius; i <= radius; i++)
                {
                    var xx = x + i;
                    if (xx < 0 || xx >= w || !covered[y * w + xx])
                        continue;
                    s += kernel[i + radius] * values[y * w + xx];
                    d += kernel[i + radius];
                }
                num[y * w + x] = s;
                den[y * w + x] = d;
            }
        });
        var o = new double[w * h];
        Parallel.For(0, w, x =>
        {
            for (var y = 0; y < h; y++)
            {
                if (!covered[y * w + x])
                {
                    o[y * w + x] = values[y * w + x];
                    continue;
                }
                double s = 0, d = 0;
                for (var i = -radius; i <= radius; i++)
                {
                    var yy = y + i;
                    if (yy < 0 || yy >= h)
                        continue;
                    s += kernel[i + radius] * num[yy * w + x];
                    d += kernel[i + radius] * den[yy * w + x];
                }
                o[y * w + x] = d > 0 ? s / d : values[y * w + x];
            }
        });
        return o;
    }
}
