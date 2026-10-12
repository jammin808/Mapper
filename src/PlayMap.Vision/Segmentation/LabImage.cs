using System.Runtime.CompilerServices;
using PlayMap.Core.Vision;

namespace PlayMap.Vision.Segmentation;

/// <summary>A picture in CIE L*a*b* (D65, sRGB primaries), three float planes, optionally box-smoothed. ΔE76
/// (the Euclidean distance here) is roughly "one just-noticeable difference" per unit, so colour tolerances
/// read in units an operator could judge.</summary>
internal sealed class LabImage
{
    private static readonly float[] SrgbToLinear = BuildLinearTable();

    public LabImage(int width, int height)
    {
        Width = width;
        Height = height;
        L = new float[width * height];
        A = new float[width * height];
        B = new float[width * height];
    }

    public int Width { get; }
    public int Height { get; }
    public float[] L { get; }
    public float[] A { get; }
    public float[] B { get; }

    private static float[] BuildLinearTable()
    {
        var t = new float[256];
        for (var i = 0; i < 256; i++)
        {
            var c = i / 255.0;
            t[i] = (float)(c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4));
        }
        return t;
    }

    private static float F(float t) => t > 0.008856452f ? MathF.Cbrt(t) : t * 7.787037f + 4f / 29f;

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static LabImage From(ImageRgb8 image, int smoothRadius)
    {
        ArgumentNullException.ThrowIfNull(image);
        var lab = new LabImage(image.Width, image.Height);
        for (var y = 0; y < image.Height; y++)
        {
            var row = image.Row(y);
            var o = y * image.Width;
            for (var x = 0; x < image.Width; x++)
            {
                var r = SrgbToLinear[row[x * 3]];
                var g = SrgbToLinear[row[x * 3 + 1]];
                var b = SrgbToLinear[row[x * 3 + 2]];
                var fx = F((0.4124564f * r + 0.3575761f * g + 0.1804375f * b) / 0.95047f);
                var fy = F(0.2126729f * r + 0.7151522f * g + 0.0721750f * b);
                var fz = F((0.0193339f * r + 0.1191920f * g + 0.9503041f * b) / 1.08883f);
                lab.L[o + x] = 116f * fy - 16f;
                lab.A[o + x] = 500f * (fx - fy);
                lab.B[o + x] = 200f * (fy - fz);
            }
        }
        if (smoothRadius > 0)
        {
            var scratch = new float[lab.L.Length];
            BoxBlur(lab.L, scratch, image.Width, image.Height, smoothRadius);
            BoxBlur(lab.A, scratch, image.Width, image.Height, smoothRadius);
            BoxBlur(lab.B, scratch, image.Width, image.Height, smoothRadius);
        }
        return lab;
    }

    public float DeltaE(int i, float l, float a, float b)
    {
        var dl = L[i] - l;
        var da = A[i] - a;
        var db = B[i] - b;
        return MathF.Sqrt(dl * dl + da * da + db * db);
    }

    /// <summary>In-place separable box blur with edge clamping (the window shrinks at the border).</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal static void BoxBlur(float[] plane, float[] scratch, int w, int h, int r)
    {
        for (var y = 0; y < h; y++)
        {
            var o = y * w;
            float sum = 0;
            var n = 0;
            for (var x = 0; x <= Math.Min(r, w - 1); x++, n++)
                sum += plane[o + x];
            for (var x = 0; x < w; x++)
            {
                scratch[o + x] = sum / n;
                var add = x + r + 1;
                var sub = x - r;
                if (add < w)
                {
                    sum += plane[o + add];
                    n++;
                }
                if (sub >= 0)
                {
                    sum -= plane[o + sub];
                    n--;
                }
            }
        }
        for (var x = 0; x < w; x++)
        {
            float sum = 0;
            var n = 0;
            for (var y = 0; y <= Math.Min(r, h - 1); y++, n++)
                sum += scratch[y * w + x];
            for (var y = 0; y < h; y++)
            {
                plane[y * w + x] = sum / n;
                var add = y + r + 1;
                var sub = y - r;
                if (add < h)
                {
                    sum += scratch[add * w + x];
                    n++;
                }
                if (sub >= 0)
                {
                    sum -= scratch[sub * w + x];
                    n--;
                }
            }
        }
    }
}
