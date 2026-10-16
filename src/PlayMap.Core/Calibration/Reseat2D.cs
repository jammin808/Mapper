using System.Collections.Immutable;
using PlayMap.Core.Geometry;
using PlayMap.Core.Mapping;
using PlayMap.Core.Model;
using PlayMap.Core.Numerics;

namespace PlayMap.Core.Calibration;

/// <summary>
/// What a re-solve carries along with a projector's pose (calibops §3.8): a pin keeps its world point and its pixel becomes the point's
/// projection through the new pose; a pixel quad drawn in the projector's raster keeps lighting the same wall spots — each corner and
/// lattice node is taken out along the old pose's ray onto the stage and back in through the new pose (a point whose ray misses the
/// stage is taken out to the distance the stage lies at on average). Masks, 3D surfaces and output warps are not touched here.
/// </summary>
public static class Reseat2D
{
    /// <summary>A raster point (projector px) re-seated from the old pose to the new one; null when it falls behind the new pose.</summary>
    public static Vec2d? Point(Vec2d pixel, DeviceView before, DeviceView after, MeshRaycaster? stage, double fallbackDistance)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        var ray = before.Ray(pixel);
        var world = stage?.Cast(ray)?.Point ?? ray.At(fallbackDistance);
        return after.Project(world);
    }

    /// <summary>Pins following a pose: each enabled or not keeps its world point; its pixel is the world point seen through the pose
    /// (unchanged when the point is behind it).</summary>
    public static ImmutableArray<CalibrationPin> Pins(ImmutableArray<CalibrationPin> pins, DeviceView after)
    {
        ArgumentNullException.ThrowIfNull(after);
        return pins.IsDefaultOrEmpty ? pins : [.. pins.Select(p => after.Project(p.World) is { } px ? p with { Pixel = px } : p)];
    }

    /// <summary>A pixel quad re-seated: its corners and lattice nodes moved so they light the wall spots they lit under the old pose.
    /// Answers the largest move (projector px); null when a corner falls behind the new pose or the moved corners make no quad.</summary>
    public static (PixelQuad Quad, double LargestMovePx)? Quad(PixelQuad quad, DeviceView before, DeviceView after, MeshRaycaster? stage, double fallbackDistance)
    {
        ArgumentNullException.ThrowIfNull(quad);
        ArgumentNullException.ThrowIfNull(before);
        var w = (double)before.Intrinsics.Width;
        var h = (double)before.Intrinsics.Height;
        var largest = 0.0;
        Vec2d? Move(Vec2d normalised)
        {
            var px = new Vec2d(normalised.X * w, normalised.Y * h);
            if (Point(px, before, after, stage, fallbackDistance) is not { } moved)
                return null;
            largest = Math.Max(largest, Vec2d.Distance(px, moved));
            return new Vec2d(moved.X / w, moved.Y / h);
        }
        var shape = PixelQuadShape.Of(quad);
        var nodes = new List<(int C, int R, Vec2d At)>();
        if (quad.Lattice is { } lattice && shape is not null)
            for (var r = 0; r < lattice.Rows; r++)
                for (var c = 0; c < lattice.Columns; c++)
                    nodes.Add((c, r, PixelQuadMath.Node(shape, c, r)));
        if (Move(quad.TopLeft) is not { } tl || Move(quad.TopRight) is not { } tr || Move(quad.BottomRight) is not { } br || Move(quad.BottomLeft) is not { } bl)
            return null;
        var moved = quad with { TopLeft = tl, TopRight = tr, BottomRight = br, BottomLeft = bl };
        if (PixelQuadShape.Of(moved) is null)
            return null;
        foreach (var (c, r, at) in nodes)
        {
            if (Move(at) is not { } to)
                return null;
            moved = PixelQuadMath.WithNode(moved, c, r, to);
        }
        return (moved, largest);
    }
}
