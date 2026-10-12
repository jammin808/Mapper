using PlayMap.Core.Geometry;
using PlayMap.Core.Model;
using PlayMap.Core.Numerics;
using PlayMap.Core.Vision;

namespace PlayMap.Vision.Segmentation;

/// <summary>How the classical segmenter decides.</summary>
public sealed record ClassicalSegmenterOptions(
    GeometryEdgeOptions? Edges = null,
    /// <summary>The colour tolerance (ΔE76) is chosen per click within this range from the region's stability.</summary>
    double MinColourTolerance = 3,
    double MaxColourTolerance = 45,
    /// <summary>Holes up to this fraction of the region's area are filled (a missed decode, a speck); larger ones
    /// (a window in a wall) are kept.</summary>
    double HoleFillFraction = 0.02,
    /// <summary>Holes with no scan data in them at all are filled up to this fraction of the region's area.</summary>
    double NoDataHoleFillFraction = 0.1,
    /// <summary>A click on an invalid scan point moves to the nearest valid one within this many pixels.</summary>
    int SeedSnapRadius = 4,
    /// <summary>Passes of edge completion: pixels the edge tests left at a crease, added back where they continue
    /// the region's surface in a straight line.</summary>
    int CompletionPasses = 2,
    /// <summary>The largest deviation from that straight continuation, as a fraction of the step.</summary>
    double CompletionTolerance = 0.3)
{
    public static readonly ClassicalSegmenterOptions Default = new();

    public GeometryEdgeOptions EdgeOptions => Edges ?? GeometryEdgeOptions.Default;
}

/// <summary>Click to select with no model installed — and the better tool when a scan is there. With per-pixel
/// points (the smart scan's organised cloud) a click grows over its surface and stops at depth jumps and creases,
/// so two faces of one colour come apart; a negative click on the same surface splits it by colour. With only a
/// picture, a seeded colour watershed in Lab grows every click at once, and each positive region stops at the
/// colour tolerance where its area is most stable — the tolerance adapts to the surface rather than being a
/// slider. Holes are filled, specks removed; the score is how much of the boundary is a real edge (geometry) or
/// how stable the region is (colour).</summary>
public sealed class ClassicalSegmenter : ISegmenter
{
    private readonly ClassicalSegmenterOptions _options;
    private readonly Lock _cacheLock = new();
    private (PointCloud? Cloud, GeometryEdges? Edges) _edgeCache;
    private (ImageRgb8? Image, LabImage? Lab) _labCache;

    public ClassicalSegmenter(ClassicalSegmenterOptions? options = null) => _options = options ?? ClassicalSegmenterOptions.Default;

    public string Id => "classical";

    public SegmenterCapabilities Capabilities => SegmenterCapabilities.Points | SegmenterCapabilities.Box;

    public Task<SegmentationResult> SegmentAsync(VisionFrame frame, SegmentationPrompt prompt, CancellationToken cancellationToken) =>
        Task.Run(() => Segment(frame, prompt, cancellationToken), cancellationToken);

    /// <summary>The same work on the caller's thread (for a worker that is already off the desk's thread).</summary>
    public SegmentationResult Segment(VisionFrame frame, SegmentationPrompt prompt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(prompt);
        var img = frame.Image;
        int w = img.Width, h = img.Height;
        if (prompt.HasText && !prompt.HasPoints && !prompt.HasBox)
            return SegmentationResult.Refused("the classical segmenter cannot read a phrase; click on the surface, or install a text-prompt model");
        var positives = new List<int>();
        foreach (var p in prompt.PositivePoints)
            positives.Add(PixelOf(p, w, h));
        bool[]? allowed = null;
        if (prompt.HasBox)
        {
            var box = prompt.Box!.Value;
            allowed = BoxMask(box, w, h);
            if (positives.Count == 0)
                positives.Add(PixelOf(new Vec2d(box.X + box.W / 2, box.Y + box.H / 2), w, h));
        }
        if (positives.Count == 0)
            return SegmentationResult.Refused("a click on what to select is needed");
        var negatives = new List<int>();
        foreach (var p in prompt.NegativePoints)
            negatives.Add(PixelOf(p, w, h));

        if (frame.Points is { } cloud)
        {
            var edges = EdgesFor(cloud);
            var snapped = new List<int>(positives.Count);
            foreach (var s in positives)
            {
                var v = SnapToValid(edges, s, _options.SeedSnapRadius);
                if (v >= 0)
                    snapped.Add(v);
            }
            if (snapped.Count == positives.Count)
                return ByGeometry(frame, edges, snapped, negatives, allowed, cancellationToken);
        }
        return ByColour(img, positives, negatives, allowed, cancellationToken);
    }

