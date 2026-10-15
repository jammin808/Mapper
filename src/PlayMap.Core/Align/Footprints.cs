using PlayMap.Core.Mapping;
using PlayMap.Core.Numerics;
using PlayMap.Core.Scan;

namespace PlayMap.Core.Align;

/// <summary>Where one output's light falls in the camera picture: a mask (row-major, the camera's raster), its area in camera pixels,
/// its centroid and bounds, and the white − black level that split lit from unlit.</summary>
public sealed record Footprint(int Width, int Height, bool[] Mask, int Area, Vec2d Centroid, PixelRect Bounds, int Threshold)
{
    public bool Contains(int x, int y) => x >= 0 && y >= 0 && x < Width && y < Height && Mask[y * Width + x];

    /// <summary>The share of the smaller footprint the two have in common (0 when either is empty).</summary>
    public double Overlap(Footprint other)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (Area == 0 || other.Area == 0 || other.Mask.Length != Mask.Length)
            return 0;
        var both = 0;
        for (var i = 0; i < Mask.Length; i++)
            if (Mask[i] && other.Mask[i])
                both++;
        return (double)both / Math.Min(Area, other.Area);
    }
}

/// <summary>Footprints from a black and a white picture: the difference split by Otsu's threshold, closed by a disc, its largest
/// connected part kept. Pure.</summary>
public static class Footprints
{
    /// <summary>Output's footprint from the camera's picture with every output black (<paramref name="black"/>) and with this output
    /// white alone (<paramref name="white"/>), both gray8 at the camera's raster.</summary>
    public static Footprint Find(ReadOnlySpan<byte> black, ReadOnlySpan<byte> white, int width, int height, double closeRadius = 3)
    {
        var n = width * height;
        if (black.Length < n || white.Length < n)
            throw new ArgumentException("The pictures are smaller than the raster.");
        Span<int> histogram = stackalloc int[256];
        var diff = new byte[n];
        for (var i = 0; i < n; i++)
        {
            var d = Math.Max(0, white[i] - black[i]);
            diff[i] = (byte)d;
            histogram[d]++;
        }
        var t = Otsu(histogram);
        var mask = new bool[n];
        for (var i = 0; i < n; i++)
            mask[i] = diff[i] > t;
        if (closeRadius > 0)
            mask = Erode(Dilate(mask, width, height, closeRadius), width, height, closeRadius);
        mask = Largest(mask, width, height);
        return Describe(mask, width, height, t);
    }

    /// <summary>A footprint from a mask (its area, centroid and bounds).</summary>
    public static Footprint Describe(bool[] mask, int width, int height, int threshold)
    {
        ArgumentNullException.ThrowIfNull(mask);
        long sx = 0, sy = 0;
        int area = 0, x0 = width, y0 = height, x1 = -1, y1 = -1;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                if (!mask[y * width + x])
                    continue;
                area++;
                sx += x;
                sy += y;
                x0 = Math.Min(x0, x);
                y0 = Math.Min(y0, y);
                x1 = Math.Max(x1, x);
                y1 = Math.Max(y1, y);
            }
        }
        var centroid = area > 0 ? new Vec2d((double)sx / area + 0.5, (double)sy / area + 0.5) : Vec2d.Zero;
        var bounds = area > 0 ? new PixelRect(x0, y0, x1 - x0 + 1, y1 - y0 + 1) : new PixelRect(0, 0, 0, 0);
        return new Footprint(width, height, mask, area, centroid, bounds, threshold);
    }

    /// <summary>Otsu's threshold: the level that best splits the histogram into two classes (between-class variance highest).</summary>
    public static int Otsu(ReadOnlySpan<int> histogram)
    {
        long total = 0;
        double sum = 0;
        for (var i = 0; i < histogram.Length; i++)
        {
            total += histogram[i];
            sum += (double)i * histogram[i];
        }
        if (total == 0)
            return 0;
        long below = 0;
        double sumBelow = 0, best = -1;
        var threshold = 0;
        for (var t = 0; t < histogram.Length; t++)
        {
            below += histogram[t];
            if (below == 0)
                continue;
            var above = total - below;
            if (above == 0)
                break;
            sumBelow += (double)t * histogram[t];
            var mb = sumBelow / below;
            var ma = (sum - sumBelow) / above;
            var between = (double)below * above * (mb - ma) * (mb - ma);
            if (between > best)
            {
                best = between;
                threshold = t;
            }
        }
        return threshold;
    }

    /// <summary>Every pixel within <paramref name="radius"/> of a set one (a disc, exactly).</summary>
    public static bool[] Dilate(bool[] mask, int width, int height, double radius)
    {
        ArgumentNullException.ThrowIfNull(mask);
        var d = DistanceTransform.Euclidean(mask, width, height);
        var r = (float)radius;
        var o = new bool[mask.Length];
        for (var i = 0; i < o.Length; i++)
            o[i] = d[i] <= r;
        return o;
    }

    /// <summary>Every set pixel farther than <paramref name="radius"/> from an unset one (the raster's outside counts as unset only
    /// through the mask's own pixels).</summary>
    public static bool[] Erode(bool[] mask, int width, int height, double radius)
    {
        ArgumentNullException.ThrowIfNull(mask);
        var inv = new bool[mask.Length];
        for (var i = 0; i < inv.Length; i++)
            inv[i] = !mask[i];
        var d = DistanceTransform.Euclidean(inv, width, height);
        var r = (float)radius;
        var o = new bool[mask.Length];
        for (var i = 0; i < o.Length; i++)
            o[i] = mask[i] && d[i] > r;
        return o;
    }

    /// <summary>The largest 4-connected part of a mask.</summary>
    public static bool[] Largest(bool[] mask, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(mask);
        var label = new int[mask.Length];
        var stack = new Stack<int>();
        int best = 0, bestSize = 0, next = 0;
        for (var start = 0; start < mask.Length; start++)
        {
            if (!mask[start] || label[start] != 0)
                continue;
            next++;
            var size = 0;
            label[start] = next;
            stack.Push(start);
            while (stack.Count > 0)
            {
                var i = stack.Pop();
                size++;
                var x = i % width;
                var y = i / width;
                if (x > 0 && mask[i - 1] && label[i - 1] == 0) { label[i - 1] = next; stack.Push(i - 1); }
                if (x < width - 1 && mask[i + 1] && label[i + 1] == 0) { label[i + 1] = next; stack.Push(i + 1); }
                if (y > 0 && mask[i - width] && label[i - width] == 0) { label[i - width] = next; stack.Push(i - width); }
                if (y < height - 1 && mask[i + width] && label[i + width] == 0) { label[i + width] = next; stack.Push(i + width); }
            }
            if (size > bestSize)
            {
                bestSize = size;
                best = next;
            }
        }
        var o = new bool[mask.Length];
        if (best == 0)
            return o;
        for (var i = 0; i < o.Length; i++)
            o[i] = label[i] == best;
        return o;
    }
}
