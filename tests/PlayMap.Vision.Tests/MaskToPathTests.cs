using PlayMap.Core.Numerics;
using PlayMap.Core.Vision;
using PlayMap.Vision.Segmentation;
using PlayMap.Vision.Vectorise;

namespace PlayMap.Vision.Tests;

/// <summary>A mask becomes a path the pen tool can edit: a rectangle is four corner nodes on its corners, a disc
/// is smooth nodes whose curves stay within the tolerance of the circle, a ring is an outline and a hole, and a
/// shape with both straight sides and a round end keeps both kinds of node.</summary>
public class MaskToPathTests
{
    private static SegmentationMask Mask(int w, int h, Func<int, int, bool> inside)
    {
        var c = new byte[w * h];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
                c[y * w + x] = inside(x, y) ? (byte)255 : (byte)0;
        return new SegmentationMask(w, h, c, 1, "");
    }

    [Fact]
    public void ARectangleIsFourCornerNodesOnItsCorners()
    {
        var v = MaskToPath.Vectorise(Mask(100, 80, (x, y) => x >= 20 && x < 60 && y >= 30 && y < 70));
        var path = Assert.Single(v.Outlines);
        Assert.Empty(v.Holes);
        Assert.Equal(4, path.Nodes.Length);
        Assert.All(path.Nodes, n => Assert.False(n.Smooth));
        Assert.True(path.Closed);
        var expected = new[] { new Vec2d(0.2, 30 / 80.0), new Vec2d(0.6, 30 / 80.0), new Vec2d(0.6, 70 / 80.0), new Vec2d(0.2, 70 / 80.0) };
        foreach (var e in expected)
            Assert.Contains(path.Nodes, n => Math.Abs(n.Point.X - e.X) * 100 < 0.5 && Math.Abs(n.Point.Y - e.Y) * 80 < 0.5);
        // Straight sides: no handles.
        Assert.All(path.Nodes, n => Assert.True(n.In.Length < 1e-9 && n.Out.Length < 1e-9));
        // Clockwise on screen (y down): positive shoelace area.
        Assert.True(MaskToPath.SignedArea(path.Nodes.Select(n => n.Point).ToList()) > 0);
    }

    [Fact]
    public void ADiscIsSmoothNodesWithinTheTolerance()
    {
        const double r = 25;
        var v = MaskToPath.Vectorise(Mask(100, 100, (x, y) => (x + 0.5 - 50) * (x + 0.5 - 50) + (y + 0.5 - 50) * (y + 0.5 - 50) <= r * r));
        var path = Assert.Single(v.Outlines);
        Assert.All(path.Nodes, n => Assert.True(n.Smooth));
        Assert.InRange(path.Nodes.Length, 2, 16);
        foreach (var p in MaskToPath.Sample(path, 24))
        {
            var d = new Vec2d(p.X * 100 - 50, p.Y * 100 - 50).Length;
            Assert.InRange(d, r - 1.0, r + 1.0);
        }
        // Smooth nodes keep their handles collinear.
        foreach (var n in path.Nodes)
            Assert.True(Math.Abs(Vec2d.Cross(n.In.Normalized(), n.Out.Normalized())) < 0.05);
    }

    [Fact]
    public void ARingIsAnOutlineAndAHole()
    {
        var v = MaskToPath.Vectorise(Mask(100, 100, (x, y) =>
        {
            var d2 = (x + 0.5 - 50) * (x + 0.5 - 50) + (y + 0.5 - 50) * (y + 0.5 - 50);
            return d2 <= 40 * 40 && d2 >= 20 * 20;
        }));
        Assert.Single(v.Outlines);
        var hole = Assert.Single(v.Holes);
        Assert.True(MaskToPath.SignedArea(hole.Nodes.Select(n => n.Point).ToList()) < 0);
        foreach (var p in MaskToPath.Sample(hole, 16))
            Assert.InRange(new Vec2d(p.X * 100 - 50, p.Y * 100 - 50).Length, 19, 21);
    }

    [Fact]
    public void AStadiumKeepsCornerlessStraightsAndRoundEnds()
    {
        // A rectangle with a half-disc on its right end: two sharp corners on the left, smooth on the right.
        var v = MaskToPath.Vectorise(Mask(120, 60, (x, y) =>
        {
            double px = x + 0.5, py = y + 0.5;
            return (px >= 20 && px <= 70 && py >= 15 && py <= 45) || (px - 70) * (px - 70) + (py - 30) * (py - 30) <= 15 * 15;
        }));
        var path = Assert.Single(v.Outlines);
        Assert.Equal(2, path.Nodes.Count(n => !n.Smooth));
        Assert.Contains(path.Nodes, n => n.Smooth);
        foreach (var p in MaskToPath.Sample(path, 24))
        {
            double px = p.X * 120, py = p.Y * 60;
            // Every sample lies on the shape's edge: the left side, the top/bottom, or the round end.
            var dEnd = Math.Abs(new Vec2d(px - 70, py - 30).Length - 15);
            var dFlat = px <= 71 ? Math.Min(Math.Min(Math.Abs(py - 15), Math.Abs(py - 45)), Math.Abs(px - 20)) : double.MaxValue;
            Assert.InRange(Math.Min(dEnd, dFlat), 0, 1.0);
        }
    }

    [Fact]
    public void SpecksAreDroppedAndPiecesComeLargestFirst()
    {
        var v = MaskToPath.Vectorise(Mask(100, 50, (x, y) =>
            (x >= 5 && x < 15 && y >= 5 && y < 15) || (x >= 40 && x < 90 && y >= 10 && y < 40) || (x == 30 && y == 30)));
        Assert.Equal(2, v.Outlines.Length);
        Assert.True(v.Outlines[0].Nodes.Max(n => n.Point.X) > 0.85);
        Assert.Empty(MaskToPath.Vectorise(Mask(10, 10, (x, y) => false)).Outlines);
        Assert.NotNull(MaskOps.FromCoverage(new byte[1]));
    }
}
