using PlayMap.Core.Model;
using PlayMap.Core.Numerics;

namespace PlayMap.Core.Mapping;

/// <summary>A warp lattice compiled into one bicubic Bézier patch per cell (16 control points), in the
/// lattice's own frame: normalised output units before the corner pin, (0,0) the top-left of the raster.
/// <list type="bullet">
/// <item>Linear: each cell is the bilinear patch of its four nodes (degree-elevated, so it is exact).</item>
/// <item>Catmull-Rom: the patch through the four nodes with per-node tangents of half the central
/// difference to the neighbours (one-sided at the border); the edge handles sit a third of a tangent from
/// the node, the inner points carry the twist (the cross difference), so neighbouring cells meet with
/// matching slope (C1) and a lattice at rest is the identity exactly.</item>
/// <item>Bézier: Catmull-Rom with each node's handles moved by its <see cref="WarpLattice.HandlesU"/> /
/// <see cref="WarpLattice.HandlesV"/> offsets (the out-handle by +offset, the in-handle by −offset, so the
/// two stay mirrored and the surface stays C1); all-zero handles give the Catmull-Rom surface exactly.</item>
/// </list>
/// Every mode interpolates the nodes: S(i/(C−1), j/(R−1)) = rest + offset. Immutable and thread-safe.</summary>
public sealed class LatticeSurface
{
    private readonly Vec2d[] _control; // per cell, 16 points: index a + 4b (a along u, b along v)
    private readonly int _cellsU;
    private readonly int _cellsV;
    private readonly Vec2d[] _hu = [];
    private readonly Vec2d[] _hv = [];

    public int Columns { get; }
    public int Rows { get; }
    public WarpInterpolation Interpolation { get; }

    /// <summary>The nodes as they stand (rest + offset), row-major.</summary>
    public IReadOnlyList<Vec2d> Nodes { get; }

    public LatticeSurface(WarpLattice lattice)
    {
        ArgumentNullException.ThrowIfNull(lattice);
        Columns = Math.Clamp(lattice.Columns, WarpLattice.MinSize, WarpLattice.MaxSize);
        Rows = Math.Clamp(lattice.Rows, WarpLattice.MinSize, WarpLattice.MaxSize);
        Interpolation = lattice.Interpolation;
        _cellsU = Columns - 1;
        _cellsV = Rows - 1;
        var n = Columns * Rows;
        var nodes = new Vec2d[n];
        for (var j = 0; j < Rows; j++)
            for (var i = 0; i < Columns; i++)
            {
                var k = j * Columns + i;
                var offset = k < lattice.Offsets.Length ? lattice.Offsets[k] : Vec2d.Zero;
                nodes[k] = Rest(i, j) + offset;
            }
        Nodes = nodes;
        _control = new Vec2d[_cellsU * _cellsV * 16];
        if (Interpolation == WarpInterpolation.Linear)
        {
            BuildLinear(nodes);
        }
        else
        {
            _hu = new Vec2d[n];
            _hv = new Vec2d[n];
            BuildCubic(nodes, lattice);
        }
    }

    /// <summary>Where node (i, j) rests: (i/(C−1), j/(R−1)).</summary>
    public Vec2d Rest(int i, int j) => new(i / (double)(Columns - 1), j / (double)(Rows - 1));

    private void BuildLinear(Vec2d[] nodes)
    {
        for (var cj = 0; cj < _cellsV; cj++)
            for (var ci = 0; ci < _cellsU; ci++)
            {
                var p00 = nodes[cj * Columns + ci];
                var p10 = nodes[cj * Columns + ci + 1];
                var p01 = nodes[(cj + 1) * Columns + ci];
                var p11 = nodes[(cj + 1) * Columns + ci + 1];
                var c = (cj * _cellsU + ci) * 16;
                for (var b = 0; b < 4; b++)
                    for (var a = 0; a < 4; a++)
                    {
                        // A bilinear patch elevated to bicubic: its control net is the bilinear map at thirds.
                        var s = a / 3.0;
                        var t = b / 3.0;
                        _control[c + a + 4 * b] = (1 - s) * (1 - t) * p00 + s * (1 - t) * p10 + (1 - s) * t * p01 + s * t * p11;
                    }
            }
    }

