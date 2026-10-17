using System.Runtime.CompilerServices;
using PlayMap.Core.Numerics;
using PlayMap.Core.Threading;
using PlayMap.Core.Vision;
using PlayMap.Vision.Segmentation;

namespace PlayMap.Vision.Pins;

/// <summary>How corners are found.</summary>
public sealed record CornerOptions(
    /// <summary>The structure tensor's window radius (2 → 5×5).</summary>
    int WindowRadius = 2,
    /// <summary>Corners weaker than this fraction of the strongest are ignored.</summary>
    double Quality = 0.01,
    /// <summary>Pixels this close to the border are not considered.</summary>
    int Border = 4,
    /// <summary>A corner suppresses a weaker one only when it is clearly stronger (ANMS robustness).</summary>
    double Robustness = 0.9,
    int MaxCandidates = 4000)
{
    public static readonly CornerOptions Default = new();
}

/// <summary>Shi–Tomasi corners, spread across the frame: the smaller eigenvalue of the gradient structure tensor
/// over a small window (large only where the picture changes in two directions), local maxima above a quality
/// level, then adaptive non-maximal suppression — each corner ranked by the distance to the nearest clearly
/// stronger one — so the strongest corners of every part of the frame survive rather than a cluster in the
/// busiest part. Each is refined to sub-pixel accuracy (the point where the gradients around it are all
/// orthogonal to the lines towards it). Pixel coordinates are continuous, pixel centres at +0.5.</summary>
public sealed class CornerDetector : IFeatureSuggester
{
    private readonly CornerOptions _options;

    public CornerDetector(CornerOptions? options = null) => _options = options ?? CornerOptions.Default;

    public string Id => "shi-tomasi";

    public Task<IReadOnlyList<FeaturePoint>> SuggestAsync(ImageGray8 image, int maxCount, CancellationToken cancellationToken) =>
        Compute.RunAsync(() => Detect(image, maxCount, cancellationToken), cancellationToken);

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public IReadOnlyList<FeaturePoint> Detect(ImageGray8 image, int maxCount, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(image);
        int w = image.Width, h = image.Height, n = w * h;
        var g = new float[n];
        for (var y = 0; y < h; y++)
        {
            var row = image.Row(y);
            for (var x = 0; x < w; x++)
                g[y * w + x] = row[x];
        }
        var (gx, gy) = Gradients(g, w, h);
        var xx = new float[n];
        var yy = new float[n];
        var xy = new float[n];
        for (var i = 0; i < n; i++)
        {
            xx[i] = gx[i] * gx[i];
            yy[i] = gy[i] * gy[i];
            xy[i] = gx[i] * gy[i];
        }
        var scratch = new float[n];
        var r = _options.WindowRadius;
        LabImage.BoxBlur(xx, scratch, w, h, r);
        LabImage.BoxBlur(yy, scratch, w, h, r);
        LabImage.BoxBlur(xy, scratch, w, h, r);
        cancellationToken.ThrowIfCancellationRequested();

        var response = new float[n];
        float best = 0;
        for (var i = 0; i < n; i++)
        {
            float a = xx[i], c = yy[i], b = xy[i];
            var half = (a - c) * 0.5f;
            var lambda = (a + c) * 0.5f - MathF.Sqrt(half * half + b * b);
            response[i] = lambda;
            best = Math.Max(best, lambda);
        }
        if (best <= 0)
            return [];
        var threshold = (float)(best * _options.Quality);
        var border = Math.Max(_options.Border, 1);
        var candidates = new List<(int X, int Y, float S)>();
        for (var y = border; y < h - border; y++)
        {
            for (var x = border; x < w - border; x++)
            {
                var i = y * w + x;
                var v = response[i];
                if (v < threshold)
                    continue;
                // A strict local maximum over the 3×3 (ties broken towards the earlier pixel).
                var isMax = true;
                for (var dy = -1; dy <= 1 && isMax; dy++)
                    for (var dx = -1; dx <= 1 && isMax; dx++)
                    {
                        if (dx == 0 && dy == 0)
                            continue;
                        var o = response[i + dy * w + dx];
                        isMax = o < v || (o == v && dy * w + dx > 0);
                    }
                if (isMax)
                    candidates.Add((x, y, v));
            }
        }
        candidates.Sort((p, q) => q.S.CompareTo(p.S));
        if (candidates.Count > _options.MaxCandidates)
            candidates.RemoveRange(_options.MaxCandidates, candidates.Count - _options.MaxCandidates);

        // Adaptive non-maximal suppression: the radius to the nearest clearly stronger corner.
        var radius = new double[candidates.Count];
        for (var i = 0; i < candidates.Count; i++)
        {
            var r2 = double.MaxValue;
            for (var j = 0; j < i; j++)
            {
                if (candidates[j].S * _options.Robustness <= candidates[i].S)
                    continue;
                double dx = candidates[j].X - candidates[i].X, dy = candidates[j].Y - candidates[i].Y;
                r2 = Math.Min(r2, dx * dx + dy * dy);
            }
            radius[i] = r2;
        }
        var order = Enumerable.Range(0, candidates.Count).OrderByDescending(i => radius[i]).ThenByDescending(i => candidates[i].S).Take(maxCount);
        var result = new List<FeaturePoint>();
        foreach (var i in order)
        {
            var c = candidates[i];
            result.Add(new FeaturePoint(Refine(gx, gy, w, h, c.X, c.Y), c.S));
        }
        result.Sort((p, q) => q.Strength.CompareTo(p.Strength));
        return result;
    }

