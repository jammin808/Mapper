using PlayMap.Core.Vision;
using PlayMap.Vision.Segmentation;

namespace PlayMap.Vision.Tests;

/// <summary>A mask whose edge is off by a few pixels — as a model's often is — lands on the scan's depth jumps and
/// creases; a mask whose edge lies on flat surface stays where it is.</summary>
public class DepthSnapTests
{
    private static readonly BoxScene Scene = BoxScene.Build();

    private static bool[] Shift(bool[] mask, int w, int h, int dx, int dy)
    {
        var o = new bool[mask.Length];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                int sx = x - dx, sy = y - dy;
                if (sx >= 0 && sy >= 0 && sx < w && sy < h)
                    o[y * w + x] = mask[sy * w + sx];
            }
        return o;
    }

    [Fact]
    public void AnOffsetMaskEdgeMovesOntoTheDepthEdge()
    {
        int w = Scene.Image.Width, h = Scene.Image.Height;
        var top = Scene.FaceMask(BoxScene.Top);
        var off = Shift(top, w, h, 4, -3);
        var before = MaskOps.IoU(off, top);
        Assert.InRange(before, 0.0, 0.85);
        var mask = new SegmentationMask(w, h, MaskOps.ToCoverage(off), 0.6, "box top");
        var snapped = DepthSnap.Snap(mask, Scene.Points);
        var after = MaskOps.IoU(MaskOps.FromCoverage(snapped.Coverage), top);
        Assert.InRange(after, 0.95, 1.0);
        Assert.Equal("box top", snapped.Label);
        Assert.True(snapped.Score >= 0.6);
    }

    [Fact]
    public void AMaskEdgeOnFlatSurfaceStaysPut()
    {
        int w = Scene.Image.Width, h = Scene.Image.Height;
        // A disc painted on the floor, well away from the box: no scan edge near its boundary.
        var c = Scene.Project(new PlayMap.Core.Numerics.Vec3d(-0.9, 0, 0.6));
        var disc = new bool[w * h];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
                disc[y * w + x] = (x - c.X) * (x - c.X) + (y - c.Y) * (y - c.Y) <= 100;
        var snapped = DepthSnap.Snap(new SegmentationMask(w, h, MaskOps.ToCoverage(disc), 0.8, "decal"), Scene.Points);
        Assert.Equal(disc, MaskOps.FromCoverage(snapped.Coverage));
        Assert.Equal(0.8, snapped.Score);
    }

    [Fact]
    public void AMaskThinnerThanTheBandIsNeverDestroyed()
    {
        var small = BoxScene.Build(64, 48, 0, 0, 6);
        var top = small.FaceMask(BoxScene.Top);
        var off = Shift(top, 64, 48, 4, -3);
        var snapped = DepthSnap.Snap(new SegmentationMask(64, 48, MaskOps.ToCoverage(off), 0.5, "t"), small.Points);
        Assert.True(MaskOps.IoU(MaskOps.FromCoverage(snapped.Coverage), top) >= MaskOps.IoU(off, top) - 0.01);
        Assert.True(MaskOps.Count(MaskOps.FromCoverage(snapped.Coverage)) > 0);
    }
}
