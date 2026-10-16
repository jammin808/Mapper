using PlayMap.Core.Model;
using PlayMap.Core.Numerics;
using Xunit.Abstractions;

namespace PlayMap.Engine.Tests.Align;

/// <summary>ALIGN RUN … ONLY (autoalign M17): after an accepted run, P2 is knocked 3 px; ALIGN RUN … ONLY op2 shows its patterns on op2
/// alone (the simulated display counts what reached each output), keeps op1's warp file bit for bit, fits op2 again to the same canvas
/// from the captures the first run kept beside it, makes both blends again, and brings the seams back within 0.3 px.</summary>
public sealed class AlignOnlyTests(ITestOutputHelper log)
{
    private const string Layout = "flat-screen:tile2x1";

    private static readonly Vec2d[] Grid = [.. Enumerable.Range(0, 40 * 12).Select(i => new Vec2d((i % 40 + 0.5) / 40, (i / 40 + 0.5) / 12))];

    /// <summary>Where both outputs show a canvas point, how far apart on the screen (in output px): p95.</summary>
    private static double SeamP95(AlignRig rig, string layout)
    {
        var sim = AlignRig.Truth(2, layout);
        var warps = rig.Warps(2);
        var gaps = new List<double>();
        foreach (var uv in Grid)
        {
            var l = rig.Landings(sim, warps, uv);
            if (l.Count == 2)
                gaps.Add((l[0].Screen - l[1].Screen).Length * (l[0].PxPerM + l[1].PxPerM) / 2);
        }
        Assert.True(gaps.Count > 10, $"{gaps.Count} points in the overlap");
        return AlignRig.Quantile(gaps, 0.95);
    }

    [Fact]
    public async Task OnlyTheMovedOutputIsScannedAgainAndTheSeamsComeBack()
    {
        await using var rig = new AlignRig(AlignRig.Project(2, Layout));
        var group = await rig.NewGroup();
        var first = await rig.RunToEnd(group);
        await rig.Ok($"ALIGN ACCEPT {first}");
        var op1 = rig.Current.FindOutput("op1")!.Measured!;
        var op1Bytes = await File.ReadAllBytesAsync(Path.Combine(rig.Folder.FullName, op1.Warp!.File));
        var before = SeamP95(rig, Layout);

        // P2 knocked 3 px: the seams open.
        const string knocked = Layout + ":shift2=3";
        var r = await rig.Engine.EditAsync(AlignRig.Desk, "knock P2",
            p => p.WithStage(st => st with { Cameras = st.Cameras.Update("cm_sim", c => c with { Source = knocked }) }));
        Assert.True(r.Changed);
        var open = SeamP95(rig, knocked);
        log.WriteLine($"seams: {before:0.000} px after the first run, {open:0.000} px once P2 moved");
        Assert.True(open > 1.5, $"{open} px");

        Assert.Contains("every output", await rig.Refused($"ALIGN RUN {group} ONLY op1 op2"), StringComparison.Ordinal);
        Assert.Contains("not one of", await rig.Refused($"ALIGN RUN {group} ONLY op9"), StringComparison.Ordinal);
        Assert.Contains("whole group", await rig.Refused($"ALIGN RUN {group} ONLY op2 COLOR WHITE"), StringComparison.Ordinal);
        var second = await rig.RunToEnd(group, "ONLY op2");
        Assert.Equal("staged", rig.Runner.View!.State);
        var shown = rig.Runner.LastShown!;
        log.WriteLine($"frames shown: op1 {shown[0]}, op2 {shown[1]}");
        Assert.Equal(0, shown[0]);
        Assert.True(shown[1] > 10);
        await rig.Ok($"ALIGN ACCEPT {second}");

        // op1's warp is the very file it had; op2's is new; both blends are new.
        var now1 = rig.Current.FindOutput("op1")!.Measured!;
        var now2 = rig.Current.FindOutput("op2")!.Measured!;
        Assert.Equal(op1.Warp, now1.Warp);
        Assert.Equal(op1Bytes, await File.ReadAllBytesAsync(Path.Combine(rig.Folder.FullName, now1.Warp!.File)));
        Assert.Equal(second, now2.RunId);
        Assert.Contains(second, now2.Warp!.File, StringComparison.Ordinal);
        Assert.Contains(second, now1.Blend!.File, StringComparison.Ordinal);

        var back = SeamP95(rig, knocked);
        log.WriteLine($"after ONLY op2: seams {back:0.000} px");
        Assert.True(back <= 0.3, $"{back} px");
    }

    [Fact]
    public async Task ChainedOnlyRunsKeepOneCanvasAndAChangedCanvasIsRefused()
    {
        await using var rig = new AlignRig(AlignRig.Project(2, Layout));
        var group = await rig.NewGroup();
        await rig.Ok($"ALIGN ACCEPT {await rig.RunToEnd(group)}");
        async Task Knock(string layout)
        {
            var r = await rig.Engine.EditAsync(AlignRig.Desk, "knock",
                p => p.WithStage(st => st with { Cameras = st.Cameras.Update("cm_sim", c => c with { Source = layout }) }));
            Assert.True(r.Changed);
        }

        // P2 knocked and rescanned, then P1 knocked and rescanned: the second ONLY fits P1 to the canvas the first run made, so the
        // seams close again with P2's warp from the first ONLY untouched.
        await Knock(Layout + ":shift2=3");
        await rig.Ok($"ALIGN ACCEPT {await rig.RunToEnd(group, "ONLY op2")}");
        var op2 = rig.Current.FindOutput("op2")!.Measured!.Warp;
        const string both = Layout + ":shift2=3:shift1=-2";
        await Knock(both);
        Assert.True(SeamP95(rig, both) > 1.0);
        await rig.Ok($"ALIGN ACCEPT {await rig.RunToEnd(group, "ONLY op1")}");
        Assert.Equal(op2, rig.Current.FindOutput("op2")!.Measured!.Warp);
        var seam = SeamP95(rig, both);
        log.WriteLine($"after two ONLY runs: seams {seam:0.000} px");
        Assert.True(seam <= 0.3, $"{seam} px");

        // The canvas's own settings changed since that canvas was made: ONLY refuses with words, and nothing is staged.
        await rig.Ok($"SET {group} target.marginPct 5");
        await rig.RunToEnd(group, "ONLY op2");
        var view = rig.Runner.View!;
        Assert.Equal("failed", view.State);
        Assert.Contains("canvas settings", view.Words, StringComparison.Ordinal);
    }
}
