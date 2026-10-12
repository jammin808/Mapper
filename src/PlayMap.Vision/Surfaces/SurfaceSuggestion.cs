using System.Collections.Immutable;
using PlayMap.Core.Geometry;
using PlayMap.Core.Numerics;
using PlayMap.Core.Vision;
using PlayMap.Vision.Segmentation;

namespace PlayMap.Vision.Surfaces;

/// <summary>How surfaces are judged. Distances in the cloud's units (metres).</summary>
public sealed record SurfaceSuggestionOptions(
    /// <summary>A plane's inlier band: this fraction of the region's extent, at least <see cref="PlaneFloor"/>.</summary>
    double PlaneFraction = 0.01,
    double PlaneFloor = 0.004,
    /// <summary>The share of points within the band for the region to count as planar.</summary>
    double PlanarInliers = 0.92,
    /// <summary>Regions with fewer valid points are not proposed.</summary>
    int MinPoints = 30,
    /// <summary>A mesh face belongs to the surface when at least this share of its pixels is in the mask.</summary>
    double FaceCoverage = 0.5,
    /// <summary>Planes larger than this (m²) read as floor, wall or ceiling; smaller as faces of an object.</summary>
    double RoomScaleArea = 2.0,
    /// <summary>The world's up (PlayMap's world is +Y up).</summary>
    Vec3d? Up = null)
{
    public static readonly SurfaceSuggestionOptions Default = new();

    public Vec3d UpAxis => (Up ?? Vec3d.UnitY).Normalized();
}

/// <summary>A best-fit plane: unit normal facing the viewer, a point on it, the RMS distance of the inliers and
/// the inlier share.</summary>
public sealed record PlaneFit(Vec3d Normal, Vec3d Centroid, double Rms, double InlierFraction)
{
    public double Distance(Vec3d p) => Vec3d.Dot(p - Centroid, Normal);
}

/// <summary>A proposed surface: the mask it came from, its name guess and tags, its 3D extent, the plane when it
/// is planar, its area, and the mesh faces it covers when a triangle map was given.</summary>
public sealed record SurfaceProposal(
    string Name,
    ImmutableArray<string> Tags,
    SegmentationMask Mask,
    int PointCount,
    PlaneFit? Plane,
    double AreaSquareMetres,
    Vec3d Min,
    Vec3d Max,
    ImmutableArray<int> Faces,
    double Confidence)
{
    public bool IsPlanar => Plane is not null;
}

/// <summary>From masks and the scan's organised points to surfaces worth creating: per mask, its points, a
/// RANSAC-then-least-squares plane (or "curved"), the area summed over the pixels' own 3D patches, a name — the
/// phrase that found it when there was one, otherwise floor / wall / ceiling for room-scale planes, "box face"
/// for an object face meeting another proposal's face square-on, top / side face, plane, curved surface — and the
/// mesh faces it covers. <see cref="AutoMasks"/> proposes regions with no click at all: the scan's surfaces as
/// the edge graph separates them.</summary>
public static class SurfaceSuggestion
{
    private static readonly string[] GenericLabels = ["", "surface", "region"];

    public static IReadOnlyList<SurfaceProposal> Propose(
        IReadOnlyList<SegmentationMask> masks, PointCloud points, int[]? triangleIds = null, SurfaceSuggestionOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(masks);
        ArgumentNullException.ThrowIfNull(points);
        options ??= SurfaceSuggestionOptions.Default;
        if (!points.IsOrganised)
            throw new ArgumentException("Surfaces need the organised cloud of the masks' grid.", nameof(points));
        int w = points.GridWidth, h = points.GridHeight;
        if (triangleIds is not null && triangleIds.Length != w * h)
            throw new ArgumentException("One triangle id per pixel.", nameof(triangleIds));
        var faceTotals = triangleIds is null ? null : CountFaces(triangleIds);
        var edges = GeometryEdges.Build(points, GeometryEdgeOptions.Default);

        var drafts = new List<Draft>();
        foreach (var mask in masks)
        {
            if (mask.Width != w || mask.Height != h)
                throw new ArgumentException("Every mask must be on the cloud's grid.", nameof(masks));
            var d = Measure(mask, points, edges, options);
            if (d is null)
                continue;
            if (triangleIds is not null && faceTotals is not null)
                d.Faces = FacesOf(mask, triangleIds, faceTotals, options.FaceCoverage);
            drafts.Add(d);
        }
        Name(drafts, w, h, options);
        return [.. drafts.Select(d => new SurfaceProposal(d.Name, [.. d.Tags], d.Mask, d.Count, d.Plane, d.Area, d.Min, d.Max, d.Faces,
            Math.Clamp(d.Mask.Score * (d.Plane?.InlierFraction ?? 1), 0, 1)))];
    }

