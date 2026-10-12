using PlayMap.Core.Geometry;
using PlayMap.Core.Vision;

namespace PlayMap.Vision.Segmentation;

/// <summary>How far a mask's edge may move to meet the scan's edges.</summary>
public sealed record DepthSnapOptions(int BandRadius = 8, GeometryEdgeOptions? Edges = null, int CompletionPasses = 2, double CompletionTolerance = 0.3)
{
    public static readonly DepthSnapOptions Default = new();
}

/// <summary>Moves a mask's boundary (from a model or the classical path) onto the depth jumps and creases of the
/// scan — the picture alone cannot separate two surfaces of one colour, and a model's edge wanders by a few
/// pixels. Within a band of <see cref="DepthSnapOptions.BandRadius"/> pixels around the edge, the mask's
/// confident inside and confident outside each spread into the band twice: over the scan's surface (links below 1
/// only, never across an edge) and over the bare grid. A pixel whose own side reaches it only by a detour (or not
/// at all) while the other side reaches it straight changes sides; every other pixel keeps its label. Where no
/// scan edge is near, nothing detours and the edge stays put whatever its shape; where one is, the pixels on the
/// wrong side of it change over and the edge lands on it. No graph cut: four breadth-first floods.</summary>
public static class DepthSnap
{
    public static SegmentationMask Snap(SegmentationMask mask, PointCloud points, DepthSnapOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(mask);
        ArgumentNullException.ThrowIfNull(points);
        options ??= DepthSnapOptions.Default;
        if (points.GridWidth != mask.Width || points.GridHeight != mask.Height)
            throw new ArgumentException("The points must be organised on the mask's grid.", nameof(points));
        var original = MaskOps.FromCoverage(mask.Coverage);
        // The band may not swallow either side: both need a confident core beyond it. A mask thinner than the
        // band snaps within what it has, and one with no core at all is returned as it came.
        var depth = MaskOps.DistanceToBoundary(original, mask.Width, mask.Height, int.MaxValue / 2);
        int deepIn = 0, deepOut = 0;
        for (var i = 0; i < original.Length; i++)
        {
            if (original[i])
                deepIn = Math.Max(deepIn, depth[i]);
            else
                deepOut = Math.Max(deepOut, depth[i]);
        }
        var radius = Math.Min(options.BandRadius, Math.Min(deepIn, deepOut) - 2);
        if (radius < 1)
            return mask;
        var edges = GeometryEdges.Build(points, options.Edges ?? GeometryEdgeOptions.Default);
        var snapped = Snap(original, edges, radius, out var moved, out var isolated);
        // Only pixels the edge tests cut off from both sides (those at a crease) may be completed; a mask that
        // ends in the middle of a flat surface is not grown.
        SurfaceRegion.Complete(edges, snapped, isolated, options.CompletionPasses, options.CompletionTolerance);
        MaskOps.FillHoles(snapped, mask.Width, mask.Height, 16);
        // A snap that keeps under a quarter of the mask or more than quadruples it has followed the wrong edge.
        var before = MaskOps.Count(original);
        var after = MaskOps.Count(snapped);
        if (after * 4 < before || after > before * 4)
            return mask;
        var score = moved == 0 ? mask.Score : Math.Max(mask.Score, SurfaceRegion.BoundaryStrength(edges, snapped));
        return new SegmentationMask(mask.Width, mask.Height, MaskOps.ToCoverage(snapped), score, mask.Label);
    }

    internal static bool[] Snap(bool[] mask, GeometryEdges edges, int radius, out int moved, out bool[] isolated)
    {
        int w = edges.Width, h = edges.Height, n = w * h;
        const int slack = 2;
        var dist = MaskOps.DistanceToBoundary(mask, w, h, radius + 1);
        var inGeo = Flood(mask, dist, edges, radius, inside: true, overSurface: true);
        var inFree = Flood(mask, dist, edges, radius, inside: true, overSurface: false);
        var outGeo = Flood(mask, dist, edges, radius, inside: false, overSurface: true);
        var outFree = Flood(mask, dist, edges, radius, inside: false, overSurface: false);
        var result = (bool[])mask.Clone();
        isolated = new bool[n];
        moved = 0;
        for (var i = 0; i < n; i++)
        {
            if (dist[i] > radius)
                continue;
            // A pixel neither side reaches over the surface (beside a crease, an invalid point) starts outside
            // and is decided by completion.
            isolated[i] = inGeo[i] == int.MaxValue && outGeo[i] == int.MaxValue;
            var detourIn = Detour(inGeo[i], inFree[i]);
            var detourOut = Detour(outGeo[i], outFree[i]);
            bool v;
            if (isolated[i])
                v = false;
            else if (mask[i])
                v = !(detourIn > slack && detourOut <= slack);
            else
                v = detourOut > slack && detourIn <= slack;
            if (v != mask[i])
                moved++;
            result[i] = v;
        }
        return result;
    }

    /// <summary>How many more steps the way over the surface takes than the straight way (∞ when there is none).</summary>
    private static long Detour(int overSurface, int free) =>
        overSurface == int.MaxValue ? long.MaxValue : free == int.MaxValue ? 0 : (long)overSurface - free;

    /// <summary>Breadth-first steps from the confident side (the mask's side beyond the band) into the band — over
    /// links below 1 when <paramref name="overSurface"/>, over the bare grid otherwise.</summary>
    private static int[] Flood(bool[] mask, int[] dist, GeometryEdges edges, int radius, bool inside, bool overSurface)
    {
        int w = edges.Width, h = edges.Height, n = w * h;
        var steps = new int[n];
        Array.Fill(steps, int.MaxValue);
        var queue = new Queue<int>();
        for (var i = 0; i < n; i++)
        {
            if (mask[i] == inside && dist[i] == radius + 1 && (!overSurface || edges.Valid[i]))
            {
                steps[i] = 0;
                queue.Enqueue(i);
            }
        }
        while (queue.Count > 0)
        {
            var i = queue.Dequeue();
            int x = i % w, y = i / w;
            var next = steps[i] + 1;
            if (x + 1 < w && (!overSurface || edges.Right[i] < 1)) Visit(i + 1);
            if (x > 0 && (!overSurface || edges.Right[i - 1] < 1)) Visit(i - 1);
            if (y + 1 < h && (!overSurface || edges.Down[i] < 1)) Visit(i + w);
            if (y > 0 && (!overSurface || edges.Down[i - w] < 1)) Visit(i - w);

            void Visit(int j)
            {
                if (steps[j] <= next || dist[j] > radius)
                    return;
                steps[j] = next;
                queue.Enqueue(j);
            }
        }
        return steps;
    }
}
