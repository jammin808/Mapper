using System.Globalization;
using PlayMap.Core.Align;
using PlayMap.Core.Numerics;
using Xunit.Abstractions;

namespace PlayMap.Engine.Tests.Align;

/// <summary>The measured warp on the air, judged by the simulator's truth after ALIGN ACCEPT: the warp files the project names are read
/// back, and each canvas grid point is followed through every output that shows it onto the screen. Three outputs side by side, FIT
/// INSIDE: the points land on a regular grid (the best affine map of the canvas onto the screen's own metres) within 0.5 projector px
/// at the 95th percentile, from every output, seams included. FIT CORNERS from corners clicked where the screen's own rectangle is:
/// the points land where that rectangle puts them within 0.5 px. A stacked pair: both members put every canvas point within 0.5 px of
/// each other.</summary>
public sealed class MeasuredWarpTests(ITestOutputHelper output)
{
    private static readonly Vec2d[] Grid = [.. Enumerable.Range(0, 17 * 9).Select(i => new Vec2d(0.03 + 0.94 * (i % 17) / 16, 0.04 + 0.92 * (i / 17) / 8))];

    [Fact]
    public async Task ThreeSideBySideLandTheCanvasGridWithinHalfAPixel()
    {
        const string layout = "flat-screen:tile3x1:o20";
        await using var rig = new AlignRig(AlignRig.Project(3, layout));
        var group = await rig.NewGroup();
        var run = await rig.RunToEnd(group);
        await rig.Ok($"ALIGN ACCEPT {run}");
        var sim = AlignRig.Truth(3, layout);
        var warps = rig.Warps(3);

        var landings = Grid.Select(uv => (uv, rig.Landings(sim, warps, uv))).Where(x => x.Item2.Count > 0).ToList();
        Assert.True(landings.Count > Grid.Length * 0.95, $"{landings.Count} of {Grid.Length} grid points shown");
        Assert.Contains(landings, l => l.Item2.Count >= 2);
        var fit = Affine([.. landings.SelectMany(l => l.Item2.Select(x => (l.uv, x.Screen)))]);
        var errors = landings.SelectMany(l => l.Item2.Select(x => (fit(l.uv) - x.Screen).Length * x.PxPerM)).ToList();
        var p95 = AlignRig.Quantile(errors, 0.95);
        output.WriteLine($"3 × 1 INSIDE: p95 {p95:0.000} px over {errors.Count} landings");
        Assert.True(p95 <= 0.5, $"canvas grid points land {p95:0.000} px (p95) off a regular grid");
    }

    [Fact]
    public async Task CornersClickedOnTheScreenAreWhereTheCanvasLands()
    {
        const string layout = "flat-screen:tile3x1:o20";
        var sim = AlignRig.Truth(3, layout);
        // A 3.2 m × 0.6 m rectangle of the screen, inside what the three cover, clicked on the camera picture.
        var screen = new[] { new Vec2d(-1.6, -0.3), new Vec2d(1.6, -0.3), new Vec2d(1.6, 0.3), new Vec2d(-1.6, 0.3) };
        var clicks = screen.Select(s => sim.ScreenToCamera(new Vec3d(s.X, -s.Y, 0))!.Value).Select(c => new Vec2d(c.X / sim.CameraWidth, c.Y / sim.CameraHeight)).ToList();
        await using var rig = new AlignRig(AlignRig.Project(3, layout));
        var group = await rig.NewGroup();
        var corners = string.Join(' ', clicks.Select(c => string.Create(CultureInfo.InvariantCulture, $"{c.X:0.000000} {c.Y:0.000000}")));
        await rig.Ok($"ALIGN CORNERS {group} {corners}");
        await rig.Ok(string.Create(CultureInfo.InvariantCulture, $"SET {group} target.aspect {3.2 / 0.6:R}"));
        var run = await rig.RunToEnd(group);
        await rig.Ok($"ALIGN ACCEPT {run}");
        var warps = rig.Warps(3);
        var errors = new List<double>();
        foreach (var uv in Grid)
        {
            var want = screen[0] + (screen[1] - screen[0]) * uv.X + (screen[3] - screen[0]) * uv.Y;
            foreach (var l in rig.Landings(sim, warps, uv))
                errors.Add((l.Screen - want).Length * l.PxPerM);
        }
        Assert.True(errors.Count > Grid.Length, $"{errors.Count} landings");
        var p95 = AlignRig.Quantile(errors, 0.95);
        output.WriteLine($"3 × 1 CORNERS: p95 {p95:0.000} px over {errors.Count} landings");
        Assert.True(p95 <= 0.5, $"the canvas lands {p95:0.000} px (p95) off the clicked rectangle");
    }

    [Fact]
    public async Task AStackedPairConverges()
    {
        const string layout = "flat-screen:stack2";
        await using var rig = new AlignRig(AlignRig.Project(2, layout));
        var group = await rig.NewGroup();
        var run = await rig.RunToEnd(group);
        await rig.Ok($"ALIGN ACCEPT {run}");
        var sim = AlignRig.Truth(2, layout);
        var warps = rig.Warps(2);
        var gaps = new List<double>();
        foreach (var uv in Grid)
        {
            var l = rig.Landings(sim, warps, uv);
            if (l.Count == 2)
                gaps.Add((l[0].Screen - l[1].Screen).Length * (l[0].PxPerM + l[1].PxPerM) / 2);
        }
        Assert.True(gaps.Count > Grid.Length * 0.95, $"{gaps.Count} of {Grid.Length} points shown by both members");
        var p95 = AlignRig.Quantile(gaps, 0.95);
        output.WriteLine($"stack of 2: p95 {p95:0.000} px over {gaps.Count} points");
        Assert.True(p95 <= 0.5, $"the stack's members disagree by {p95:0.000} px (p95)");
    }

    /// <summary>The least-squares affine map from canvas uv to screen metres.</summary>
    private static Func<Vec2d, Vec2d> Affine(List<(Vec2d Uv, Vec2d Screen)> pairs)
    {
        var a = new DenseMatrix(pairs.Count, 3);
        var bx = new double[pairs.Count];
        var by = new double[pairs.Count];
        for (var i = 0; i < pairs.Count; i++)
        {
            a[i, 0] = pairs[i].Uv.X;
            a[i, 1] = pairs[i].Uv.Y;
            a[i, 2] = 1;
            bx[i] = pairs[i].Screen.X;
            by[i] = pairs[i].Screen.Y;
        }
        var cx = LinearSolve.LeastSquares(a, bx);
        var cy = LinearSolve.LeastSquares(a, by);
        return uv => new Vec2d(cx[0] * uv.X + cx[1] * uv.Y + cx[2], cy[0] * uv.X + cy[1] * uv.Y + cy[2]);
    }
}