    private void BuildCubic(Vec2d[] nodes, WarpLattice lattice)
    {
        var cols = Columns;
        var rows = Rows;
        var n = cols * rows;
        // Handle offsets (a third of the per-cell tangent), Bézier mode adds the node's own offsets.
        var hu = _hu;
        var hv = _hv;
        var bezier = Interpolation == WarpInterpolation.Bezier;
        for (var j = 0; j < rows; j++)
            for (var i = 0; i < cols; i++)
            {
                var k = j * cols + i;
                var i0 = Math.Max(0, i - 1);
                var i1 = Math.Min(cols - 1, i + 1);
                var j0 = Math.Max(0, j - 1);
                var j1 = Math.Min(rows - 1, j + 1);
                var tu = (nodes[j * cols + i1] - nodes[j * cols + i0]) / (i1 - i0);
                var tv = (nodes[j1 * cols + i] - nodes[j0 * cols + i]) / (j1 - j0);
                hu[k] = tu / 3;
                hv[k] = tv / 3;
                if (bezier)
                {
                    if (k < lattice.HandlesU.Length)
                        hu[k] += lattice.HandlesU[k];
                    if (k < lattice.HandlesV.Length)
                        hv[k] += lattice.HandlesV[k];
                }
            }
        // Twist (∂²S/∂u∂v per cell²) at each node: the mean of the u-difference of the v-tangents and the
        // v-difference of the u-tangents. Zero for a lattice at rest, so the rest surface stays affine.
        var twist = new Vec2d[n];
        for (var j = 0; j < rows; j++)
            for (var i = 0; i < cols; i++)
            {
                var i0 = Math.Max(0, i - 1);
                var i1 = Math.Min(cols - 1, i + 1);
                var j0 = Math.Max(0, j - 1);
                var j1 = Math.Min(rows - 1, j + 1);
                var dTvDu = (hv[j * cols + i1] - hv[j * cols + i0]) * 3 / (i1 - i0);
                var dTuDv = (hu[j1 * cols + i] - hu[j0 * cols + i]) * 3 / (j1 - j0);
                twist[j * cols + i] = (dTvDu + dTuDv) / 2;
            }
        for (var cj = 0; cj < _cellsV; cj++)
            for (var ci = 0; ci < _cellsU; ci++)
            {
                var k00 = cj * cols + ci;
                var k10 = k00 + 1;
                var k01 = k00 + cols;
                var k11 = k01 + 1;
                var c = (cj * _cellsU + ci) * 16;
                var b = _control.AsSpan(c, 16);
                b[0] = nodes[k00];
                b[3] = nodes[k10];
                b[12] = nodes[k01];
                b[15] = nodes[k11];
                b[1] = nodes[k00] + hu[k00];
                b[2] = nodes[k10] - hu[k10];
                b[13] = nodes[k01] + hu[k01];
                b[14] = nodes[k11] - hu[k11];
                b[4] = nodes[k00] + hv[k00];
                b[8] = nodes[k01] - hv[k01];
                b[7] = nodes[k10] + hv[k10];
                b[11] = nodes[k11] - hv[k11];
                // Inner points from the corner twists: S_uv at a corner is 9 × the corner's control "parallelogram defect".
                b[5] = b[1] + b[4] - b[0] + twist[k00] / 9;
                b[6] = b[2] + b[7] - b[3] - twist[k10] / 9;
                b[9] = b[8] + b[13] - b[12] - twist[k01] / 9;
                b[10] = b[11] + b[14] - b[15] + twist[k11] / 9;
            }
    }

    /// <summary>The surface at lattice coordinates (u, v), 0..1 across the whole lattice. Outside 0..1 the
    /// border cells' polynomials extend.</summary>
    public Vec2d Evaluate(double u, double v)
    {
        var (cell, s, t) = Locate(u, v);
        Span<double> bu = stackalloc double[4];
        Span<double> bv = stackalloc double[4];
        Bernstein(s, bu);
        Bernstein(t, bv);
        return Combine(cell, bu, bv);
    }

