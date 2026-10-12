using PlayMap.Core.Geometry;
using PlayMap.Core.Model;
using PlayMap.Core.Numerics;
using PlayMap.Vision.Pins;

namespace PlayMap.Vision.Tests;

/// <summary>On a rendered box, the corner finder lands on the box's corners to sub-pixel accuracy and the pin
/// suggester pairs them with the right mesh corners from an approximate pose — every suggestion correct, the
/// visible corners covered, the pins spread.</summary>
public class PinSuggesterTests
{
    private static readonly BoxScene Scene = BoxScene.Build(320, 240, 0, 0, 3);

    private static readonly Vec3d[] VisibleBoxCorners =
    [
        new(-0.25, 0.3, -0.2), new(0.25, 0.3, -0.2), new(0.25, 0.3, 0.2), new(-0.25, 0.3, 0.2),
        new(-0.25, 0, 0.2), new(0.25, 0, 0.2), new(0.25, 0, -0.2),
    ];

    [Fact]
    public async Task TheCornerFinderLandsOnTheBoxCorners()
    {
        var features = await new CornerDetector().SuggestAsync(Scene.Shaded(), 60, CancellationToken.None);
        var errors = VisibleBoxCorners.Select(c => features.Min(f => Vec2d.Distance(f.Pixel, Scene.Project(c)))).ToList();
        // Sharp corners within a pixel (on an aliased render); the blunt back corner, where the side meets the
        // floor at 135°, within two.
        Assert.True(errors.Count(e => e <= 1.0) >= 6);
        Assert.All(errors, e => Assert.InRange(e, 0, 2.0));
    }

    [Fact]
    public async Task SuggestedPinsPairPictureCornersWithTheRightMeshCorners()
    {
        // The approximate pose: a couple of centimetres and half a degree off.
        var approx = Scene.Pose with
        {
            Position = Scene.Pose.Position + new Vec3d(0.02, -0.015, 0.01),
            Rotation = Scene.Pose.Rotation + new Vec3d(0.004, -0.006, 0.003),
        };
        var pins = await new PinSuggester().SuggestAsync(Scene.Shaded(), Scene.Mesh, Scene.Intrinsics, LensDistortion.None, approx, CancellationToken.None);
        Assert.InRange(pins.Count, 7, 12);
        // Every pair is right: the world point's true projection is where the picture's corner is.
        var errors = pins.Select(p => Vec2d.Distance(Scene.Project(p.World), p.Pixel)).Order().ToList();
        Assert.All(errors, e => Assert.InRange(e, 0, 2.0));
        Assert.InRange(errors[errors.Count / 2], 0, 0.6);
        foreach (var c in VisibleBoxCorners)
            Assert.Contains(pins, p => Vec3d.Distance(p.World, c) < 1e-6);
        Assert.DoesNotContain(pins, p => Vec3d.Distance(p.World, HiddenCorner) < 1e-6);
        Assert.All(pins, p => Assert.NotEqual(PinFeatureKind.Edge, p.Kind));
        Assert.True(pins[0].Score >= pins[^1].Score);
    }

    [Fact]
    public async Task EdgePinsWhenAskedLieOnTheEdgeAcrossWhichTheyFixThePicture()
    {
        var pins = await new PinSuggester(options: PinSuggesterOptions.Default with { IncludeEdgePins = true, MaxPins = 20 })
            .SuggestAsync(Scene.Shaded(), Scene.Mesh, Scene.Intrinsics, LensDistortion.None, Scene.Pose, CancellationToken.None);
        var edgePins = pins.Where(p => p.Kind == PinFeatureKind.Edge).ToList();
        Assert.NotEmpty(edgePins);
        foreach (var p in edgePins)
        {
            Assert.Equal(-1, p.Vertex);
            Assert.True(p.Score < pins[0].Score);
            // The world point is on a box or floor edge, and the picture point is within reach of that edge's
            // projection (an edge pin says nothing about where along it).
            Assert.True(OnBoxEdge(p.World), $"{p.World} is on an edge");
        }
    }

    private static bool OnBoxEdge(Vec3d p)
    {
        static bool Near(double a, double b) => Math.Abs(a - b) < 1e-4;
        var onX = Near(Math.Abs(p.X), 0.25) || Near(Math.Abs(p.X), 1.5);
        var onY = Near(p.Y, 0) || Near(p.Y, 0.3);
        var onZ = Near(Math.Abs(p.Z), 0.2) || Near(Math.Abs(p.Z), 1.5);
        return (onX ? 1 : 0) + (onY ? 1 : 0) + (onZ ? 1 : 0) >= 2;
    }

    /// <summary>The back-left bottom corner, behind the box from this camera.</summary>
    private static readonly Vec3d HiddenCorner = new(-0.25, 0, -0.2);

    [Fact]
    public void TheHiddenCornerIsAMeshFeatureTheRayCastRulesOut()
    {
        var hidden = HiddenCorner;
        var caster = new MeshRaycaster(Scene.Mesh);
        var to = hidden - Scene.Pose.Position;
        Assert.True(caster.Occluded(new Ray(Scene.Pose.Position, to.Normalized()), to.Length - 0.01));
        var features = MeshFeaturesProbe.Corners(Scene.Mesh);
        Assert.Contains(features, f => Vec3d.Distance(f, hidden) < 1e-6);
    }
}

/// <summary>Reads the mesh features the suggester sees (welded corners), for the hidden-corner test.</summary>
internal static class MeshFeaturesProbe
{
    public static List<Vec3d> Corners(MeshData mesh)
    {
        var f = MeshFeatures.Build(mesh, 30);
        return f.Vertices.Select(v => f.Position(v.Vertex)).ToList();
    }
}
