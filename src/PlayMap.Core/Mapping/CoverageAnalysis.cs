using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using PlayMap.Core.Geometry;
using PlayMap.Core.Model;
using PlayMap.Core.Numerics;

namespace PlayMap.Core.Mapping;

/// <summary>How finely the planner samples the surfaces.</summary>
public sealed record CoverageOptions
{
    public static readonly CoverageOptions Default = new();

    /// <summary>The spacing of the samples on the surfaces in metres; 0 takes the stage's diagonal ÷ 200.</summary>
    public double SampleSpacing { get; init; }

    /// <summary>The most samples one triangle is cut into (a huge flat triangle is cut no finer).</summary>
    public int MaxSamplesPerTriangle { get; init; } = 4096;
}

/// <summary>One sample of a surface: a small piece of it (its centre, normal, UV and area) and what reaches
/// it — how many projectors, which (bit q for projector q, the first 64), the finest projector's pixel
/// density there and which projector that is (−1: none). The planner paints its heatmaps from these.</summary>
public readonly record struct CoverageSample(int Surface, int Triangle, Vec3d Point, Vec3d Normal, Vec2d Uv, double Area, int Projectors, ulong Reach, double Density, int Best);

/// <summary>A connected piece of a surface no projector reaches: its area, where it is in the world and on
/// the surface's UVs.</summary>
public sealed record UncoveredArea(string SurfaceId, double Area, Vec3d Centroid, Vec3d Min, Vec3d Max, Vec2d UvMin, Vec2d UvMax, int Samples);

/// <summary>One surface in the planner's terms: how much of it is lit and by how many projectors, how fine
/// the light is (projector pixels per metre, the finest projector at each point), and what nobody reaches.</summary>
public sealed record SurfaceCoverage(
    string SurfaceId,
    string Name,
    /// <summary>Square metres.</summary>
    double Area,
    double CoveredFraction,
    /// <summary>The fraction of the area reached by exactly 0, 1, 2 and 3-or-more projectors.</summary>
    ImmutableArray<double> FractionByCount,
    double MinDensity,
    double MeanDensity,
    double MaxDensity,
    /// <summary>The fraction of the area each projector (by id; the first 64) reaches.</summary>
    IReadOnlyDictionary<string, double> FractionByProjector,
    /// <summary>What nobody reaches, largest first.</summary>
    IReadOnlyList<UncoveredArea> Uncovered,
    string Words);

/// <summary>The planner's coverage report over the stage.</summary>
public sealed record CoverageReport(IReadOnlyList<SurfaceCoverage> Surfaces, IReadOnlyList<CoverageSample> Samples, string Words)
{
    public SurfaceCoverage? For(string surfaceId) => Surfaces.FirstOrDefault(s => s.SurfaceId == surfaceId);
}

