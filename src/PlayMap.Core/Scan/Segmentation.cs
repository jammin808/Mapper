using PlayMap.Core.Geometry;
using PlayMap.Core.Numerics;

namespace PlayMap.Core.Scan;

/// <summary>Which points of a cloud neighbour which: an organised cloud's 8 grid neighbours that are not across
/// a depth jump (further than <c>jumpFactor</c> local grid steps), or an unorganised cloud's k nearest within a
/// radius (by default three times the typical nearest-neighbour distance).</summary>
internal sealed class Neighbourhood
{
    private readonly PointCloud _cloud;
    private readonly float[]? _spacing;
    private readonly KdTree3? _tree;
    private readonly double _jumpFactor;
    private readonly int _k;
    private readonly double _radius2;

    public Neighbourhood(PointCloud cloud, double jumpFactor, int k, double radius)
    {
        _cloud = cloud;
        _jumpFactor = jumpFactor;
        _k = Math.Max(3, k);
        if (cloud.IsOrganised)
        {
            _spacing = PointCloudOps.GridSpacing(cloud);
            return;
        }
        _tree = new KdTree3(cloud.Points);
        if (radius <= 0)
            radius = 3 * TypicalNearest(cloud, _tree);
        _radius2 = radius * radius;
    }

    public int Capacity => Math.Max(8, _k);

    private static double TypicalNearest(PointCloud cloud, KdTree3 tree)
    {
        var d = new List<double>();
        var step = Math.Max(1, cloud.Count / 500);
        for (var i = 0; i < cloud.Count; i += step)
        {
            if (!PointCloud.IsValid(cloud.Points[i]))
                continue;
            var near = tree.KNearest(cloud.Points[i], 2);
            if (near.Length == 2)
                d.Add((cloud.Points[near[1]] - cloud.Points[i]).Length);
        }
        if (d.Count == 0)
            return 0;
        d.Sort();
        return d[d.Count / 2];
    }

    /// <summary>Writes the neighbours of point <paramref name="i"/> into <paramref name="into"/> (at least
    /// <see cref="Capacity"/> long) and returns how many.</summary>
    public int Of(int i, Span<int> into)
    {
        var points = _cloud.Points;
        var p = points[i];
        var n = 0;
        if (_spacing is { } spacing)
        {
            var w = _cloud.GridWidth;
            var h = _cloud.GridHeight;
            var x = i % w;
            var y = i / w;
            for (var dy = -1; dy <= 1; dy++)
            {
                for (var dx = -1; dx <= 1; dx++)
                {
                    var xx = x + dx;
                    var yy = y + dy;
                    if ((dx == 0 && dy == 0) || xx < 0 || yy < 0 || xx >= w || yy >= h)
                        continue;
                    var q = yy * w + xx;
                    if (!PointCloud.IsValid(points[q]))
                        continue;
                    var step = Math.Min(spacing[i], spacing[q]) * (dx != 0 && dy != 0 ? Math.Sqrt(2) : 1);
                    var reach = _jumpFactor * step;
                    if ((points[q] - p).LengthSquared <= reach * reach)
                        into[n++] = q;
                }
            }
            return n;
        }
        foreach (var q in _tree!.KNearest(p, _k + 1))
        {
            if (q != i && (points[q] - p).LengthSquared <= _radius2 && n < into.Length)
                into[n++] = q;
        }
        return n;
    }
}

/// <summary>Options for <see cref="RegionGrowing"/>.</summary>
public sealed record RegionGrowingOptions
{
    /// <summary>Neighbouring normals further apart than this split two regions.</summary>
    public double MaxAngleDeg { get; init; } = 12;

    /// <summary>Organised: a neighbour further than this many local grid steps is across a depth jump.</summary>
    public double JumpFactor { get; init; } = 4;

    /// <summary>Unorganised: neighbours per point, and the radius they must lie within (0 = three typical steps).</summary>
    public int K { get; init; } = 12;

    public double Radius { get; init; }

    /// <summary>A region grows on from a point only while the point's surface variation is below this (edges and
    /// corners join the region but do not carry it across). Needs the curvature from normal estimation.</summary>
    public double MaxCurvature { get; init; } = 0.05;

    /// <summary>Smaller regions are dropped (their points stay unlabelled).</summary>
    public int MinRegionSize { get; init; } = 50;
}

/// <summary>A segmentation: a label per point (−1 for none) and each region's points, largest first; a point's
/// label is its region's index.</summary>
public sealed record Segmentation(int[] Labels, IReadOnlyList<int[]> Regions);

