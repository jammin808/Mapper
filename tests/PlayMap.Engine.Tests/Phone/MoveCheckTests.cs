using PlayMap.Core.Numerics;
using PlayMap.Core.Phone;
using PlayMap.Engine.Scanning;

namespace PlayMap.Engine.Tests.Phone;

/// <summary>The end check's measure on its own: a tag place's shift between two all-cells-lit pictures, read to a fraction of a
/// pixel on a dark background (lock) and on a lit one (steady), and a place too near the picture's edge to search left out rather
/// than called moved.</summary>
public sealed class MoveCheckTests
{
    private const int W = 200;
    private const int H = 120;

    /// <summary>A site's square of <paramref name="size"/> px at (<paramref name="x0"/> + <paramref name="dx"/>, <paramref name="y0"/>),
    /// each pixel the share of it the square covers, between the background and the lit level.</summary>
    private static byte[] Picture(double x0, double y0, double size, double background, double lit, double dx = 0, double dy = 0) =>
        Picture(x0, y0, size, size, background, lit, dx, dy);

    private static byte[] Picture(double x0, double y0, double sizeX, double sizeY, double background, double lit, double dx, double dy)
    {
        var g = new byte[W * H];
        for (var y = 0; y < H; y++)
        {
            for (var x = 0; x < W; x++)
            {
                var cx = Math.Max(0, Math.Min(x + 1, x0 + dx + sizeX) - Math.Max(x, x0 + dx));
                var cy = Math.Max(0, Math.Min(y + 1, y0 + dy + sizeY) - Math.Max(y, y0 + dy));
                g[y * W + x] = (byte)Math.Round(background + (lit - background) * cx * cy);
            }
        }
        return g;
    }

    /// <summary>A site of 4 × 4 cells of <paramref name="cell"/> px whose square starts at (x0, y0).</summary>
    private static TagSitePlacement Site(double x0, double y0, double cell) => Site(x0, y0, cell, cell);

    /// <summary>A site whose cells are <paramref name="cellX"/> × <paramref name="cellY"/> px in the picture (a surface seen aslant).</summary>
    private static TagSitePlacement Site(double x0, double y0, double cellX, double cellY) =>
        new(0, [.. Enumerable.Range(0, 16).Select(c => new Vec2d(x0 + (c % 4 + 0.5) * cellX, y0 + (c / 4 + 0.5) * cellY))],
            (int)y0, (int)(y0 + 4 * cellY));

    [Fact]
    public void ASiteSeenAslantIsMeasuredWithEachAxisItsOwnStep()
    {
        // Cells three times as tall as wide: a window sized by the width alone would sit inside the lit quad and read noise.
        var site = Site(80, 20, 6, 18);
        var before = Picture(80, 20, 24, 72, 10, 230, 0, 0);
        Assert.True(ScanRunner.MovedPx(before, Picture(80, 20, 24, 72, 12, 236, 0, 0), [site], W, H) < 0.2);
        var moved = ScanRunner.MovedPx(before, Picture(80, 20, 24, 72, 10, 230, 0, 1.5), [site], W, H);
        Assert.True(moved is { } v && Math.Abs(v - 1.5) < 0.2, $"read {moved}");
    }

    [Theory]
    [InlineData(10, 230)] // lock: a dark background
    [InlineData(120, 230)] // steady: a lit background between the cells
    public void AShiftIsReadToAFractionOfAPixelWhateverTheBackground(double background, double lit)
    {
        var site = Site(80, 40, 6);
        var before = Picture(80, 40, 24, background, lit);
        foreach (var shift in new[] { 0.0, 0.6, 1.3, 2.0 })
        {
            var after = Picture(80, 40, 24, background, lit, shift, shift / 2);
            var px = MoveCheck(before, after, site);
            var truth = Math.Sqrt(shift * shift + shift * shift / 4);
            Assert.True(px is { } v && Math.Abs(v - truth) < 0.2, $"shift {truth:0.00} read {px}");
        }
        // A brightness change alone moves nothing.
        Assert.True(MoveCheck(before, Picture(80, 40, 24, background * 0.8, lit * 0.8), site) < 0.1);
    }

    [Fact]
    public void APlaceTooNearThePicturesEdgeIsLeftOutNotCalledMoved()
    {
        var edge = Site(2, 40, 6);
        var still = Picture(2, 40, 24, 10, 230);
        Assert.Null(ScanRunner.MovedPx(still, still, [edge], W, H));
        // Beside a place that can be measured, it changes nothing.
        var inner = Site(120, 40, 6);
        Assert.True(ScanRunner.MovedPx(Picture(120, 40, 24, 10, 230), Picture(120, 40, 24, 10, 230), [edge, inner], W, H) < 0.1);
        // A place that moved beyond the search is gone (infinite).
        Assert.Equal(double.PositiveInfinity, MoveCheck(Picture(80, 40, 24, 10, 230), Picture(80, 40, 24, 10, 230, 5), Site(80, 40, 6)));
    }

    private static double? MoveCheck(byte[] before, byte[] after, TagSitePlacement site) => ScanRunner.MovedPx(before, after, [site], W, H);
}