    /// <summary>The scan's surfaces with no click: the components of the edge graph (links below 1) of at least
    /// <paramref name="minFraction"/> of the valid points, largest first, as masks scored by how much of their
    /// boundary is a real edge.</summary>
    public static IReadOnlyList<SegmentationMask> AutoMasks(PointCloud points, double minFraction = 0.01, GeometryEdgeOptions? edgeOptions = null)
    {
        ArgumentNullException.ThrowIfNull(points);
        var edges = GeometryEdges.Build(points, edgeOptions ?? GeometryEdgeOptions.Default);
        int w = edges.Width, h = edges.Height;
        var label = new int[w * h];
        var sizes = new List<int> { 0 };
        var queue = new Queue<int>();
        var validCount = 0;
        for (var s = 0; s < label.Length; s++)
        {
            if (!edges.Valid[s])
                continue;
            validCount++;
            if (label[s] != 0)
                continue;
            var id = sizes.Count;
            sizes.Add(0);
            label[s] = id;
            queue.Enqueue(s);
            while (queue.Count > 0)
            {
                var i = queue.Dequeue();
                sizes[id]++;
                int x = i % w, y = i / w;
                if (x + 1 < w && edges.Right[i] < 1) Visit(i + 1);
                if (x > 0 && edges.Right[i - 1] < 1) Visit(i - 1);
                if (y + 1 < h && edges.Down[i] < 1) Visit(i + w);
                if (y > 0 && edges.Down[i - w] < 1) Visit(i - w);

                void Visit(int j)
                {
                    if (label[j] != 0)
                        return;
                    label[j] = id;
                    queue.Enqueue(j);
                }
            }
        }
        var min = Math.Max(1, (int)(validCount * minFraction));
        var masks = new List<SegmentationMask>();
        for (var id = 1; id < sizes.Count; id++)
        {
            if (sizes[id] < min)
                continue;
            var region = new bool[w * h];
            for (var i = 0; i < region.Length; i++)
                region[i] = label[i] == id;
            SurfaceRegion.Complete(edges, region, null, 2, 0.3);
            MaskOps.FillHoles(region, w, h, sizes[id] / 50 + 4);
            masks.Add(new SegmentationMask(w, h, MaskOps.ToCoverage(region), SurfaceRegion.BoundaryStrength(edges, region), "surface"));
        }
        masks.Sort((a, b) => b.Area().CompareTo(a.Area()));
        return masks;
    }

    private sealed class Draft
    {
        public required SegmentationMask Mask { get; init; }
        public required bool[] Inside { get; init; }
        public int Count { get; init; }
        public PlaneFit? Plane { get; init; }
        public double Area { get; init; }
        public Vec3d Min { get; init; }
        public Vec3d Max { get; init; }
        public ImmutableArray<int> Faces { get; set; } = [];
        public string Name { get; set; } = "";
        public List<string> Tags { get; } = [];
    }

    private static Draft? Measure(SegmentationMask mask, PointCloud cloud, GeometryEdges edges, SurfaceSuggestionOptions o)
    {
        int w = mask.Width, h = mask.Height;
        var p = cloud.Points;
        var inside = MaskOps.FromCoverage(mask.Coverage);
        var pts = new List<Vec3d>();
        var viewSum = Vec3d.Zero;
        var min = new Vec3d(double.MaxValue, double.MaxValue, double.MaxValue);
        var max = new Vec3d(double.MinValue, double.MinValue, double.MinValue);
        for (var i = 0; i < inside.Length; i++)
        {
            if (!inside[i] || !edges.Valid[i])
                continue;
            pts.Add(p[i]);
            min = Vec3d.Min(min, p[i]);
            max = Vec3d.Max(max, p[i]);
            // The grid's normals point away from the camera (x-tangent × y-tangent); their sum says which way the
            // viewer is.
            viewSum -= edges.Normals[i];
        }
        if (pts.Count < o.MinPoints)
            return null;
        var extent = (max - min).Length;
        var band = Math.Max(o.PlaneFloor, o.PlaneFraction * extent);
        var plane = FitPlane(pts, band, viewSum);
        if (plane.InlierFraction < o.PlanarInliers || plane.Rms > band)
            plane = null;
        return new Draft
        {
            Mask = mask,
            Inside = inside,
            Count = pts.Count,
            Plane = plane,
            Area = Area(inside, p, edges, w, h),
            Min = min,
            Max = max,
        };
    }

