using System.Collections.Immutable;
using System.Globalization;
using PlayMap.Core.Model;
using PlayMap.Core.Numerics;
using PlayMap.Core.Scan;

namespace PlayMap.Core.Align;

/// <summary>What the camera saw of one output: its structured-light decode at the camera's raster, its footprint, the picture with it
/// white alone, its photometry, and what the rectification may assume about its projector.</summary>
public sealed record AlignCapture(string OutputId, int Width, int Height, CorrespondenceMap Map, Footprint Footprint, byte[] White,
    ProjectorPhotometry Photometry, ProjectorPrior Prior);

/// <summary>A solve's inputs: the camera's raster and its picture with every output black, each output's capture, where the canvas goes
/// (INSIDE with the canvas's aspect and a margin, or CORNERS with the corners clicked on the camera picture, 0–1, TL TR BR BL) and
/// the group's options.</summary>
public sealed record AlignSolveInput(int CameraWidth, int CameraHeight, byte[] Black, IReadOnlyList<AlignCapture> Outputs, CanvasFit Fit,
    double Aspect, IReadOnlyList<Vec2d> CameraCorners, double MarginPct, AlignOptions Options)
{
    /// <summary>ONLY: the earlier run's captures of the same outputs, in order. The screen, the canvas and the kept outputs' warps come
    /// from them exactly as that run made them; only the outputs in <see cref="Rescanned"/> are fitted from their new captures, to that
    /// same canvas. The blend is made again from every output's light (the new black, the kept whites and the new ones).</summary>
    public IReadOnlyList<AlignCapture>? Reference { get; init; }

    /// <summary>The outputs (indices) a run with a reference scanned again.</summary>
    public IReadOnlySet<int>? Rescanned { get; init; }
}

/// <summary>One output's result: its measured warp, its blend at a quarter of its raster, its γ, the share of its footprint decoded,
/// and its residual to the flat fit (output pixels).</summary>
public sealed record AlignOutputResult(string OutputId, WarpFitResult Warp, OutputBlend Blend, double Gamma, double DecodedShare, double FitRmsPx);

/// <summary>A solve's result: whether it staged anything, in words; the flat fit, the rectification and the canvas target; each output's
/// result; and the measures the score is made of — the seams' disagreement (p95, output px), the share of the canvas covered, the
/// white's p5/p95 after the blend.</summary>
public sealed record AlignSolveResult(bool Ok, string Words, PlanarFitResult? Planar, Rectification? Rectification, CanvasTargetResult? Target,
    ImmutableArray<AlignOutputResult> Outputs, double SeamPx, double CoverageShare, double WhiteP5OverP95)
{
    public static AlignSolveResult Failed(string words, PlanarFitResult? planar = null) => new(false, words, planar, null, null, [], -1, 0, double.NaN);
}

/// <summary>
/// The flat-screen solve, from what the camera saw to each output's measured warp and blend: dense pairs from each decode inside its
/// footprint; the joint fit of the camera's lens and one homography per output, which refuses a screen that is not flat; the plane
/// rectified (from the corners, else from the projectors); the canvas placed on it (INSIDE or CORNERS); every output's warp fitted
/// to the canvas uv its camera pixels see — the same target for every output, so seams and stacks agree to the decode's precision;
/// then the measured blend. Pure.
/// </summary>
public static class AlignSolve
{
    /// <summary>The canvas raster the blend works on, across.</summary>
    public const int BlendCanvasWidth = 1024;

    public static AlignSolveResult Solve(AlignSolveInput input, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.Outputs.Count == 0)
            return AlignSolveResult.Failed("there is no output to align");
        var cw = input.CameraWidth;
        var chh = input.CameraHeight;

        var reference = input.Reference is { } r && r.Count == input.Outputs.Count ? r : null;
        var basis = reference ?? input.Outputs;

        // Dense pairs inside each footprint: of the captures the geometry comes from, and (with a reference) of the new ones.
        var pairs = Pairs(basis, cw, chh, out var decoded);
        var fresh = reference is null ? pairs : Pairs(input.Outputs, cw, chh, out decoded);
        ct.ThrowIfCancellationRequested();

        var planar = PlanarFit.Fit(cw, chh, pairs, ct: ct);
        if (!planar.Flat)
            return AlignSolveResult.Failed(planar.Words, planar);
        ct.ThrowIfCancellationRequested();

