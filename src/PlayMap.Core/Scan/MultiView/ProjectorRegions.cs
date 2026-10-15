using System.Collections.Immutable;
using PlayMap.Core.Numerics;

namespace PlayMap.Core.Scan.MultiView;

/// <summary>How per-projector regions are found and merged.</summary>
public sealed record ProjectorRegionOptions
{
    public static readonly ProjectorRegionOptions Default = new();

    /// <summary>How each projector's depth map is split into regions (creases and depth jumps separate them). A dense
    /// depth map is smooth enough that a region carries on only where the surface varies less than 0.01: a curved
    /// surface (a column of 25 cm radius sampled every centimetre varies about 0.001) grows whole, a crease stops it.</summary>
    public RegionGrowingOptions Growing { get; init; } = new() { MaxCurvature = 0.01 };

    /// <summary>The voxel two projectors' regions are compared on, in the solve's units (1 cm when metric).</summary>
    public double Voxel { get; init; } = 0.01;

    /// <summary>Two projectors' regions are one when this share of the smaller lies on the larger (a voxel counts when
    /// the other region has a voxel at it or next to it, so two samplings of one surface agree).</summary>
    public double MinOverlap { get; init; } = 0.5;

    /// <summary>A merged region whose points lie within this of their best plane (RMS, the solve's units) is planar.</summary>
    public double PlanarTolerance { get; init; } = 0.005;
}

/// <summary>A region's part on one projector's raster: the depth-map cells it covers there.</summary>
public sealed record ProjectorRegionPart(string ProjectorId, int[] Cells);

/// <summary>A surface region measured across projectors: the projectors that light it (one part each), its centroid,
/// its best plane's normal and RMS distance, whether that makes it planar, and how many voxels it fills.</summary>
public sealed record MultiProjectorRegion(string Id, ImmutableArray<string> Projectors, ImmutableArray<ProjectorRegionPart> Parts,
    Vec3d Centroid, Vec3d Normal, double RmsDistance, bool Planar, int Voxels)
{
    public ProjectorRegionPart? On(string projectorId) => Parts.FirstOrDefault(p => p.ProjectorId == projectorId);
}

/// <summary>
/// Regions per projector, on its own raster, merged across projectors by 3D overlap. Each projector's depth-map cloud
/// is split by region growing (flattest seeds first; creases and depth jumps stop a region), so its regions work with
/// click-to-surface on that projector's own smart scan. Then regions of different projectors that lie on one surface
/// (<see cref="ProjectorRegionOptions.MinOverlap"/> of the smaller on the larger, on a voxel grid) are joined, and the
/// joined region lists every projector that lights it. Ids rg1, rg2, … go largest first.
/// </summary>
public static class ProjectorRegions
{
    public static ImmutableArray<MultiProjectorRegion> Build(IReadOnlyList<ProjectorDepthMap> maps, ProjectorRegionOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(maps);
        options ??= ProjectorRegionOptions.Default;
        if (!(options.Voxel > 0))
            throw new ArgumentOutOfRangeException(nameof(options), "A voxel is positive.");

        // Every projector's regions, each with its voxel set.
        var parts = new List<(int Map, int[] Cells, HashSet<(long, long, long)> Voxels)>();
        for (var m = 0; m < maps.Count; m++)
        {
            var map = maps[m];
            var seg = RegionGrowing.Segment(map.Cloud, options.Growing, map.Curvature.Length == map.Cloud.Count ? map.Curvature : null);
            foreach (var cells in seg.Regions)
                parts.Add((m, cells, [.. cells.Select(c => Key(map.Cloud.Points[c], options.Voxel))]));
        }

        // Join regions of different projectors that lie on one surface (union-find over the parts).
        var parent = Enumerable.Range(0, parts.Count).ToArray();
        int Find(int i)
        {
            while (parent[i] != i)
                i = parent[i] = parent[parent[i]];
            return i;
        }
        for (var a = 0; a < parts.Count; a++)
        {
            for (var b = a + 1; b < parts.Count; b++)
            {
                if (parts[a].Map == parts[b].Map)
                    continue;
                var (small, large) = parts[a].Voxels.Count <= parts[b].Voxels.Count ? (parts[a].Voxels, parts[b].Voxels) : (parts[b].Voxels, parts[a].Voxels);
                if (small.Count > 0 && (double)Near(small, large) / small.Count >= options.MinOverlap)
                    parent[Find(b)] = Find(a);
            }
        }

        var groups = Enumerable.Range(0, parts.Count).GroupBy(Find).Select(g => g.ToList()).ToList();
        var regions = new List<MultiProjectorRegion>();
        foreach (var g in groups)
        {
            // One part per projector: a projector with two regions on one surface gives their union.
            var byProjector = g.GroupBy(i => parts[i].Map).OrderBy(x => x.Key)
                .Select(x => new ProjectorRegionPart(maps[x.Key].ProjectorId, [.. x.SelectMany(i => parts[i].Cells).Order()]))
                .ToImmutableArray();
            var points = byProjector.SelectMany(p => p.Cells.Select(c => maps.First(m => m.ProjectorId == p.ProjectorId).Cloud.Points[c])).ToArray();
            var fit = PcaFit.Of(points, [.. Enumerable.Range(0, points.Length)]);
            var voxels = new HashSet<(long, long, long)>(g.SelectMany(i => parts[i].Voxels));
            regions.Add(new MultiProjectorRegion("", [.. byProjector.Select(p => p.ProjectorId)], byProjector, fit.Centroid, fit.Normal,
                fit.RmsDistance, fit.RmsDistance <= options.PlanarTolerance, voxels.Count));
        }
        return [.. regions.OrderByDescending(r => r.Voxels).ThenBy(r => r.Projectors[0], StringComparer.Ordinal)
            .Select((r, i) => r with { Id = "rg" + (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) })];
    }

    private static (long, long, long) Key(Vec3d p, double voxel) =>
        ((long)Math.Floor(p.X / voxel), (long)Math.Floor(p.Y / voxel), (long)Math.Floor(p.Z / voxel));

    /// <summary>How many of <paramref name="small"/>'s voxels have a voxel of <paramref name="large"/> at or next to them.</summary>
    private static int Near(HashSet<(long X, long Y, long Z)> small, HashSet<(long, long, long)> large)
    {
        var n = 0;
        foreach (var v in small)
        {
            var hit = false;
            for (var dx = -1; dx <= 1 && !hit; dx++)
                for (var dy = -1; dy <= 1 && !hit; dy++)
                    for (var dz = -1; dz <= 1 && !hit; dz++)
                        hit = large.Contains((v.X + dx, v.Y + dy, v.Z + dz));
            if (hit)
                n++;
        }
        return n;
    }
}
