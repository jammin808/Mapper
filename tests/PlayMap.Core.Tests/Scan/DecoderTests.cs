using PlayMap.Core.Scan;
using Xunit.Abstractions;

namespace PlayMap.Core.Tests.Scan;

/// <summary>The decoder on simulated scans of a floor with a box: Fast decodes whole projector pixels, Robust
/// decodes a fraction of a pixel, both hold up under noise and a dark scene, shadows and clipped pixels are
/// refused, a region limits the decode, and the XOR-coded Difficult mode survives a bounce of light that
/// flips the plain code's coarse stripes.</summary>
public class DecoderTests(ITestOutputHelper output)
{
    [Fact]
    public void AFastScanDecodesNinetyFivePercentOfLitPixelsWithinOneProjectorPixel()
    {
        var scan = ScanTestKit.Fast;
        var lit = scan.LitPixels();
        Assert.True(lit.Count > 20000, $"{lit.Count} lit pixels");
        var within = scan.ShareWithin(lit, 1.0);
        var (rms, decoded) = scan.Rms(lit);
        output.WriteLine($"Fast: {within:P2} within 1 px, RMS {rms:0.000} px over {decoded}/{lit.Count}; {scan.Map.Stats}");
        Assert.True(within >= 0.95, $"{within:P2} within 1 px");
        Assert.True(rms < 0.45, $"RMS {rms:0.000}");
        Assert.False(scan.Map.SubPixel);
        // A Gray decode lands on a column's centre.
        var k = lit[lit.Count / 2];
        Assert.Equal(0.5, scan.Map.ProjectorX[k] - Math.Floor(scan.Map.ProjectorX[k]), 6);
    }

    [Fact]
    public void ARobustScanDecodesToAQuarterPixelRms()
    {
        var scan = ScanTestKit.Robust;
        var lit = scan.LitPixels();
        var (rms, decoded) = scan.Rms(lit);
        var within = scan.ShareWithin(lit, 1.0);
        output.WriteLine($"Robust: RMS {rms:0.0000} px over {decoded}/{lit.Count}, {within:P2} within 1 px; {scan.Map.Stats}");
        Assert.True(scan.Map.SubPixel);
        Assert.True(rms <= 0.25, $"RMS {rms:0.000} px");
        Assert.True(within >= 0.97, $"{within:P2} within 1 px");
        Assert.True(scan.Map.Stats.MeanConfidence > 0.6, $"mean confidence {scan.Map.Stats.MeanConfidence:0.00}");
    }

    [Theory]
    [InlineData(ScanMode.Fast)]
    [InlineData(ScanMode.Robust)]
    [InlineData(ScanMode.Difficult)]
    public void DecodingHoldsUpUnderNoiseAndADarkerScene(ScanMode mode)
    {
        var scan = ScanTestKit.Run(SimScenes.FloorAndBox(), new ScanPlanOptions { Mode = mode },
            new SimOptions { NoiseSigma = 3, AlbedoScale = 0.4, Ambient = 0.06, Seed = 5 });
        var lit = scan.LitPixels(24);
        var within = scan.ShareWithin(lit, 1.0);
        var (rms, decoded) = scan.Rms(lit, excludeAbove: 4);
        var gross = scan.GrossShare(lit);
        output.WriteLine($"{mode} dark+noisy: {within:P2} within 1 px, RMS {rms:0.000}, {gross:P3} off by > 4 px, over {decoded}/{lit.Count}; {scan.Map.Stats}");
        Assert.True(lit.Count > 10000);
        Assert.True(within >= (mode == ScanMode.Fast ? 0.95 : 0.9), $"{within:P2} within 1 px");
        Assert.True(rms < 0.5, $"RMS {rms:0.000}");
        Assert.True(gross < 0.002, $"{gross:P3} off by more than 4 px");
        Assert.True(scan.Map.Stats.DecodedFraction > 0.95, $"{scan.Map.Stats.DecodedFraction:P1} decoded");
        if (mode != ScanMode.Fast)
            Assert.InRange(scan.Map.Stats.NoiseSigma, 2.4, 3.6);
    }

