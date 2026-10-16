using PlayMap.Core.Align;
using Xunit.Abstractions;

namespace PlayMap.Engine.Tests.Align;

/// <summary>WHITE colour and level match (autoalign §3.5, the diagonal half), on the simulator: P1 a 6 500 K projector at full level, P2 a
/// 7 500 K one at 0.8 with a redder red. After the colour step the gains, put through each projector's own measured γ as the output
/// stage does, bring both whites within Δu′v′ 0.003 of each other and their levels within 3 %. A camera that sets its own exposure
/// refuses colour with words, while the geometry of the same run completes and is staged.</summary>
public sealed class ColourMatchTests(ITestOutputHelper log)
{
    private const string Layout = "flat-screen:tile2x1:k6500,7500:lvl1,0.8";

    [Fact]
    public async Task TwoProjectorsWhitesAndLevelsMatchAfterWhite()
    {
        var sim = AlignRig.Truth(2, Layout);
        var shooter = new SimShooter(sim);
        var specs = Enumerable.Range(0, 2).Select(j => new AlignOutputSpec($"op{j + 1}", $"P{j + 1}", AlignRig.W, AlignRig.H,
            ProjectorPrior.Unknown(AlignRig.W, AlignRig.H))).ToList();
        var (_, captures) = await AlignSequence.CaptureAsync(shooter, specs, null, CancellationToken.None);
        var colour = await AlignSequence.ColourAsync(shooter, specs, captures, null, CancellationToken.None);
        log.WriteLine(colour.Words);
        Assert.True(colour.Stable);
        Assert.NotNull(colour.Match);
        Assert.Equal(AlignSequence.ColourShots(2), colour.Shots);

        var footprints = captures.Select(c => c.Footprint).ToList();
        var cores = captures.Select(c => Photometry.Core(c.Footprint, footprints)).ToList();
        var blackFrames = new byte[]?[2];
        var black = (await shooter.ShootRgbAsync(blackFrames, CancellationToken.None))!;
        async Task<(double U, double V, double Y)> Look(int j, byte r, byte g, byte b)
        {
            var frames = new byte[]?[2];
            frames[j] = AlignSequence.SolidRgb(AlignRig.W, AlignRig.H, r, g, b, 1);
            var shot = (await shooter.ShootRgbAsync(frames, CancellationToken.None))!;
            var light = ColourMatch.Mean(shot.Rgb, cores[j]) - ColourMatch.Mean(black.Rgb, cores[j]);
            var (u, v) = ColourMatch.Uv(light);
            return (u, v, ColourMatch.Luminance(light));
        }
        static double Duv((double U, double V, double Y) a, (double U, double V, double Y) b) => Math.Sqrt((a.U - b.U) * (a.U - b.U) + (a.V - b.V) * (a.V - b.V));

        var before = new[] { await Look(0, 255, 255, 255), await Look(1, 255, 255, 255) };
        log.WriteLine($"before: Δu′v′ {Duv(before[0], before[1]):0.0000}, levels {before[0].Y:0.000} and {before[1].Y:0.000}");
        Assert.True(Duv(before[0], before[1]) > 0.006);

        // The output stage's measured colour: content white (1.0 in linear light) times the gain, out through the projector's own γ.
        var after = new (double U, double V, double Y)[2];
        for (var j = 0; j < 2; j++)
        {
            var g = colour.Match!.Gains[j];
            var gamma = captures[j].Photometry.Gamma;
            byte Level(double gain) => (byte)Math.Clamp(Math.Round(255 * Math.Pow(gain, 1 / gamma)), 0, 255);
            after[j] = await Look(j, Level(g.R), Level(g.G), Level(g.B));
            log.WriteLine($"P{j + 1}: gains {g.R:0.000} {g.G:0.000} {g.B:0.000} (γ {gamma:0.00}) → u′v′ {after[j].U:0.0000} {after[j].V:0.0000}, level {after[j].Y:0.000}");
        }
        var duv = Duv(after[0], after[1]);
        var levels = Math.Abs(after[0].Y - after[1].Y) / Math.Max(after[0].Y, after[1].Y);
        log.WriteLine($"after: Δu′v′ {duv:0.0000}, levels {levels * 100:0.0} % apart");
        Assert.True(duv <= 0.003, $"Δu′v′ {duv}");
        Assert.True(levels <= 0.03, $"levels {levels * 100:0.0} % apart");
    }

    [Fact]
    public async Task ARunWithWhiteStagesTheGainsAndAnAutoExposingCameraRefusesColourWhileTheGeometryCompletes()
    {
        await using (var rig = new AlignRig(AlignRig.Project(2, Layout)))
        {
            var group = await rig.NewGroup();
            var run = await rig.RunToEnd(group, "COLOR WHITE");
            var view = rig.Runner.View!;
            Assert.Equal("staged", view.State);
            log.WriteLine(view.Words);
            Assert.Contains("whites matched", view.Words, StringComparison.Ordinal);
            var record = rig.Runner.Finished(run)!;
            var p2 = record.Outputs[1].Measured.Color!;
            Assert.True(p2.GainR < 1 || p2.GainG < 1 || p2.GainB < 1);
            Assert.True(record.Outputs.All(o => o.Measured.Color is { GainR: <= 1, GainG: <= 1, GainB: <= 1 }));
            // Accepted, the gains are what the output stage draws.
            await rig.Ok($"ALIGN ACCEPT {run}");
            Assert.Equal(p2, rig.Current.FindOutput("op2")!.Measured!.Color);
        }

        await using (var rig = new AlignRig(AlignRig.Project(2, Layout + ":autoexposure")))
        {
            var group = await rig.NewGroup();
            var run = await rig.RunToEnd(group, "STEPS SCAN,SOLVE,COLOR");
            var view = rig.Runner.View!;
            log.WriteLine(view.Words);
            Assert.Equal("staged", view.State);
            Assert.Contains("sets its own exposure", view.Words, StringComparison.Ordinal);
            var record = rig.Runner.Finished(run)!;
            Assert.All(record.Outputs, o => Assert.Equal((1.0, 1.0, 1.0), (o.Measured.Color!.GainR, o.Measured.Color.GainG, o.Measured.Color.GainB)));
            Assert.All(record.Outputs, o => Assert.NotNull(o.Measured.Warp));
        }
    }

    [Fact]
    public async Task AStackMatchesItsMembersWhites()
    {
        await using var rig = new AlignRig(AlignRig.Project(2, "flat-screen:stack2:k6500,7500:lvl1,0.8"));
        var group = await rig.NewGroup();
        var run = await rig.RunToEnd(group, "COLOR WHITE");
        log.WriteLine(rig.Runner.View!.Words);
        Assert.Contains("whites matched", rig.Runner.View!.Words, StringComparison.Ordinal);
        Assert.True(rig.Runner.Finished(run)!.Outputs[1].Measured.Color is { } c && (c.GainR < 1 || c.GainG < 1 || c.GainB < 1));
    }
}