/// <summary>Region growing by normal angle and distance: seeds are taken flattest first (by surface variation,
/// when given), each region floods its neighbours whose normals agree with the point they are reached from;
/// depth jumps stop it on an organised cloud, distance on an unorganised one.</summary>
public static class RegionGrowing
{
    public static Segmentation Segment(PointCloud cloud, RegionGrowingOptions? options = null, float[]? curvature = null)
    {
        ArgumentNullException.ThrowIfNull(cloud);
        if (!cloud.HasNormals)
            throw new ArgumentException("Estimate the cloud's normals first.", nameof(cloud));
        options ??= new RegionGrowingOptions();
        var points = cloud.Points;
        var normals = cloud.Normals;
        var cos = Math.Cos(options.MaxAngleDeg * Math.PI / 180);
        var neighbourhood = new Neighbourhood(cloud, options.JumpFactor, options.K, options.Radius);
        var seeds = Enumerable.Range(0, points.Length).Where(i => PointCloud.IsValid(points[i]) && normals[i] != Vec3d.Zero).ToArray();
        if (curvature is not null)
            seeds = [.. seeds.OrderBy(i => float.IsNaN(curvature[i]) ? float.MaxValue : curvature[i])];
        var labels = new int[points.Length];
        Array.Fill(labels, -1);
        var visited = new bool[points.Length];
        var regions = new List<int[]>();
        var queue = new Queue<int>();
        var buffer = new int[neighbourhood.Capacity];
        var region = new List<int>();
        foreach (var seed in seeds)
        {
            if (visited[seed])
                continue;
            region.Clear();
            visited[seed] = true;
            region.Add(seed);
            queue.Enqueue(seed);
            while (queue.Count > 0)
            {
                var p = queue.Dequeue();
                if (curvature is not null && p != seed && !(curvature[p] <= options.MaxCurvature))
                    continue;
                var n = neighbourhood.Of(p, buffer);
                for (var j = 0; j < n; j++)
                {
                    var q = buffer[j];
                    if (visited[q] || normals[q] == Vec3d.Zero || Math.Abs(Vec3d.Dot(normals[p], normals[q])) < cos)
                        continue;
                    visited[q] = true;
                    region.Add(q);
                    queue.Enqueue(q);
                }
            }
            if (region.Count >= options.MinRegionSize)
                regions.Add([.. region]);
        }
        regions.Sort((a, b) => b.Length.CompareTo(a.Length));
        for (var r = 0; r < regions.Count; r++)
        {
            foreach (var i in regions[r])
                labels[i] = r;
        }
        return new Segmentation(labels, regions);
    }
}

/// <summary>What a click selected.</summary>
public enum SurfaceKind
{
    /// <summary>The selection is flat: <see cref="SurfaceSelection.Plane"/> holds its plane.</summary>
    Plane,

    /// <summary>A curved or irregular selection.</summary>
    Freeform,
}

/// <summary>A click's selection: the point indices, the fitted primitive, the centroid and the RMS distance of
/// the points from the best plane through them. The plane's normal agrees with the points' normals.</summary>
public sealed record SurfaceSelection(int[] Indices, SurfaceKind Kind, PlaneEquation? Plane, Vec3d Centroid, double RmsDistance)
{
    public int Count => Indices.Length;
}

/// <summary>Options for <see cref="ClickSelect"/>.</summary>
public sealed record ClickSelectOptions
{
    /// <summary>A neighbour whose normal turns more than this from the point it is reached from is across a crease.</summary>
    public double MaxCreaseAngleDeg { get; init; } = 20;

    /// <summary>No selected normal turns more than this from the clicked point's, so a rounded edge that turns
    /// gradually still stops the selection (180 follows a curved surface all the way round).</summary>
    public double MaxSeedAngleDeg { get; init; } = 35;

    /// <summary>Organised: a neighbour further than this many local grid steps is across a depth jump.</summary>
    public double JumpFactor { get; init; } = 4;

    /// <summary>Unorganised: neighbours per point and their radius (0 = three typical steps).</summary>
    public int K { get; init; } = 12;

    public double Radius { get; init; }

    /// <summary>A selection whose RMS distance from its plane is at most this (metres) is a plane.</summary>
    public double PlanarTolerance { get; init; } = 0.006;

    /// <summary>How far (grid cells) from an invalid clicked pixel to look for a valid one.</summary>
    public int SnapRadius { get; init; } = 5;
}

