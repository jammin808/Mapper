using PlayMap.Core.Mapping;
using PlayMap.Core.Model;
using PlayMap.Core.Numerics;

namespace PlayMap.Core.Tests.Mapping;

/// <summary>A quad drawn in a projector's raster: its corners are the homography's images of the square's corners and back, a
/// lattice adds offsets that land each node exactly where it was dragged, a lattice given to a quad keeps its shape, and a
/// raster point inverts to its content point through both.</summary>
public class PixelQuadMathTests
{
    private const int W = 1920;
    private const int H = 1080;

    private static PixelQuad Quad(WarpLattice? lattice = null) =>
        new("pj_1", new Vec2d(0.20, 0.15), new Vec2d(0.78, 0.22), new Vec2d(0.70, 0.88), new Vec2d(0.25, 0.80), lattice);

    [Fact]
    public void TheCornersAreTheSquaresCornersThroughTheHomographyAndBack()
    {
        var q = Quad();
        var shape = PixelQuadShape.Of(q)!;
        Assert.Equal(0, (shape.Evaluate(0, 0) - q.TopLeft).Length, 12);
        Assert.Equal(0, (shape.Evaluate(1, 0) - q.TopRight).Length, 12);
        Assert.Equal(0, (shape.Evaluate(1, 1) - q.BottomRight).Length, 12);
        Assert.Equal(0, (shape.Evaluate(0, 1) - q.BottomLeft).Length, 12);
        for (var i = 0; i < 4; i++)
        {
            var uv = shape.Invert(PixelQuadMath.Corner(q, i))!.Value;
            var expected = i switch { 0 => new Vec2d(0, 0), 1 => new Vec2d(1, 0), 2 => new Vec2d(1, 1), _ => new Vec2d(0, 1) };
            Assert.Equal(0, (uv - expected).Length, 10);
        }
    }

    [Fact]
    public void AQuadIsProjectiveNotAffineAcrossItsDiagonal()
    {
        // The centre of the square lands on the intersection of the quad's diagonals (a projective map keeps lines), not on the
        // mean of the corners (where an affine split of two triangles would put it).
        var q = Quad();
        var shape = PixelQuadShape.Of(q)!;
        var centre = shape.Evaluate(0.5, 0.5);
        var x = Intersect(q.TopLeft, q.BottomRight, q.TopRight, q.BottomLeft);
        Assert.Equal(0, (centre - x).Length, 12);
        var mean = (q.TopLeft + q.TopRight + q.BottomRight + q.BottomLeft) * 0.25;
        Assert.True((centre - mean).Length * W > 1, "this quad's centre differs from its corners' mean by more than a pixel");
    }

    [Fact]
    public void ALatticeAtRestKeepsTheShapeAndANodeLandsWhereItWasDragged()
    {
        var plain = Quad();
        var (latticed, change) = PixelQuadMath.WithLattice(plain, 3, 3, WarpInterpolation.CatmullRom, W, H);
        Assert.Equal(0, change, 9);
        var shape = PixelQuadShape.Of(latticed)!;
        var before = PixelQuadShape.Of(plain)!;
        for (var j = 0; j <= 10; j++)
            for (var i = 0; i <= 10; i++)
                Assert.Equal(0, (shape.Evaluate(i / 10.0, j / 10.0) - before.Evaluate(i / 10.0, j / 10.0)).Length, 12);

        var target = new Vec2d(0.52, 0.47);
        var moved = PixelQuadMath.WithNode(latticed, 1, 1, target);
        var after = PixelQuadShape.Of(moved)!;
        Assert.Equal(0, (after.Evaluate(0.5, 0.5) - target).Length, 12);
        Assert.Equal(0, (PixelQuadMath.Node(after, 1, 1) - target).Length, 12);
        // The offset is the target less the corners' own point for the node's rest.
        Assert.Equal(0, (moved.Lattice!.Offset(1, 1) - (target - before.Plain(0.5, 0.5))).Length, 12);
        // The corners stay where they were: the border nodes did not move.
        Assert.Equal(0, (after.Evaluate(0, 0) - plain.TopLeft).Length, 12);
        Assert.Equal(0, (after.Evaluate(1, 1) - plain.BottomRight).Length, 12);
    }

    [Fact]
    public void MovingACornerCarriesTheLatticeWithIt()
    {
        var (latticed, _) = PixelQuadMath.WithLattice(Quad(), 3, 3, WarpInterpolation.Linear, W, H);
        var bent = PixelQuadMath.WithNode(latticed, 1, 1, new Vec2d(0.55, 0.45));
        var offset = bent.Lattice!.Offset(1, 1);
        var moved = PixelQuadMath.WithCorner(bent, 2, new Vec2d(0.74, 0.92));
        var shape = PixelQuadShape.Of(moved)!;
        Assert.Equal(0, (shape.Evaluate(1, 1) - new Vec2d(0.74, 0.92)).Length, 12);
        Assert.Equal(0, (PixelQuadMath.Node(shape, 1, 1) - (shape.Plain(0.5, 0.5) + offset)).Length, 12);
    }

