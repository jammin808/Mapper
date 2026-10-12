using System.Collections.Immutable;
using PlayMap.Core.Model;
using PlayMap.Core.Numerics;

namespace PlayMap.Core.Mapping;

/// <summary>Tessellates an output's 2D warp for the GPU and answers the editor's questions about it: where a
/// content point lands, which content point a click hits, which lattice node is under the cursor, and the
/// lattice at another density with its shape kept. The heavy lifting is <see cref="WarpField"/>; callers
/// that ask many questions of one warp keep a field rather than going through these one-shot helpers.</summary>
public static class WarpMeshBuilder
{
    /// <summary>Vertices above which the per-vertex layers run on the thread pool.</summary>
    private const int ParallelVertexCount = 16_384;

    /// <summary>The identity: one quad, positions equal to texture coordinates.</summary>
    public static WarpMeshGeometry IdentityQuad() =>
        new([0, 0, 1, 0, 1, 1, 0, 1], [0, 0, 1, 0, 1, 1, 0, 1], [0, 1, 2, 0, 2, 3], 1, 1);

    /// <summary>The warp as a textured grid: <see cref="WarpMeshGeometry.GridColumns"/> ×
    /// <see cref="WarpMeshGeometry.GridRows"/> quads, (columns + 1) × (rows + 1) vertices row-major, two
    /// triangles a quad. Each lattice cell is cut into at least <paramref name="subdivisionsPerCell"/> quads
    /// a side, and the whole grid into at least <paramref name="minGrid"/> a side so a corner pin's
    /// perspective is sampled finely enough that affine interpolation inside a triangle does not show.
    /// A disabled or resting warp is <see cref="IdentityQuad"/>.</summary>
    public static WarpMeshGeometry Build(OutputWarp warp, int subdivisionsPerCell = 8, int minGrid = 32, PinWarp? pins = null)
    {
        ArgumentNullException.ThrowIfNull(warp);
        return Tessellate(new WarpField(warp, pins), subdivisionsPerCell, minGrid);
    }

    /// <summary>Tessellates a compiled field (see <see cref="Build"/>).</summary>
    public static WarpMeshGeometry Tessellate(WarpField field, int subdivisionsPerCell = 8, int minGrid = 32)
    {
        ArgumentNullException.ThrowIfNull(field);
        if (field.IsIdentity)
            return IdentityQuad();
        subdivisionsPerCell = Math.Clamp(subdivisionsPerCell, 1, 64);
        minGrid = Math.Clamp(minGrid, 1, 1024);
        var lattice = field.Lattice;
        var cellsU = lattice?.CellsU ?? 1;
        var cellsV = lattice?.CellsV ?? 1;
        var ku = Math.Max(subdivisionsPerCell, (minGrid + cellsU - 1) / cellsU);
        var kv = Math.Max(subdivisionsPerCell, (minGrid + cellsV - 1) / cellsV);
        var gw = cellsU * ku;
        var gh = cellsV * kv;
        var vw = gw + 1;
        var vh = gh + 1;
        var n = vw * vh;
        var pts = new Vec2d[n];
        if (lattice is not null)
        {
            lattice.EvaluateGrid(ku, kv, pts);
        }
        else
        {
            for (var y = 0; y < vh; y++)
                for (var x = 0; x < vw; x++)
                    pts[y * vw + x] = new Vec2d(x / (double)gw, y / (double)gh);
        }
        var positions = new float[n * 2];
        var tex = new float[n * 2];
        void Row(int y)
        {
            for (var x = 0; x < vw; x++)
            {
                var k = y * vw + x;
                var p = field.AfterLattice(pts[k]);
                positions[k * 2] = (float)p.X;
                positions[k * 2 + 1] = (float)p.Y;
                tex[k * 2] = (float)(x / (double)gw);
                tex[k * 2 + 1] = (float)(y / (double)gh);
            }
        }
        if (n >= ParallelVertexCount)
            Parallel.For(0, vh, Row);
        else
            for (var y = 0; y < vh; y++)
                Row(y);
        var indices = new uint[gw * gh * 6];
        var t = 0;
        for (var y = 0; y < gh; y++)
            for (var x = 0; x < gw; x++)
            {
                var v00 = (uint)(y * vw + x);
                var v10 = v00 + 1;
                var v01 = v00 + (uint)vw;
                var v11 = v01 + 1;
                indices[t++] = v00;
                indices[t++] = v10;
                indices[t++] = v11;
                indices[t++] = v00;
                indices[t++] = v11;
                indices[t++] = v01;
            }
        return new WarpMeshGeometry(positions, tex, indices, gw, gh);
    }

    /// <summary>Where content point (u, v) lands on the output (normalised units).</summary>
    public static Vec2d Evaluate(OutputWarp warp, double u, double v, PinWarp? pins = null) =>
        new WarpField(warp, pins).Evaluate(u, v);

    /// <summary>The content point under output point (x, y), or null (see <see cref="WarpField.Invert"/>).</summary>
    public static Vec2d? Invert(OutputWarp warp, double x, double y, PinWarp? pins = null) =>
        new WarpField(warp, pins).Invert(x, y);

