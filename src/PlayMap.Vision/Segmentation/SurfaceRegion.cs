using System.Runtime.CompilerServices;
namespace PlayMap.Vision.Segmentation;

/// <summary>Regions over a scan's edge graph: grow along links below 1, add back the pixels an edge test left at
/// a crease, and measure how much of a region's boundary is a real edge.</summary>
internal static class SurfaceRegion
{
    /// <summary>Every pixel joined to a seed by links below 1 (one surface), within <paramref name="allowed"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static bool[] Grow(GeometryEdges edges, IReadOnlyList<int> seeds, bool[]? allowed, CancellationToken ct)
    {
        int w = edges.Width, h = edges.Height;
        var region = new bool[w * h];
        var queue = new Queue<int>();
        foreach (var s in seeds)
        {
            if (!region[s] && edges.Valid[s])
            {
                region[s] = true;
                queue.Enqueue(s);
            }
        }
        var steps = 0;
        while (queue.Count > 0)
        {
            var i = queue.Dequeue();
            if ((++steps & 0xFFFF) == 0)
                ct.ThrowIfCancellationRequested();
            int x = i % w, y = i / w;
            if (x + 1 < w && edges.Right[i] < 1) Visit(i + 1);
            if (x > 0 && edges.Right[i - 1] < 1) Visit(i - 1);
            if (y + 1 < h && edges.Down[i] < 1) Visit(i + w);
            if (y > 0 && edges.Down[i - w] < 1) Visit(i - w);
        }
        return region;

        void Visit(int j)
        {
            if (region[j] || (allowed is not null && !allowed[j]))
                return;
            region[j] = true;
            queue.Enqueue(j);
        }
    }

    /// <summary>Adds pixels next to the region that continue its surface in a straight line: the point predicted
    /// from the two region pixels behind it (2·P(r) − P(r′)) lies within <paramref name="tolerance"/> steps of the
    /// actual point. The crease test marks the pixels on either side of a crease, so the faces stop a pixel short;
    /// this puts back the ones that lie on the face. A jump or a turn fails the prediction.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static void Complete(GeometryEdges edges, bool[] region, bool[]? allowed, int passes, double tolerance)
    {
        int w = edges.Width, h = edges.Height;
        var p = edges.Points;
        var add = new List<int>();
        for (var pass = 0; pass < passes; pass++)
        {
            add.Clear();
            for (var y = 0; y < h; y++)
            {
                for (var x = 0; x < w; x++)
                {
                    var i = y * w + x;
                    if (region[i] || !edges.Valid[i] || (allowed is not null && !allowed[i]))
                        continue;
                    if ((x >= 2 && Continues(i, -1)) || (x + 2 < w && Continues(i, 1)) ||
                        (y >= 2 && Continues(i, -w)) || (y + 2 < h && Continues(i, w)))
                        add.Add(i);
                }
            }
            if (add.Count == 0)
                return;
            foreach (var i in add)
                region[i] = true;
        }

        // Is pixel i the straight continuation of region pixels i+d and i+2d (d points into the region)?
        bool Continues(int i, int d)
        {
            int r = i + d, r2 = i + 2 * d;
            if (!region[r] || !region[r2] || !edges.Valid[r2])
                return false;
            var step = (p[r] - p[r2]).Length;
            var predicted = p[r] * 2 - p[r2];
            return (p[i] - predicted).Length <= tolerance * step;
        }
    }

    /// <summary>The fraction of the region's boundary links (to a neighbour on the grid) that are real edges —
    /// a link of cost ≥ 1 or to an invalid point. 1 when the region is closed by edges everywhere; ½ when it has
    /// no boundary inside the picture.</summary>
    public static double BoundaryStrength(GeometryEdges edges, bool[] region)
    {
        int w = edges.Width, h = edges.Height;
        long strong = 0, total = 0;
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var i = y * w + x;
                if (!region[i])
                    continue;
                if (x + 1 < w && !region[i + 1]) Count(edges.Right[i]);
                if (x > 0 && !region[i - 1]) Count(edges.Right[i - 1]);
                if (y + 1 < h && !region[i + w]) Count(edges.Down[i]);
                if (y > 0 && !region[i - w]) Count(edges.Down[i - w]);
            }
        }
        return total == 0 ? 0.5 : strong / (double)total;

        void Count(float cost)
        {
            total++;
            if (cost >= 1)
                strong++;
        }
    }
}
