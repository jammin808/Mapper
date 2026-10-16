using PlayMap.Core.Numerics;
using PlayMap.Core.Scan;

namespace PlayMap.Core.Stacks;

/// <summary>
/// A projector-space view taken back to the camera: the continuous projector point a camera point sees. The view holds, for each
/// projector pixel centre, the camera point that sees it; between centres it is bilinear, and the inverse is found by Newton from
/// the projector pixel whose camera point lies nearest (found through a grid of camera cells). Null where the view does not reach.
/// </summary>
public sealed class ViewInverse
{
    private readonly ProjectorView _view;
    private readonly int[] _cells;
    private readonly int _cw;
    private readonly int _ch;

    public ViewInverse(ProjectorView view)
    {
        ArgumentNullException.ThrowIfNull(view);
        _view = view;
        double maxX = 0, maxY = 0;
        for (var k = 0; k < view.CameraX.Length; k++)
        {
            if (view.Coverage[k] == ProjectorCoverage.None)
                continue;
            maxX = Math.Max(maxX, view.CameraX[k]);
            maxY = Math.Max(maxY, view.CameraY[k]);
        }
        _cw = (int)Math.Min(1 << 14, Math.Ceiling(maxX) + 2);
        _ch = (int)Math.Min(1 << 14, Math.Ceiling(maxY) + 2);
        _cells = new int[_cw * _ch];
        Array.Fill(_cells, -1);
        var best = new float[_cw * _ch];
        for (var k = 0; k < view.CameraX.Length; k++)
        {
            if (view.Coverage[k] == ProjectorCoverage.None)
                continue;
            var cx = view.CameraX[k];
            var cy = view.CameraY[k];
            var ix = (int)Math.Floor(cx);
            var iy = (int)Math.Floor(cy);
            if (ix < 0 || iy < 0 || ix >= _cw || iy >= _ch)
                continue;
            var c = iy * _cw + ix;
            var d = (cx - ix - 0.5f) * (cx - ix - 0.5f) + (cy - iy - 0.5f) * (cy - iy - 0.5f);
            if (_cells[c] < 0 || d < best[c])
            {
                _cells[c] = k;
                best[c] = d;
            }
        }
    }

    /// <summary>The projector point (pixel units, centres at +0.5) the camera point sees.</summary>
    public Vec2d? At(Vec2d camera)
    {
        if (Seed(camera) is not { } seed)
            return null;
        var w = _view.Width;
        var p = new Vec2d(seed % w + 0.5, seed / w + 0.5);
        for (var it = 0; it < 8; it++)
        {
            if (Sample(p) is not { } sample)
                return null;
            var (c, du, dv) = sample;
            var f = c - camera;
            if (f.LengthSquared < 1e-8)
                return p;
            var det = du.X * dv.Y - dv.X * du.Y;
            if (Math.Abs(det) < 1e-12)
                return null;
            var step = new Vec2d((dv.Y * f.X - dv.X * f.Y) / det, (-du.Y * f.X + du.X * f.Y) / det);
            if (step.Length > 4)
                step = step * (4 / step.Length);
            p -= step;
        }
        return Sample(p) is { } last && (last.C - camera).Length < 0.02 ? p : null;
    }

    private int? Seed(Vec2d camera)
    {
        var ix = (int)Math.Floor(camera.X);
        var iy = (int)Math.Floor(camera.Y);
        var found = -1;
        var bestD = double.MaxValue;
        for (var r = 0; r <= 3 && found < 0; r++)
            for (var y = iy - r; y <= iy + r; y++)
                for (var x = ix - r; x <= ix + r; x++)
                {
                    if (x < 0 || y < 0 || x >= _cw || y >= _ch || _cells[y * _cw + x] is var k && k < 0)
                        continue;
                    var d = (new Vec2d(_view.CameraX[k], _view.CameraY[k]) - camera).LengthSquared;
                    if (d < bestD)
                    {
                        bestD = d;
                        found = k;
                    }
                }
        return found >= 0 ? found : null;
    }