    [Fact]
    public void ShadowedPixelsAreMarkedInvalid()
    {
        foreach (var scan in new[] { ScanTestKit.Fast, ScanTestKit.Robust })
        {
            var t = scan.Truth;
            var shadowed = 0;
            var refused = 0;
            for (var k = 0; k < t.Shadowed.Length; k++)
            {
                if (!t.Shadowed[k])
                    continue;
                shadowed++;
                if ((scan.Map.Flags[k] & DecodeStatus.Valid) == 0)
                    refused++;
            }
            output.WriteLine($"{scan.Plan.Mode}: {refused}/{shadowed} shadowed pixels refused");
            Assert.True(shadowed > 500, $"{shadowed} shadowed pixels");
            Assert.True(refused >= 0.99 * shadowed, $"{refused}/{shadowed} refused");
        }
    }

    [Fact]
    public void ARegionScanDecodesOnlyInsideTheRegion()
    {
        var region = new PixelRect(64, 40, 120, 80);
        var scan = ScanTestKit.Run(SimScenes.FloorAndBox(), new ScanPlanOptions { Mode = ScanMode.Robust, Region = region });
        var t = scan.Truth;
        var map = scan.Map;
        var valid = 0;
        for (var k = 0; k < map.Flags.Length; k++)
        {
            if ((map.Flags[k] & DecodeStatus.Valid) == 0)
                continue;
            valid++;
            Assert.InRange(map.ProjectorX[k], region.X, region.Right);
            Assert.InRange(map.ProjectorY[k], region.Y, region.Bottom);
            if (float.IsNaN(t.ProjectorX[k]))
            {
                // Only a camera pixel looking just past the region's edge sees some of its light.
                var nearEdge = Math.Min(Math.Abs(map.ProjectorX[k] - region.X), Math.Abs(map.ProjectorX[k] - region.Right)) <= 1
                    || Math.Min(Math.Abs(map.ProjectorY[k] - region.Y), Math.Abs(map.ProjectorY[k] - region.Bottom)) <= 1;
                Assert.True(nearEdge, $"camera pixel {k} decoded to {map.ProjectorX[k]}, {map.ProjectorY[k]} outside the region's light");
            }
        }
        var lit = scan.LitPixels();
        var (rms, _) = scan.Rms(lit);
        Assert.True(valid > 3000, $"{valid} valid");
        Assert.True(rms <= 0.25, $"RMS {rms:0.000}");
        Assert.True(scan.ShareWithin(lit, 1) >= 0.95);
    }

    [Fact]
    public void AGammaCameraIsLinearisedBeforeTheSinusoidsAreRead()
    {
        var scan = ScanTestKit.Run(SimScenes.FloorAndBox(), new ScanPlanOptions { Mode = ScanMode.Robust },
            new SimOptions { CameraGamma = 2.2 }, new DecodeOptions { CameraGamma = 2.2 });
        var (rms, _) = scan.Rms(scan.LitPixels());
        output.WriteLine($"gamma camera: RMS {rms:0.000}");
        Assert.True(rms <= 0.25, $"RMS {rms:0.000}");
    }

    [Fact]
    public void FloatFramesDecodeLikeTheirBytes()
    {
        var scan = ScanTestKit.Robust;
        var floats = scan.Frames.Select(f => f.Select(v => v / 255f).ToArray()).ToList();
        var map = new StructuredLightDecoder(scan.Plan).Decode(ScanTestKit.CameraWidth, ScanTestKit.CameraHeight, floats);
        Assert.Equal(scan.Map.Stats.Valid, map.Stats.Valid);
        for (var k = 0; k < map.Flags.Length; k += 37)
        {
            Assert.Equal(scan.Map.Flags[k], map.Flags[k]);
            if ((map.Flags[k] & DecodeStatus.Valid) != 0)
                Assert.Equal(scan.Map.ProjectorX[k], map.ProjectorX[k], 3);
        }
        Assert.Equal(scan.Frames[scan.Plan.WhiteIndex], map.White);
    }

