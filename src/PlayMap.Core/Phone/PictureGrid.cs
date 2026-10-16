using PlayMap.Core.Scan;

namespace PlayMap.Core.Phone;

/// <summary>
/// The camera page's sparse view of a picture (tags.js <c>Settle</c>, the same integers): 48 × 27 samples spread over the lit
/// rectangle (the whole picture before a projector's tag places are known), each the 3 × 3 mean (sum + 4) / 9 round its point. Two
/// pictures agree when their samples differ by less than a tolerance on average. A picture has changed from another when at least
/// <see cref="MinChangedSamples"/> samples (1 % of them) moved by the change level or more: a projection small in the picture still
/// moves that many, where an average over the whole picture would hide it, and sensor noise on a 3 × 3 mean moves none that far.
/// </summary>
public static class PictureGrid
{
    public const int Columns = 48;

    public const int Rows = 27;

    public const int MinChangedSamples = 13;

    /// <summary>The samples of a gray8 picture over <paramref name="lit"/> (empty: the whole picture).</summary>
    public static byte[] Of(ReadOnlySpan<byte> gray, int width, int height, PixelRect lit)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        if (gray.Length < width * height)
            throw new ArgumentException("the picture is smaller than its raster", nameof(gray));
        var r = lit.Width > 0 && lit.Height > 0 ? lit : new PixelRect(0, 0, width, height);
        var g = new byte[Columns * Rows];
        for (var j = 0; j < Rows; j++)
        {
            var y = Math.Min(height - 1, (int)Math.Floor(r.Y + (j + 0.5) * r.Height / Rows));
            for (var i = 0; i < Columns; i++)
            {
                var x = Math.Min(width - 1, (int)Math.Floor(r.X + (i + 0.5) * r.Width / Columns));
                var sum = 0;
                for (var dy = -1; dy <= 1; dy++)
                {
                    var yy = Math.Clamp(y + dy, 0, height - 1) * width;
                    for (var dx = -1; dx <= 1; dx++)
                        sum += gray[yy + Math.Clamp(x + dx, 0, width - 1)];
                }
                g[j * Columns + i] = (byte)((sum + 4) / 9);
            }
        }
        return g;
    }

    /// <summary>The mean absolute difference of two grids.</summary>
    public static double Diff(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        var s = 0;
        for (var k = 0; k < a.Length; k++)
            s += Math.Abs(a[k] - b[k]);
        return s / (double)a.Length;
    }

    /// <summary>How many samples moved by <paramref name="level"/> or more.</summary>
    public static int Moved(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, double level)
    {
        var n = 0;
        for (var k = 0; k < a.Length; k++)
        {
            if (Math.Abs(a[k] - b[k]) >= level)
                n++;
        }
        return n;
    }

    public static bool Changed(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, double level) => Moved(a, b, level) >= MinChangedSamples;
}