        // The plane, and what the outputs cover on it.
        var eroded = basis.Select(o => Footprints.Erode(o.Footprint.Mask, cw, chh, 2)).ToArray();
        var union = new bool[cw * chh];
        foreach (var m in eroded)
            for (var i = 0; i < union.Length; i++)
                union[i] |= m[i];
        var lens = planar.Lens;
        Rectification rect;
        var aspect = input.Aspect > 0 ? input.Aspect : 16.0 / 9;
        if (input.Fit == CanvasFit.Corners)
        {
            if (input.CameraCorners.Count != 4)
                return AlignSolveResult.Failed("FIT CORNERS needs the screen's corners — ALIGN CORNERS first", planar);
            rect = PlaneRectify.FromCorners([.. input.CameraCorners.Select(c => lens.Undistort(new Vec2d(c.X * cw, c.Y * chh)))], aspect);
        }
        else
        {
            var samples = new List<Vec2d>();
            foreach (var o in basis)
                samples.Add(lens.Undistort(o.Footprint.Centroid));
            for (var i = 0; i < union.Length; i += 97)
                if (union[i])
                    samples.Add(lens.Undistort(new Vec2d(i % cw + 0.5, i / cw + 0.5)));
            rect = PlaneRectify.FromProjectors(planar.Homographies, [.. basis.Select(o => o.Prior)], samples);
        }
        bool Covered(Vec2d plane)
        {
            var c = lens.Distort(rect.Inverse(plane));
            var x = (int)Math.Floor(c.X);
            var y = (int)Math.Floor(c.Y);
            return x >= 0 && y >= 0 && x < cw && y < chh && union[y * cw + x];
        }
        var min = new Vec2d(double.MaxValue, double.MaxValue);
        var max = new Vec2d(double.MinValue, double.MinValue);
        for (var i = 0; i < union.Length; i += 3)
        {
            if (!union[i])
                continue;
            var p = rect.Apply(lens.Undistort(new Vec2d(i % cw + 0.5, i / cw + 0.5)));
            min = new Vec2d(Math.Min(min.X, p.X), Math.Min(min.Y, p.Y));
            max = new Vec2d(Math.Max(max.X, p.X), Math.Max(max.Y, p.Y));
        }
        var target = input.Fit == CanvasFit.Corners
            ? CanvasTarget.Corners(aspect, Covered)
            : CanvasTarget.Inside(Covered, min, max, aspect, input.MarginPct);
        if (target is null)
            return AlignSolveResult.Failed("the outputs cover no part of the screen the canvas could take", planar);
        ct.ThrowIfCancellationRequested();

        // Each output's warp to the one target.
        var warps = new WarpFitResult[input.Outputs.Count];
        var fitRms = planar.OutputRmsPx;
        Parallel.For(0, input.Outputs.Count, new ParallelOptions { CancellationToken = ct }, j =>
        {
            var o = input.Outputs[j];
            var from = reference is not null && input.Rescanned?.Contains(j) == true ? fresh[j] : pairs[j];
            var uv = new Vec2d[from.Camera.Length];
            for (var i = 0; i < uv.Length; i++)
                uv[i] = target.ToUv(rect.Apply(lens.Undistort(from.Camera[i])));
            warps[j] = MeasuredWarpFit.Fit(o.Width, o.Height, input.Options.GridColumns, input.Options.GridRows, from.Projector, uv, ct);
        });
        var seam = SeamP95(input, warps);
        ct.ThrowIfCancellationRequested();

        // The blend from the camera's light.
        var black = Smooth(Photometry.Linear(input.Black), cw, chh);
        var whites = input.Outputs.Select(o =>
        {
            var w = Smooth(Photometry.Linear(o.White), cw, chh);
            for (var i = 0; i < w.Length; i++)
                w[i] = o.Footprint.Mask[i] ? Math.Max(0, w[i] - black[i]) : float.NaN;
            return w;
        }).ToArray();
        Vec2d CameraAt(Vec2d uv) => lens.Distort(rect.Inverse(target.FromUv(uv)));
        double WhiteLight(int k, Vec2d uv) => Sample(whites[k], cw, chh, CameraAt(uv));
        double BlackLight(Vec2d uv) => Sample(black, cw, chh, CameraAt(uv));
        var ch = Math.Clamp((int)Math.Round(BlendCanvasWidth * target.Height / target.Width), 16, 4096);
        var gammas = input.Outputs.Select(o => o.Photometry.Measured ? o.Photometry.Gamma : 2.2).ToArray();
        var blend = MeasuredBlend2D.Solve(new BlendInput([.. warps.Select(w => w.Grid)], WhiteLight, BlackLight, input.Options.Curve,
            input.Options.EdgeWidth, input.Options.Uniformity, input.Options.BlackLevel, BlendCanvasWidth, ch), ct);

