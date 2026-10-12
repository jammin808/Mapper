using System.Runtime.CompilerServices;
using PlayMap.Core.Model;
using PlayMap.Core.Vision;

namespace PlayMap.Vision.Imaging;

/// <summary>The taps of a separable triangle filter along one axis: for each destination sample, the first source
/// index, how many follow, and their weights (summing to one). Pixel centres align: destination sample i (centre
/// i + 0.5) reads the source around start + (i + 0.5) · scale. Upscaling is plain bilinear; downscaling widens
/// the triangle to the scale so every source pixel contributes (no aliasing).</summary>
internal sealed class AxisTaps
{
    public AxisTaps(double srcStart, double srcLength, int srcLimit, int dstLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(srcLimit);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(dstLength);
        var scale = srcLength / dstLength;
        var support = Math.Max(scale, 1.0);
        var maxTaps = (int)Math.Ceiling(support) * 2 + 2;
        Start = new int[dstLength];
        Count = new int[dstLength];
        Weights = new float[dstLength * maxTaps];
        MaxTaps = maxTaps;
        for (var i = 0; i < dstLength; i++)
        {
            var centre = srcStart + (i + 0.5) * scale;
            var lo = Math.Max((int)Math.Floor(centre - support), 0);
            var hi = Math.Min((int)Math.Ceiling(centre + support), srcLimit);
            if (hi <= lo)
            {
                // The centre lies outside the source: hold the nearest edge pixel.
                lo = Math.Clamp((int)Math.Floor(centre), 0, srcLimit - 1);
                hi = lo + 1;
            }
            double sum = 0;
            var n = 0;
            for (var j = lo; j < hi && n < maxTaps; j++, n++)
            {
                var w = Math.Max(0.0, 1.0 - Math.Abs(j + 0.5 - centre) / support);
                Weights[i * maxTaps + n] = (float)w;
                sum += w;
            }
            if (sum <= 0)
            {
                // Between taps with no weight (only at a clamped edge): the nearest pixel.
                lo = Math.Clamp((int)Math.Floor(centre), 0, srcLimit - 1);
                n = 1;
                Weights[i * maxTaps] = 1;
                sum = 1;
            }
            for (var k = 0; k < n; k++)
                Weights[i * maxTaps + k] = (float)(Weights[i * maxTaps + k] / sum);
            Start[i] = lo;
            Count[i] = n;
        }
    }

    public int[] Start { get; }
    public int[] Count { get; }
    public float[] Weights { get; }
    public int MaxTaps { get; }
}