/// <summary>Coverage and pixel density for the planner (previz): every visible surface is cut into small
/// samples; at each, every projector that sees it (inside its raster, the surface facing it, nothing in
/// between) is counted, and its pixel density measured as √|det J| of the map from the surface's own
/// tangent plane to the projector's pixels — projector pixels per metre, exact for any angle, distance and
/// lens. Pieces nobody reaches are joined into connected areas. Deterministic; samples run in parallel.</summary>
public static class CoverageAnalysis
{
    /// <summary>Analyses the stage's visible surfaces against <paramref name="projectors"/> (default: every
    /// projector on the stage, planned ones too).</summary>
    public static CoverageReport Analyse(Stage stage, Func<string, MeshData?> meshes, IReadOnlyList<Projector>? projectors = null,
        CoverageOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stage);
        var scene = StageScene.Build(stage, meshes);
        return Analyse(scene, projectors ?? stage.Projectors, options, cancellationToken);
    }

    /// <summary>Analyses a scene already built.</summary>
    public static CoverageReport Analyse(StageScene scene, IReadOnlyList<Projector> projectors, CoverageOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(projectors);
        options ??= CoverageOptions.Default;
        var spacing = options.SampleSpacing > 0 ? options.SampleSpacing : scene.Diagonal / 200;
        var world = scene.World;
        var maxPerSide = Math.Max(1, (int)Math.Sqrt(Math.Max(1, options.MaxSamplesPerTriangle)));

        // Cut each triangle into n² similar pieces, n from its longest edge over the spacing.
        var cuts = new int[world.TriangleCount];
        var offsets = new int[world.TriangleCount + 1];
        for (var t = 0; t < world.TriangleCount; t++)
        {
            var (a, b, c) = world.Triangle(t);
            var pa = world.Position(a);
            var pb = world.Position(b);
            var pc = world.Position(c);
            var longest = Math.Max((pb - pa).Length, Math.Max((pc - pb).Length, (pa - pc).Length));
            cuts[t] = Math.Clamp((int)Math.Ceiling(longest / spacing), 1, maxPerSide);
            offsets[t + 1] = offsets[t] + cuts[t] * cuts[t];
        }
        var samples = new CoverageSample[offsets[^1]];
        var optics = projectors.Select(p => (Projector: p, Rot: p.Pose.CameraToWorld.Transposed())).ToArray();
        var delta = spacing * 0.25;
        Parallel.For(0, world.TriangleCount, new ParallelOptions { CancellationToken = cancellationToken }, t =>
        {
            var (ia, ib, ic) = world.Triangle(t);
            var pa = world.Position(ia);
            var pb = world.Position(ib);
            var pc = world.Position(ic);
            var ua = world.Uv(ia);
            var ub = world.Uv(ib);
            var uc = world.Uv(ic);
            var normal = scene.FaceNormal(t);
            var area = Vec3d.Cross(pb - pa, pc - pa).Length / 2;
            var n = cuts[t];
            var piece = area / (n * n);
            var t1 = (pb - pa).Normalized();
            var t2 = Vec3d.Cross(normal, t1).Normalized();
            var k = offsets[t];
            for (var i = 0; i < n; i++)
                for (var j = 0; i + j < n; j++)
                {
                    // The upward piece at (i, j), and the downward one beside it when there is room.
                    Emit((i + 1 / 3.0) / n, (j + 1 / 3.0) / n);
                    if (i + j < n - 1)
                        Emit((i + 2 / 3.0) / n, (j + 2 / 3.0) / n);
                }

            void Emit(double s, double r)
            {
                var point = pa + (pb - pa) * s + (pc - pa) * r;
                var uv = ua + (ub - ua) * s + (uc - ua) * r;
                var count = 0;
                ulong reach = 0;
                var best = -1;
                var bestDensity = 0.0;
                for (var q = 0; q < optics.Length; q++)
                {
                    var (p, rot) = optics[q];
                    var to = p.Pose.Position - point;
                    if (Vec3d.Dot(normal, to) <= 0)
                        continue;
                    var px = Project(rot, p, point);
                    if (px is not { } pix || !CameraModel.InRaster(pix, p.Intrinsics) || !scene.Sees(point, p.Pose.Position))
                        continue;
                    count++;
                    if (q < 64)
                        reach |= 1UL << q;
                    var p1 = Project(rot, p, point + t1 * delta);
                    var p2 = Project(rot, p, point + t2 * delta);
                    if (p1 is not { } a1 || p2 is not { } a2)
                        continue;
                    var j1 = (a1 - pix) / delta;
                    var j2 = (a2 - pix) / delta;
                    var density = Math.Sqrt(Math.Abs(Vec2d.Cross(j1, j2)));
                    if (density > bestDensity)
                    {
                        bestDensity = density;
                        best = q;
                    }
                }
                samples[k++] = new CoverageSample(scene.SurfaceOfTriangle(t), t, point, normal, uv, piece, count, reach, bestDensity, best);
            }
        });

        var surfaces = new List<SurfaceCoverage>(scene.Surfaces.Count);
        for (var si = 0; si < scene.Surfaces.Count; si++)
            surfaces.Add(Summarise(scene.Surfaces[si], si, samples, projectors, spacing));
        return new CoverageReport(surfaces, samples, Summary(surfaces, projectors));
    }

    private static Vec2d? Project(Mat3d worldToCam, Projector p, Vec3d point) =>
        CameraModel.ProjectCamera(worldToCam * (point - p.Pose.Position), p.Intrinsics, p.Lens);

    private static SurfaceCoverage Summarise(Surface surface, int index, CoverageSample[] samples, IReadOnlyList<Projector> projectors, double spacing)
    {
        double area = 0, covered = 0, densitySum = 0;
        var byCount = new double[4];
        var byProjector = new double[projectors.Count];
        var minD = double.MaxValue;
        var maxD = 0.0;
        var uncovered = new List<int>();
        for (var k = 0; k < samples.Length; k++)
        {
            var s = samples[k];
            if (s.Surface != index)
                continue;
            area += s.Area;
            byCount[Math.Min(s.Projectors, 3)] += s.Area;
            if (s.Projectors == 0)
            {
                uncovered.Add(k);
                continue;
            }
            covered += s.Area;
            for (var q = 0; q < Math.Min(64, projectors.Count); q++)
                if ((s.Reach & (1UL << q)) != 0)
                    byProjector[q] += s.Area;
            if (s.Best >= 0)
            {
                densitySum += s.Density * s.Area;
                minD = Math.Min(minD, s.Density);
                maxD = Math.Max(maxD, s.Density);
            }
        }
        var fractionByProjector = new Dictionary<string, double>(StringComparer.Ordinal);
        for (var q = 0; q < projectors.Count; q++)
            fractionByProjector[projectors[q].Id] = area > 0 ? byProjector[q] / area : 0;
        var areas = Regions(surface.Id, uncovered, samples, spacing);
        var coveredFraction = area > 0 ? covered / area : 0;
        var meanD = covered > 0 ? densitySum / covered : 0;
        if (minD == double.MaxValue)
            minD = 0;
        var fractions = ImmutableArray.Create(byCount.Select(c => area > 0 ? c / area : 0).ToArray());
        var words = new StringBuilder();
        words.Append(CultureInfo.InvariantCulture, $"{surface.Name}: {area:0.##} m², {coveredFraction * 100:0}% lit");
        if (covered > 0)
        {
            words.Append(CultureInfo.InvariantCulture, $" ({fractions[1] * 100:0}% by one projector");
            if (fractions[2] > 0)
                words.Append(CultureInfo.InvariantCulture, $", {fractions[2] * 100:0}% by two");
            if (fractions[3] > 0)
                words.Append(CultureInfo.InvariantCulture, $", {fractions[3] * 100:0}% by three or more");
            words.Append(CultureInfo.InvariantCulture, $"); {minD:0}–{maxD:0} px/m, {meanD:0} on average");
        }
        if (areas.Count > 0)
        {
            var big = areas[0];
            words.Append(CultureInfo.InvariantCulture,
                $"; {areas.Count} area{(areas.Count == 1 ? "" : "s")} nobody reaches, the largest {big.Area:0.##} m² around ({big.Centroid.X:0.##}, {big.Centroid.Y:0.##}, {big.Centroid.Z:0.##})");
        }
        words.Append('.');
        return new SurfaceCoverage(surface.Id, surface.Name, area, coveredFraction, fractions, minD, meanD, maxD, fractionByProjector, areas, words.ToString());
    }

    /// <summary>Joins unreached samples closer than two spacings into connected areas (a hash grid of
    /// cells one spacing wide and union-find), largest first.</summary>
    private static List<UncoveredArea> Regions(string surfaceId, List<int> indices, CoverageSample[] samples, double spacing)
    {
        var result = new List<UncoveredArea>();
        if (indices.Count == 0)
            return result;
        var cell = spacing;
        var grid = new Dictionary<(long, long, long), List<int>>();
        (long, long, long) Key(Vec3d p) => ((long)Math.Floor(p.X / cell), (long)Math.Floor(p.Y / cell), (long)Math.Floor(p.Z / cell));
        for (var m = 0; m < indices.Count; m++)
        {
            var key = Key(samples[indices[m]].Point);
            if (!grid.TryGetValue(key, out var list))
                grid[key] = list = [];
            list.Add(m);
        }
        var parent = Enumerable.Range(0, indices.Count).ToArray();
        int Find(int a)
        {
            while (parent[a] != a)
                a = parent[a] = parent[parent[a]];
            return a;
        }
        var reach2 = 4 * spacing * spacing;
        for (var m = 0; m < indices.Count; m++)
        {
            var p = samples[indices[m]].Point;
            var (kx, ky, kz) = Key(p);
            for (var dx = -2; dx <= 2; dx++)
                for (var dy = -2; dy <= 2; dy++)
                    for (var dz = -2; dz <= 2; dz++)
                    {
                        if (!grid.TryGetValue((kx + dx, ky + dy, kz + dz), out var list))
                            continue;
                        foreach (var other in list)
                            if (other > m && (samples[indices[other]].Point - p).LengthSquared <= reach2)
                                parent[Find(other)] = Find(m);
                    }
        }
        var groups = new Dictionary<int, List<int>>();
        for (var m = 0; m < indices.Count; m++)
        {
            var root = Find(m);
            if (!groups.TryGetValue(root, out var list))
                groups[root] = list = [];
            list.Add(indices[m]);
        }
        foreach (var members in groups.Values)
        {
            double area = 0;
            var centroid = Vec3d.Zero;
            var min = new Vec3d(double.MaxValue, double.MaxValue, double.MaxValue);
            var max = new Vec3d(double.MinValue, double.MinValue, double.MinValue);
            var uvMin = new Vec2d(double.MaxValue, double.MaxValue);
            var uvMax = new Vec2d(double.MinValue, double.MinValue);
            foreach (var k in members)
            {
                var s = samples[k];
                area += s.Area;
                centroid += s.Point * s.Area;
                min = Vec3d.Min(min, s.Point);
                max = Vec3d.Max(max, s.Point);
                uvMin = new Vec2d(Math.Min(uvMin.X, s.Uv.X), Math.Min(uvMin.Y, s.Uv.Y));
                uvMax = new Vec2d(Math.Max(uvMax.X, s.Uv.X), Math.Max(uvMax.Y, s.Uv.Y));
            }
            result.Add(new UncoveredArea(surfaceId, area, area > 0 ? centroid / area : min, min, max, uvMin, uvMax, members.Count));
        }
        result.Sort((a, b) => b.Area.CompareTo(a.Area));
        return result;
    }

    private static string Summary(List<SurfaceCoverage> surfaces, IReadOnlyList<Projector> projectors)
    {
        var area = surfaces.Sum(s => s.Area);
        var lit = surfaces.Sum(s => s.Area * s.CoveredFraction);
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture,
            $"{projectors.Count} projector{(projectors.Count == 1 ? "" : "s")} on {surfaces.Count} surface{(surfaces.Count == 1 ? "" : "s")}: {(area > 0 ? lit / area * 100 : 0):0}% of {area:0.##} m² is lit.");
        foreach (var s in surfaces)
            sb.Append(' ').Append(s.Words);
        return sb.ToString();
    }
}
