using System.Diagnostics;
using PlayMap.Core.Mapping;
using PlayMap.Core.Model;
using PlayMap.Core.Numerics;
using Xunit.Abstractions;

namespace PlayMap.Core.Tests.Mapping;

/// <summary>Soft masks rasterised: a feather is a smooth step centred on the outline, the modes combine in
/// order, inversion flips, the fast scanline path agrees with the exact per-pixel rule, and a full-HD
/// mask costs milliseconds.</summary>
public class MaskRasterizerTests(ITestOutputHelper output)
{
    private static Mask M(BezierPath path, MaskMode mode = MaskMode.Add, double feather = 0, bool invert = false, bool enabled = true) =>
        new("m", "mask", path, mode, feather, invert, enabled);

    [Fact]
    public void AFeatheredEdgeIsHalfOnTheOutlineAndRisesMonotonicallyAcrossIt()
    {
        // A 200 × 100 raster; the rectangle's left edge sits at x = 50 px; feather 20 px.
        var mask = M(BezierPath.Rectangle(0.25, 0.25, 0.5, 0.5), feather: 20);
        var map = MaskRasterizer.RasterizeOutputMasks([mask], 200, 100, 200, 100);
        var row = 50;
        for (var x = 30; x < 70; x++)
            Assert.True(map[row * 200 + x + 1] >= map[row * 200 + x], $"coverage fell from x={x} to {x + 1}");
        Assert.Equal(0f, map[row * 200 + 39]);
        Assert.Equal(1f, map[row * 200 + 61]);
        // Pixels 49 and 50 straddle the outline symmetrically: their mean is a half.
        Assert.Equal(0.5, (map[row * 200 + 49] + map[row * 200 + 50]) / 2, 6);
        Assert.Equal(0.5, MaskRasterizer.CoverageAt(mask, 200, 100, 1, new Vec2d(50, 50)), 12);
        Assert.Equal(1f, map[50 * 200 + 100]);
        Assert.Equal(0f, map[5 * 200 + 5]);
    }

    [Fact]
    public void AHardEdgeIsAntiAliasedOverOnePixel()
    {
        // The edge at x = 50.25 px: pixel 49 ([49, 50]) is wholly outside, pixel 50 ([50, 51]) three
        // quarters inside — its centre is a quarter of a pixel in, and the one-pixel ramp reads 0.75.
        var mask = M(BezierPath.Rectangle(50.25 / 200, 0, 0.5, 1));
        var map = MaskRasterizer.RasterizeOutputMasks([mask], 200, 10, 200, 10);
        Assert.Equal(0f, map[5 * 200 + 48]);
        Assert.Equal(0f, map[5 * 200 + 49]);
        Assert.Equal(0.75, map[5 * 200 + 50], 5);
        Assert.Equal(1f, map[5 * 200 + 51]);
    }

    [Fact]
    public void SubtractIntersectAndInvertCombineInOrder()
    {
        const int w = 100, h = 100;
        var outer = M(BezierPath.Rectangle(0.1, 0.1, 0.8, 0.8));
        var hole = M(BezierPath.Rectangle(0.4, 0.4, 0.2, 0.2), MaskMode.Subtract);
        var left = M(BezierPath.Rectangle(0, 0, 0.5, 1), MaskMode.Intersect);
        float At(float[] m, int x, int y) => m[y * w + x];

        var ring = MaskRasterizer.RasterizeOutputMasks([outer, hole], w, h, w, h);
        Assert.Equal(1f, At(ring, 20, 20));
        Assert.Equal(0f, At(ring, 50, 50));
        Assert.Equal(0f, At(ring, 5, 5));

        var halfRing = MaskRasterizer.RasterizeOutputMasks([outer, hole, left], w, h, w, h);
        Assert.Equal(1f, At(halfRing, 20, 20));
        Assert.Equal(0f, At(halfRing, 80, 20));

        // An inverted add of a small square shows everything but the square.
        var inverted = MaskRasterizer.RasterizeOutputMasks([M(BezierPath.Rectangle(0.4, 0.4, 0.2, 0.2), invert: true)], w, h, w, h);
        Assert.Equal(0f, At(inverted, 50, 50));
        Assert.Equal(1f, At(inverted, 10, 10));

        // A list that starts by subtracting starts from everything.
        var cut = MaskRasterizer.RasterizeOutputMasks([hole], w, h, w, h);
        Assert.Equal(0f, At(cut, 50, 50));
        Assert.Equal(1f, At(cut, 10, 10));

        // No masks, or only disabled ones, show everything.
        Assert.All(MaskRasterizer.RasterizeOutputMasks([], 8, 8, 8, 8), v => Assert.Equal(1f, v));
        Assert.All(MaskRasterizer.RasterizeOutputMasks([outer with { Enabled = false }], 8, 8, 8, 8), v => Assert.Equal(1f, v));
    }