    [Fact]
    public void ClippedWhitePixelsAreRefusedAndCounted()
    {
        var scan = ScanTestKit.Run(SimScenes.FloorAndBox(), new ScanPlanOptions { Mode = ScanMode.Fast }, new SimOptions { Exposure = 2.2 });
        var stats = scan.Map.Stats;
        output.WriteLine(stats.ToString());
        Assert.True(stats.Saturated > 1000, $"{stats.Saturated} saturated");
        Assert.True(stats.SaturationShare > 0.05);
        for (var k = 0; k < scan.Map.Flags.Length; k++)
        {
            if ((scan.Map.Flags[k] & DecodeStatus.Saturated) != 0)
            {
                Assert.Equal(DecodeStatus.Lit | DecodeStatus.Saturated, scan.Map.Flags[k]);
                Assert.True(float.IsNaN(scan.Map.ProjectorX[k]));
                Assert.Equal(0f, scan.Map.Confidence[k]);
            }
        }
    }

    [Fact]
    public void TheStatsCountWhatTheFlagsSay()
    {
        var map = ScanTestKit.Robust.Map;
        var stats = map.Stats;
        Assert.Equal(map.Flags.Count(f => (f & DecodeStatus.Valid) != 0), stats.Valid);
        Assert.Equal(map.Flags.Count(f => (f & DecodeStatus.Lit) != 0), stats.Lit);
        Assert.Equal(ScanTestKit.CameraWidth * ScanTestKit.CameraHeight, stats.Pixels);
        Assert.True(stats.DecodedFraction is > 0.9 and <= 1);
        Assert.True(stats.DecidedBits > 0 && stats.UncertainBits < stats.DecidedBits / 10);
        var pairs = map.Pairs(0.5f, 4);
        Assert.NotEmpty(pairs);
        Assert.All(pairs, p => Assert.True(p.Confidence >= 0.5f));
        Assert.True(map.TryGet((int)(pairs[0].Camera.X - 0.5), (int)(pairs[0].Camera.Y - 0.5), out var projector));
        Assert.Equal(pairs[0].Projector, projector);
    }

    [Fact]
    public void XorCodesSurviveABounceThatFlipsThePlainCodesCoarseStripes()
    {
        // A crude long-range interreflection: the right half of the picture also receives light bounced from
        // the left half — 1.5 × the left half's mean brightness in each frame. A coarse stripe lighting the
        // left half floods the right half's dark pixels; a fine stripe lights both halves alike and the bounce
        // cancels between a pattern and its inverse.
        byte[][] Bounce(byte[][] frames)
        {
            var w = ScanTestKit.CameraWidth;
            var h = ScanTestKit.CameraHeight;
            return frames.Select(f =>
            {
                double sum = 0;
                for (var y = 0; y < h; y++)
                    for (var x = 0; x < w / 2; x++)
                        sum += f[y * w + x];
                var bounce = 1.5 * sum / (h * w / 2);
                var g = (byte[])f.Clone();
                for (var y = 0; y < h; y++)
                    for (var x = w / 2; x < w; x++)
                        g[y * w + x] = (byte)Math.Clamp(g[y * w + x] + bounce, 0, 255);
                return g;
            }).ToArray();
        }
        var sim = new SimOptions { ProjectorGain = 0.5, Seed = 3 };
        var plain = ScanTestKit.Run(SimScenes.FloorAndBox(), new ScanPlanOptions { Mode = ScanMode.Robust }, sim, alter: Bounce);
        var xor = ScanTestKit.Run(SimScenes.FloorAndBox(), new ScanPlanOptions { Mode = ScanMode.Difficult }, sim, alter: Bounce);
        var right = plain.LitPixels().Where(k => k % ScanTestKit.CameraWidth >= ScanTestKit.CameraWidth / 2 + 4).ToList();
        var plainWithin = plain.ShareWithin(right, 1);
        var xorWithin = xor.ShareWithin(right, 1);
        output.WriteLine($"bounce: plain {plainWithin:P2}, xor {xorWithin:P2} within 1 px over {right.Count}");
        Assert.True(xorWithin >= 0.9, $"XOR {xorWithin:P2}");
        Assert.True(plainWithin < xorWithin - 0.1, $"plain {plainWithin:P2} vs XOR {xorWithin:P2}");
    }
}
