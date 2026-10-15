using System.Collections.Immutable;
using System.Globalization;
using PlayMap.Core.Calibration;
using PlayMap.Core.Numerics;

namespace PlayMap.Core.Align;

/// <summary>One output's dense correspondences: camera pixel → the projector (output raster) point that lights it.</summary>
public sealed record OutputPairs(string OutputId, int Width, int Height, Vec2d[] Camera, Vec2d[] Projector);

/// <summary>The camera's lens as the flat fit models it: a camera pixel normalised about the picture's centre by its half-diagonal
/// (x = (c − centre) / scale), undistorted by u = x·(1 + k1·r² + k2·r⁴). The centre is the picture's: a camera of unknown optics
/// has no better one.</summary>
public sealed record CameraLens(double Cx, double Cy, double Scale, double K1, double K2)
{
    public static CameraLens For(int width, int height) => new(width / 2.0, height / 2.0, Math.Sqrt((double)width * width + (double)height * height) / 2, 0, 0);

    public Vec2d Normalise(Vec2d pixel) => new((pixel.X - Cx) / Scale, (pixel.Y - Cy) / Scale);

    /// <summary>A camera pixel → its undistorted normalised point.</summary>
    public Vec2d Undistort(Vec2d pixel) => Undistort(Normalise(pixel), K1, K2);

    internal static Vec2d Undistort(Vec2d x, double k1, double k2)
    {
        var r2 = x.LengthSquared;
        return x * (1 + k1 * r2 + k2 * r2 * r2);
    }

    /// <summary>An undistorted normalised point → the camera pixel that sees it (Newton on the radius).</summary>
    public Vec2d Distort(Vec2d u)
    {
        var ru = u.Length;
        if (ru < 1e-15)
            return new Vec2d(Cx, Cy);
        var r = ru;
        for (var i = 0; i < 12; i++)
        {
            var r2 = r * r;
            var f = r * (1 + K1 * r2 + K2 * r2 * r2) - ru;
            var df = 1 + 3 * K1 * r2 + 5 * K2 * r2 * r2;
            if (Math.Abs(df) < 1e-9)
                break;
            r -= f / df;
        }
        var x = u * (r / ru);
        return new Vec2d(x.X * Scale + Cx, x.Y * Scale + Cy);
    }
}

/// <summary>The flat fit: the camera's lens, one homography per output (undistorted normalised camera point → output raster pixel), each
/// output's and the whole fit's residual in output pixels (RMS over the pairs within 3 px; the 95th percentile of all), the share of
/// pairs within 3 px, and whether the screen is flat (RMS under the tolerance with nine pairs in ten within 3 px).</summary>
public sealed record PlanarFitResult(CameraLens Lens, ImmutableArray<Mat3d> Homographies, ImmutableArray<double> OutputRmsPx, double RmsPx,
    double P95Px, double InlierShare, bool Flat, string Words)
{
    /// <summary>Output <paramref name="index"/>'s raster pixel for a camera pixel, through the fit.</summary>
    public Vec2d ToOutput(int index, Vec2d cameraPixel) => Homographies[index].TransformPoint(Lens.Undistort(cameraPixel));
}

/// <summary>
/// The camera's radial distortion (k1, k2) and one homography per output, fitted jointly to the outputs' dense pairs: a plane seen
/// through a lens is a homography only once the lens is undone, and every output's homography shares the one lens, which pins it
/// down. Levenberg–Marquardt from each output's robust DLT, re-weighted by Huber (δ 1 px) over three passes, on an even subsample of
/// each output's pairs; the residuals are then measured on every pair. A screen whose pairs a plane cannot explain to
/// <see cref="TolerancePx"/> is not flat. Pure.
/// </summary>
public static class PlanarFit
{
    /// <summary>The flat test: the robust RMS residual (output pixels) a flat screen stays under.</summary>
    public const double TolerancePx = 0.5;

    private const double InlierPx = 3;
    private const double HuberPx = 1;