    /// <summary>Where each lattice node lands on the output (normalised units), row-major, with the warp on —
    /// the editor shows a bypassed warp's lattice where it would take effect; empty without a lattice.</summary>
    public static Vec2d[] NodePositions(OutputWarp warp, PinWarp? pins = null)
    {
        ArgumentNullException.ThrowIfNull(warp);
        if (warp.Lattice is not { } lattice)
            return [];
        var field = new WarpField(warp with { Enabled = true }, pins);
        var surface = field.Lattice ?? new LatticeSurface(lattice);
        var nodes = new Vec2d[surface.Nodes.Count];
        for (var k = 0; k < nodes.Length; k++)
            nodes[k] = field.AfterLattice(surface.Nodes[k]);
        return nodes;
    }

    /// <summary>The lattice node nearest to output point <paramref name="at"/> (normalised units) within
    /// <paramref name="radiusPx"/> output pixels — distances measured in pixels of a
    /// <paramref name="width"/> × <paramref name="height"/> output, so the reach is round on a wide raster —
    /// or −1 when none is that close.</summary>
    public static int NearestNode(OutputWarp warp, Vec2d at, double radiusPx, int width, int height, PinWarp? pins = null)
    {
        var nodes = NodePositions(warp, pins);
        var best = -1;
        var bestD = radiusPx * radiusPx;
        for (var k = 0; k < nodes.Length; k++)
        {
            var dx = (nodes[k].X - at.X) * width;
            var dy = (nodes[k].Y - at.Y) * height;
            var d = dx * dx + dy * dy;
            if (d <= bestD)
            {
                bestD = d;
                best = k;
            }
        }
        return best;
    }

    /// <summary>The lattice at another density with its shape kept: each new node takes the old surface's
    /// point where it rests; in Bézier mode each node's handles are set so the new surface also takes the old
    /// one's slope there. Refining keeps the shape (exactly for Linear on a multiple of the old density);
    /// coarsening keeps what the fewer nodes can carry.</summary>
    public static WarpLattice Resample(WarpLattice lattice, int columns, int rows)
    {
        ArgumentNullException.ThrowIfNull(lattice);
        columns = Math.Clamp(columns, WarpLattice.MinSize, WarpLattice.MaxSize);
        rows = Math.Clamp(rows, WarpLattice.MinSize, WarpLattice.MaxSize);
        var old = new LatticeSurface(lattice);
        var n = columns * rows;
        var nodes = new Vec2d[n];
        var du = new Vec2d[n];
        var dv = new Vec2d[n];
        var offsets = new Vec2d[n];
        for (var j = 0; j < rows; j++)
            for (var i = 0; i < columns; i++)
            {
                var k = j * columns + i;
                var rest = new Vec2d(i / (double)(columns - 1), j / (double)(rows - 1));
                var (p, pu, pv) = old.EvaluateWithDerivatives(rest.X, rest.Y);
                nodes[k] = p;
                du[k] = pu / (columns - 1);
                dv[k] = pv / (rows - 1);
                offsets[k] = p - rest;
            }
        var handlesU = new Vec2d[n];
        var handlesV = new Vec2d[n];
        if (lattice.Interpolation == WarpInterpolation.Bezier)
        {
            for (var j = 0; j < rows; j++)
                for (var i = 0; i < columns; i++)
                {
                    var k = j * columns + i;
                    var i0 = Math.Max(0, i - 1);
                    var i1 = Math.Min(columns - 1, i + 1);
                    var j0 = Math.Max(0, j - 1);
                    var j1 = Math.Min(rows - 1, j + 1);
                    var cru = (nodes[j * columns + i1] - nodes[j * columns + i0]) / (i1 - i0);
                    var crv = (nodes[j1 * columns + i] - nodes[j0 * columns + i]) / (j1 - j0);
                    handlesU[k] = (du[k] - cru) / 3;
                    handlesV[k] = (dv[k] - crv) / 3;
                }
        }
        return new WarpLattice(columns, rows, [.. offsets], lattice.Interpolation, [.. handlesU], [.. handlesV]);
    }

    /// <summary>The lattice with node <paramref name="index"/> pulled to rest + <paramref name="offset"/>.</summary>
    public static WarpLattice WithNode(WarpLattice lattice, int index, Vec2d offset)
    {
        ArgumentNullException.ThrowIfNull(lattice);
        if (index < 0 || index >= lattice.Offsets.Length)
            return lattice;
        return lattice with { Offsets = lattice.Offsets.SetItem(index, offset) };
    }

    /// <summary>The lattice with node <paramref name="index"/>'s Bézier handles set (offsets from the
    /// Catmull-Rom handles, lattice frame).</summary>
    public static WarpLattice WithHandles(WarpLattice lattice, int index, Vec2d handleU, Vec2d handleV)
    {
        ArgumentNullException.ThrowIfNull(lattice);
        var n = lattice.Columns * lattice.Rows;
        if (index < 0 || index >= n)
            return lattice;
        var hu = lattice.HandlesU.Length == n ? lattice.HandlesU : ImmutableArray.CreateRange(Enumerable.Repeat(Vec2d.Zero, n));
        var hv = lattice.HandlesV.Length == n ? lattice.HandlesV : ImmutableArray.CreateRange(Enumerable.Repeat(Vec2d.Zero, n));
        return lattice with { HandlesU = hu.SetItem(index, handleU), HandlesV = hv.SetItem(index, handleV) };
    }
}
