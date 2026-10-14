using System.Collections.Immutable;
using System.Globalization;
using PlayMap.Core.Model;
using PlayMap.Core.Numerics;

namespace PlayMap.Core.Mapping;

/// <summary>A pixel quad compiled for evaluation: the unit square's homography onto its corners and its lattice surface.
/// A content point (u, v) ∈ [0, 1]² lands at p(u, v) = H(u, v) + L(u, v), raster-normalised, where L is the lattice's
/// offset field (S(u, v) − (u, v)); a lattice at rest adds nothing, so a quad given a lattice keeps its shape exactly.
/// Immutable and thread-safe.</summary>
public sealed class PixelQuadShape
{
    private readonly Mat3d _h;
    private readonly Mat3d _inverse;

    private PixelQuadShape(PixelQuad quad, Mat3d h, Mat3d inverse)
    {
        Quad = quad;
        _h = h;
        _inverse = inverse;
        Lattice = quad.Lattice is { } l ? new LatticeSurface(l) : null;
    }

    public PixelQuad Quad { get; }

    /// <summary>The unit square onto the four corners (row-major, acting on (u, v, 1)).</summary>
    public Mat3d Homography => _h;

    public LatticeSurface? Lattice { get; }

    /// <summary>The compiled quad, or null when three corners are in a line (no homography).</summary>
    public static PixelQuadShape? Of(PixelQuad quad)
    {
        ArgumentNullException.ThrowIfNull(quad);
        if (!QuadHomography.TryUnitSquareToQuad(quad.TopLeft, quad.TopRight, quad.BottomRight, quad.BottomLeft, out var h)
            || !h.TryInverse(out var inverse))
            return null;
        return new PixelQuadShape(quad, h, inverse);
    }

    /// <summary>The homography's own point for (u, v), before the lattice.</summary>
    public Vec2d Plain(double u, double v) => QuadHomography.Apply(_h, new Vec2d(u, v));

    /// <summary>The homogeneous weight w = h₂₀u + h₂₁v + h₂₂ at (u, v): a content attribute divided by it interpolates
    /// linearly across the raster.</summary>
    public double W(double u, double v) => _h.M20 * u + _h.M21 * v + _h.M22;

    /// <summary>Where content point (u, v) lands, raster-normalised.</summary>
    public Vec2d Evaluate(double u, double v)
    {
        var p = Plain(u, v);
        return Lattice is { } l ? p + (l.Evaluate(u, v) - new Vec2d(u, v)) : p;
    }

    /// <summary>The point and its partial derivatives along u and v.</summary>
    public (Vec2d Point, Vec2d Du, Vec2d Dv) EvaluateWithDerivatives(double u, double v)
    {
        var uv = new Vec2d(u, v);
        var p = QuadHomography.Apply(_h, uv);
        var (du, dv) = QuadHomography.Jacobian(_h, uv);
        if (Lattice is { } l)
        {
            var (s, su, sv) = l.EvaluateWithDerivatives(u, v);
            p += s - uv;
            du += su - new Vec2d(1, 0);
            dv += sv - new Vec2d(0, 1);
        }
        return (p, du, dv);
    }

    /// <summary>The content point that lands on raster point <paramref name="raster"/>: the homography's inverse, then Newton on
    /// the lattice's offsets; null when it does not converge (a folded lattice).</summary>
    public Vec2d? Invert(Vec2d raster)
    {
        var uv = QuadHomography.Apply(_inverse, raster);
        if (Lattice is null)
            return uv;
        for (var it = 0; it < 20; it++)
        {
            var (p, du, dv) = EvaluateWithDerivatives(uv.X, uv.Y);
            var f = p - raster;
            if (f.LengthSquared < 1e-24)
                return uv;
            var det = du.X * dv.Y - dv.X * du.Y;
            if (Math.Abs(det) < 1e-18)
                return null;
            uv -= new Vec2d((dv.Y * f.X - dv.X * f.Y) / det, (-du.Y * f.X + du.X * f.Y) / det);
        }
        return (Evaluate(uv.X, uv.Y) - raster).LengthSquared < 1e-18 ? uv : null;
    }
}

