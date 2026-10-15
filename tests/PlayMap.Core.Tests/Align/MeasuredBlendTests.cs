using PlayMap.Core.Align;
using PlayMap.Core.Model;
using PlayMap.Core.Numerics;
using Xunit.Abstractions;

namespace PlayMap.Core.Tests.Align;

/// <summary>The measured blend on the simulator (2 × 1, the projectors' γ 2.2 and 2.6), judged by the truth: the light every projector
/// puts on a screen point when each draws the content through its measured warp and blend as the output stage does (the content's
/// light through the output's measured γ, ped + (1 − ped)·weight·light, back to its signal, then the projector's true response). White
/// is even across the overlap within ±2 %; with the pedestal, black everywhere is within ±5 % of the deepest overlap (more than one
/// pedestal-map pixel, 4 output px, from a raster's edge, where the step is as sharp as the quarter-raster map); with 30 %
/// corner fall-off and uniformity 1, the white's p5/p95 is at least 0.95.</summary>
public sealed class MeasuredBlendTests(ITestOutputHelper output)
{
    private const double Aspect = 3.0;

    private static SimLayout TwoByOne(double falloff = 0) => new() { Gammas = [2.2, 2.6], CornerFalloff = falloff, CoveredWidthM = 3.0 };

    [Fact]
    public async Task WhiteIsEvenInLightAcrossTheOverlap()
    {
        var sim = MultiProjectorSim.Tiles(2, 1, TwoByOne());
        var (result, _) = await AlignSim.RunAsync(sim, aspect: Aspect, options: AlignOptions.Default with { Uniformity = 0 });
        Assert.True(result.Ok, result.Words);
        Assert.InRange(result.Outputs[0].Gamma, 2.1, 2.3);
        Assert.InRange(result.Outputs[1].Gamma, 2.5, 2.7);
        var inverses = AlignSim.Inverses(result);
        foreach (var v in new[] { 0.2, 0.5, 0.8 })
        {
            var line = new List<(double U, double Light, int Covering)>();
            for (var i = 0; i <= 200; i++)
            {
                var uv = new Vec2d(0.02 + 0.96 * i / 200, v);
                var landings = AlignSim.Landings(sim, result, inverses, uv);
                if (landings.Count == 0)
                    continue;
                var world = landings[0].World;
                line.Add((uv.X, StageLight(sim, result, world, 1), landings.Count));
            }
            var single = line.Where(p => p.Covering == 1).Select(p => p.Light).ToList();
            var reference = AlignSim.Quantile(single, 0.5);
            output.WriteLine($"v {v}: white {line.Min(p => p.Light) / reference:0.0000}–{line.Max(p => p.Light) / reference:0.0000} of the rest");
            Assert.Contains(line, p => p.Covering == 2);
            foreach (var (u, light, _) in line)
                Assert.True(Math.Abs(light / reference - 1) <= 0.02, $"v {v}, u {u:0.000}: white {light / reference:0.000} of the rest");
        }
    }

    [Fact]
    public async Task ThePedestalLiftsBlackToTheDeepestOverlap()
    {
        var sim = MultiProjectorSim.Tiles(2, 1, TwoByOne());
        var (result, _) = await AlignSim.RunAsync(sim, aspect: Aspect);
        Assert.True(result.Ok, result.Words);
        var inverses = AlignSim.Inverses(result);
        var blacks = new List<(double Light, int Covering, double Edge)>();
        for (var y = 0; y <= 20; y++)
        {
            for (var x = 0; x <= 60; x++)
            {
                var uv = new Vec2d(0.02 + 0.96 * x / 60, 0.03 + 0.94 * y / 20);
                var landings = AlignSim.Landings(sim, result, inverses, uv);
                if (landings.Count == 0)
                    continue;
                var world = landings[0].World;
                blacks.Add((StageLight(sim, result, world, 0), landings.Count, Enumerable.Range(0, 2).Min(k => sim.DistanceToRasterEdge(k, world))));
            }
        }
        var deepest = AlignSim.Quantile([.. blacks.Where(b => b.Covering == 2).Select(b => b.Light)], 0.5);
        var single = sim.Camera.Ambient + sim.Projectors[0].Black;
        Assert.True(deepest > single * 1.2, "the overlap's black is deeper than one projector's");
        // The pedestal map is a quarter of the raster: within one of its pixels (4 output px) of a raster's edge, where another
        // projector's black begins, the step is only as sharp as the map.
        var judged = blacks.Where(b => b.Edge >= 4).ToList();
        output.WriteLine($"black {judged.Min(b => b.Light) / deepest:0.0000}–{judged.Max(b => b.Light) / deepest:0.0000} of the deepest overlap over {judged.Count} points");
        Assert.True(judged.Count > 0.9 * blacks.Count, $"{judged.Count} of {blacks.Count} points are a map pixel from every edge");
        foreach (var (light, covering, _) in judged)
            Assert.True(Math.Abs(light / deepest - 1) <= 0.05, $"black {light / deepest:0.000} of the deepest overlap where {covering} reach");
        var lifted = blacks.Where(b => b.Covering == 1 && b.Edge >= 4).Select(b => b.Light).ToList();
        Assert.True(AlignSim.Quantile(lifted, 0.5) / deepest > 0.97, "where one output reaches, black is lifted to the overlap's");
    }

