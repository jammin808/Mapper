using System.Collections.Immutable;
using PlayMap.Core.Mapping;
using PlayMap.Core.Model;
using PlayMap.Core.Numerics;

namespace PlayMap.Core.Tests.Mapping;

/// <summary>The pen tool's maths: flattening within a tolerance, inside tests, signed distance, tight bounds,
/// and node edits that keep the outline where they promise to.</summary>
public class BezierToolsTests
{
    /// <summary>A circle of radius r about c from n smooth nodes (handles of the standard length).</summary>
    internal static BezierPath Circle(Vec2d c, double r, int n = 4)
    {
        var k = 4.0 / 3 * Math.Tan(Math.PI / (2 * n));
        var nodes = ImmutableArray.CreateBuilder<BezierNode>(n);
        for (var i = 0; i < n; i++)
        {
            var a = 2 * Math.PI * i / n;
            var p = c + new Vec2d(Math.Cos(a), Math.Sin(a)) * r;
            var tangent = new Vec2d(-Math.Sin(a), Math.Cos(a)) * (r * k);
            nodes.Add(new BezierNode(p, -tangent, tangent, true));
        }
        return new BezierPath(nodes.MoveToImmutable(), true);
    }

    private static readonly BezierPath Blob = new(
    [
        new BezierNode(new Vec2d(0, 0), new Vec2d(-0.2, 0.3), new Vec2d(0.4, -0.3), true),
        new BezierNode(new Vec2d(1, 0.2), new Vec2d(-0.1, -0.4), new Vec2d(0.1, 0.4), true),
        new BezierNode(new Vec2d(0.8, 1), new Vec2d(0.3, 0), new Vec2d(-0.3, 0), true),
        BezierNode.Corner(0.1, 0.8),
    ], true);

    private static IEnumerable<Vec2d> Dense(BezierPath path, int perSegment = 400)
    {
        for (var k = 0; k < BezierTools.SegmentCount(path); k++)
            for (var s = 0; s <= perSegment; s++)
                yield return BezierTools.PointAt(path, k, s / (double)perSegment);
    }

    private static double DistanceToPolygon(Vec2d p, Vec2d[] poly)
    {
        var best = double.MaxValue;
        for (var i = 0; i < poly.Length; i++)
        {
            var a = poly[i];
            var b = poly[(i + 1) % poly.Length];
            var ab = b - a;
            var t = Math.Clamp(Vec2d.Dot(p - a, ab) / Math.Max(ab.LengthSquared, 1e-300), 0, 1);
            best = Math.Min(best, (p - (a + ab * t)).Length);
        }
        return best;
    }

    [Fact]
    public void FlatteningKeepsEveryPointOfTheCurveWithinTheTolerance()
    {
        foreach (var tol in new[] { 1e-2, 1e-3, 1e-4 })
        {
            var poly = BezierTools.Flatten(Blob, tol);
            var worst = Dense(Blob).Max(p => DistanceToPolygon(p, poly));
            Assert.True(worst <= tol, $"tolerance {tol}: the curve strays {worst} from the polyline");
        }
        Assert.True(BezierTools.Flatten(Blob, 1e-2).Length < BezierTools.Flatten(Blob, 1e-4).Length);
        // Straight segments cost one point each; the closing point is not repeated.
        Assert.Equal(4, BezierTools.Flatten(BezierPath.Rectangle(0, 0, 2, 1)).Length);
    }

    [Fact]
    public void ContainsCountsNonZeroWindingOnAConcaveOutline()
    {
        // An L: the notch at the top right is outside.
        var l = new BezierPath([BezierNode.Corner(0, 0), BezierNode.Corner(1, 0), BezierNode.Corner(1, 0.4),
            BezierNode.Corner(0.4, 0.4), BezierNode.Corner(0.4, 1), BezierNode.Corner(0, 1)], true);
        Assert.True(BezierTools.Contains(l, new Vec2d(0.2, 0.2)));
        Assert.True(BezierTools.Contains(l, new Vec2d(0.8, 0.2)));
        Assert.True(BezierTools.Contains(l, new Vec2d(0.2, 0.8)));
        Assert.False(BezierTools.Contains(l, new Vec2d(0.8, 0.8)));
        Assert.False(BezierTools.Contains(l, new Vec2d(-0.1, 0.5)));
        // Reversing the direction flips the winding's sign, not the answer.
        var reversed = l with { Nodes = [.. l.Nodes.Reverse()] };
        Assert.Equal(-BezierTools.Winding(BezierTools.Flatten(l), new Vec2d(0.2, 0.2)), BezierTools.Winding(BezierTools.Flatten(reversed), new Vec2d(0.2, 0.2)));
        Assert.True(BezierTools.Contains(reversed, new Vec2d(0.2, 0.2)));
        var circle = Circle(new Vec2d(0.5, 0.5), 0.3, 6);
        Assert.True(BezierTools.Contains(circle, new Vec2d(0.5 + 0.29 * Math.Cos(1), 0.5 + 0.29 * Math.Sin(1))));
        Assert.False(BezierTools.Contains(circle, new Vec2d(0.5 + 0.31 * Math.Cos(1), 0.5 + 0.31 * Math.Sin(1))));
    }