    private GeometryEdges EdgesFor(PointCloud cloud)
    {
        lock (_cacheLock)
        {
            if (ReferenceEquals(_edgeCache.Cloud, cloud) && _edgeCache.Edges is not null)
                return _edgeCache.Edges;
        }
        var edges = GeometryEdges.Build(cloud, _options.EdgeOptions);
        lock (_cacheLock)
            _edgeCache = (cloud, edges);
        return edges;
    }

    private LabImage LabFor(ImageRgb8 image)
    {
        lock (_cacheLock)
        {
            if (ReferenceEquals(_labCache.Image, image) && _labCache.Lab is not null)
                return _labCache.Lab;
        }
        var lab = LabImage.From(image, smoothRadius: 1);
        lock (_cacheLock)
            _labCache = (image, lab);
        return lab;
    }

    private static int PixelOf(Vec2d p, int w, int h) =>
        Math.Clamp((int)Math.Floor(p.Y), 0, h - 1) * w + Math.Clamp((int)Math.Floor(p.X), 0, w - 1);

    private static bool[] BoxMask(RectD box, int w, int h)
    {
        // The box with a margin of a tenth of its size: an operator's box is drawn loosely.
        var mx = box.W * 0.1;
        var my = box.H * 0.1;
        int x0 = Math.Clamp((int)Math.Floor(box.X - mx), 0, w - 1), x1 = Math.Clamp((int)Math.Ceiling(box.Right + mx), 0, w);
        int y0 = Math.Clamp((int)Math.Floor(box.Y - my), 0, h - 1), y1 = Math.Clamp((int)Math.Ceiling(box.Bottom + my), 0, h);
        var m = new bool[w * h];
        for (var y = y0; y < y1; y++)
            for (var x = x0; x < x1; x++)
                m[y * w + x] = true;
        return m;
    }

    private static int SnapToValid(GeometryEdges edges, int pixel, int radius)
    {
        if (edges.Valid[pixel])
            return pixel;
        int w = edges.Width, h = edges.Height, px = pixel % w, py = pixel / w;
        var best = -1;
        var bestD = int.MaxValue;
        for (var y = Math.Max(0, py - radius); y <= Math.Min(h - 1, py + radius); y++)
        {
            for (var x = Math.Max(0, px - radius); x <= Math.Min(w - 1, px + radius); x++)
            {
                var d = (x - px) * (x - px) + (y - py) * (y - py);
                if (d < bestD && d <= radius * radius && edges.Valid[y * w + x])
                {
                    bestD = d;
                    best = y * w + x;
                }
            }
        }
        return best;
    }

    // ---- geometry ----

    private SegmentationResult ByGeometry(VisionFrame frame, GeometryEdges edges, List<int> positives, List<int> negatives, bool[]? allowed, CancellationToken ct)
    {
        int w = edges.Width, h = edges.Height;
        var region = SurfaceRegion.Grow(edges, positives, allowed, ct);
        SurfaceRegion.Complete(edges, region, allowed, _options.CompletionPasses, _options.CompletionTolerance);
        ct.ThrowIfCancellationRequested();

        // A negative click on the same surface cannot be told apart by geometry: split that surface by colour.
        var inside = negatives.Where(n => region[n]).ToList();
        if (inside.Count > 0)
        {
            var lab = LabFor(frame.Image);
            var seeds = positives.Concat(inside).Select(p => SeedAt(lab, p)).ToList();
            var (labels, _) = ColourFlood.Run(lab, seeds, region, ColourFlood.Cap, ct);
            for (var i = 0; i < region.Length; i++)
                region[i] = region[i] && labels[i] >= 0 && labels[i] < positives.Count;
        }

        var forbidden = Forbidden(negatives, w * h);
        // A hole with no scan data at all (a missed decode, a highlight) is filled up to a larger size than one
        // holding another surface (a recess, a fixture), which is kept unless it is a speck.
        var area = MaskOps.Count(region);
        var noData = new bool[w * h];
        for (var i = 0; i < noData.Length; i++)
            noData[i] = !edges.Valid[i];
        MaskOps.FillHoles(region, w, h, (int)(area * _options.NoDataHoleFillFraction) + 4, forbidden, noData);
        MaskOps.FillHoles(region, w, h, (int)(area * _options.HoleFillFraction) + 4, forbidden);
        MaskOps.KeepSeeded(region, w, h, positives);
        var score = SurfaceRegion.BoundaryStrength(edges, region);
        return new SegmentationResult([new SegmentationMask(w, h, MaskOps.ToCoverage(region), score, "surface")],
            "grown over the scanned surface to its depth edges and creases");
    }

