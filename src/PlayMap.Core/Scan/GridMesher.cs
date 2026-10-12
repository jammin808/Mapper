using PlayMap.Core.Geometry;
using PlayMap.Core.Numerics;

namespace PlayMap.Core.Scan;

/// <summary>Options for <see cref="GridMesher"/>.</summary>
public sealed record GridMeshOptions
{
    /// <summary>A triangle edge longer than this many local grid steps (of its shorter-stepped end) spans a depth
    /// jump and the triangle is dropped.</summary>
    public double JumpFactor { get; init; } = 4;

    /// <summary>An absolute cap on an edge's length (metres).</summary>
    public double MaxEdge { get; init; } = double.PositiveInfinity;
}

/// <summary>A mesh made from a grid: the mesh, the grid cell each vertex came from, and how many triangles
/// were dropped across depth jumps.</summary>
public sealed record GridMesh(MeshData Mesh, int[] VertexCells, int DroppedTriangles);

/// <summary>
/// The fastest scan mesh: an organised cloud's grid (the camera's or the projector's raster) triangulated
/// cell by cell — two triangles per 2 × 2 block of valid points, split along the shorter diagonal, wound
/// counter-clockwise as the sensor sees them (so their front faces it). A triangle with an edge longer than a
/// few local grid steps spans a depth jump (an occluding edge, not a surface) and is dropped. UVs are the grid
/// coordinates normalised, at the cells' centres, in PlayMap's one UV convention — origin at the texture's
/// top-left, v growing down: grid row 0 is v ≈ 0, u = (column + 0.5)/width, v = (row + 0.5)/height. On a
/// projector-grid mesh they are the projector's own pixels, so content in projector space maps straight on.
/// </summary>
public static class GridMesher
{
    public static GridMesh Build(PointCloud organised, GridMeshOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(organised);
        if (!organised.IsOrganised)
            throw new ArgumentException("The cloud has no grid.", nameof(organised));
        options ??= new GridMeshOptions();
        var w = organised.GridWidth;
        var h = organised.GridHeight;
        var points = organised.Points;
        var spacing = PointCloudOps.GridSpacing(organised);
        var vertexOf = new int[points.Length];
        Array.Fill(vertexOf, -1);
        var cells = new List<int>();
        var indices = new List<uint>();
        var dropped = 0;

        uint Vertex(int k)
        {
            if (vertexOf[k] < 0)
            {
                vertexOf[k] = cells.Count;
                cells.Add(k);
            }
            return (uint)vertexOf[k];
        }

        bool Short(int a, int b)
        {
            var length = (points[a] - points[b]).Length;
            var step = Math.Min(spacing[a], spacing[b]);
            var reach = options.JumpFactor * step * (Math.Abs(a % w - b % w) + Math.Abs(a / w - b / w) == 2 ? Math.Sqrt(2) : 1);
            return length <= options.MaxEdge && length <= reach;
        }

        void Triangle(int a, int b, int c)
        {
            if (!PointCloud.IsValid(points[a]) || !PointCloud.IsValid(points[b]) || !PointCloud.IsValid(points[c]))
                return;
            if (!Short(a, b) || !Short(b, c) || !Short(c, a))
            {
                dropped++;
                return;
            }
            indices.Add(Vertex(a));
            indices.Add(Vertex(b));
            indices.Add(Vertex(c));
        }

        for (var y = 0; y + 1 < h; y++)
        {
            for (var x = 0; x + 1 < w; x++)
            {
                var k00 = y * w + x;
                var k10 = k00 + 1;
                var k01 = k00 + w;
                var k11 = k01 + 1;
                // Counter-clockwise as the sensor sees the grid (x right, y down): (00, 01, 10) and (10, 01, 11),
                // or across the other diagonal (00, 01, 11) and (00, 11, 10).
                var d0 = Diagonal(points, k10, k01);
                var d1 = Diagonal(points, k00, k11);
                if (d0 <= d1)
                {
                    Triangle(k00, k01, k10);
                    Triangle(k10, k01, k11);
                }
                else
                {
                    Triangle(k00, k01, k11);
                    Triangle(k00, k11, k10);
                }
            }
        }

        var positions = new float[cells.Count * 3];
        var uvs = new float[cells.Count * 2];
        for (var v = 0; v < cells.Count; v++)
        {
            var k = cells[v];
            var p = points[k];
            positions[v * 3] = (float)p.X;
            positions[v * 3 + 1] = (float)p.Y;
            positions[v * 3 + 2] = (float)p.Z;
            uvs[v * 2] = (float)((k % w + 0.5) / w);
            uvs[v * 2 + 1] = (float)((k / w + 0.5) / h);
        }
        var mesh = new MeshData(positions, [], uvs, [.. indices]).WithComputedNormals();
        return new GridMesh(mesh, [.. cells], dropped);
    }

    /// <summary>A diagonal's squared length, or infinity when an end is invalid (so the other split is taken).</summary>
    private static double Diagonal(Vec3d[] points, int a, int b) =>
        PointCloud.IsValid(points[a]) && PointCloud.IsValid(points[b]) ? (points[a] - points[b]).LengthSquared : double.PositiveInfinity;
}
