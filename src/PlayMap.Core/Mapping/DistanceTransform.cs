namespace PlayMap.Core.Mapping;

/// <summary>The exact Euclidean distance transform of a raster (the lower envelope of parabolas, separably by
/// columns then rows): for every pixel the distance to the nearest seed pixel, in pixels. Linear time.</summary>
public static class DistanceTransform
{
    /// <summary>Far: what a pixel reads when there is no seed at all (finite, so the envelope arithmetic
    /// never meets ∞ − ∞).</summary>
    public const float Far = 1e9f;

    /// <summary>The distance from each pixel to the nearest pixel where <paramref name="seed"/> is true.</summary>
    public static float[] Euclidean(ReadOnlySpan<bool> seed, int width, int height)
    {
        if (seed.Length < width * height)
            throw new ArgumentException("The seed map is smaller than the raster.", nameof(seed));
        var d2 = new double[width * height];
        const double inf = 1e20;
        for (var i = 0; i < width * height; i++)
            d2[i] = seed[i] ? 0 : inf;
        var n = Math.Max(width, height);
        var f = new double[n];
        var outBuf = new double[n];
        var v = new int[n];
        var z = new double[n + 1];
        for (var x = 0; x < width; x++)
        {
            for (var y = 0; y < height; y++)
                f[y] = d2[y * width + x];
            Envelope(f, height, outBuf, v, z);
            for (var y = 0; y < height; y++)
                d2[y * width + x] = outBuf[y];
        }
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
                f[x] = d2[y * width + x];
            Envelope(f, width, outBuf, v, z);
            for (var x = 0; x < width; x++)
                d2[y * width + x] = outBuf[x];
        }
        var result = new float[width * height];
        for (var i = 0; i < result.Length; i++)
            result[i] = d2[i] >= inf / 2 ? Far : (float)Math.Sqrt(d2[i]);
        return result;
    }

    /// <summary>One dimension: d[q] = min over p of (q − p)² + f[p].</summary>
    private static void Envelope(double[] f, int n, double[] d, int[] v, double[] z)
    {
        var k = 0;
        v[0] = 0;
        z[0] = double.NegativeInfinity;
        z[1] = double.PositiveInfinity;
        for (var q = 1; q < n; q++)
        {
            var s = Intersection(f, q, v[k]);
            while (s <= z[k])
            {
                k--;
                s = Intersection(f, q, v[k]);
            }
            k++;
            v[k] = q;
            z[k] = s;
            z[k + 1] = double.PositiveInfinity;
        }
        k = 0;
        for (var q = 0; q < n; q++)
        {
            while (z[k + 1] < q)
                k++;
            var dq = q - v[k];
            d[q] = dq * dq + f[v[k]];
        }
    }

    private static double Intersection(double[] f, int q, int p) =>
        (f[q] + (double)q * q - (f[p] + (double)p * p)) / (2.0 * q - 2.0 * p);
}
