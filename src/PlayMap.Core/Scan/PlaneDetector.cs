using System.Runtime.InteropServices;
using PlayMap.Core.Geometry;
using PlayMap.Core.Numerics;

namespace PlayMap.Core.Scan;

/// <summary>Options for <see cref="PlaneDetector"/>.</summary>
public sealed record PlaneDetectionOptions
{
    /// <summary>A point within this distance (metres) of a plane is on it.</summary>
    public double DistanceThreshold { get; init; } = 0.01;

    /// <summary>With normals, a point is on a plane only if its normal is within this angle of the plane's.</summary>
    public double MaxNormalAngleDeg { get; init; } = 25;

    /// <summary>A plane needs at least this many inliers…</summary>
    public int MinInliers { get; init; } = 100;

    /// <summary>…and at least this share of the valid points.</summary>
    public double MinInlierFraction { get; init; } = 0.005;

    public int MaxPlanes { get; init; } = 8;

    /// <summary>The most hypotheses per plane; fewer are drawn once the best one's inlier share makes it
    /// 99.9 % likely that an all-inlier triple has been drawn.</summary>
    public int Iterations { get; init; } = 500;

    /// <summary>Hypotheses are scored on a random sample of this many of the remaining points.</summary>
    public int ScoreSample { get; init; } = 4000;

    /// <summary>Least-squares refits of the winner on its inliers.</summary>
    public int RefineIterations { get; init; } = 3;

    public ulong Seed { get; init; } = 1;
}

/// <summary>A plane found in a cloud: the equation (normal toward the cloud's points' side the fit chose), the
/// indices of its inliers, their centroid and RMS distance.</summary>
public sealed record DetectedPlane(PlaneEquation Plane, int[] Inliers, Vec3d Centroid, double RmsDistance)
{
    public int Count => Inliers.Length;
}

/// <summary>
/// RANSAC multi-plane detection: the largest plane first, its inliers removed, and again. Each round draws
/// seeded triples from the remaining points (a triple whose own normals disagree with its plane is skipped
/// before scoring), scores each hypothesis on a random sample of the remaining points (distance and, with
/// normals, angle), then refits the winner by PCA on all its inliers and recollects them. Planes come back
/// ordered by inlier count. Deterministic for a seed.
/// </summary>
public static class PlaneDetector
{
    private const int MinIterations = 30;

    public static IReadOnlyList<DetectedPlane> Detect(PointCloud cloud, PlaneDetectionOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(cloud);
        options ??= new PlaneDetectionOptions();
        var points = cloud.Points;
        var normals = cloud.HasNormals ? cloud.Normals : null;
        var cosLimit = Math.Cos(options.MaxNormalAngleDeg * Math.PI / 180);
        var remaining = new List<int>();
        for (var i = 0; i < points.Length; i++)
        {
            if (PointCloud.IsValid(points[i]) && (normals is null || normals[i] != Vec3d.Zero))
                remaining.Add(i);
        }
        var minInliers = Math.Max(options.MinInliers, (int)(options.MinInlierFraction * remaining.Count));
        var rng = new Pcg32(options.Seed);
        var planes = new List<DetectedPlane>();
        var sample = new List<int>();
        var taken = new bool[points.Length];
        while (planes.Count < options.MaxPlanes && remaining.Count >= minInliers)
        {
            sample.Clear();
            if (remaining.Count <= options.ScoreSample)
                sample.AddRange(remaining);
            else
                for (var s = 0; s < options.ScoreSample; s++)
                    sample.Add(remaining[rng.NextInt(remaining.Count)]);

            PlaneEquation? best = null;
            var bestScore = 0;
            var needed = options.Iterations;
            for (var it = 0; it < Math.Min(needed, options.Iterations); it++)
            {
                var a = remaining[rng.NextInt(remaining.Count)];
                var b = remaining[rng.NextInt(remaining.Count)];
                var c = remaining[rng.NextInt(remaining.Count)];
                if (a == b || b == c || a == c || PlaneEquation.FromPoints(points[a], points[b], points[c]) is not { } plane)
                    continue;
                if (normals is not null && (Math.Abs(Vec3d.Dot(normals[a], plane.Normal)) < cosLimit
                    || Math.Abs(Vec3d.Dot(normals[b], plane.Normal)) < cosLimit || Math.Abs(Vec3d.Dot(normals[c], plane.Normal)) < cosLimit))
                    continue;
                var score = 0;
                foreach (var i in sample)
                {
                    if (IsInlier(i, plane, points, normals, options.DistanceThreshold, cosLimit))
                        score++;
                }
                if (score > bestScore)
                {
                    bestScore = score;
                    best = plane;
                    var w = (double)score / sample.Count;
                    var allInliers = w * w * w;
                    needed = allInliers >= 1 - 1e-12 ? MinIterations : Math.Max(MinIterations, (int)Math.Ceiling(Math.Log(1e-3) / Math.Log(1 - allInliers)));
                }
            }
            if (best is not { } winner)
                break;
            var inliers = Collect(remaining, winner, points, normals, options.DistanceThreshold, cosLimit);
            for (var r = 0; r < options.RefineIterations && inliers.Count >= 3; r++)
            {
                var refined = PcaFit.Of(points, CollectionsMarshal.AsSpan(inliers)).Plane;
                var again = Collect(remaining, refined, points, normals, options.DistanceThreshold, cosLimit);
                if (again.Count < 3)
                    break;
                winner = refined;
                inliers = again;
            }
            if (inliers.Count < minInliers)
                break;
            var fit = PcaFit.Of(points, CollectionsMarshal.AsSpan(inliers));
            planes.Add(new DetectedPlane(winner, [.. inliers], fit.Centroid, RmsDistance(points, inliers, winner)));
            foreach (var i in inliers)
                taken[i] = true;
            remaining.RemoveAll(i => taken[i]);
        }
        planes.Sort((x, y) => y.Count.CompareTo(x.Count));
        return planes;
    }

    private static bool IsInlier(int i, PlaneEquation plane, Vec3d[] points, Vec3d[]? normals, double threshold, double cosLimit) =>
        Math.Abs(plane.SignedDistance(points[i])) <= threshold
        && (normals is null || Math.Abs(Vec3d.Dot(normals[i], plane.Normal)) >= cosLimit);

    private static List<int> Collect(List<int> remaining, PlaneEquation plane, Vec3d[] points, Vec3d[]? normals, double threshold, double cosLimit)
    {
        var inliers = new List<int>();
        foreach (var i in remaining)
        {
            if (IsInlier(i, plane, points, normals, threshold, cosLimit))
                inliers.Add(i);
        }
        return inliers;
    }

    private static double RmsDistance(Vec3d[] points, List<int> inliers, PlaneEquation plane)
    {
        double sum = 0;
        foreach (var i in inliers)
        {
            var d = plane.SignedDistance(points[i]);
            sum += d * d;
        }
        return inliers.Count > 0 ? Math.Sqrt(sum / inliers.Count) : 0;
    }
}
