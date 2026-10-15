using PlayMap.Core.Scan;
using PlayMap.Core.Tests.Phone;

namespace PlayMap.Core.Tests.Scan;

/// <summary>The steady profile's maths on the simulator: references made from the coarse pairs stand in for the white and black the
/// camera never saw, and a camera's auto-exposure walk is levelled out of the coded frames with its size reported.</summary>
public sealed class ReferenceSynthesisTests
{
    [Fact]
    public void SynthesisedReferencesMatchTheCapturedOnesOnTheLitPicture()
    {
        var kit = TagKit.Floor();
        var frames = kit.Simulator.RenderAll();
        var white = frames[kit.Plan.WhiteIndex];
        var black = frames[kit.Plan.BlackIndex];
        var copy = frames.ToArray();
        ReferenceSynthesis.Fill(kit.Plan, copy);
        Assert.Equal(6, ReferenceSynthesis.Pairs(kit.Plan).Count);
        var truth = kit.Simulator.Truth;
        double whiteErr = 0, blackErr = 0;
        var n = 0;
        for (var k = 0; k < truth.Lit.Length; k++)
        {
            if (!truth.Lit[k] || truth.ExpectedContrast[k] < 30)
                continue;
            whiteErr += Math.Abs(copy[kit.Plan.WhiteIndex][k] - white[k]);
            blackErr += Math.Abs(copy[kit.Plan.BlackIndex][k] - black[k]);
            n++;
        }
        Assert.True(n > 1000);
        // Within the noise of one frame (σ 1 level) and a little blur at stripe edges.
        Assert.True(whiteErr / n < 2.5, $"white {whiteErr / n:0.00} levels off");
        Assert.True(blackErr / n < 2.5, $"black {blackErr / n:0.00} levels off");
    }

    [Fact]
    public void AWalkingGainIsLevelledAndItsDriftReported()
    {
        var walking = TagKit.Floor(new SimOptions { GainWalk = 0.01, Seed = 5 });
        var frames = walking.Simulator.RenderAll();
        var gains = Enumerable.Range(0, walking.Plan.Count).Select(walking.Simulator.GainOf).ToArray();
        Assert.True(gains.Max() / gains.Min() > 1.03, "the walk moved");
        var drift = GainNormaliser.PrepareSteady(walking.Plan, frames);
        Assert.True(drift > 1, $"drift {drift} %");

        var steady = TagKit.Floor(new SimOptions { Seed = 5 });
        var still = steady.Simulator.RenderAll();
        Assert.True(GainNormaliser.PrepareSteady(steady.Plan, still) < drift);
        // After levelling, the phase frames of the walking camera agree with the still camera's to within a level or two.
        var lit = GainNormaliser.LitMask(steady.Plan, still);
        for (var i = 0; i < steady.Plan.Count; i++)
        {
            if (steady.Plan.Frames[i].Kind != ScanFrameKind.Phase)
                continue;
            double d = 0;
            var n = 0;
            for (var k = 0; k < lit.Length; k++)
            {
                if (!lit[k])
                    continue;
                d += Math.Abs(frames[i][k] - still[i][k]);
                n++;
            }
            Assert.True(d / n < 2.5, $"frame {i}: {d / n:0.00} levels from the still camera's");
        }
    }
}