/// <summary>
/// Click-to-select: from a clicked pixel of an organised cloud (or a seed point of any cloud) the selection
/// floods outward, stopping at depth discontinuities (a neighbour further than a few local grid steps) and at
/// normal creases (a sharp turn between neighbours, or a turn from the clicked point's normal beyond a limit),
/// then fits a plane to what it got. On a box, a click on the top face selects the top face only.
/// </summary>
public static class ClickSelect
{
    /// <summary>Selects from pixel (x, y) of an organised cloud with normals; null when no valid point lies within
    /// the snap radius.</summary>
    public static SurfaceSelection? FromPixel(PointCloud organised, int x, int y, ClickSelectOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(organised);
        if (!organised.IsOrganised)
            throw new ArgumentException("The cloud has no grid.", nameof(organised));
        options ??= new ClickSelectOptions();
        var w = organised.GridWidth;
        var h = organised.GridHeight;
        var best = -1;
        var bestD = int.MaxValue;
        for (var dy = -options.SnapRadius; dy <= options.SnapRadius; dy++)
        {
            for (var dx = -options.SnapRadius; dx <= options.SnapRadius; dx++)
            {
                var xx = x + dx;
                var yy = y + dy;
                if (xx < 0 || yy < 0 || xx >= w || yy >= h)
                    continue;
                var k = yy * w + xx;
                var d = dx * dx + dy * dy;
                if (d < bestD && PointCloud.IsValid(organised.Points[k]) && organised.HasNormals && organised.Normals[k] != Vec3d.Zero)
                {
                    best = k;
                    bestD = d;
                }
            }
        }
        return best < 0 ? null : FromPoint(organised, best, options);
    }

    /// <summary>Selects from point <paramref name="seed"/> of a cloud with normals.</summary>
    public static SurfaceSelection FromPoint(PointCloud cloud, int seed, ClickSelectOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(cloud);
        if (!cloud.HasNormals)
            throw new ArgumentException("Estimate the cloud's normals first.", nameof(cloud));
        ArgumentOutOfRangeException.ThrowIfNegative(seed);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(seed, cloud.Count);
        options ??= new ClickSelectOptions();
        var points = cloud.Points;
        var normals = cloud.Normals;
        if (!PointCloud.IsValid(points[seed]) || normals[seed] == Vec3d.Zero)
            throw new ArgumentException("The seed point has no position or normal.", nameof(seed));
        var crease = Math.Cos(options.MaxCreaseAngleDeg * Math.PI / 180);
        var drift = Math.Cos(options.MaxSeedAngleDeg * Math.PI / 180);
        var seedNormal = normals[seed];
        var neighbourhood = new Neighbourhood(cloud, options.JumpFactor, options.K, options.Radius);
        var buffer = new int[neighbourhood.Capacity];
        var visited = new bool[points.Length];
        var selected = new List<int> { seed };
        var queue = new Queue<int>();
        visited[seed] = true;
        queue.Enqueue(seed);
        while (queue.Count > 0)
        {
            var p = queue.Dequeue();
            var n = neighbourhood.Of(p, buffer);
            for (var j = 0; j < n; j++)
            {
                var q = buffer[j];
                if (visited[q] || normals[q] == Vec3d.Zero)
                    continue;
                if (Math.Abs(Vec3d.Dot(normals[p], normals[q])) < crease || Math.Abs(Vec3d.Dot(seedNormal, normals[q])) < drift)
                    continue;
                visited[q] = true;
                selected.Add(q);
                queue.Enqueue(q);
            }
        }
        var indices = selected.ToArray();
        Array.Sort(indices);
        var fit = PcaFit.Of(points, indices);
        if (fit.Count < 3)
            return new SurfaceSelection(indices, SurfaceKind.Freeform, null, fit.Centroid, 0);
        if (fit.RmsDistance <= 2 * options.PlanarTolerance)
        {
            // A flat selection drops what its blended edge normals let in from across a crease: points off its
            // plane by more than three robust standard deviations.
            var trimmed = TrimToPlane(points, indices, fit.Plane, options.PlanarTolerance);
            if (trimmed.Length >= 3 && trimmed.Length < indices.Length)
            {
                indices = trimmed;
                fit = PcaFit.Of(points, indices);
            }
        }
        var plane = fit.Plane;
        var meanNormal = Vec3d.Zero;
        foreach (var i in indices)
            meanNormal += normals[i];
        if (Vec3d.Dot(meanNormal, plane.Normal) < 0)
            plane = new PlaneEquation(-plane.Normal, -plane.Offset);
        var kind = fit.RmsDistance <= options.PlanarTolerance ? SurfaceKind.Plane : SurfaceKind.Freeform;
        return new SurfaceSelection(indices, kind, kind == SurfaceKind.Plane ? plane : null, fit.Centroid, fit.RmsDistance);
    }

    private static int[] TrimToPlane(Vec3d[] points, int[] indices, PlaneEquation plane, double tolerance)
    {
        var distances = new double[indices.Length];
        for (var i = 0; i < indices.Length; i++)
            distances[i] = Math.Abs(plane.SignedDistance(points[indices[i]]));
        var sorted = (double[])distances.Clone();
        Array.Sort(sorted);
        var limit = Math.Max(3 * 1.4826 * sorted[sorted.Length / 2], 0.25 * tolerance);
        var kept = new List<int>(indices.Length);
        for (var i = 0; i < indices.Length; i++)
        {
            if (distances[i] <= limit)
                kept.Add(indices[i]);
        }
        return [.. kept];
    }
}