    /// <summary>RANSAC over a deterministic sample (seeded, so a proposal never changes between runs), then a
    /// least-squares refit on the inliers; the normal faces the viewer.</summary>
    internal static PlaneFit FitPlane(List<Vec3d> pts, double band, Vec3d towardViewer)
    {
        var rng = new Pcg32(0x5eed);
        var bestCount = -1;
        var best = (N: Vec3d.UnitY, C: pts[0]);
        var sample = pts.Count <= 4000 ? pts : Subsample(pts, 4000, ref rng);
        for (var it = 0; it < 200; it++)
        {
            var a = sample[rng.NextInt(sample.Count)];
            var b = sample[rng.NextInt(sample.Count)];
            var c = sample[rng.NextInt(sample.Count)];
            var n = Vec3d.Cross(b - a, c - a);
            if (n.LengthSquared < 1e-18)
                continue;
            n = n.Normalized();
            var count = 0;
            foreach (var q in sample)
                if (Math.Abs(Vec3d.Dot(q - a, n)) <= band)
                    count++;
            if (count > bestCount)
            {
                bestCount = count;
                best = (n, a);
            }
        }
        var inliers = pts.Where(q => Math.Abs(Vec3d.Dot(q - best.C, best.N)) <= band).ToList();
        if (inliers.Count < 3)
            inliers = pts;
        var (normal, centroid) = LeastSquaresPlane(inliers);
        if (Vec3d.Dot(normal, towardViewer) < 0)
            normal = -normal;
        double sq = 0;
        var within = 0;
        foreach (var q in pts)
        {
            var d = Math.Abs(Vec3d.Dot(q - centroid, normal));
            if (d <= band)
            {
                within++;
                sq += d * d;
            }
        }
        return new PlaneFit(normal, centroid, within == 0 ? double.PositiveInfinity : Math.Sqrt(sq / within), within / (double)pts.Count);
    }

    private static List<Vec3d> Subsample(List<Vec3d> pts, int n, ref Pcg32 rng)
    {
        var s = new List<Vec3d>(n);
        for (var i = 0; i < n; i++)
            s.Add(pts[rng.NextInt(pts.Count)]);
        return s;
    }

    private static (Vec3d Normal, Vec3d Centroid) LeastSquaresPlane(List<Vec3d> pts)
    {
        var c = Vec3d.Zero;
        foreach (var q in pts)
            c += q;
        c /= pts.Count;
        var m = new DenseMatrix(3, 3);
        foreach (var q in pts)
        {
            var d = q - c;
            m[0, 0] += d.X * d.X; m[0, 1] += d.X * d.Y; m[0, 2] += d.X * d.Z;
            m[1, 1] += d.Y * d.Y; m[1, 2] += d.Y * d.Z; m[2, 2] += d.Z * d.Z;
        }
        m[1, 0] = m[0, 1];
        m[2, 0] = m[0, 2];
        m[2, 1] = m[1, 2];
        var (_, vectors) = SymmetricEigen.Decompose(m);
        return (new Vec3d(vectors[0, 0], vectors[1, 0], vectors[2, 0]).Normalized(), c);
    }

