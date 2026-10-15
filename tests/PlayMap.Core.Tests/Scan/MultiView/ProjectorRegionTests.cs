using System.Globalization;
using PlayMap.Core.Numerics;
using PlayMap.Core.Scan.MultiView;
using Xunit.Abstractions;

namespace PlayMap.Core.Tests.Scan.MultiView;

/// <summary>Regions per projector on its own raster, merged across projectors by 3D overlap: on the wall-and-cylinder
/// scene the wall, each piece of the floor and the cylinder are lit by both projectors and must each appear once,
/// listing both, with each projector's part on that projector's own depth grid.</summary>
public sealed class ProjectorRegionTests(ITestOutputHelper output)
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>The cylinder of the scene: axis through (0.1, y, 0.5), radius 0.25 m, 1.2 m tall.</summary>
    private static double FromCylinder(Vec3d p) => Math.Abs(new Vec2d(p.X - 0.1, p.Z - 0.5).Length - 0.25);

    [Fact]
    public void TheWallTheFloorPiecesAndTheCylinderEachAppearOnceWithBothProjectors()
    {
        var maps = MultiViewKit.WorldDense;
        var regions = ProjectorRegions.Build(maps);
        foreach (var r in regions)
        {
            output.WriteLine(string.Create(Inv,
                $"{r.Id}: [{string.Join(", ", r.Projectors)}] {r.Voxels} voxels, centroid {r.Centroid}, normal {r.Normal}, RMS {r.RmsDistance * 1000:0.0} mm{(r.Planar ? ", planar" : "")}; cells {string.Join(", ", r.Parts.Select(p => $"{p.ProjectorId} {p.Cells.Length}"))}"));
        }
        Assert.Equal(Enumerable.Range(1, regions.Length).Select(i => "rg" + i.ToString(Inv)), regions.Select(r => r.Id));
        Assert.True(regions.Zip(regions.Skip(1)).All(x => x.First.Voxels >= x.Second.Voxels));

        // Each projector's cell belongs to one region at most, and each part names a projector the region lists.
        foreach (var map in maps)
        {
            var cells = regions.SelectMany(r => r.On(map.ProjectorId)?.Cells ?? []).ToList();
            Assert.Equal(cells.Count, cells.Distinct().Count());
            Assert.All(cells, c => Assert.True(float.IsFinite(map.Depth[c])));
        }
        Assert.All(regions, r => Assert.Equal(r.Projectors.AsEnumerable(), r.Parts.Select(p => p.ProjectorId)));

        Vec3d PointOf(string projector, int cell) => maps.Single(m => m.ProjectorId == projector).Cloud.Points[cell];
        double ShareOn(MultiProjectorRegion r, Func<Vec3d, bool> on)
        {
            var points = r.Parts.SelectMany(p => p.Cells.Select(c => PointOf(p.ProjectorId, c))).ToList();
            return (double)points.Count(on) / points.Count;
        }

        // The wall (z = 0) and the floor (y = 0): one planar region each, lit by both projectors.
        var wall = Assert.Single(regions, r => ShareOn(r, p => Math.Abs(p.Z) < 0.01) > 0.9);
        Assert.Equal(["pj1", "pj2"], wall.Projectors.AsEnumerable());
        Assert.True(wall.Planar);
        Assert.True(Math.Abs(wall.Normal.Z) > 0.999, $"{wall.Normal}");
        // The floor's lit strip is cut in two by the cylinder's shadow: each piece is planar, and the largest piece on
        // either side of the cylinder is lit by both projectors.
        var floors = regions.Where(r => ShareOn(r, p => Math.Abs(p.Y) < 0.01) > 0.9).ToList();
        Assert.All(floors, f => Assert.True(f.Planar && Math.Abs(f.Normal.Y) > 0.999, $"{f.Id}: {f.Normal}"));
        foreach (var left in new[] { true, false })
        {
            var piece = floors.Where(f => f.Centroid.X < 0.1 == left).MaxBy(f => f.Voxels)!;
            Assert.Equal(["pj1", "pj2"], piece.Projectors.AsEnumerable());
        }

        // No surface appears twice: no region lies on another (under 20 % of the smaller's points within 1 cm of the larger's).
        var clouds = regions.Select(r => r.Parts.SelectMany(p => p.Cells.Select(c => PointOf(p.ProjectorId, c))).ToList()).ToList();
        var trees = clouds.Select(c => new Geometry.KdTree3(c)).ToList();
        for (var a = 0; a < regions.Length; a++)
        {
            for (var b = 0; b < regions.Length; b++)
            {
                if (a == b || clouds[a].Count > clouds[b].Count)
                    continue;
                var on = clouds[a].Count(p => { trees[b].Nearest(p, out var d2); return d2 < 0.01 * 0.01; });
                Assert.True(on < 0.2 * clouds[a].Count, $"{regions[a].Id} lies on {regions[b].Id}: {on} of {clouds[a].Count}");
            }
        }

        // The cylinder's side: one curved region, lit by both, holding nearly all of what either projector measured there.
        var side = Assert.Single(regions, r => ShareOn(r, p => FromCylinder(p) < 0.01 && p.Y < 1.19) > 0.9);
        Assert.Equal(["pj1", "pj2"], side.Projectors.AsEnumerable());
        Assert.False(side.Planar);
        foreach (var map in maps)
        {
            bool OnSide(int c) => float.IsFinite(map.Depth[c]) && FromCylinder(map.Cloud.Points[c]) < 0.005 && map.Cloud.Points[c].Y is > 0.02 and < 1.18;
            var onSide = Enumerable.Range(0, map.Depth.Length).Count(OnSide);
            var inRegion = side.On(map.ProjectorId)!.Cells.Count(OnSide);
            output.WriteLine(string.Create(Inv, $"{map.ProjectorId}: {inRegion} of {onSide} cylinder-side cells in {side.Id}"));
            Assert.True(inRegion >= 0.9 * onSide, $"{map.ProjectorId}: {inRegion} of {onSide}");
        }
    }

    [Fact]
    public void ProjectorsThatShareNoSurfaceKeepTheirRegionsApart()
    {
        // One projector's map against a copy of itself moved 2 m away: nothing overlaps, so nothing joins.
        var map = MultiViewKit.WorldDense[0];
        var moved = map with
        {
            ProjectorId = "far",
            Cloud = new Geometry.PointCloud([.. map.Cloud.Points.Select(p => p + new Vec3d(0, 0, 2))], map.Cloud.Normals,
                gridWidth: map.Cloud.GridWidth, gridHeight: map.Cloud.GridHeight),
        };
        var both = ProjectorRegions.Build([map, moved]);
        var alone = ProjectorRegions.Build([map]);
        Assert.All(both, r => Assert.Single(r.Projectors));
        Assert.Equal(2 * alone.Length, both.Length);
        Assert.Equal(alone.Select(r => r.Voxels).Order(), both.Where(r => r.Projectors[0] == map.ProjectorId).Select(r => r.Voxels).Order());
    }
}