    [Fact]
    public void SignedDistanceIsNegativeInsideAndMeasuresToTheOutline()
    {
        var rect = BezierPath.Rectangle(0, 0, 2, 1);
        Assert.Equal(-0.5, BezierTools.SignedDistance(rect, new Vec2d(1, 0.5)), 12);
        Assert.Equal(1, BezierTools.SignedDistance(rect, new Vec2d(3, 0.5)), 12);
        Assert.Equal(0.25, BezierTools.SignedDistance(rect, new Vec2d(1, -0.25)), 12);
        Assert.Equal(Math.Sqrt(2), BezierTools.SignedDistance(rect, new Vec2d(3, 2)), 12);
        var circle = Circle(new Vec2d(0, 0), 1, 8);
        Assert.Equal(-0.6, BezierTools.SignedDistance(circle, new Vec2d(0.4, 0), 1e-5), 3);
        Assert.Equal(0.5, BezierTools.SignedDistance(circle, new Vec2d(0, 1.5), 1e-5), 3);
    }

    [Fact]
    public void BoundsHugTheCurveNotItsControlPoints()
    {
        var bounds = BezierTools.Bounds(Blob);
        var pts = Dense(Blob, 4000).ToList();
        Assert.Equal(pts.Min(p => p.X), bounds.X, 6);
        Assert.Equal(pts.Min(p => p.Y), bounds.Y, 6);
        Assert.Equal(pts.Max(p => p.X), bounds.Right, 6);
        Assert.Equal(pts.Max(p => p.Y), bounds.Bottom, 6);
        // The control box pokes out further than the curve does.
        var controlMinY = Blob.Nodes.Min(n => Math.Min(n.Point.Y + n.In.Y, n.Point.Y + n.Out.Y));
        Assert.True(bounds.Y > controlMinY + 0.01);
    }

    [Fact]
    public void SplittingASegmentOrAddingANodeKeepsTheShape()
    {
        var split = BezierTools.SplitSegment(Blob, 1, 0.3);
        Assert.Equal(5, split.Nodes.Length);
        AssertSameOutline(Blob, split, 1e-12);
        var (added, index) = BezierTools.AddNode(Blob, new Vec2d(1.2, 0.6));
        Assert.Equal(2, index);
        AssertSameOutline(Blob, added, 1e-12);
        var near = BezierTools.Nearest(Blob, new Vec2d(1.2, 0.6))!.Value;
        Assert.Equal(near.Point.X, added.Nodes[index].Point.X, 9);
        // A straight segment splits into two straight ones with a corner between.
        var rect = BezierTools.SplitSegment(BezierPath.Rectangle(0, 0, 1, 1), 0, 0.5);
        Assert.Equal(BezierNode.Corner(0.5, 0), rect.Nodes[1]);
    }

    [Fact]
    public void DeletingANodeKeepsTheSweepAndACornerOfStraightSidesGoesStraight()
    {
        var circle = Circle(Vec2d.Zero, 1, 8);
        var fewer = BezierTools.DeleteNode(circle, 3);
        Assert.Equal(7, fewer.Nodes.Length);
        var worst = Dense(fewer).Max(p => Math.Abs(p.Length - 1));
        Assert.True(worst < 0.02, $"the outline left the circle by {worst}");

        var square = BezierTools.DeleteNode(BezierPath.Rectangle(0, 0, 1, 1), 2);
        Assert.Equal(3, square.Nodes.Length);
        Assert.All(square.Nodes, n => Assert.Equal(Vec2d.Zero, n.In));
        Assert.All(square.Nodes, n => Assert.Equal(Vec2d.Zero, n.Out));
    }

    [Fact]
    public void ConvertingSmoothToCornerKeepsTheShapeAndCornerToSmoothLinesTheHandlesUp()
    {
        var corner = BezierTools.ConvertNode(Blob, 1, smooth: false);
        Assert.False(corner.Nodes[1].Smooth);
        AssertSameOutline(Blob, corner, 0);

        // An already-collinear corner becomes smooth with the same shape.
        var back = BezierTools.ConvertNode(corner, 1, smooth: true);
        AssertSameOutline(Blob, back, 1e-12);

        // A bare corner gets handles along its neighbours' chord, a third of the way to each.
        var rect = BezierTools.ConvertNode(BezierPath.Rectangle(0, 0, 3, 3), 1, smooth: true);
        var node = rect.Nodes[1];
        Assert.True(node.Smooth);
        Assert.Equal(0, Vec2d.Cross(node.In, node.Out), 12);
        Assert.True(Vec2d.Dot(node.In, node.Out) < 0);
        Assert.Equal(1, node.In.Length, 12);
        Assert.Equal(1, node.Out.Length, 12);

        // Dragging one handle of a smooth node turns the other to stay in line, keeping its length.
        var moved = BezierTools.MoveHandle(Blob, 0, outHandle: true, new Vec2d(0, 0.5));
        Assert.Equal(0, Vec2d.Cross(moved.Nodes[0].In, moved.Nodes[0].Out), 12);
        Assert.Equal(Blob.Nodes[0].In.Length, moved.Nodes[0].In.Length, 12);
    }

    [Fact]
    public void OffsetOutlinesSitAtTheFeatherDistanceInsideAndOut()
    {
        var circle = Circle(new Vec2d(0.5, 0.5), 0.3, 8);
        foreach (var d in new[] { 0.05, -0.05 })
        {
            var ring = BezierTools.Offset(circle, d, 1e-5);
            foreach (var p in ring)
                Assert.Equal(d, BezierTools.SignedDistance(circle, p, 1e-5), 3);
        }
    }

    private static void AssertSameOutline(BezierPath expected, BezierPath actual, double tolerance)
    {
        var a = BezierTools.Flatten(expected, 1e-6);
        foreach (var p in Dense(actual, 100))
            Assert.True(DistanceToPolygon(p, a) <= tolerance + 1e-6, $"{p} is off the outline");
        var b = BezierTools.Flatten(actual, 1e-6);
        foreach (var p in Dense(expected, 100))
            Assert.True(DistanceToPolygon(p, b) <= tolerance + 1e-6, $"{p} is off the outline");
    }
}