    /// <summary>The surface and its partial derivatives with respect to u and v (lattice-wide units).</summary>
    public (Vec2d Point, Vec2d Du, Vec2d Dv) EvaluateWithDerivatives(double u, double v)
    {
        var (cell, s, t) = Locate(u, v);
        Span<double> bu = stackalloc double[4];
        Span<double> bv = stackalloc double[4];
        Span<double> du = stackalloc double[4];
        Span<double> dv = stackalloc double[4];
        Bernstein(s, bu);
        Bernstein(t, bv);
        BernsteinDerivative(s, du);
        BernsteinDerivative(t, dv);
        var p = Combine(cell, bu, bv);
        var pu = Combine(cell, du, bv) * _cellsU;
        var pv = Combine(cell, bu, dv) * _cellsV;
        return (p, pu, pv);
    }

    /// <summary>Evaluates a whole grid of (cellsU·ku + 1) × (cellsV·kv + 1) points — ku and kv samples per
    /// cell side — row-major into <paramref name="target"/>: the tessellation's inner loop, with the
    /// Bernstein weights computed once. Cell borders fall on grid lines, so every node is a vertex.</summary>
    public void EvaluateGrid(int ku, int kv, Span<Vec2d> target)
    {
        ku = Math.Max(1, ku);
        kv = Math.Max(1, kv);
        var gw = _cellsU * ku + 1;
        var gh = _cellsV * kv + 1;
        if (target.Length < gw * gh)
            throw new ArgumentException("The target is smaller than the grid.", nameof(target));
        var wu = new double[(ku + 1) * 4];
        var wv = new double[(kv + 1) * 4];
        for (var s = 0; s <= ku; s++)
            Bernstein(s / (double)ku, wu.AsSpan(s * 4, 4));
        for (var s = 0; s <= kv; s++)
            Bernstein(s / (double)kv, wv.AsSpan(s * 4, 4));
        for (var gy = 0; gy < gh; gy++)
        {
            var cj = Math.Min(gy / kv, _cellsV - 1);
            var bv = wv.AsSpan((gy - cj * kv) * 4, 4);
            for (var gx = 0; gx < gw; gx++)
            {
                var ci = Math.Min(gx / ku, _cellsU - 1);
                target[gy * gw + gx] = Combine(cj * _cellsU + ci, wu.AsSpan((gx - ci * ku) * 4, 4), bv);
            }
        }
    }

    /// <summary>Node k's effective u handle: the out-handle sits at node + this, the in-handle at node − this
    /// (lattice frame). Zero in Linear mode, where a cell edge is straight.</summary>
    public Vec2d HandleU(int k) => _hu.Length == 0 ? Vec2d.Zero : _hu[k];

    /// <summary>Node k's effective v handle (see <see cref="HandleU"/>).</summary>
    public Vec2d HandleV(int k) => _hv.Length == 0 ? Vec2d.Zero : _hv[k];

    public int CellsU => _cellsU;
    public int CellsV => _cellsV;

    private (int Cell, double S, double T) Locate(double u, double v)
    {
        var x = u * _cellsU;
        var y = v * _cellsV;
        var ci = Math.Clamp((int)Math.Floor(x), 0, _cellsU - 1);
        var cj = Math.Clamp((int)Math.Floor(y), 0, _cellsV - 1);
        return (cj * _cellsU + ci, x - ci, y - cj);
    }

    private Vec2d Combine(int cell, ReadOnlySpan<double> bu, ReadOnlySpan<double> bv)
    {
        var c = cell * 16;
        double x = 0, y = 0;
        for (var b = 0; b < 4; b++)
        {
            double rx = 0, ry = 0;
            for (var a = 0; a < 4; a++)
            {
                var p = _control[c + a + 4 * b];
                rx += bu[a] * p.X;
                ry += bu[a] * p.Y;
            }
            x += bv[b] * rx;
            y += bv[b] * ry;
        }
        return new Vec2d(x, y);
    }

    private static void Bernstein(double t, Span<double> b)
    {
        var s = 1 - t;
        b[0] = s * s * s;
        b[1] = 3 * s * s * t;
        b[2] = 3 * s * t * t;
        b[3] = t * t * t;
    }

    private static void BernsteinDerivative(double t, Span<double> b)
    {
        var s = 1 - t;
        b[0] = -3 * s * s;
        b[1] = 3 * s * s - 6 * s * t;
        b[2] = 6 * s * t - 3 * t * t;
        b[3] = 3 * t * t;
    }
}
