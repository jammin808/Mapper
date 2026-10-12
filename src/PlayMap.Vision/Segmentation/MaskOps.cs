using System.Runtime.CompilerServices;
namespace PlayMap.Vision.Segmentation;

/// <summary>Binary mask operations on row-major W×H grids, 4-connected throughout (a mask that touches only at a
/// corner is two pieces — the same rule the contour tracer and the flood use).</summary>
public static class MaskOps
{
    public static bool[] FromCoverage(byte[] coverage)
    {
        ArgumentNullException.ThrowIfNull(coverage);
        var m = new bool[coverage.Length];
        for (var i = 0; i < m.Length; i++)
            m[i] = coverage[i] >= 128;
        return m;
    }

    public static byte[] ToCoverage(bool[] mask)
    {
        ArgumentNullException.ThrowIfNull(mask);
        var c = new byte[mask.Length];
        for (var i = 0; i < c.Length; i++)
            c[i] = mask[i] ? (byte)255 : (byte)0;
        return c;
    }

    public static int Count(bool[] mask)
    {
        ArgumentNullException.ThrowIfNull(mask);
        var n = 0;
        foreach (var b in mask)
            if (b)
                n++;
        return n;
    }

    /// <summary>Labels the 4-connected components of pixels equal to <paramref name="value"/>: 0 for the other
    /// pixels, 1…count for the components.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static int[] Components(bool[] mask, int w, int h, bool value, out int count)
    {
        ArgumentNullException.ThrowIfNull(mask);
        var labels = new int[w * h];
        var stack = new Stack<int>();
        var current = 0;
        for (var s = 0; s < labels.Length; s++)
        {
            if (mask[s] != value || labels[s] != 0)
                continue;
            current++;
            labels[s] = current;
            stack.Push(s);
            while (stack.Count > 0)
            {
                var i = stack.Pop();
                int x = i % w, y = i / w;
                if (x > 0) Visit(i - 1);
                if (x + 1 < w) Visit(i + 1);
                if (y > 0) Visit(i - w);
                if (y + 1 < h) Visit(i + w);
            }
        }
        count = current;
        return labels;

        void Visit(int j)
        {
            if (mask[j] == value && labels[j] == 0)
            {
                labels[j] = current;
                stack.Push(j);
            }
        }
    }

    /// <summary>Keeps only the components that contain one of the seed pixels.</summary>
    public static void KeepSeeded(bool[] mask, int w, int h, IEnumerable<int> seeds)
    {
        ArgumentNullException.ThrowIfNull(seeds);
        var labels = Components(mask, w, h, true, out var count);
        var keep = new bool[count + 1];
        foreach (var s in seeds)
            if (s >= 0 && s < labels.Length)
                keep[labels[s]] = true;
        for (var i = 0; i < mask.Length; i++)
            if (mask[i] && !keep[labels[i]])
                mask[i] = false;
    }

    /// <summary>Fills the holes (background components that do not reach the border) of at most
    /// <paramref name="maxArea"/> pixels, except a hole holding a <paramref name="forbidden"/> pixel (a negative
    /// click stays out) and, when <paramref name="eligible"/> is given, a hole with any pixel it does not admit.
    /// Returns how many pixels were filled.</summary>
    public static int FillHoles(bool[] mask, int w, int h, int maxArea, bool[]? forbidden = null, bool[]? eligible = null)
    {
        var labels = Components(mask, w, h, false, out var count);
        var area = new int[count + 1];
        var open = new bool[count + 1];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var i = y * w + x;
                var l = labels[i];
                if (l == 0)
                    continue;
                area[l]++;
                if (x == 0 || y == 0 || x == w - 1 || y == h - 1 || (forbidden is not null && forbidden[i]) || (eligible is not null && !eligible[i]))
                    open[l] = true;
            }
        }
        var filled = 0;
        for (var i = 0; i < mask.Length; i++)
        {
            var l = labels[i];
            if (l != 0 && !open[l] && area[l] <= maxArea)
            {
                mask[i] = true;
                filled++;
            }
        }
        return filled;
    }

    /// <summary>City-block distance from every pixel to the nearest pixel of the other value (1 for a pixel on
    /// the boundary), capped at <paramref name="cap"/>. Two raster passes.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static int[] DistanceToBoundary(bool[] mask, int w, int h, int cap)
    {
        ArgumentNullException.ThrowIfNull(mask);
        var d = new int[w * h];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var i = y * w + x;
                var v = cap;
                if (x > 0) v = mask[i - 1] != mask[i] ? 1 : Math.Min(v, d[i - 1] + 1);
                if (y > 0) v = mask[i - w] != mask[i] ? 1 : Math.Min(v, d[i - w] + 1);
                d[i] = v;
            }
        }
        for (var y = h - 1; y >= 0; y--)
        {
            for (var x = w - 1; x >= 0; x--)
            {
                var i = y * w + x;
                var v = d[i];
                if (x + 1 < w) v = mask[i + 1] != mask[i] ? 1 : Math.Min(v, d[i + 1] + 1);
                if (y + 1 < h) v = mask[i + w] != mask[i] ? 1 : Math.Min(v, d[i + w] + 1);
                d[i] = Math.Min(v, cap);
            }
        }
        return d;
    }

    /// <summary>Opening then closing with a 3×3 square: removes one-pixel spurs and specks, closes one-pixel
    /// notches, keeps straight edges and square corners where they are. The border replicates the edge pixel.</summary>
    public static void OpenClose(bool[] mask, int w, int h)
    {
        ArgumentNullException.ThrowIfNull(mask);
        var a = new bool[mask.Length];
        var b = new bool[mask.Length];
        Filter(mask, a, b, w, h, erode: true);
        Filter(a, mask, b, w, h, erode: false);
        Filter(mask, a, b, w, h, erode: false);
        Filter(a, mask, b, w, h, erode: true);
    }

    /// <summary>A 3×3 square erosion or dilation as a 3-tap pass along rows then along columns.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void Filter(bool[] src, bool[] dst, bool[] tmp, int w, int h, bool erode)
    {
        for (var y = 0; y < h; y++)
        {
            var o = y * w;
            for (var x = 0; x < w; x++)
            {
                bool l = src[o + Math.Max(x - 1, 0)], c = src[o + x], r = src[o + Math.Min(x + 1, w - 1)];
                tmp[o + x] = erode ? l && c && r : l || c || r;
            }
        }
        for (var y = 0; y < h; y++)
        {
            int up = Math.Max(y - 1, 0) * w, mid = y * w, down = Math.Min(y + 1, h - 1) * w;
            for (var x = 0; x < w; x++)
            {
                bool u = tmp[up + x], c = tmp[mid + x], d = tmp[down + x];
                dst[mid + x] = erode ? u && c && d : u || c || d;
            }
        }
    }

    /// <summary>Intersection over union of two masks.</summary>
    public static double IoU(bool[] a, bool[] b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        int inter = 0, union = 0;
        for (var i = 0; i < a.Length; i++)
        {
            if (a[i] && b[i]) inter++;
            if (a[i] || b[i]) union++;
        }
        return union == 0 ? 1 : inter / (double)union;
    }
}