/// <summary>Resampling without an image library: RGB bytes to planar floats (the model input) and one float
/// channel to another (logits, depth). Two passes, horizontal then vertical, through one float scratch row
/// block; no per-pixel allocation.</summary>
public static class Resampler
{
    /// <summary>Resamples <paramref name="region"/> of the picture (pixel units, may be fractional) into a
    /// <paramref name="dstWidth"/> × <paramref name="dstHeight"/> rectangle placed at (<paramref name="dstX"/>,
    /// <paramref name="dstY"/>) of three planes of a <paramref name="planeWidth"/>-wide CHW tensor, each value
    /// mapped v ↦ v · <paramref name="gain"/>[c] + <paramref name="bias"/>[c] with c the tensor's plane
    /// (normalisation folded in; planes are R G B, or B G R when <paramref name="swapRedBlue"/>). Tensor pixels
    /// outside the rectangle are left as they are.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static void RgbToPlanar(
        ImageRgb8 src, RectD region, Span<float> chw, int planeWidth, int planeHeight,
        int dstX, int dstY, int dstWidth, int dstHeight,
        ReadOnlySpan<float> gain, ReadOnlySpan<float> bias, bool swapRedBlue = false)
    {
        ArgumentNullException.ThrowIfNull(src);
        if (dstX < 0 || dstY < 0 || dstX + dstWidth > planeWidth || dstY + dstHeight > planeHeight)
            throw new ArgumentOutOfRangeException(nameof(dstWidth), "The rectangle must lie inside the tensor.");
        if (chw.Length < 3 * planeWidth * planeHeight)
            throw new ArgumentException("The tensor is smaller than three planes.", nameof(chw));
        var xt = new AxisTaps(region.X, region.W, src.Width, dstWidth);
        var yt = new AxisTaps(region.Y, region.H, src.Height, dstHeight);

        // Horizontal pass over only the source rows the vertical pass will read.
        var rowLo = yt.Start[0];
        var rowHi = yt.Start[dstHeight - 1] + yt.Count[dstHeight - 1];
        var rows = rowHi - rowLo;
        var tmp = new float[rows * dstWidth * 3];
        for (var r = 0; r < rows; r++)
        {
            var srcRow = src.Row(rowLo + r);
            var o = r * dstWidth * 3;
            for (var x = 0; x < dstWidth; x++)
            {
                float a = 0, b = 0, c = 0;
                var s = xt.Start[x];
                var wi = x * xt.MaxTaps;
                for (var k = 0; k < xt.Count[x]; k++)
                {
                    var w = xt.Weights[wi + k];
                    var p = (s + k) * 3;
                    a += srcRow[p] * w;
                    b += srcRow[p + 1] * w;
                    c += srcRow[p + 2] * w;
                }
                tmp[o + x * 3] = a;
                tmp[o + x * 3 + 1] = b;
                tmp[o + x * 3 + 2] = c;
            }
        }

        var plane = planeWidth * planeHeight;
        var (r0, b0) = swapRedBlue ? (2, 0) : (0, 2);
        for (var y = 0; y < dstHeight; y++)
        {
            var s = yt.Start[y] - rowLo;
            var wi = y * yt.MaxTaps;
            var outRow = (dstY + y) * planeWidth + dstX;
            for (var x = 0; x < dstWidth; x++)
            {
                float a = 0, b = 0, c = 0;
                for (var k = 0; k < yt.Count[y]; k++)
                {
                    var w = yt.Weights[wi + k];
                    var p = ((s + k) * dstWidth + x) * 3;
                    a += tmp[p] * w;
                    b += tmp[p + 1] * w;
                    c += tmp[p + 2] * w;
                }
                chw[r0 * plane + outRow + x] = a * gain[r0] + bias[r0];
                chw[plane + outRow + x] = b * gain[1] + bias[1];
                chw[b0 * plane + outRow + x] = c * gain[b0] + bias[b0];
            }
        }
    }

    /// <summary>Resamples <paramref name="region"/> of a single float channel (row-major
    /// <paramref name="srcWidth"/> × <paramref name="srcHeight"/>) to fill <paramref name="dst"/>
    /// (<paramref name="dstWidth"/> × <paramref name="dstHeight"/>). NaN sources are skipped and the remaining
    /// weights renormalised; a destination with no finite source is NaN.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static void Channel(
        ReadOnlySpan<float> src, int srcWidth, int srcHeight, RectD region,
        Span<float> dst, int dstWidth, int dstHeight)
    {
        if (src.Length < srcWidth * srcHeight)
            throw new ArgumentException("The source is shorter than its raster.", nameof(src));
        if (dst.Length < dstWidth * dstHeight)
            throw new ArgumentException("The destination is shorter than its raster.", nameof(dst));
        var xt = new AxisTaps(region.X, region.W, srcWidth, dstWidth);
        var yt = new AxisTaps(region.Y, region.H, srcHeight, dstHeight);
        var rowLo = yt.Start[0];
        var rowHi = yt.Start[dstHeight - 1] + yt.Count[dstHeight - 1];
        var rows = rowHi - rowLo;
        // Value and weight sums travel together so NaN holes renormalise instead of spreading.
        var tmpV = new float[rows * dstWidth];
        var tmpW = new float[rows * dstWidth];
        for (var r = 0; r < rows; r++)
        {
            var srcRow = src.Slice((rowLo + r) * srcWidth, srcWidth);
            for (var x = 0; x < dstWidth; x++)
            {
                float v = 0, ws = 0;
                var s = xt.Start[x];
                var wi = x * xt.MaxTaps;
                for (var k = 0; k < xt.Count[x]; k++)
                {
                    var value = srcRow[s + k];
                    if (float.IsNaN(value))
                        continue;
                    var w = xt.Weights[wi + k];
                    v += value * w;
                    ws += w;
                }
                tmpV[r * dstWidth + x] = v;
                tmpW[r * dstWidth + x] = ws;
            }
        }
        for (var y = 0; y < dstHeight; y++)
        {
            var s = yt.Start[y] - rowLo;
            var wi = y * yt.MaxTaps;
            for (var x = 0; x < dstWidth; x++)
            {
                float v = 0, ws = 0;
                for (var k = 0; k < yt.Count[y]; k++)
                {
                    var w = yt.Weights[wi + k];
                    var p = (s + k) * dstWidth + x;
                    v += tmpV[p] * w;
                    ws += tmpW[p] * w;
                }
                dst[y * dstWidth + x] = ws > 1e-6f ? v / ws : float.NaN;
            }
        }
    }
}
