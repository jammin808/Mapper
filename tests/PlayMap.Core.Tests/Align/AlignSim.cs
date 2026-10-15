using PlayMap.Core.Align;
using PlayMap.Core.Model;
using PlayMap.Core.Numerics;

namespace PlayMap.Core.Tests.Align;

/// <summary>An alignment run on the simulator, start to end, as the engine runs it (the camera sequence, then the solve), and the truth
/// metrics the tests read: where a canvas point lands on the screen from each output that shows it.</summary>
internal static class AlignSim
{
    public static List<AlignOutputSpec> Specs(MultiProjectorSim sim, double focalSigma = 0.05) =>
        [.. sim.Projectors.Select((p, j) => new AlignOutputSpec($"op{j + 1}", $"P{j + 1}", p.Intrinsics.Width, p.Intrinsics.Height,
            new ProjectorPrior(p.Intrinsics.Width, p.Intrinsics.Height, p.Intrinsics.Cx, p.Intrinsics.Cy, p.Intrinsics.Fx, focalSigma)))];

    public static async Task<(AlignSolveResult Result, SimShooter Shooter)> RunAsync(MultiProjectorSim sim, CanvasFit fit = CanvasFit.Inside,
        double aspect = 0, IReadOnlyList<Vec2d>? corners = null, AlignOptions? options = null)
    {
        var shooter = new SimShooter(sim);
        var (black, captures) = await AlignSequence.CaptureAsync(shooter, Specs(sim), null, CancellationToken.None);
        var result = AlignSolve.Solve(new AlignSolveInput(sim.CameraWidth, sim.CameraHeight, black.Gray, captures, fit, aspect, corners ?? [], 0,
            options ?? AlignOptions.Default));
        return (result, shooter);
    }

    /// <summary>The raster point of output <paramref name="j"/> that shows canvas <paramref name="uv"/>, or null.</summary>
    public static Vec2d? RasterOf(AlignOutputResult o, CanvasInverse inverse, Vec2d uv) =>
        inverse.At(uv) is { } start ? o.Warp.Grid.Invert(uv, start) : null;

    /// <summary>For every output that shows canvas <paramref name="uv"/>: the output, its raster point, the screen point it lights (in the
    /// world and in the screen's own metres) and that output's pixels per metre there.</summary>
    public static List<(int Output, Vec2d Raster, Vec3d World, Vec2d Screen, double PxPerM)> Landings(MultiProjectorSim sim, AlignSolveResult result,
        IReadOnlyList<CanvasInverse> inverses, Vec2d uv)
    {
        var list = new List<(int, Vec2d, Vec3d, Vec2d, double)>();
        for (var j = 0; j < result.Outputs.Length; j++)
        {
            if (RasterOf(result.Outputs[j], inverses[j], uv) is not { } q || sim.ProjectorToScreen(j, q) is not { } world)
                continue;
            list.Add((j, q, world, sim.OnScreen(world), sim.PixelsPerMetre(j, q)));
        }
        return list;
    }

    public static List<CanvasInverse> Inverses(AlignSolveResult result) =>
        [.. result.Outputs.Select(o => CanvasInverse.Build(o.Warp.Grid, 1024, 1024))];

    public static double Quantile(List<double> values, double q)
    {
        values.Sort();
        return values[Math.Min(values.Count - 1, (int)(q * values.Count))];
    }
}