    /// <summary>The 3D area: each pixel's patch spanned by its steps to the right and down (when both are in the
    /// mask, valid and no jump); pixels without a patch take the mean patch.</summary>
    private static double Area(bool[] inside, Vec3d[] p, GeometryEdges edges, int w, int h)
    {
        double sum = 0;
        var measured = 0;
        var counted = 0;
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var i = y * w + x;
                if (!inside[i] || !edges.Valid[i])
                    continue;
                counted++;
                if (x + 1 < w && y + 1 < h && inside[i + 1] && inside[i + w] && edges.Right[i] < 1 && edges.Down[i] < 1)
                {
                    sum += Vec3d.Cross(p[i + 1] - p[i], p[i + w] - p[i]).Length;
                    measured++;
                }
            }
        }
        return measured == 0 ? 0 : sum / measured * counted;
    }

    private static Dictionary<int, int> CountFaces(int[] ids)
    {
        var d = new Dictionary<int, int>();
        foreach (var t in ids)
            if (t >= 0)
                d[t] = d.GetValueOrDefault(t) + 1;
        return d;
    }

    private static ImmutableArray<int> FacesOf(SegmentationMask mask, int[] ids, Dictionary<int, int> totals, double coverage)
    {
        var inMask = new Dictionary<int, int>();
        for (var i = 0; i < ids.Length; i++)
            if (ids[i] >= 0 && mask.Coverage[i] >= 128)
                inMask[ids[i]] = inMask.GetValueOrDefault(ids[i]) + 1;
        return [.. inMask.Where(kv => kv.Value >= coverage * totals[kv.Key]).Select(kv => kv.Key).Order()];
    }

    private static void Name(List<Draft> drafts, int w, int h, SurfaceSuggestionOptions o)
    {
        var up = o.UpAxis;
        var cosLevel = Math.Cos(20 * Math.PI / 180);
        var sinLevel = Math.Sin(20 * Math.PI / 180);
        // The floor is the lowest large upward plane; its height decides between floor and a top face.
        var lowest = drafts.Where(d => d.Plane is { } pl && Vec3d.Dot(pl.Normal, up) > cosLevel)
            .Select(d => Vec3d.Dot(d.Plane!.Centroid, up)).DefaultIfEmpty(double.NaN).Min();
        foreach (var d in drafts)
        {
            d.Tags.Add(d.Plane is null ? "curved" : "planar");
            if (d.Plane is not { } pl)
            {
                d.Name = "curved surface";
                continue;
            }
            var dot = Vec3d.Dot(pl.Normal, up);
            var large = d.Area >= o.RoomScaleArea;
            if (dot > cosLevel)
            {
                d.Tags.Add("horizontal");
                var height = Vec3d.Dot(pl.Centroid, up);
                d.Name = large || (drafts.Count > 1 && height <= lowest + 0.02) ? "floor" : "top face";
            }
            else if (dot < -cosLevel)
            {
                d.Tags.Add("horizontal");
                d.Name = large ? "ceiling" : "underside";
            }
            else if (Math.Abs(dot) < sinLevel)
            {
                d.Tags.Add("vertical");
                d.Name = large ? "wall" : "side face";
            }
            else
                d.Name = "plane";
        }
        // An object's face that meets another proposal's face square-on is a box face.
        for (var a = 0; a < drafts.Count; a++)
        {
            for (var b = 0; b < drafts.Count; b++)
            {
                if (a == b || drafts[a].Plane is not { } pa || drafts[b].Plane is not { } pb)
                    continue;
                if (drafts[a].Area >= o.RoomScaleArea || drafts[a].Name == "floor")
                    continue;
                var angle = Math.Acos(Math.Clamp(Math.Abs(Vec3d.Dot(pa.Normal, pb.Normal)), 0, 1)) * 180 / Math.PI;
                if (angle > 70 && Touch(drafts[a].Inside, drafts[b].Inside, w, h))
                {
                    if (!drafts[a].Tags.Contains("box"))
                        drafts[a].Tags.Add("box");
                    if (drafts[a].Name is "top face" or "side face" or "plane" or "underside")
                    {
                        drafts[a].Tags.Add(drafts[a].Name.Replace(" face", "", StringComparison.Ordinal));
                        drafts[a].Name = "box face";
                    }
                }
            }
        }
        // A phrase that found the region names it; the guess stays as a tag.
        foreach (var d in drafts)
        {
            if (!GenericLabels.Contains(d.Mask.Label.Trim()))
            {
                d.Tags.Add(d.Name);
                d.Name = d.Mask.Label.Trim();
            }
        }
    }

    /// <summary>Two masks touch when a pixel of one is within two pixels of a pixel of the other.</summary>
    private static bool Touch(bool[] a, bool[] b, int w, int h)
    {
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                if (!a[y * w + x])
                    continue;
                for (var dy = -2; dy <= 2; dy++)
                    for (var dx = -2; dx <= 2; dx++)
                    {
                        int xx = x + dx, yy = y + dy;
                        if (xx >= 0 && yy >= 0 && xx < w && yy < h && b[yy * w + xx])
                            return true;
                    }
            }
        }
        return false;
    }
}