    private static bool[]? Forbidden(List<int> negatives, int n)
    {
        if (negatives.Count == 0)
            return null;
        var f = new bool[n];
        foreach (var p in negatives)
            f[p] = true;
        return f;
    }

    // ---- colour ----

    private SegmentationResult ByColour(ImageRgb8 img, List<int> positives, List<int> negatives, bool[]? allowed, CancellationToken ct)
    {
        int w = img.Width, h = img.Height;
        var lab = LabFor(img);
        var seeds = positives.Concat(negatives).Select(p => SeedAt(lab, p)).ToList();
        // The stability measure looks two ΔE past a tolerance, so the flood goes that far past the largest.
        var (labels, levels) = ColourFlood.Run(lab, seeds, allowed, (float)_options.MaxColourTolerance + 3, ct);
        var region = new bool[w * h];
        double worst = 0;
        for (var s = 0; s < positives.Count; s++)
        {
            var noise = SeedNoise(lab, seeds[s]);
            var min = (float)Math.Max(_options.MinColourTolerance, 3 * noise);
            var max = (float)Math.Max(min, _options.MaxColourTolerance);
            var (tol, instability) = ColourFlood.StableTolerance(labels, levels, s, min, max);
            worst = Math.Max(worst, instability);
            for (var i = 0; i < region.Length; i++)
                if (labels[i] == s && levels[i] <= tol)
                    region[i] = true;
        }
        ct.ThrowIfCancellationRequested();
        MaskOps.OpenClose(region, w, h);
        var forbidden = Forbidden(negatives, w * h);
        if (forbidden is not null)
            for (var i = 0; i < region.Length; i++)
                region[i] &= !forbidden[i];
        MaskOps.FillHoles(region, w, h, (int)(MaskOps.Count(region) * _options.HoleFillFraction) + 4, forbidden);
        foreach (var p in positives)
            region[p] = region[p] || labels[p] >= 0;
        MaskOps.KeepSeeded(region, w, h, positives);
        var score = Math.Clamp(1 - worst, 0, 1);
        return new SegmentationResult([new SegmentationMask(w, h, MaskOps.ToCoverage(region), score, "region")],
            "grown by colour to its most stable tolerance (no scan points under the click)");
    }

    /// <summary>The seed's colour: the mean of the 5×5 window's pixels within 10 ΔE of the clicked pixel, so a
    /// click beside an edge does not average two surfaces.</summary>
    private static ColourFlood.Seed SeedAt(LabImage lab, int pixel)
    {
        int w = lab.Width, h = lab.Height, px = pixel % w, py = pixel / w;
        float l0 = lab.L[pixel], a0 = lab.A[pixel], b0 = lab.B[pixel];
        double sl = 0, sa = 0, sb = 0;
        var n = 0;
        for (var y = Math.Max(0, py - 2); y <= Math.Min(h - 1, py + 2); y++)
        {
            for (var x = Math.Max(0, px - 2); x <= Math.Min(w - 1, px + 2); x++)
            {
                var i = y * w + x;
                if (lab.DeltaE(i, l0, a0, b0) > 10)
                    continue;
                sl += lab.L[i];
                sa += lab.A[i];
                sb += lab.B[i];
                n++;
            }
        }
        return new ColourFlood.Seed(pixel, (float)(sl / n), (float)(sa / n), (float)(sb / n));
    }

    /// <summary>RMS ΔE of the seed's window about its colour: the surface's own texture and sensor noise.</summary>
    private static double SeedNoise(LabImage lab, ColourFlood.Seed seed)
    {
        int w = lab.Width, h = lab.Height, px = seed.Pixel % w, py = seed.Pixel / w;
        double sum = 0;
        var n = 0;
        for (var y = Math.Max(0, py - 2); y <= Math.Min(h - 1, py + 2); y++)
        {
            for (var x = Math.Max(0, px - 2); x <= Math.Min(w - 1, px + 2); x++)
            {
                var d = lab.DeltaE(y * w + x, seed.L, seed.A, seed.B);
                if (d > 10)
                    continue;
                sum += d * d;
                n++;
            }
        }
        return n == 0 ? 0 : Math.Sqrt(sum / n);
    }
}
