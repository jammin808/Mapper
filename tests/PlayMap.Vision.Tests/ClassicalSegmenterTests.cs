using PlayMap.Core.Geometry;
using PlayMap.Core.Model;
using PlayMap.Core.Numerics;
using PlayMap.Core.Vision;
using PlayMap.Vision.Segmentation;

namespace PlayMap.Vision.Tests;

/// <summary>Click-to-select with no model: on a scan, a click on a box's top takes the top and nothing else even
/// though box and floor are one colour; on a picture alone, the colour flood finds a textured region's own
/// tolerance, and a negative click takes back what a positive one leaked into.</summary>
public class ClassicalSegmenterTests
{
    private static readonly BoxScene Scene = BoxScene.Build();

    private static bool[] MaskOf(SegmentationResult r) => MaskOps.FromCoverage(r.Best!.Coverage);

    /// <summary>Mask pixels with no truth pixel in their 3×3 neighbourhood: a leak, not an edge pixel.</summary>
    internal static int Beyond(bool[] mask, bool[] truth, int w, int h)
    {
        var n = 0;
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                if (!mask[y * w + x] || truth[y * w + x])
                    continue;
                var near = false;
                for (var dy = -1; dy <= 1; dy++)
                    for (var dx = -1; dx <= 1; dx++)
                    {
                        int xx = x + dx, yy = y + dy;
                        near |= xx >= 0 && yy >= 0 && xx < w && yy < h && truth[yy * w + xx];
                    }
                if (!near)
                    n++;
            }
        return n;
    }

    [Fact]
    public async Task AClickOnTheBoxTopSelectsOnlyTheTopThanksToDepth()
    {
        var seg = new ClassicalSegmenter();
        var click = Scene.Project(new Vec3d(0, 0.3, 0));
        var r = await seg.SegmentAsync(Scene.Frame, SegmentationPrompt.Click(click.X, click.Y), CancellationToken.None);
        var mask = MaskOf(r);
        var top = Scene.FaceMask(BoxScene.Top);
        Assert.True(MaskOps.Count(top) > 400, "the top is a sizeable target");
        Assert.InRange(MaskOps.IoU(mask, top), 0.95, 1.0);
        // Nothing beyond the edge line: a pixel the mask takes from another face is one whose point lies on the
        // edge itself, next to a true top pixel.
        Assert.Equal(0, Beyond(mask, top, Scene.Image.Width, Scene.Image.Height));
        Assert.InRange(r.Best!.Score, 0.8, 1.0);
        Assert.Equal("surface", r.Best.Label);
    }

    [Fact]
    public void WithoutTheScanTheSameClickCannotSeparateSameColouredFaces()
    {
        var seg = new ClassicalSegmenter();
        var click = Scene.Project(new Vec3d(0, 0.3, 0));
        var r = seg.Segment(Scene.Image, SegmentationPrompt.Click(click.X, click.Y), CancellationToken.None);
        var mask = MaskOf(r);
        var floor = 0;
        for (var i = 0; i < mask.Length; i++)
            if (mask[i] && Scene.Face[i] == BoxScene.Floor)
                floor++;
        Assert.True(floor > 1000, "colour alone floods the floor too: depth is what separates them");
    }

    [Fact]
    public void AClickOnTheFloorStopsAtTheBoxAndGoesAroundIt()
    {
        var seg = new ClassicalSegmenter();
        var click = Scene.Project(new Vec3d(-0.9, 0, 0.6));
        var r = seg.Segment(Scene.Frame, SegmentationPrompt.Click(click.X, click.Y), CancellationToken.None);
        var mask = MaskOf(r);
        Assert.InRange(MaskOps.IoU(mask, Scene.FaceMask(BoxScene.Floor)), 0.95, 1.0);
    }

    [Fact]
    public void ANegativeClickTakesBackARegionThePositiveOneLeakedInto()
    {
        // Two red squares joined by a red bridge on grey: one click takes both; a negative click on the right
        // square gives it back.
        var img = Pictures.Filled(120, 80, 128, 128, 128);
        Pictures.Rect(img, 10, 20, 50, 60, 200, 40, 40);
        Pictures.Rect(img, 70, 20, 110, 60, 200, 40, 40);
        Pictures.Rect(img, 50, 37, 70, 43, 200, 40, 40);
        Pictures.Noise(img, 4, 3);
        var seg = new ClassicalSegmenter();
        var both = MaskOf(seg.Segment(img, SegmentationPrompt.Click(30, 40), CancellationToken.None));
        Assert.True(both[40 * 120 + 90], "the bridge carries the flood into the right square");
        Assert.True(both[40 * 120 + 30]);

        var prompt = SegmentationPrompt.Click(30, 40).WithClick(new Vec2d(90, 40), positive: false, previous: null);
        var mask = MaskOf(seg.Segment(img, prompt, CancellationToken.None));
        for (var y = 25; y < 55; y++)
        {
            for (var x = 15; x < 45; x++)
                Assert.True(mask[y * 120 + x]);
            for (var x = 75; x < 105; x++)
                Assert.False(mask[y * 120 + x]);
        }
        for (var y = 0; y < 80; y++)
            Assert.False(mask[y * 120 + 5]);
    }

    [Fact]
    public void TheColourToleranceAdaptsToATexturedSurface()
    {
        // A rough-textured disc on a background a moderate colour step away: a fixed small tolerance would take
        // specks of the disc, a fixed large one the background; the stable one takes the disc.
        var img = Pictures.Filled(100, 100, 90, 120, 160);
        for (var y = 0; y < 100; y++)
            for (var x = 0; x < 100; x++)
                if ((x - 50) * (x - 50) + (y - 50) * (y - 50) <= 30 * 30)
                    Pictures.Rect(img, x, y, x + 1, y + 1, 150, 120, 90);
        Pictures.Noise(img, 12, 9);
        var seg = new ClassicalSegmenter();
        var r = seg.Segment(img, SegmentationPrompt.Click(50, 50), CancellationToken.None);
        var mask = MaskOf(r);
        var disc = new bool[100 * 100];
        for (var y = 0; y < 100; y++)
            for (var x = 0; x < 100; x++)
                disc[y * 100 + x] = (x - 50) * (x - 50) + (y - 50) * (y - 50) <= 30 * 30;
        Assert.InRange(MaskOps.IoU(mask, disc), 0.93, 1.0);
        Assert.Equal("region", r.Best!.Label);
    }

    [Fact]
    public void AHoleInTheScanInsideTheTopIsFilledAndAnInvalidClickSnapsToTheSurface()
    {
        var pts = (Vec3d[])Scene.Points.Points.Clone();
        var c = Scene.Project(new Vec3d(0, 0.3, 0));
        int cx = (int)c.X, cy = (int)c.Y, w = Scene.Image.Width;
        for (var y = cy - 2; y <= cy + 2; y++)
            for (var x = cx - 2; x <= cx + 2; x++)
                pts[y * w + x] = PointCloud.Invalid;
        var frame = new VisionFrame(Scene.Image, new PointCloud(pts, gridWidth: w, gridHeight: Scene.Image.Height));
        var r = new ClassicalSegmenter().Segment(frame, SegmentationPrompt.Click(c.X, c.Y), CancellationToken.None);
        var mask = MaskOf(r);
        Assert.True(mask[cy * w + cx], "the dropped-out patch is filled");
        Assert.InRange(MaskOps.IoU(mask, Scene.FaceMask(BoxScene.Top)), 0.95, 1.0);
    }

    [Fact]
    public void ABoxPromptSelectsInsideTheBoxAndAPhraseAloneIsRefused()
    {
        var seg = new ClassicalSegmenter();
        var corners = new[] { new Vec3d(-0.25, 0.3, -0.2), new Vec3d(0.25, 0.3, -0.2), new Vec3d(0.25, 0.3, 0.2), new Vec3d(-0.25, 0.3, 0.2) }
            .Select(Scene.Project).ToArray();
        double x0 = corners.Min(p => p.X), x1 = corners.Max(p => p.X), y0 = corners.Min(p => p.Y), y1 = corners.Max(p => p.Y);
        var box = new RectD(x0, y0, x1 - x0, y1 - y0);
        var r = seg.Segment(Scene.Frame, SegmentationPrompt.ForBox(box), CancellationToken.None);
        Assert.InRange(MaskOps.IoU(MaskOf(r), Scene.FaceMask(BoxScene.Top)), 0.9, 1.0);

        var refused = seg.Segment(Scene.Frame, SegmentationPrompt.ForText("box"), CancellationToken.None);
        Assert.Null(refused.Best);
        Assert.Contains("phrase", refused.Words, StringComparison.Ordinal);
        Assert.Null(seg.Segment(Scene.Frame, SegmentationPrompt.Empty, CancellationToken.None).Best);
        Assert.Equal(SegmenterCapabilities.Points | SegmenterCapabilities.Box, seg.Capabilities);
    }
}
