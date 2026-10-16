using PlayMap.Core.Align;
using PlayMap.Engine.Align;
using Xunit.Abstractions;

namespace PlayMap.Engine.Tests.Align;

/// <summary>A timed acceptance of the whole flat path: four projectors in a 2 × 2 tile through every step (identify, scan, solve, colour,
/// verify) in at most 90 s of pattern time — the run's pictures times a picture's hold (its present, the camera's latency, one settled
/// picture). The pictures counted on the simulated run are the ones the plan's own arithmetic gives, and the same arithmetic for
/// projectors at 1920 × 1080 stays inside the bar too.</summary>
public sealed class AlignTimingTests(ITestOutputHelper log)
{
    [Fact]
    public async Task FourFlatProjectorsAlignInUnderNinetySecondsOfPatterns()
    {
        await using var rig = new AlignRig(AlignRig.Project(4, "flat-screen:tile2x2:k6500,7000,6800,7200"));
        var group = await rig.NewGroup();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        await rig.RunToEnd(group, "STEPS ALL");
        var view = rig.Runner.View!;
        log.WriteLine(view.Words);
        Assert.Equal("staged", view.State);
        Assert.Contains("whites matched", view.Words, StringComparison.Ordinal);
        Assert.Contains("verified", view.Words, StringComparison.Ordinal);

        static int Pictures(int w, int h) =>
            AlignSequence.Shots([.. Enumerable.Range(0, 4).Select(j => new AlignOutputSpec($"op{j + 1}", $"P{j + 1}", w, h, ProjectorPrior.Unknown(w, h)))])
            + AlignSequence.ColourShots(4) + AlignVerify.Shots(4);
        var hold = new AlignRunnerOptions().PatternHold.TotalSeconds;
        var pictures = rig.Runner.LastPictures;
        Assert.Equal(Pictures(AlignRig.W, AlignRig.H), pictures);
        var seconds = pictures * hold;
        var hd = Pictures(1920, 1080) * hold;
        log.WriteLine($"{pictures} pictures: {seconds:0.0} s of patterns here, {hd:0.0} s at 1920 × 1080; {watch.Elapsed.TotalSeconds:0.0} s of machine time");
        Assert.True(seconds <= 90, $"{seconds} s");
        Assert.True(hd <= 90, $"{hd} s at 1920 × 1080");
    }
}