    [Fact]
    public async Task FullUniformityEvensACornerFalloff()
    {
        var sim = MultiProjectorSim.Tiles(2, 1, TwoByOne(falloff: 0.3));
        var (result, _) = await AlignSim.RunAsync(sim, aspect: Aspect, options: AlignOptions.Default with { Uniformity = 1 });
        Assert.True(result.Ok, result.Words);
        var inverses = AlignSim.Inverses(result);
        var whites = new List<double>();
        var raw = new List<double>();
        for (var y = 0; y <= 20; y++)
        {
            for (var x = 0; x <= 60; x++)
            {
                var uv = new Vec2d(0.02 + 0.96 * x / 60, 0.03 + 0.94 * y / 20);
                var landings = AlignSim.Landings(sim, result, inverses, uv);
                if (landings.Count == 0)
                    continue;
                var world = landings[0].World;
                whites.Add(StageLight(sim, result, world, 1));
                raw.Add(landings.Sum(l => sim.WhiteAt(l.Output, l.Raster)));
            }
        }
        var before = AlignSim.Quantile(raw, 0.05) / AlignSim.Quantile(raw, 0.95);
        var after = AlignSim.Quantile(whites, 0.05) / AlignSim.Quantile(whites, 0.95);
        Assert.True(before < 0.9, $"the fall-off shows before the gain ({before:0.000})");
        output.WriteLine($"white p5/p95 {before:0.000} before, {after:0.000} after");
        Assert.True(after >= 0.95, $"white p5/p95 {after:0.000} after uniformity 1");
    }

    /// <summary>The light on a screen point while every output shows content signal <paramref name="content"/> through its measured
    /// correction, as the output stage draws it and the projectors' true response emits it.</summary>
    internal static double StageLight(MultiProjectorSim sim, AlignSolveResult r, Vec3d world, double content)
    {
        var total = sim.Camera.Ambient;
        for (var k = 0; k < sim.Projectors.Count; k++)
        {
            if (sim.ScreenToProjector(k, world) is not { } q)
                continue;
            var p = sim.Projectors[k];
            var o = r.Outputs[k];
            var (uv, ok) = o.Warp.Grid.Sample(q);
            var inside = ok >= 0.5 && uv.X >= 0 && uv.Y >= 0 && uv.X <= 1 && uv.Y <= 1;
            var lin = inside ? Math.Pow(content, o.Gamma) : 0;
            var (w, ped) = BlendAt(o.Blend, q, p.Intrinsics.Width, p.Intrinsics.Height);
            var e = ped + (1 - ped) * w * lin;
            var signal = Math.Pow(Math.Clamp(e, 0, 1), 1 / o.Gamma);
            total += p.Black + sim.WhiteAt(k, q) * Math.Pow(signal, p.Gamma);
        }
        return total;
    }

    private static (double Weight, double Pedestal) BlendAt(OutputBlend b, Vec2d q, int width, int height)
    {
        var fx = Math.Clamp(q.X / width * b.Width - 0.5, 0, b.Width - 1);
        var fy = Math.Clamp(q.Y / height * b.Height - 0.5, 0, b.Height - 1);
        var x0 = Math.Min((int)fx, b.Width - 2);
        var y0 = Math.Min((int)fy, b.Height - 2);
        var tx = fx - x0;
        var ty = fy - y0;
        double At(int x, int y, Func<int, int, float> f) => f(x, y);
        double Lerp(Func<int, int, float> f) =>
            (At(x0, y0, f) * (1 - tx) + At(x0 + 1, y0, f) * tx) * (1 - ty) + (At(x0, y0 + 1, f) * (1 - tx) + At(x0 + 1, y0 + 1, f) * tx) * ty;
        return (Lerp(b.Weight), Lerp(b.Pedestal));
    }
}