    /// <summary>The camera point at a projector point and its derivatives, bilinear between the four pixel centres around it.</summary>
    private (Vec2d C, Vec2d Du, Vec2d Dv)? Sample(Vec2d p)
    {
        var w = _view.Width;
        var h = _view.Height;
        var gx = p.X - 0.5;
        var gy = p.Y - 0.5;
        var x0 = Math.Clamp((int)Math.Floor(gx), 0, w - 2);
        var y0 = Math.Clamp((int)Math.Floor(gy), 0, h - 2);
        if (w < 2 || h < 2)
            return null;
        var fx = gx - x0;
        var fy = gy - y0;
        if (fx < -1 || fx > 2 || fy < -1 || fy > 2)
            return null;
        var k00 = y0 * w + x0;
        var k10 = k00 + 1;
        var k01 = k00 + w;
        var k11 = k01 + 1;
        var cov = _view.Coverage;
        if (cov[k00] == ProjectorCoverage.None || cov[k10] == ProjectorCoverage.None || cov[k01] == ProjectorCoverage.None || cov[k11] == ProjectorCoverage.None)
            return null;
        Vec2d C(int k) => new(_view.CameraX[k], _view.CameraY[k]);
        var c00 = C(k00);
        var c10 = C(k10);
        var c01 = C(k01);
        var c11 = C(k11);
        var c = (1 - fx) * (1 - fy) * c00 + fx * (1 - fy) * c10 + (1 - fx) * fy * c01 + fx * fy * c11;
        var du = (1 - fy) * (c10 - c00) + fy * (c11 - c01);
        var dv = (1 - fx) * (c01 - c00) + fx * (c11 - c10);
        return (c, du, dv);
    }
}

/// <summary>A stack member's measured pairs against its master (facade §3.9, target MASTER): every member pixel the camera saw is
/// taken to the camera, back into the master's raster, and from there to where the member's render puts the same picture point
/// (<paramref name="masterToRender"/>: the identity, scaled between rasters, when member and master share one model pose; else
/// through the stage). The pair says: what the render puts at Render must leave from Emit.</summary>
public static class StackPairs
{
    /// <summary>At most <paramref name="maxPairs"/> pairs, taken evenly over the member's measured pixels.</summary>
    public static List<StackPair> Against(ProjectorView master, ProjectorView member, Func<Vec2d, Vec2d?> masterToRender, int maxPairs = 120_000)
    {
        ArgumentNullException.ThrowIfNull(master);
        ArgumentNullException.ThrowIfNull(member);
        ArgumentNullException.ThrowIfNull(masterToRender);
        var inverse = new ViewInverse(master);
        var measured = 0;
        foreach (var c in member.Coverage)
            if (c == ProjectorCoverage.Measured || c == ProjectorCoverage.Interpolated)
                measured++;
        var stride = Math.Max(1, (int)Math.Ceiling(measured / (double)Math.Max(1, maxPairs)));
        var pairs = new List<StackPair>(Math.Min(measured, maxPairs) + 1);
        var seen = 0;
        for (var y = 0; y < member.Height; y++)
            for (var x = 0; x < member.Width; x++)
            {
                var k = y * member.Width + x;
                if (member.Coverage[k] is not (ProjectorCoverage.Measured or ProjectorCoverage.Interpolated))
                    continue;
                if (seen++ % stride != 0)
                    continue;
                var camera = new Vec2d(member.CameraX[k], member.CameraY[k]);
                if (inverse.At(camera) is not { } m || masterToRender(m) is not { } render)
                    continue;
                pairs.Add(new StackPair(render, new Vec2d(x + 0.5, y + 0.5)));
            }
        return pairs;
    }

    /// <summary>Master raster to member raster when both share one model pose (the rasters' scale between them).</summary>
    public static Func<Vec2d, Vec2d?> Scaled(int masterWidth, int masterHeight, int memberWidth, int memberHeight)
    {
        var sx = memberWidth / (double)masterWidth;
        var sy = memberHeight / (double)masterHeight;
        return m => new Vec2d(m.X * sx, m.Y * sy);
    }
}