    [Fact]
    public void ARasterPointInvertsThroughTheLattice()
    {
        var (latticed, _) = PixelQuadMath.WithLattice(Quad(), 4, 3, WarpInterpolation.CatmullRom, W, H);
        var bent = PixelQuadMath.WithNode(latticed, 2, 1, new Vec2d(0.60, 0.41));
        var shape = PixelQuadShape.Of(bent)!;
        foreach (var (u, v) in new[] { (0.1, 0.2), (0.5, 0.5), (0.66, 0.4), (0.9, 0.95) })
        {
            var p = shape.Evaluate(u, v);
            var back = shape.Invert(p)!.Value;
            Assert.Equal(u, back.X, 9);
            Assert.Equal(v, back.Y, 9);
        }
    }

    [Fact]
    public void ResizingABentLatticeReportsHowFarTheShapeMoved()
    {
        var (latticed, _) = PixelQuadMath.WithLattice(Quad(), 3, 3, WarpInterpolation.CatmullRom, W, H);
        var bent = PixelQuadMath.WithNode(latticed, 1, 1, new Vec2d(0.55, 0.45));
        var (finer, change) = PixelQuadMath.WithLattice(bent, 5, 5, WarpInterpolation.CatmullRom, W, H);
        Assert.Equal(5, finer.Lattice!.Columns);
        // The old field sampled at the new nodes: every new node sits on the old surface.
        var old = PixelQuadShape.Of(bent)!;
        var shape = PixelQuadShape.Of(finer)!;
        for (var j = 0; j < 5; j++)
            for (var i = 0; i < 5; i++)
                Assert.Equal(0, (PixelQuadMath.Node(shape, i, j) - old.Evaluate(i / 4.0, j / 4.0)).Length, 12);
        Assert.InRange(change, 0, 20);
        var (off, offChange) = PixelQuadMath.WithLattice(bent, 0, 0, WarpInterpolation.Linear, W, H);
        Assert.Null(off.Lattice);
        Assert.True(offChange > 1, $"dropping a bent lattice moves the shape ({offChange} px)");
    }

    [Fact]
    public void FourClicksInAnyOrderBecomeTopLeftFirstClockwise()
    {
        Vec2d tl = new(100, 120), tr = new(900, 100), br = new(950, 700), bl = new(80, 650);
        var orders = new[] { new[] { br, tl, bl, tr }, new[] { tr, bl, tl, br }, new[] { bl, br, tr, tl } };
        foreach (var clicks in orders)
        {
            var (a, b, c, d) = PixelQuadMath.Order(clicks);
            Assert.Equal(tl, a);
            Assert.Equal(tr, b);
            Assert.Equal(br, c);
            Assert.Equal(bl, d);
        }
    }

    [Fact]
    public void CrossedOrTinyQuadsAreRefusedWithTheReason()
    {
        var q = Quad();
        Assert.Null(PixelQuadMath.Check(q.TopLeft, q.TopRight, q.BottomRight, q.BottomLeft, W, H));
        Assert.Contains("cross", PixelQuadMath.Check(q.TopLeft, q.BottomRight, q.TopRight, q.BottomLeft, W, H), StringComparison.Ordinal);
        var p = 1.0 / W;
        Assert.Contains("px²", PixelQuadMath.Check(new Vec2d(0.5, 0.5), new Vec2d(0.5 + 5 * p, 0.5), new Vec2d(0.5 + 5 * p, 0.5 + 5.0 / H),
            new Vec2d(0.5, 0.5 + 5.0 / H), W, H), StringComparison.Ordinal);
        Assert.Equal(-1, PixelQuadMath.CornerIndex("XX"));
        Assert.Equal(2, PixelQuadMath.CornerIndex("br"));
        Assert.Equal(3, PixelQuadMath.CornerIndex("3"));
    }

    [Fact]
    public void TheDrawnVerticesCarryHomogeneousContentCoordinates()
    {
        var shape = PixelQuadShape.Of(Quad())!;
        var v = new float[PixelQuadMath.VertexCount(shape.Quad) * PixelQuadMath.VertexFloats];
        Assert.Equal(6, PixelQuadMath.WriteVertices(shape, W, H, v));
        // Vertex 2 is (u, v) = (1, 1): the bottom-right corner in pixels, and (s, t) / q = (1, 1).
        var o = 2 * PixelQuadMath.VertexFloats;
        Assert.Equal(0.70 * W, v[o], 2);
        Assert.Equal(0.88 * H, v[o + 1], 2);
        Assert.Equal(1, v[o + 2] / v[o + 4], 5);
        Assert.Equal(1, v[o + 3] / v[o + 4], 5);
        // Interpolating (s, t, q) linearly along the diagonal and dividing gives the homography's own (u, v) there.
        var a = 0;
        var mid = 0.37f;
        float S(int k) => v[a + k] + (v[o + k] - v[a + k]) * mid;
        var px = new Vec2d(S(0) / W, S(1) / H);
        var uv = shape.Invert(px)!.Value;
        Assert.Equal(uv.X, S(2) / S(4), 4);
        Assert.Equal(uv.Y, S(3) / S(4), 4);
        var (latticed, _) = PixelQuadMath.WithLattice(shape.Quad, 3, 2, WarpInterpolation.Linear, W, H);
        Assert.Equal(2 * 8 * 1 * 8 * 6, PixelQuadMath.VertexCount(latticed));
    }

    private static Vec2d Intersect(Vec2d a, Vec2d b, Vec2d c, Vec2d d)
    {
        var r = b - a;
        var s = d - c;
        var t = Vec2d.Cross(c - a, s) / Vec2d.Cross(r, s);
        return a + r * t;
    }
}