/// <summary>The maths of a quad drawn in a projector's raster: ordering four clicks, the shape's checks, corners and lattice
/// nodes moved to raster points, a lattice resized under a kept shape, and the vertices the pixel-surface program draws
/// (raster pixels with homogeneous content coordinates, so the picture is projectively exact across the diagonal).</summary>
public static class PixelQuadMath
{
    /// <summary>The smallest quad, in raster pixels² (an accidental double-click makes no surface).</summary>
    public const double MinAreaPx = 64;

    /// <summary>Sub-cells per lattice cell along each axis when a latticed quad is drawn.</summary>
    public const int LatticeSubdivision = 8;

    /// <summary>Floats per drawn vertex: x, y (raster pixels), s, t, q (content (u, v) = (s, t) / q).</summary>
    public const int VertexFloats = 5;

    public static readonly ImmutableArray<string> CornerNames = ["TL", "TR", "BR", "BL"];

    /// <summary>The corner a word names (TL, TR, BR, BL or 0–3, any case), else −1.</summary>
    public static int CornerIndex(string word)
    {
        ArgumentNullException.ThrowIfNull(word);
        for (var i = 0; i < 4; i++)
            if (word.Equals(CornerNames[i], StringComparison.OrdinalIgnoreCase) || word == i.ToString(CultureInfo.InvariantCulture))
                return i;
        return -1;
    }

    public static Vec2d Corner(PixelQuad quad, int index)
    {
        ArgumentNullException.ThrowIfNull(quad);
        return index switch
        {
            0 => quad.TopLeft,
            1 => quad.TopRight,
            2 => quad.BottomRight,
            3 => quad.BottomLeft,
            _ => throw new ArgumentOutOfRangeException(nameof(index)),
        };
    }

    public static PixelQuad WithCorner(PixelQuad quad, int index, Vec2d at)
    {
        ArgumentNullException.ThrowIfNull(quad);
        return index switch
        {
            0 => quad with { TopLeft = at },
            1 => quad with { TopRight = at },
            2 => quad with { BottomRight = at },
            3 => quad with { BottomLeft = at },
            _ => throw new ArgumentOutOfRangeException(nameof(index)),
        };
    }

    /// <summary>Four clicks in any order as top-left, top-right, bottom-right, bottom-left: sorted by angle about their centroid
    /// (clockwise on a y-down raster), then turned so the first is the one nearest the raster's top-left (least x + y). Give the
    /// clicks in pixels: the top-left test is not invariant under a change of aspect.</summary>
    public static (Vec2d TopLeft, Vec2d TopRight, Vec2d BottomRight, Vec2d BottomLeft) Order(ReadOnlySpan<Vec2d> clicks)
    {
        if (clicks.Length != 4)
            throw new ArgumentException("a quad is four points", nameof(clicks));
        var c = (clicks[0] + clicks[1] + clicks[2] + clicks[3]) * 0.25;
        Span<Vec2d> sorted = [clicks[0], clicks[1], clicks[2], clicks[3]];
        Span<double> angle = stackalloc double[4];
        for (var i = 0; i < 4; i++)
            angle[i] = Math.Atan2(sorted[i].Y - c.Y, sorted[i].X - c.X);
        for (var i = 1; i < 4; i++)
            for (var j = i; j > 0 && angle[j] < angle[j - 1]; j--)
            {
                (angle[j], angle[j - 1]) = (angle[j - 1], angle[j]);
                (sorted[j], sorted[j - 1]) = (sorted[j - 1], sorted[j]);
            }
        var first = 0;
        for (var i = 1; i < 4; i++)
            if (sorted[i].X + sorted[i].Y < sorted[first].X + sorted[first].Y)
                first = i;
        return (sorted[first], sorted[(first + 1) % 4], sorted[(first + 2) % 4], sorted[(first + 3) % 4]);
    }