    public static PlanarFitResult Fit(int cameraWidth, int cameraHeight, IReadOnlyList<OutputPairs> outputs, int maxPairsPerOutput = 1500,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(outputs);
        if (outputs.Count == 0)
            throw new ArgumentException("The fit needs an output.", nameof(outputs));
        var lens0 = CameraLens.For(cameraWidth, cameraHeight);
        var problems = new List<Part>();
        var init = new List<double> { 0, 0 };
        foreach (var o in outputs)
        {
            if (o.Camera.Length < 16)
                return Failed(lens0, outputs.Count, $"{o.OutputId} decoded too few camera pixels ({o.Camera.Length}) for a fit");
            var stride = Math.Max(1, o.Camera.Length / Math.Max(16, maxPairsPerOutput));
            var cam = new List<Vec2d>();
            var proj = new List<Vec2d>();
            for (var i = 0; i < o.Camera.Length; i += stride)
            {
                cam.Add(o.Camera[i]);
                proj.Add(o.Projector[i]);
            }
            var robust = Homography.FitRobust(cam, proj, 3);
            if (!robust.Succeeded)
                return Failed(lens0, outputs.Count, $"{o.OutputId}'s pairs fit no homography");
            var half = Math.Max(o.Width, o.Height) / 2.0;
            var toCamNorm = new Mat3d(lens0.Scale, 0, lens0.Cx, 0, lens0.Scale, lens0.Cy, 0, 0, 1);
            var toProjNorm = new Mat3d(1 / half, 0, -o.Width / 2.0 / half, 0, 1 / half, -o.Height / 2.0 / half, 0, 0, 1);
            var hn = toProjNorm * robust.H * toCamNorm;
            var a = hn.ToArray();
            for (var i = 0; i < 8; i++)
                init.Add(a[i] / a[8]);
            var xn = cam.Select(lens0.Normalise).ToArray();
            var qn = proj.Select(q => new Vec2d((q.X - o.Width / 2.0) / half, (q.Y - o.Height / 2.0) / half)).ToArray();
            problems.Add(new Part(xn, qn, half, new double[xn.Length]));
        }
        foreach (var p in problems)
            Array.Fill(p.Weights, 1.0);
        var parameters = init.ToArray();
        for (var pass = 0; pass < 3; pass++)
        {
            ct.ThrowIfCancellationRequested();
            var lm = LevenbergMarquardt.Solve(new Joint(problems), parameters, maxIterations: 60, tolerance: 1e-12);
            parameters = lm.Parameters;
            for (var j = 0; j < problems.Count; j++)
            {
                var p = problems[j];
                var h = H(parameters, j);
                for (var i = 0; i < p.Camera.Length; i++)
                {
                    var r = Residual(parameters[0], parameters[1], h, p, i).Length;
                    p.Weights[i] = r <= HuberPx ? 1 : HuberPx / r;
                }
            }
        }
        var lens = lens0 with { K1 = parameters[0], K2 = parameters[1] };
        var hs = ImmutableArray.CreateBuilder<Mat3d>(outputs.Count);
        var rmsEach = ImmutableArray.CreateBuilder<double>(outputs.Count);
        var all = new List<double>();
        double sum = 0;
        var inliers = 0;
        for (var j = 0; j < outputs.Count; j++)
        {
            var o = outputs[j];
            var half = problems[j].Half;
            var fromNorm = new Mat3d(half, 0, o.Width / 2.0, 0, half, o.Height / 2.0, 0, 0, 1);
            var h = fromNorm * H(parameters, j);
            hs.Add(h);
            double s = 0;
            var n = 0;
            for (var i = 0; i < o.Camera.Length; i++)
            {
                var r = (h.TransformPoint(lens.Undistort(o.Camera[i])) - o.Projector[i]).Length;
                all.Add(r);
                if (r > InlierPx)
                    continue;
                s += r * r;
                n++;
            }
            rmsEach.Add(n > 0 ? Math.Sqrt(s / n) : double.PositiveInfinity);
            sum += s;
            inliers += n;
        }
        var rms = inliers > 0 ? Math.Sqrt(sum / inliers) : double.PositiveInfinity;
        var share = all.Count > 0 ? (double)inliers / all.Count : 0;
        all.Sort();
        var p95 = all.Count > 0 ? all[(int)Math.Min(all.Count - 1, Math.Floor(0.95 * all.Count))] : double.PositiveInfinity;
        var flat = rms < TolerancePx && share >= 0.9;
        var words = flat
            ? string.Create(CultureInfo.InvariantCulture, $"flat: the outputs fit one plane to {rms:0.00} px (95 % within {p95:0.00} px); the camera's lens k1 {lens.K1:0.000}, k2 {lens.K2:0.000}")
            : string.Create(CultureInfo.InvariantCulture, $"not flat: the best plane misses by {rms:0.00} px RMS with {share * 100:0} % of the pairs within {InlierPx:0} px — a curved screen is next round (cylinder, then dome)");
        return new PlanarFitResult(lens, hs.ToImmutable(), rmsEach.ToImmutable(), rms, p95, share, flat, words);
    }

    private static PlanarFitResult Failed(CameraLens lens, int count, string words) =>
        new(lens, [.. Enumerable.Repeat(Mat3d.Identity, count)], [.. Enumerable.Repeat(double.PositiveInfinity, count)], double.PositiveInfinity,
            double.PositiveInfinity, 0, false, words);

    private static Mat3d H(ReadOnlySpan<double> p, int j)
    {
        var o = 2 + 8 * j;
        return new Mat3d(p[o], p[o + 1], p[o + 2], p[o + 3], p[o + 4], p[o + 5], p[o + 6], p[o + 7], 1);
    }

    private static Vec2d Residual(double k1, double k2, Mat3d h, Part p, int i)
    {
        var u = CameraLens.Undistort(p.Camera[i], k1, k2);
        var q = h.TransformPoint(u);
        return (q - p.Projector[i]) * p.Half;
    }

    /// <summary>One output's share of the joint problem: normalised camera points, normalised output points, the output's half size
    /// (normalised → pixels) and the Huber weights of the pass.</summary>
    private sealed record Part(Vec2d[] Camera, Vec2d[] Projector, double Half, double[] Weights);

    private sealed class Joint(List<Part> parts) : ILeastSquaresProblem
    {
        public int ParameterCount => 2 + 8 * parts.Count;

        public int ResidualCount => parts.Sum(p => 2 * p.Camera.Length);

        public void Residuals(ReadOnlySpan<double> parameters, Span<double> residuals)
        {
            var at = 0;
            for (var j = 0; j < parts.Count; j++)
            {
                var h = H(parameters, j);
                var p = parts[j];
                for (var i = 0; i < p.Camera.Length; i++)
                {
                    var r = Residual(parameters[0], parameters[1], h, p, i);
                    var w = Math.Sqrt(p.Weights[i]);
                    residuals[at++] = r.X * w;
                    residuals[at++] = r.Y * w;
                }
            }
        }

        public bool Jacobian(ReadOnlySpan<double> parameters, DenseMatrix jacobian) => false;
    }
}
