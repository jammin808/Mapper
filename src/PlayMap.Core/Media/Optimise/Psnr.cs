namespace PlayMap.Core.Media.Optimise;

/// <summary>Peak signal-to-noise ratio of two 8-bit pictures over their RGB channels (alpha is not counted): 10·log10(255² / MSE).
/// Identical pictures read <see cref="Identical"/>.</summary>
public static class Psnr
{
    /// <summary>What two identical pictures read (a finite stand-in for infinity, so averages stay numbers).</summary>
    public const double Identical = 100;

    /// <summary>The mean squared error of two RGBA (or BGRA: the order does not matter) pictures of the same size, over R, G and B.</summary>
    public static double MeanSquaredError(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        if (a.Length != b.Length || a.Length % 4 != 0)
            throw new ArgumentException("Two pictures of the same size, four bytes a pixel.", nameof(b));
        long sum = 0;
        for (var i = 0; i < a.Length; i += 4)
        {
            var d0 = a[i] - b[i];
            var d1 = a[i + 1] - b[i + 1];
            var d2 = a[i + 2] - b[i + 2];
            sum += d0 * d0 + d1 * d1 + d2 * d2;
        }
        return a.Length == 0 ? 0 : sum / (a.Length / 4 * 3.0);
    }

    public static double Rgb(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b) => FromMse(MeanSquaredError(a, b));

    public static double FromMse(double mse) => mse <= 0 ? Identical : Math.Min(Identical, 10 * Math.Log10(255.0 * 255.0 / mse));
}