    /// <summary>The quad's area in pixels² (shoelace; positive for a clockwise quad on a y-down raster).</summary>
    public static double AreaPx(Vec2d tl, Vec2d tr, Vec2d br, Vec2d bl, int width, int height)
    {
        ReadOnlySpan<Vec2d> q = [tl, tr, br, bl];
        var a = 0.0;
        for (var i = 0; i < 4; i++)
        {
            var p = q[i];
            var n = q[(i + 1) % 4];
            a += p.X * width * (n.Y * height) - n.X * width * (p.Y * height);
        }
        return Math.Abs(a) * 0.5;
    }

    /// <summary>Why four raster-normalised corners make no quad (they cross or fold, or the quad is smaller than
    /// <see cref="MinAreaPx"/> at the raster's size), or null when they do.</summary>
    public static string? Check(Vec2d tl, Vec2d tr, Vec2d br, Vec2d bl, int width, int height)
    {
        if (!QuadHomography.IsConvex(tl, tr, br, bl))
            return "the corners cross or fold — click them around the shape";
        var area = AreaPx(tl, tr, br, bl, width, height);
        if (area < MinAreaPx)
            return string.Create(CultureInfo.InvariantCulture, $"the quad covers {area:0.#} px² — it needs at least {MinAreaPx:0} px²");
        return null;
    }

    /// <summary>The quad's lattice node (col, row) moved so that it lands exactly on <paramref name="at"/> (raster-normalised):
    /// its offset is the target less where the corners alone put its rest point.</summary>
    public static PixelQuad WithNode(PixelQuad quad, int column, int row, Vec2d at)
    {
        ArgumentNullException.ThrowIfNull(quad);
        if (quad.Lattice is not { } lattice)
            throw new ArgumentException("the quad has no lattice", nameof(quad));
        if (column < 0 || column >= lattice.Columns || row < 0 || row >= lattice.Rows)
            throw new ArgumentOutOfRangeException(nameof(column));
        var shape = PixelQuadShape.Of(quad) ?? throw new ArgumentException("the quad's corners make no homography", nameof(quad));
        var rest = new Vec2d(column / (double)(lattice.Columns - 1), row / (double)(lattice.Rows - 1));
        var k = row * lattice.Columns + column;
        var offsets = lattice.Offsets.SetItem(k, at - shape.Plain(rest.X, rest.Y));
        return quad with { Lattice = lattice with { Offsets = offsets } };
    }

    /// <summary>Where lattice node (col, row) stands now, raster-normalised.</summary>
    public static Vec2d Node(PixelQuadShape shape, int column, int row)
    {
        ArgumentNullException.ThrowIfNull(shape);
        var lattice = shape.Quad.Lattice ?? throw new ArgumentException("the quad has no lattice", nameof(shape));
        var rest = new Vec2d(column / (double)(lattice.Columns - 1), row / (double)(lattice.Rows - 1));
        return shape.Plain(rest.X, rest.Y) + lattice.Offset(column, row);
    }

    /// <summary>The quad with a lattice of the given size (or none, for 0 columns): the new nodes' offsets are the old offset field
    /// sampled at their rest points, so a quad at rest keeps its shape exactly. Answers how far its shape moved: the largest
    /// distance over a 64 × 64 sample of the square, in raster pixels.</summary>
    public static (PixelQuad Quad, double ShapeChangePx) WithLattice(PixelQuad quad, int columns, int rows, WarpInterpolation interpolation,
        int width, int height)
    {
        ArgumentNullException.ThrowIfNull(quad);
        var before = PixelQuadShape.Of(quad) ?? throw new ArgumentException("the quad's corners make no homography", nameof(quad));
        PixelQuad next;
        if (columns <= 0)
        {
            next = quad with { Lattice = null };
        }
        else
        {
            var fresh = WarpLattice.AtRest(columns, rows, interpolation);
            if (before.Lattice is { } old)
            {
                var offsets = ImmutableArray.CreateBuilder<Vec2d>(fresh.Columns * fresh.Rows);
                for (var j = 0; j < fresh.Rows; j++)
                    for (var i = 0; i < fresh.Columns; i++)
                    {
                        var rest = new Vec2d(i / (double)(fresh.Columns - 1), j / (double)(fresh.Rows - 1));
                        offsets.Add(old.Evaluate(rest.X, rest.Y) - rest);
                    }
                fresh = fresh with { Offsets = offsets.MoveToImmutable() };
            }
            next = quad with { Lattice = fresh };
        }
        var after = PixelQuadShape.Of(next)!;
        var worst = 0.0;
        for (var j = 0; j <= 63; j++)
            for (var i = 0; i <= 63; i++)
            {
                double u = i / 63.0, v = j / 63.0;
                var d = after.Evaluate(u, v) - before.Evaluate(u, v);
                worst = Math.Max(worst, Math.Sqrt(d.X * width * (d.X * width) + d.Y * height * (d.Y * height)));
            }
        return (next, worst);
    }

