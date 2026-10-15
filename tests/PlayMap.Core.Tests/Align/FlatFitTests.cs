using PlayMap.Core.Align;
using PlayMap.Core.Model;
using PlayMap.Core.Numerics;

namespace PlayMap.Core.Tests.Align;

/// <summary>The flat fit against a world richer than its model: the simulated camera's lens has k3 and both tangential terms the fit
/// leaves out, and the screen bulges 4 mm — within the flat test's tolerance. The fit's residual stays under its bound (and is not
/// zero: the model is poorer than the world); straight lines of the canvas land straight on the screen within 0.5 projector px, and
/// the canvas lands as a rectangle of its aspect. No parameter is asserted to come back exactly. A curved screen is refused in
/// words.</summary>
public sealed class FlatFitTests
{
    private const double Aspect = 4.5;

    [Fact]
    public async Task ARicherWorldFitsFlatUnderItsBoundAndTheCanvasLandsStraight()
    {
        var sim = MultiProjectorSim.Tiles(3, 1, new SimLayout { Screen = SimScreen.Bulged(0.004) });
        var (result, _) = await AlignSim.RunAsync(sim, aspect: Aspect);
        Assert.True(result.Ok, result.Words);
        var planar = result.Planar!;
        Assert.True(planar.Flat, planar.Words);
        Assert.InRange(planar.RmsPx, 1e-4, PlanarFit.TolerancePx);
        Assert.Equal("projectors", result.Rectification!.Source);

        var inverses = AlignSim.Inverses(result);
        foreach (var (from, to) in new[]
        {
            (new Vec2d(0.01, 0.02), new Vec2d(0.99, 0.02)), (new Vec2d(0.01, 0.5), new Vec2d(0.99, 0.5)), (new Vec2d(0.01, 0.98), new Vec2d(0.99, 0.98)),
            (new Vec2d(0.01, 0.02), new Vec2d(0.01, 0.98)), (new Vec2d(0.5, 0.02), new Vec2d(0.5, 0.98)), (new Vec2d(0.99, 0.02), new Vec2d(0.99, 0.98)),
        })
        {
            var points = new List<(Vec2d Screen, double PxPerM)>();
            for (var i = 0; i <= 96; i++)
                foreach (var l in AlignSim.Landings(sim, result, inverses, Vec2d.Lerp(from, to, i / 96.0)))
                    points.Add((l.Screen, l.PxPerM));
            Assert.True(points.Count > 90, $"{from}–{to}: {points.Count} landings");
            var worst = WorstOffLinePx(points);
            Assert.True(worst <= 0.5, $"the canvas line {from}–{to} bends {worst:0.00} projector px on the screen");
        }

        // The canvas lands as a rectangle of its aspect: corner angles and the side ratio from the screen's own metres.
        var c = new[] { new Vec2d(0.02, 0.02), new Vec2d(0.98, 0.02), new Vec2d(0.98, 0.98), new Vec2d(0.02, 0.98) }
            .Select(uv => AlignSim.Landings(sim, result, inverses, uv)[0].Screen).ToArray();
        for (var i = 0; i < 4; i++)
        {
            var a = c[(i + 3) % 4] - c[i];
            var b = c[(i + 1) % 4] - c[i];
            var angle = Math.Acos(Vec2d.Dot(a, b) / (a.Length * b.Length)) * 180 / Math.PI;
            Assert.True(Math.Abs(angle - 90) < 0.5, $"corner {i}: {angle:0.00}°");
        }
        var aspect = (Vec2d.Distance(c[0], c[1]) + Vec2d.Distance(c[3], c[2])) / (Vec2d.Distance(c[0], c[3]) + Vec2d.Distance(c[1], c[2]));
        Assert.True(Math.Abs(aspect / Aspect - 1) < 0.01, $"the canvas landed {aspect:0.000}:1, not {Aspect}:1");
    }

    [Fact]
    public async Task ACurvedScreenIsRefusedInWords()
    {
        var sim = MultiProjectorSim.Tiles(3, 1, new SimLayout { Screen = SimScreen.Curved(3) });
        var (result, _) = await AlignSim.RunAsync(sim, aspect: Aspect);
        Assert.False(result.Ok);
        Assert.False(result.Planar!.Flat);
        Assert.True(result.Planar.RmsPx > PlanarFit.TolerancePx || result.Planar.InlierShare < 0.9);
        Assert.Contains("not flat", result.Words, StringComparison.Ordinal);
        Assert.Contains("curved screen is next round", result.Words, StringComparison.Ordinal);
        Assert.Empty(result.Outputs);
    }

    [Fact]
    public void TheLensModelRoundTrips()
    {
        var lens = CameraLens.For(800, 450) with { K1 = 0.07, K2 = -0.02 };
        foreach (var p in new[] { new Vec2d(10, 10), new Vec2d(400, 225), new Vec2d(790, 440), new Vec2d(120, 400) })
            Assert.True((lens.Distort(lens.Undistort(p)) - p).Length < 1e-6);
    }

    /// <summary>The largest distance (projector px) of a point from the total-least-squares line through them all.</summary>
    private static double WorstOffLinePx(List<(Vec2d Screen, double PxPerM)> points)
    {
        var mean = new Vec2d(points.Average(p => p.Screen.X), points.Average(p => p.Screen.Y));
        double sxx = 0, sxy = 0, syy = 0;
        foreach (var (s, _) in points)
        {
            var d = s - mean;
            sxx += d.X * d.X;
            sxy += d.X * d.Y;
            syy += d.Y * d.Y;
        }
        var theta = 0.5 * Math.Atan2(2 * sxy, sxx - syy);
        var normal = new Vec2d(-Math.Sin(theta), Math.Cos(theta));
        return points.Max(p => Math.Abs(Vec2d.Dot(p.Screen - mean, normal)) * p.PxPerM);
    }
}
