using PlayMap.Core.Model;

namespace PlayMap.Core.Stacks;

/// <summary>
/// The Catmull-Rom warp lattice (<see cref="Mapping.LatticeSurface"/>, handles at rest) as weights on its node offsets: the surface
/// is linear in the nodes, and a lattice at rest is the identity, so a point's displacement is Σₖ wₖ(u, v)·offsetₖ with the same
/// weights for x and y. A point in cell (i, j) depends on the cell's four corners and their neighbours (each corner's tangents and
/// twist reach one node further), at most 4 × 4. The weights are built per cell once (16 Bézier control values per node, from a unit impulse on that node through
/// the surface's own construction) and evaluated with the Bernstein basis, so they equal the surface's to rounding.
/// </summary>
public sealed class LatticeBasis
{
    /// <summary>The most nodes a point depends on.</summary>
    public const int MaxSupport = 16;

    private readonly int[][] _nodes;       // per cell: the nodes it depends on
    private readonly double[][] _control;  // per cell: 16 control values per node, node-major

    public LatticeBasis(int columns, int rows)
    {
        Columns = Math.Clamp(columns, WarpLattice.MinSize, WarpLattice.MaxSize);
        Rows = Math.Clamp(rows, WarpLattice.MinSize, WarpLattice.MaxSize);
        CellsU = Columns - 1;
        CellsV = Rows - 1;
        _nodes = new int[CellsU * CellsV][];
        _control = new double[CellsU * CellsV][];
        for (var cj = 0; cj < CellsV; cj++)
            for (var ci = 0; ci < CellsU; ci++)
                BuildCell(ci, cj);
    }

    public int Columns { get; }

    public int Rows { get; }

    public int CellsU { get; }

    public int CellsV { get; }

    public int Nodes => Columns * Rows;

    /// <summary>The cell a lattice point (0..1 across the lattice) lies in, and the nodes that move it; outside 0..1 the border cell.</summary>
    public int CellOf(double u, double v)
    {
        var ci = Math.Clamp((int)Math.Floor(u * CellsU), 0, CellsU - 1);
        var cj = Math.Clamp((int)Math.Floor(v * CellsV), 0, CellsV - 1);
        return cj * CellsU + ci;
    }

    public IReadOnlyList<int> NodesOf(int cell) => _nodes[cell];

    /// <summary>Writes the weights of <see cref="NodesOf"/>(cell) at lattice point (u, v) into <paramref name="weights"/> and answers
    /// the cell.</summary>
    public int Weights(double u, double v, Span<double> weights)
    {
        var cell = CellOf(u, v);
        var s = u * CellsU - cell % CellsU;
        var t = v * CellsV - cell / CellsU;
        Span<double> bu = stackalloc double[4];
        Span<double> bv = stackalloc double[4];
        Bernstein(s, bu);
        Bernstein(t, bv);
        var nodes = _nodes[cell];
        var control = _control[cell];
        for (var m = 0; m < nodes.Length; m++)
        {
            var c = m * 16;
            double sum = 0;
            for (var b = 0; b < 4; b++)
            {
                double row = 0;
                for (var a = 0; a < 4; a++)
                    row += bu[a] * control[c + a + 4 * b];
                sum += bv[b] * row;
            }
            weights[m] = sum;
        }
        return cell;
    }

    private void BuildCell(int ci, int cj)
    {
        var nodes = new List<int>(MaxSupport);
        for (var j = Math.Max(0, cj - 1); j <= Math.Min(Rows - 1, cj + 2); j++)
            for (var i = Math.Max(0, ci - 1); i <= Math.Min(Columns - 1, ci + 2); i++)
                nodes.Add(j * Columns + i);
        var control = new double[nodes.Count * 16];
        for (var m = 0; m < nodes.Count; m++)
            Impulse(ci, cj, nodes[m] % Columns, nodes[m] / Columns, control.AsSpan(m * 16, 16));
        _nodes[cj * CellsU + ci] = [.. nodes];
        _control[cj * CellsU + ci] = control;
    }

    /// <summary>The cell's 16 control values for a unit offset on node (mi, mj) and none elsewhere, through the same steps as the
    /// surface's cubic construction: the node values, a third of the central-difference tangents (one-sided at the border), and the
    /// twist from the tangents' cross differences.</summary>
    private void Impulse(int ci, int cj, int mi, int mj, Span<double> b)
    {
        double F(int i, int j) => i == mi && j == mj ? 1 : 0;
        double Hu(int i, int j)
        {
            var i0 = Math.Max(0, i - 1);
            var i1 = Math.Min(Columns - 1, i + 1);
            return (F(i1, j) - F(i0, j)) / (i1 - i0) / 3;
        }
        double Hv(int i, int j)
        {
            var j0 = Math.Max(0, j - 1);
            var j1 = Math.Min(Rows - 1, j + 1);
            return (F(i, j1) - F(i, j0)) / (j1 - j0) / 3;
        }
        double Twist(int i, int j)
        {
            var i0 = Math.Max(0, i - 1);
            var i1 = Math.Min(Columns - 1, i + 1);
            var j0 = Math.Max(0, j - 1);
            var j1 = Math.Min(Rows - 1, j + 1);
            var dTvDu = (Hv(i1, j) - Hv(i0, j)) * 3 / (i1 - i0);
            var dTuDv = (Hu(i, j1) - Hu(i, j0)) * 3 / (j1 - j0);
            return (dTvDu + dTuDv) / 2;
        }
        int i00 = ci, j00 = cj, i10 = ci + 1, j01 = cj + 1;
        b[0] = F(i00, j00);
        b[3] = F(i10, j00);
        b[12] = F(i00, j01);
        b[15] = F(i10, j01);
        b[1] = b[0] + Hu(i00, j00);
        b[2] = b[3] - Hu(i10, j00);
        b[13] = b[12] + Hu(i00, j01);
        b[14] = b[15] - Hu(i10, j01);
        b[4] = b[0] + Hv(i00, j00);
        b[8] = b[12] - Hv(i00, j01);
        b[7] = b[3] + Hv(i10, j00);
        b[11] = b[15] - Hv(i10, j01);
        b[5] = b[1] + b[4] - b[0] + Twist(i00, j00) / 9;
        b[6] = b[2] + b[7] - b[3] - Twist(i10, j00) / 9;
        b[9] = b[8] + b[13] - b[12] - Twist(i00, j01) / 9;
        b[10] = b[11] + b[14] - b[15] + Twist(i10, j01) / 9;
    }

    private static void Bernstein(double t, Span<double> b)
    {
        var s = 1 - t;
        b[0] = s * s * s;
        b[1] = 3 * s * s * t;
        b[2] = 3 * s * t * t;
        b[3] = t * t * t;
    }
}
