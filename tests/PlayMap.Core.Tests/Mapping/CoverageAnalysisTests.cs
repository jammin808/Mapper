using PlayMap.Core.Geometry;
using PlayMap.Core.Mapping;
using PlayMap.Core.Model;
using PlayMap.Core.Numerics;
using Xunit.Abstractions;

namespace PlayMap.Core.Tests.Mapping;

/// <summary>The planner's coverage: how much of each surface is lit and by how many projectors, the pixel
/// density on it, and the areas nobody reaches — measured against what the geometry says they must be.</summary>
public class CoverageAnalysisTests(ITestOutputHelper output)
{
    private static (Stage Stage, Dictionary<string, MeshData> Meshes) Wall(bool box)
    {
        var meshes = new Dictionary<string, MeshData> { ["wall"] = Primitives.Plane(6, 3, 6, 3) };
        var surfaces = new List<Surface> { BlendSolver3DTests.SurfaceOf("wall", "wall") };
        if (box)
        {
            meshes["box"] = Primitives.Box(0.5, 0.5, 0.5);
            surfaces.Add(BlendSolver3DTests.SurfaceOf("box", "box", Transform3.Identity with { Position = new Vec3d(0, 0, 1.2) }));
        }
        var stage = Stage.Empty with
        {
            Surfaces = [.. surfaces],
            Projectors = [BlendSolver3DTests.ProjectorAt("p", new Vec3d(0, 0, 4), Vec3d.Zero)],
        };
        return (stage, meshes);
    }

    [Fact]
    public void OneProjectorLightsItsPictureAtItsPixelDensityAndTheRestIsNamed()
    {
        var (stage, meshes) = Wall(box: false);
        var report = CoverageAnalysis.Analyse(stage, id => meshes.GetValueOrDefault(id), options: new CoverageOptions { SampleSpacing = 0.03 });
        output.WriteLine(report.Words);
        var wall = report.For("wall")!;
        Assert.Equal(18, wall.Area, 1e-6);
        // A throw ratio of 1 from 4 m: a 4 × 2.25 m picture, half the wall.
        Assert.Equal(9.0 / 18, wall.CoveredFraction, 0.01);
        Assert.Equal(0.5, wall.FractionByCount[0], 0.01);
        Assert.Equal(0.5, wall.FractionByCount[1], 0.01);
        Assert.Equal(0.5, wall.FractionByProjector["p"], 0.01);
        // 1920 pixels across 4 m: 480 px/m — and the same in the corners, since a wall square to the lens is
        // a pure scaling of the raster (the longer throw there is met by the wider angle per pixel).
        Assert.Equal(480, wall.MaxDensity, 480 * 0.01);
        Assert.Equal(480, wall.MinDensity, 480 * 0.01);
        // The unlit ring round the picture is one area.
        Assert.Single(wall.Uncovered);
        Assert.Equal(9, wall.Uncovered[0].Area, 0.15);
    }

    [Fact]
    public void ABoxsShadowIsAnAreaNobodyReachesAndItsBackIsUnlit()
    {
        var (stage, meshes) = Wall(box: true);
        var report = CoverageAnalysis.Analyse(stage, id => meshes.GetValueOrDefault(id), options: new CoverageOptions { SampleSpacing = 0.03 });
        output.WriteLine(report.Words);
        var wall = report.For("wall")!;
        Assert.Equal(2, wall.Uncovered.Count);
        // The shadow: the box's front face (0.5 m wide at 2.55 m from the lens) cast onto the wall 4 m away.
        var shadow = wall.Uncovered.MinBy(a => a.Area)!;
        Assert.True(shadow.Centroid.Length < 0.05);
        var side = 0.5 * 4 / 2.55;
        Assert.Equal(side * side, shadow.Area, 0.05);
        Assert.Equal(-side / 2, shadow.Min.X, 0.05);
        Assert.Equal(side / 2, shadow.Max.X, 0.05);

        // Of the box, the projector sees the front only (its sides are edge-on, the back faces away).
        var box = report.For("box")!;
        Assert.Equal(1.5, box.Area, 1e-6);
        Assert.Equal(0.25 / 1.5, box.CoveredFraction, 0.02);
        Assert.Contains(box.Uncovered, a => a.Area > 1.2);
        Assert.All(report.Samples.Where(s => s.Projectors > 0 && s.Surface == 1), s => Assert.True(s.Point.Z > 1.44));
    }

    [Fact]
    public void TwoProjectorsShareTheMiddleAndTheCountsSayHowMuch()
    {
        var (stage, meshes, _) = BlendSolver3DTests.Room(box: false);
        var report = CoverageAnalysis.Analyse(stage, id => meshes.GetValueOrDefault(id), options: new CoverageOptions { SampleSpacing = 0.03 });
        output.WriteLine(report.Words);
        var wall = report.For("wall")!;
        // Each projector is turned 4.3° inward of its 26.6° half-angle, so its picture runs from −2.84 to
        // 1.19 m (the other's mirrored) and about 2.25 m tall: they share 2.38 m of width.
        Assert.Equal(2.38 * 2.25 / 18, wall.FractionByCount[2], 0.02);
        Assert.Equal(5.68 * 2.25 / 18, wall.CoveredFraction, 0.02);
        Assert.Equal(0, wall.FractionByCount[3], 1e-12);
        Assert.Equal(4.03 * 2.25 / 18, wall.FractionByProjector["pa"], 0.02);
        // Turned to the wall, each projector's density is no longer uniform: finer on its near side.
        Assert.True(wall.MaxDensity > wall.MinDensity * 1.05);
    }
}
