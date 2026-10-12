using PlayMap.Core.Model;
using PlayMap.Core.Numerics;

namespace PlayMap.Core.Mapping;

/// <summary>The closed-form homography from the unit square onto a quadrilateral (the four-corner case of
/// texture mapping: (0,0)→top-left, (1,0)→top-right, (1,1)→bottom-right, (0,1)→bottom-left). Exact for
/// any non-degenerate quad, no solve: the corner pin is four points, so it never needs a fit.</summary>
public static class QuadHomography
{
    /// <summary>The 3×3 matrix (row-major, acting on column vectors (u, v, 1)) taking the unit square onto
    /// the quad; false when three corners are collinear and no homography exists.</summary>
    public static bool TryUnitSquareToQuad(Vec2d topLeft, Vec2d topRight, Vec2d bottomRight, Vec2d bottomLeft, out Mat3d h)
    {
        // The square's corners (0,0) (1,0) (1,1) (0,1) go to p0 p1 p2 p3. When p0 − p1 + p2 − p3 = 0 the quad
        // is a parallelogram and the map is affine; otherwise the projective row (g, h) is the 2×2 solve below.
        var sx = topLeft.X - topRight.X + bottomRight.X - bottomLeft.X;
        var sy = topLeft.Y - topRight.Y + bottomRight.Y - bottomLeft.Y;
        double g = 0, k = 0;
        if (Math.Abs(sx) > 1e-15 || Math.Abs(sy) > 1e-15)
        {
            var dx1 = topRight.X - bottomRight.X;
            var dy1 = topRight.Y - bottomRight.Y;
            var dx2 = bottomLeft.X - bottomRight.X;
            var dy2 = bottomLeft.Y - bottomRight.Y;
            var den = dx1 * dy2 - dx2 * dy1;
            if (Math.Abs(den) < 1e-15)
            {
                h = Mat3d.Identity;
                return false;
            }
            g = (sx * dy2 - dx2 * sy) / den;
            k = (dx1 * sy - sx * dy1) / den;
        }
        h = new Mat3d(
            topRight.X - topLeft.X + g * topRight.X, bottomLeft.X - topLeft.X + k * bottomLeft.X, topLeft.X,
            topRight.Y - topLeft.Y + g * topRight.Y, bottomLeft.Y - topLeft.Y + k * bottomLeft.Y, topLeft.Y,
            g, k, 1);
        var det = h.Determinant;
        if (Math.Abs(det) < 1e-15 || double.IsNaN(det))
        {
            h = Mat3d.Identity;
            return false;
        }
        return true;
    }

    /// <summary>The corner pin's quad in normalised output units: each raster corner plus its offset.</summary>
    public static (Vec2d TopLeft, Vec2d TopRight, Vec2d BottomRight, Vec2d BottomLeft) Corners(CornerPin pin)
    {
        ArgumentNullException.ThrowIfNull(pin);
        return (pin.TopLeft, new Vec2d(1, 0) + pin.TopRight, new Vec2d(1, 1) + pin.BottomRight, new Vec2d(0, 1) + pin.BottomLeft);
    }

    /// <summary>The corner pin as a homography from the unit square; identity when the pin rests or its quad
    /// is degenerate (three corners in a line).</summary>
    public static Mat3d ForPin(CornerPin pin)
    {
        ArgumentNullException.ThrowIfNull(pin);
        if (pin.IsNone)
            return Mat3d.Identity;
        var (tl, tr, br, bl) = Corners(pin);
        return TryUnitSquareToQuad(tl, tr, br, bl, out var h) ? h : Mat3d.Identity;
    }

    /// <summary>True when the quad is strictly convex with a consistent turn at every corner — the only
    /// shape whose homography keeps the whole square on one side of the horizon (no fold, no flip through
    /// infinity). A bow-tie or a dart is refused by the desk with this.</summary>
    public static bool IsConvex(Vec2d topLeft, Vec2d topRight, Vec2d bottomRight, Vec2d bottomLeft)
    {
        ReadOnlySpan<Vec2d> q = [topLeft, topRight, bottomRight, bottomLeft];
        var sign = 0;
        for (var i = 0; i < 4; i++)
        {
            var a = q[i];
            var b = q[(i + 1) % 4];
            var c = q[(i + 2) % 4];
            var cross = Vec2d.Cross(b - a, c - b);
            if (Math.Abs(cross) < 1e-12)
                return false;
            var s = Math.Sign(cross);
            if (sign == 0)
                sign = s;
            else if (s != sign)
                return false;
        }
        return true;
    }

    /// <summary>True when the pin's quad is convex (see <see cref="IsConvex"/>).</summary>
    public static bool IsValid(CornerPin pin)
    {
        ArgumentNullException.ThrowIfNull(pin);
        var (tl, tr, br, bl) = Corners(pin);
        return IsConvex(tl, tr, br, bl);
    }

    /// <summary>Applies a homography to a point (divides by w).</summary>
    public static Vec2d Apply(in Mat3d h, Vec2d p)
    {
        var w = h.M20 * p.X + h.M21 * p.Y + h.M22;
        return new Vec2d((h.M00 * p.X + h.M01 * p.Y + h.M02) / w, (h.M10 * p.X + h.M11 * p.Y + h.M12) / w);
    }

    /// <summary>The 2×2 Jacobian of the homography at a point, as its two columns (∂/∂x, ∂/∂y).</summary>
    public static (Vec2d Dx, Vec2d Dy) Jacobian(in Mat3d h, Vec2d p)
    {
        var w = h.M20 * p.X + h.M21 * p.Y + h.M22;
        var x = (h.M00 * p.X + h.M01 * p.Y + h.M02) / w;
        var y = (h.M10 * p.X + h.M11 * p.Y + h.M12) / w;
        var dx = new Vec2d((h.M00 - x * h.M20) / w, (h.M10 - y * h.M20) / w);
        var dy = new Vec2d((h.M01 - x * h.M21) / w, (h.M11 - y * h.M21) / w);
        return (dx, dy);
    }
}
