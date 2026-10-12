using PlayMap.Core.Scan;
using Xunit.Abstractions;

namespace PlayMap.Core.Tests.Scan;

/// <summary>The scan quality score: a clean scan grades good, a dark noisy one lower, a clipped one is told to
/// lower its exposure, a scan that failed on one side names that side, and a camera that sees nothing fails.</summary>
public class ScanQualityTests(ITestOutputHelper output)
{
    [Fact]
    public void ACleanScanGradesGood()
    {
        var report = ScanQuality.Assess(ScanTestKit.Robust.Map);
        output.WriteLine(report.Words);
        Assert.True(report.Score >= 90, $"score {report.Score}");
        Assert.Equal("Good", report.Grade);
        Assert.Empty(report.Advice);
        Assert.True(report.DecodedFraction > 0.99);
        Assert.StartsWith("Good scan (score ", report.Words, StringComparison.Ordinal);
        Assert.Contains("nothing clipped", report.Words, StringComparison.Ordinal);
    }

    [Fact]
    public void ADarkNoisyScanScoresLowerThanACleanOne()
    {
        var clean = ScanQuality.Assess(ScanTestKit.Robust.Map);
        var dark = ScanQuality.Assess(ScanTestKit.Run(ScanTestKit.Floor, new ScanPlanOptions { Mode = ScanMode.Robust },
            new SimOptions { NoiseSigma = 3, AlbedoScale = 0.3, Ambient = 0.06, Seed = 8 }).Map);
        output.WriteLine(dark.Words);
        Assert.True(dark.Score < clean.Score - 10, $"dark {dark.Score} vs clean {clean.Score}");
        Assert.True(dark.MeanConfidence < clean.MeanConfidence);
    }

    [Fact]
    public void AClippedScanIsToldToLowerItsExposure()
    {
        var report = ScanQuality.Assess(ScanTestKit.Clipped.Map);
        output.WriteLine(report.Words);
        Assert.True(report.SaturationShare > 0.05);
        Assert.Contains(report.Advice, a => a.Contains("lower the camera's exposure", StringComparison.Ordinal));
        Assert.True(report.Score < ScanQuality.Assess(ScanTestKit.Fast.Map).Score);
    }

    [Fact]
    public void AScanThatFailedOnOneSideNamesThatSide()
    {
        var scan = ScanTestKit.Run(ScanTestKit.Floor, new ScanPlanOptions { Mode = ScanMode.Fast });
        var map = scan.Map;
        // Spoil the right third of the picture's middle band: as if glare washed it out.
        for (var y = map.Height / 3; y < 2 * map.Height / 3; y++)
        {
            for (var x = 2 * map.Width / 3; x < map.Width; x++)
            {
                var k = y * map.Width + x;
                if ((map.Flags[k] & DecodeStatus.Valid) != 0 && (x + y) % 5 != 0)
                    map.Flags[k] &= ~DecodeStatus.Valid;
            }
        }
        map.Recount(map.Stats.UncertainBits, map.Stats.DecidedBits, map.Stats.NoiseSigma);
        var report = ScanQuality.Assess(map);
        output.WriteLine(report.Words);
        Assert.Equal("right", report.WorstRegion);
        Assert.InRange(report.WorstRegionDecoded, 0.1, 0.3);
        Assert.Contains(report.Advice, a => a.StartsWith("The right of the picture decodes poorly", StringComparison.Ordinal));
    }

    [Fact]
    public void ACameraThatSeesNothingFails()
    {
        var map = new CorrespondenceMap(64, 48, 256, 160, new PixelRect(0, 0, 256, 160), ScanAxes.Both, false);
        var report = ScanQuality.Assess(map);
        Assert.Equal(0, report.Score);
        Assert.Equal("Failed", report.Grade);
        Assert.Contains("barely sees", report.Words, StringComparison.Ordinal);
    }
}
