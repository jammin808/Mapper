using PlayMap.Core.Numerics;

namespace PlayMap.Core.Align;

/// <summary>
/// One output's warp turned around: a raster over the canvas (uv 0–1, y down; <see cref="Width"/> × <see cref="Height"/> cells)
/// holding, per cell, the output raster point whose picture lands there — NaN where the output does not reach. Made by drawing every
/// grid cell whose four nodes are valid as two triangles in uv and interpolating the raster points barycentrically. Pure.
/// </summary>
public sealed class CanvasInverse
{
    private CanvasInverse(int width, int height, float[] x, float[] y)
    {
        Width = width;
        Height = height;
        X = x;
        Y = y;
    }

    public int Width { get; }
    public int Height { get; }

    /// <summary>The raster point's x per cell (NaN: not reached).</summary>
    public float[] X { get; }

    public float[] Y { get; }

    public bool Covers(int cx, int cy) => cx >= 0 && cy >= 0 && cx < Width && cy < Height && !float.IsNaN(X[cy * Width + cx]);

    /// <summary>The raster point that lands at canvas cell (cx, cy), or null.</summary>
    public Vec2d? At(int cx, int cy) => Covers(cx, cy) ? new Vec2d(X[cy * Width + cx], Y[cy * Width + cx]) : null;

    /// <summary>The raster point that lands at canvas <paramref name="uv"/> (its cell), or null.</summary>
    public Vec2d? At(Vec2d uv) => At((int)Math.Floor(uv.X * Width), (int)Math.Floor(uv.Y * Height));

    public static CanvasInverse Build(WarpGrid grid, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(grid);
        var x = new float[width * height];
        var y = new float[width * height];
        Array.Fill(x, float.NaN);
        Array.Fill(y, float.NaN);
        for (var k = 0; k < grid.Rows - 1; k++)
        {
            for (var i = 0; i < grid.Columns - 1; i++)
            {
                var a = k * grid.Columns + i;
                var b = a + 1;
                var c = a + grid.Columns;
                var d = c + 1;
                if (!(grid.Valid[a] && grid.Valid[b] && grid.Valid[c] && grid.Valid[d]))
                    continue;
                var ua = Uv(grid, a);
                var ub = Uv(grid, b);
                var uc = Uv(grid, c);
                var ud = Uv(grid, d);
                var qa = grid.Node(i, k);
                var qb = grid.Node(i + 1, k);
                var qc = grid.Node(i, k + 1);
                var qd = grid.Node(i + 1, k + 1);
                Triangle(ua, ub, ud, qa, qb, qd, width, height, x, y);
                Triangle(ua, ud, uc, qa, qd, qc, width, height, x, y);
            }
        }
        return new CanvasInverse(width, height, x, y);
    }

    private static Vec2d Uv(WarpGrid g, int n) => new(g.U[n], g.V[n]);

    /// <summary>Fills the cells whose centres lie in triangle (u0, u1, u2) with the raster points interpolated from (q0, q1, q2).</summary>
    private static void Triangle(Vec2d u0, Vec2d u1, Vec2d u2, Vec2d q0, Vec2d q1, Vec2d q2, int width, int height, float[] x, float[] y)
    {
        var area = Vec2d.Cross(u1 - u0, u2 - u0);
        if (Math.Abs(area) < 1e-18)
            return;
        var minX = Math.Max(0, (int)Math.Floor(Math.Min(u0.X, Math.Min(u1.X, u2.X)) * width - 0.5));
        var maxX = Math.Min(width - 1, (int)Math.Ceiling(Math.Max(u0.X, Math.Max(u1.X, u2.X)) * width - 0.5));
        var minY = Math.Max(0, (int)Math.Floor(Math.Min(u0.Y, Math.Min(u1.Y, u2.Y)) * height - 0.5));
        var maxY = Math.Min(height - 1, (int)Math.Ceiling(Math.Max(u0.Y, Math.Max(u1.Y, u2.Y)) * height - 0.5));
        for (var cy = minY; cy <= maxY; cy++)
        {
            for (var cx = minX; cx <= maxX; cx++)
            {
                var p = new Vec2d((cx + 0.5) / width, (cy + 0.5) / height);
                var w1 = Vec2d.Cross(p - u0, u2 - u0) / area;
                var w2 = Vec2d.Cross(u1 - u0, p - u0) / area;
                var w0 = 1 - w1 - w2;
                const double eps = -1e-9;
                if (w0 < eps || w1 < eps || w2 < eps)
                    continue;
                var q = q0 * w0 + q1 * w1 + q2 * w2;
                x[cy * width + cx] = (float)q.X;
                y[cy * width + cx] = (float)q.Y;
            }
        }
    }
}
