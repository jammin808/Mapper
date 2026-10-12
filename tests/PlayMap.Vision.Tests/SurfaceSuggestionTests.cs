using PlayMap.Core.Geometry;
using PlayMap.Core.Numerics;
using PlayMap.Core.Vision;
using PlayMap.Vision.Segmentation;
using PlayMap.Vision.Surfaces;

namespace PlayMap.Vision.Tests;

/// <summary>Masks become surfaces worth creating: the box top is a level plane of the right area named as a box
/// face, the floor is the floor, the faces under each mask come from the triangle map, a curved patch is curved,
/// a phrase names what it found, and the scan alone yields its surfaces with no click.</summary>
public class SurfaceSuggestionTests
{
    private static readonly BoxScene Scene = BoxScene.Build(240, 180, 0.0005, 2, 7);

    private static SegmentationMask MaskOf(int face, string label = "surface") =>
        new(Scene.Image.Width, Scene.Image.Height, MaskOps.ToCoverage(Scene.FaceMask(face)), 0.95, label);

    [Fact]
    public void TheBoxTopIsALevelPlaneOfItsAreaAndABoxFace()
    {
        var proposals = SurfaceSuggestion.Propose([MaskOf(BoxScene.Top), MaskOf(4), MaskOf(BoxScene.Floor)], Scene.Points, Scene.Triangle);
        var top = proposals[0];
        Assert.True(top.IsPlanar);
        Assert.True(Vec3d.Dot(top.Plane!.Normal, Vec3d.UnitY) > 0.995, "the top faces up, toward the camera");
        Assert.Equal(0.3, top.Plane.Centroid.Y, 2);
        Assert.InRange(top.AreaSquareMetres, 0.2 * 0.93, 0.2 * 1.07);
        Assert.Equal("box face", top.Name);
        Assert.Contains("top", top.Tags);
        Assert.Contains("box", top.Tags);
        // The top quad is triangles 2 and 3 of the mesh.
        Assert.Equal([2, 3], top.Faces.ToArray());

        var front = proposals[1];
        Assert.True(front.IsPlanar);
        Assert.True(Vec3d.Dot(front.Plane!.Normal, Vec3d.UnitZ) > 0.99, "the front faces +z, toward the camera");
        Assert.Equal("box face", front.Name);
        Assert.InRange(front.AreaSquareMetres, 0.15 * 0.9, 0.15 * 1.1);

        var floor = proposals[2];
        Assert.Equal("floor", floor.Name);
        Assert.Contains("horizontal", floor.Tags);
        Assert.Equal([0, 1], floor.Faces.ToArray());
    }

    [Fact]
    public void ACurvedPatchIsCurvedAndAPhraseNamesWhatItFound()
    {
        // A quarter cylinder, radius 0.5 m, seen from its axis' side: an organised grid of points on it.
        const int w = 60, h = 40;
        var pts = new Vec3d[w * h];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var a = (x / (double)(w - 1) - 0.5) * Math.PI / 2;
                pts[y * w + x] = new Vec3d(0.5 * Math.Sin(a), 1 - y * 0.02, 0.5 * Math.Cos(a) - 2);
            }
        var cloud = new PointCloud(pts, gridWidth: w, gridHeight: h);
        var all = new SegmentationMask(w, h, Enumerable.Repeat((byte)255, w * h).ToArray(), 0.9, "column");
        var p = Assert.Single(SurfaceSuggestion.Propose([all], cloud));
        Assert.False(p.IsPlanar);
        Assert.Equal("column", p.Name);
        Assert.Contains("curved surface", p.Tags);
        // A quarter of the circumference times the height: π/4 · 0.78.
        Assert.InRange(p.AreaSquareMetres, Math.PI / 4 * 0.78 * 0.95, Math.PI / 4 * 0.78 * 1.05);
    }

    [Fact]
    public void TheScanAloneYieldsItsSurfacesLargestFirst()
    {
        var masks = SurfaceSuggestion.AutoMasks(Scene.Points);
        Assert.InRange(masks.Count, 3, 6);
        Assert.InRange(MaskOps.IoU(MaskOps.FromCoverage(masks[0].Coverage), Scene.FaceMask(BoxScene.Floor)), 0.97, 1.0);
        Assert.Contains(masks, m => MaskOps.IoU(MaskOps.FromCoverage(m.Coverage), Scene.FaceMask(BoxScene.Top)) > 0.95);
        var names = SurfaceSuggestion.Propose(masks, Scene.Points).Select(s => s.Name).ToList();
        Assert.Equal("floor", names[0]);
        Assert.Contains("box face", names);
    }
}
