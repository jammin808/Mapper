using PlayMap.Core.Geometry;
using PlayMap.Core.Numerics;

namespace PlayMap.Core.Tests.Facade;

/// <summary>Quadric edge-collapse decimation: a flat grid collapses to its boundary's worth with no deviation; a relief facade meets
/// its budget with the measured deviation reported and independently re-measured (vertex to surface, both ways, brute force); the
/// boundary and UV seams keep every vertex where it was; no face turns over.</summary>
public sealed class DecimatorTests
{
    /// <summary>A cols × rows grid over w × h metres at z = depth(x, y), UVs continuous unless a seam column is asked for.</summary>
    internal static MeshData Grid(int cols, int rows, double w, double h, Func<double, double, double> depth, int seamColumn = -1)
    {
        var pos = new List<float>();
        var uv = new List<float>();
        var idx = new List<uint>();
        var index = new Dictionary<(int, int, int), uint>();
        uint V(int c, int r, int side)
        {
            var key = (c, r, c == seamColumn ? side : 0);
            if (index.TryGetValue(key, out var v))
                return v;
            v = (uint)(pos.Count / 3);
            index[key] = v;
            double x = c * w / (cols - 1), y = r * h / (rows - 1);
            pos.AddRange([(float)x, (float)y, (float)depth(x, y)]);
            uv.AddRange([(float)(c / (cols - 1.0) + (c == seamColumn && side == 1 ? 0.5 : 0)), (float)(r / (rows - 1.0))]);
            return v;
        }
        for (var r = 0; r + 1 < rows; r++)
            for (var c = 0; c + 1 < cols; c++)
            {
                // The cell's left side is the seam column's right copy, its right side the seam's left copy.
                uint a = V(c, r, 1), b = V(c + 1, r, 0), d = V(c, r + 1, 1), e = V(c + 1, r + 1, 0);
                idx.AddRange([a, b, e, a, e, d]);
            }
        return new MeshData([.. pos], [], [.. uv], [.. idx]);
    }

    private static double BruteDistance(Vec3d p, MeshData m)
    {
        var best = double.PositiveInfinity;
        for (var t = 0; t < m.TriangleCount; t++)
        {
            var (a, b, c) = m.Triangle(t);
            best = Math.Min(best, (MeshDistance.ClosestOnTriangle(p, m.Position(a), m.Position(b), m.Position(c)) - p).Length);
        }
        return best;
    }

    [Fact]
    public void AFlatGridCollapsesWithNoDeviation()
    {
        var grid = Grid(41, 21, 40, 20, (_, _) => 0);
        var d = MeshDecimator.Decimate(grid, 200, 0.005);
        Assert.True(d.TrianglesAfter <= 400, $"{d.TrianglesAfter}");
        Assert.True(d.DeviationM <= MeshDecimator.DeviationSlackM + 1e-9, $"{d.DeviationM}");
        Assert.False(d.StoppedAtError);
    }

    [Fact]
    public void AReliefFacadeMeetsItsBudgetWithinTheReportedDeviation()
    {
        static double Relief(double x, double y) => (x % 8 < 1.5 ? 1.0 : 0) + 0.3 * Math.Sin(y * 0.4) + 0.2 * Math.Sin(x * 0.3) + (y > 17 ? 0.6 : 0);
        var grid = Grid(161, 81, 40, 20, Relief);
        var d = MeshDecimator.Decimate(grid, 5000, 0.05);
        Assert.True(d.TrianglesAfter <= 5000, d.Words);
        Assert.True(d.DeviationM > 0 && d.DeviationM <= 0.05 * 3, d.Words);
        var rng = new Pcg32(4, 4);
        var worst = 0.0;
        for (var k = 0; k < 300; k++)
        {
            worst = Math.Max(worst, BruteDistance(grid.Position(rng.NextInt(grid.VertexCount)), d.Mesh));
            worst = Math.Max(worst, BruteDistance(d.Mesh.Position(rng.NextInt(d.Mesh.VertexCount)), grid));
        }
        Assert.True(worst <= d.DeviationM + 1e-9, $"measured {worst} against the reported {d.DeviationM}");
        // No face turned over: every face still faces +z (the grid's winding).
        for (var t = 0; t < d.Mesh.TriangleCount; t++)
        {
            var (a, b, c) = d.Mesh.Triangle(t);
            Assert.True(Vec3d.Cross(d.Mesh.Position(b) - d.Mesh.Position(a), d.Mesh.Position(c) - d.Mesh.Position(a)).Z > 0);
        }
    }

    [Fact]
    public void BoundariesAndUvSeamsAreNotMoved()
    {
        var grid = Grid(61, 31, 30, 15, (x, y) => 0.2 * Math.Sin(x) * Math.Cos(y), seamColumn: 30);
        var d = MeshDecimator.Decimate(grid, 1500, 1.0);
        var kept = new HashSet<(float, float, float, float, float)>();
        for (var v = 0; v < d.Mesh.VertexCount; v++)
            kept.Add((d.Mesh.Positions[v * 3], d.Mesh.Positions[v * 3 + 1], d.Mesh.Positions[v * 3 + 2], d.Mesh.Uvs[v * 2], d.Mesh.Uvs[v * 2 + 1]));
        for (var v = 0; v < grid.VertexCount; v++)
        {
            var x = grid.Positions[v * 3];
            var y = grid.Positions[v * 3 + 1];
            var boundary = x == 0 || y == 0 || Math.Abs(x - 30) < 1e-4 || Math.Abs(y - 15) < 1e-4 || Math.Abs(x - 15) < 1e-4;
            if (boundary)
                Assert.Contains((x, y, grid.Positions[v * 3 + 2], grid.Uvs[v * 2], grid.Uvs[v * 2 + 1]), kept);
        }
        Assert.True(d.TrianglesAfter < grid.TriangleCount / 2, d.Words);
    }

    [Fact]
    public void AMeshWithinItsBudgetIsKept()
    {
        var grid = Grid(5, 5, 4, 4, (_, _) => 0);
        var d = MeshDecimator.Decimate(grid, 100, 0.005);
        Assert.Same(grid, d.Mesh);
        Assert.Equal(0, d.DeviationM);
    }
}