    [Fact]
    public void TwoFeatheredMasksThatAbutSumToOneAcrossTheSeam()
    {
        var a = M(BezierPath.Rectangle(-0.5, -0.5, 1.0, 2), feather: 16);
        var b = M(BezierPath.Rectangle(0.5, -0.5, 1.0, 2), feather: 16);
        var map = MaskRasterizer.RasterizeOutputMasks([a, b], 128, 16, 128, 16);
        for (var x = 40; x < 88; x++)
            Assert.Equal(1, map[8 * 128 + x], 5);
    }

    [Fact]
    public void TheScanlinePathAgreesWithTheExactPerPixelRule()
    {
        var blob = BezierToolsTests.Circle(new Vec2d(0.5, 0.45), 0.3, 5);
        var masks = new[] { M(blob, feather: 12), M(BezierPath.Rectangle(0.45, 0.2, 0.3, 0.3), MaskMode.Subtract, feather: 3, invert: false) };
        const int w = 160, h = 90;
        var map = MaskRasterizer.RasterizeOutputMasks(masks, w, h, w, h);
        var rng = new Pcg32(29);
        for (var k = 0; k < 2000; k++)
        {
            var x = rng.NextInt(w);
            var y = rng.NextInt(h);
            var centre = new Vec2d(x + 0.5, y + 0.5);
            var expected = Math.Max(0, Math.Min(1, MaskRasterizer.CoverageAt(masks[0], w, h, 1, centre)) - MaskRasterizer.CoverageAt(masks[1], w, h, 1, centre));
            Assert.Equal(expected, map[y * w + x], 1e-5);
        }
    }

    [Fact]
    public void ASurfaceMaskFeathersInUvUnits()
    {
        // Tall enough that the top and bottom edges' bands miss the sampled row.
        var mask = M(BezierPath.Rectangle(0.25, -2, 0.5, 5), feather: 0.1);
        var map = MaskRasterizer.RasterizeSurfaceMasks([mask], 256, 4);
        // The band is 0.1 × (256 + 4)/2 = 13 px wide about x = 64.
        Assert.Equal(0f, map[2 * 256 + 56]);
        Assert.True(map[2 * 256 + 60] is > 0f and < 0.5f);
        Assert.True(map[2 * 256 + 67] is > 0.5f and < 1f);
        Assert.Equal(1f, map[2 * 256 + 72]);
    }

    [Fact]
    public void AFullHdFeatheredMaskTakesMilliseconds()
    {
        var masks = new[]
        {
            M(BezierToolsTests.Circle(new Vec2d(0.5, 0.5), 0.4, 8), feather: 40),
            M(BezierPath.Rectangle(0.3, 0.3, 0.2, 0.2), MaskMode.Subtract, feather: 10),
        };
        var map = new float[1920 * 1080];
        MaskRasterizer.Rasterize(masks, 1920, 1080, 1, map); // warm the JIT
        // The best of a few runs: the machine is shared, the rasteriser's own cost is the floor.
        var ms = double.MaxValue;
        for (var i = 0; i < 5; i++)
        {
            var sw = Stopwatch.StartNew();
            MaskRasterizer.Rasterize(masks, 1920, 1080, 1, map);
            ms = Math.Min(ms, sw.Elapsed.TotalMilliseconds);
        }
        output.WriteLine($"1920×1080, two feathered masks: {ms:0.0} ms");
        Assert.True(ms < 200, $"took {ms} ms");
        Assert.Equal(1f, map[540 * 1920 + 300]);
    }
}