    /// <summary>How many vertices <see cref="WriteVertices"/> writes for a quad.</summary>
    public static int VertexCount(PixelQuad quad)
    {
        ArgumentNullException.ThrowIfNull(quad);
        if (quad.Lattice is not { } l)
            return 6;
        var cu = (Math.Clamp(l.Columns, WarpLattice.MinSize, WarpLattice.MaxSize) - 1) * LatticeSubdivision;
        var cv = (Math.Clamp(l.Rows, WarpLattice.MinSize, WarpLattice.MaxSize) - 1) * LatticeSubdivision;
        return cu * cv * 6;
    }

    /// <summary>The quad's triangles as the pixel-surface program draws them, <see cref="VertexFloats"/> floats a vertex:
    /// the raster point in pixels and the content point as (u·q, v·q, q) with q = 1 / w of the homography, so a linear
    /// interpolation across the raster divided by q gives the exact projective (u, v). Without a lattice: two triangles; with
    /// one: <see cref="LatticeSubdivision"/>² sub-cells per lattice cell, each corner at p(u, v).</summary>
    public static int WriteVertices(PixelQuadShape shape, int width, int height, Span<float> into)
    {
        ArgumentNullException.ThrowIfNull(shape);
        var count = VertexCount(shape.Quad);
        if (into.Length < count * VertexFloats)
            throw new ArgumentException("the span is too short for the quad's vertices", nameof(into));
        var o = 0;
        if (shape.Lattice is not { } l)
        {
            Put(shape, 0, 0, width, height, into, ref o);
            Put(shape, 1, 0, width, height, into, ref o);
            Put(shape, 1, 1, width, height, into, ref o);
            Put(shape, 0, 0, width, height, into, ref o);
            Put(shape, 1, 1, width, height, into, ref o);
            Put(shape, 0, 1, width, height, into, ref o);
            return count;
        }
        var nu = (l.Columns - 1) * LatticeSubdivision;
        var nv = (l.Rows - 1) * LatticeSubdivision;
        for (var j = 0; j < nv; j++)
            for (var i = 0; i < nu; i++)
            {
                double u0 = i / (double)nu, u1 = (i + 1) / (double)nu, v0 = j / (double)nv, v1 = (j + 1) / (double)nv;
                Put(shape, u0, v0, width, height, into, ref o);
                Put(shape, u1, v0, width, height, into, ref o);
                Put(shape, u1, v1, width, height, into, ref o);
                Put(shape, u0, v0, width, height, into, ref o);
                Put(shape, u1, v1, width, height, into, ref o);
                Put(shape, u0, v1, width, height, into, ref o);
            }
        return count;
    }

    private static void Put(PixelQuadShape shape, double u, double v, int width, int height, Span<float> into, ref int o)
    {
        var p = shape.Evaluate(u, v);
        var q = 1.0 / shape.W(u, v);
        into[o] = (float)(p.X * width);
        into[o + 1] = (float)(p.Y * height);
        into[o + 2] = (float)(u * q);
        into[o + 3] = (float)(v * q);
        into[o + 4] = (float)q;
        o += VertexFloats;
    }
}