        var outputs = ImmutableArray.CreateBuilder<AlignOutputResult>(input.Outputs.Count);
        for (var j = 0; j < input.Outputs.Count; j++)
            outputs.Add(new AlignOutputResult(input.Outputs[j].OutputId, warps[j], blend.Maps[j], gammas[j], decoded[j], fitRms[j]));
        var words = string.Join("; ", planar.Words, rect.Words, target.Words, blend.Words)
            + (seam >= 0 ? string.Create(CultureInfo.InvariantCulture, $"; seams agree to {seam:0.00} px (95 %)") : "");
        return new AlignSolveResult(true, words, planar, rect, target, outputs.ToImmutable(), seam, target.CoverageShare, blend.WhiteP5OverP95);
    }

    private static OutputPairs[] Pairs(IReadOnlyList<AlignCapture> captures, int cw, int chh, out double[] decoded)
    {
        var pairs = new OutputPairs[captures.Count];
        decoded = new double[captures.Count];
        for (var j = 0; j < captures.Count; j++)
        {
            var o = captures[j];
            var cam = new List<Vec2d>();
            var proj = new List<Vec2d>();
            for (var y = 0; y < chh; y++)
            {
                for (var x = 0; x < cw; x++)
                {
                    if (!o.Footprint.Mask[y * cw + x] || !o.Map.TryGet(x, y, out var q))
                        continue;
                    cam.Add(new Vec2d(x + 0.5, y + 0.5));
                    proj.Add(q);
                }
            }
            decoded[j] = o.Footprint.Area > 0 ? (double)cam.Count / o.Footprint.Area : 0;
            pairs[j] = new OutputPairs(o.OutputId, o.Width, o.Height, [.. cam], [.. proj]);
        }
        return pairs;
    }

    /// <summary>Where two outputs both decoded a camera pixel, how far their warps put that pixel's canvas point apart, in output
    /// pixels: the 95th percentile (−1 when no pixel is shared).</summary>
    private static double SeamP95(AlignSolveInput input, WarpFitResult[] warps)
    {
        var gaps = new List<double>();
        var n = input.Outputs.Count;
        var cw = input.CameraWidth;
        for (var a = 0; a < n; a++)
        {
            for (var b = a + 1; b < n; b++)
            {
                var ma = input.Outputs[a].Map;
                var mb = input.Outputs[b].Map;
                var scale = (warps[a].UvPerPx + warps[b].UvPerPx) / 2;
                for (var i = 0; i < ma.Flags.Length; i += 2)
                {
                    var x = i % cw;
                    var y = i / cw;
                    if (!ma.TryGet(x, y, out var qa) || !mb.TryGet(x, y, out var qb))
                        continue;
                    if (warps[a].Grid.Eval(qa) is not { } ua || warps[b].Grid.Eval(qb) is not { } ub)
                        continue;
                    gaps.Add((ua - ub).Length / scale);
                }
            }
        }
        if (gaps.Count == 0)
            return -1;
        gaps.Sort();
        return gaps[Math.Min(gaps.Count - 1, (int)(0.95 * gaps.Count))];
    }

    /// <summary>A 5 × 5 box average (edges clamped): the camera's noise out of a light measure.</summary>
    private static float[] Smooth(float[] values, int w, int h)
    {
        var tmp = new float[values.Length];
        var o = new float[values.Length];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                double s = 0;
                for (var i = -2; i <= 2; i++)
                    s += values[y * w + Math.Clamp(x + i, 0, w - 1)];
                tmp[y * w + x] = (float)(s / 5);
            }
        }
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                double s = 0;
                for (var i = -2; i <= 2; i++)
                    s += tmp[Math.Clamp(y + i, 0, h - 1) * w + x];
                o[y * w + x] = (float)(s / 5);
            }
        }
        return o;
    }

    /// <summary>A camera-raster value at a continuous camera point, bilinear; NaN outside the raster or where a neighbour is NaN.</summary>
    private static double Sample(float[] values, int w, int h, Vec2d c)
    {
        var fx = c.X - 0.5;
        var fy = c.Y - 0.5;
        if (fx < 0 || fy < 0 || fx > w - 1 || fy > h - 1)
            return double.NaN;
        var x0 = Math.Min((int)fx, w - 2);
        var y0 = Math.Min((int)fy, h - 2);
        var tx = fx - x0;
        var ty = fy - y0;
        var a = values[y0 * w + x0];
        var b = values[y0 * w + x0 + 1];
        var cc = values[(y0 + 1) * w + x0];
        var d = values[(y0 + 1) * w + x0 + 1];
        return (a * (1 - tx) + b * tx) * (1 - ty) + (cc * (1 - tx) + d * tx) * ty;
    }
}