    /// <summary>Central-difference gradients (Sobel, ÷8).</summary>
    private static (float[] Gx, float[] Gy) Gradients(float[] g, int w, int h)
    {
        var gx = new float[g.Length];
        var gy = new float[g.Length];
        for (var y = 1; y < h - 1; y++)
        {
            for (var x = 1; x < w - 1; x++)
            {
                var i = y * w + x;
                gx[i] = (g[i - w + 1] + 2 * g[i + 1] + g[i + w + 1] - g[i - w - 1] - 2 * g[i - 1] - g[i + w - 1]) / 8f;
                gy[i] = (g[i + w - 1] + 2 * g[i + w] + g[i + w + 1] - g[i - w - 1] - 2 * g[i - w] - g[i - w + 1]) / 8f;
            }
        }
        return (gx, gy);
    }

    /// <summary>Sub-pixel corner: q = (Σ g gᵀ)⁻¹ Σ g gᵀ p over a Gaussian-weighted 9×9 window around q, iterated.
    /// Kept at the pixel centre when the system is singular or the refinement wanders more than 2.5 pixels.</summary>
    private static Vec2d Refine(float[] gx, float[] gy, int w, int h, int x0, int y0)
    {
        var start = new Vec2d(x0 + 0.5, y0 + 0.5);
        var q = start;
        const int r = 4;
        for (var it = 0; it < 8; it++)
        {
            double a = 0, b = 0, c = 0, bx = 0, by = 0;
            int cx = (int)Math.Floor(q.X), cy = (int)Math.Floor(q.Y);
            for (var dy = -r; dy <= r; dy++)
            {
                for (var dx = -r; dx <= r; dx++)
                {
                    int x = cx + dx, y = cy + dy;
                    if (x < 1 || y < 1 || x >= w - 1 || y >= h - 1)
                        continue;
                    var i = y * w + x;
                    double px = x + 0.5, py = y + 0.5;
                    var wt = Math.Exp(-((px - q.X) * (px - q.X) + (py - q.Y) * (py - q.Y)) / (2 * 2.5 * 2.5));
                    double u = gx[i], v = gy[i];
                    a += wt * u * u;
                    b += wt * u * v;
                    c += wt * v * v;
                    bx += wt * (u * u * px + u * v * py);
                    by += wt * (u * v * px + v * v * py);
                }
            }
            var det = a * c - b * b;
            if (Math.Abs(det) < 1e-9 * (a + c) * (a + c) || det == 0)
                return start;
            var next = new Vec2d((c * bx - b * by) / det, (a * by - b * bx) / det);
            if ((next - start).Length > 2.5)
                return start;
            var moved = (next - q).Length;
            q = next;
            if (moved < 0.005)
                break;
        }
        return q;
    }
}
