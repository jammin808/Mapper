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
    public void EachFramesGainIsReadFromThePictureAndADriftOnlyAMovingCameraHas()
    {
        var walking = TagKit.Floor(new SimOptions { GainWalk = 0.01, Seed = 5 });
        var frames = walking.Simulator.RenderAll();
        var truth = Enumerable.Range(0, walking.Plan.Count).Select(walking.Simulator.GainOf).ToArray();
        Assert.True(truth.Max() / truth.Min() > 1.03, "the walk moved");
        ReferenceSynthesis.Fill(walking.Plan, frames);
        var gains = GainNormaliser.Gains(walking.Plan, frames, GainNormaliser.LitMask(walking.Plan, frames));
        Assert.Equal(walking.Plan.Count - 2, gains.Count);
        // Read against references that carry gains of their own, the estimates are the true gains up to one common factor.
        var common = Median(gains.Select(g => g.Value / truth[g.Key]).ToList());
        foreach (var (i, g) in gains)
            Assert.True(Math.Abs(g / truth[i] / common - 1) < 0.01, $"frame {i}: read {g:0.000}, truth {truth[i]:0.000}");
        var drift = GainNormaliser.PrepareSteady(walking.Plan, walking.Simulator.RenderAll());
        Assert.True(drift > 2, $"drift {drift} %");

        // A camera that holds still reads as still, whatever the picture shows.
        var steady = TagKit.Floor(new SimOptions { Seed = 5 });
        var still = steady.Simulator.RenderAll();
        var stillDrift = GainNormaliser.PrepareSteady(steady.Plan, still);
        Assert.True(stillDrift < 0.5, $"a still camera drifted {stillDrift} %");

        // After levelling, the walking camera's sinusoids agree with the still camera's to within the noise.
        var levelled = walking.Simulator.RenderAll();
        GainNormaliser.PrepareSteady(walking.Plan, levelled);
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
                d += Math.Abs(levelled[i][k] - still[i][k]);
                n++;
            }
            Assert.True(d / n < 2.5, $"frame {i}: {d / n:0.00} levels from the still camera's");
        }
    }

    [Fact]
    public void ACameraWithAResponseCurveIsReadAsLightAndAStillOneDoesNotDrift()
    {
        // A phone's picture is gamma-encoded: its sinusoid steps no longer sum to white plus black, its binary patterns still do.
        var still = TagKit.Floor(new SimOptions { Seed = 5, CameraGamma = 2.2 });
        var frames = still.Simulator.RenderAll();
        ReferenceSynthesis.Fill(still.Plan, frames);
        var gamma = GainNormaliser.ResponseGamma(still.Plan, frames, GainNormaliser.LitMask(still.Plan, frames));
        Assert.InRange(gamma, 2.0, 2.4);
        var drift = GainNormaliser.PrepareSteady(still.Plan, still.Simulator.RenderAll());
        Assert.True(drift < 0.5, $"a still camera with a curve drifted {drift} %");

        // A walking one is read frame by frame through its curve, as a linear one is.
        var walking = TagKit.Floor(new SimOptions { GainWalk = 0.01, Seed = 5, CameraGamma = 2.2 });
        var walked = walking.Simulator.RenderAll();
        ReferenceSynthesis.Fill(walking.Plan, walked);
        var gains = GainNormaliser.Gains(walking.Plan, walked, GainNormaliser.LitMask(walking.Plan, walked));
        var common = Median(gains.Select(g => g.Value / walking.Simulator.GainOf(g.Key)).ToList());
        foreach (var (i, g) in gains)
            Assert.True(Math.Abs(g / walking.Simulator.GainOf(i) / common - 1) < 0.01, $"frame {i}: read {g:0.000}, truth {walking.Simulator.GainOf(i):0.000}");
        // And a linear camera is read as linear.
        var linearKit = TagKit.Floor(new SimOptions { Seed = 5 });
        var linear = linearKit.Simulator.RenderAll();
        ReferenceSynthesis.Fill(linearKit.Plan, linear);
        Assert.Equal(1.0, GainNormaliser.ResponseGamma(linearKit.Plan, linear, GainNormaliser.LitMask(linearKit.Plan, linear)), 2);
    }

    private static double Median(List<double> v)
    {
        v.Sort();
        return v.Count % 2 == 1 ? v[v.Count / 2] : (v[v.Count / 2 - 1] + v[v.Count / 2]) / 2;
    }
}
