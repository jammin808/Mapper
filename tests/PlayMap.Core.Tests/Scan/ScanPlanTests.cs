using PlayMap.Core.Scan;

namespace PlayMap.Core.Tests.Scan;

/// <summary>The scan plan: how many frames each mode shows, what each frame is, and that the CPU renderer
/// draws every Gray level and its inverse as exact complements inside the scanned region and black outside.</summary>
public class ScanPlanTests
{
    [Fact]
    public void GrayCodeRoundTripsAndNeighboursDifferInOneBit()
    {
        for (var v = 0; v < 4096; v++)
        {
            Assert.Equal(v, GrayCode.Decode(GrayCode.Encode(v)));
            if (v > 0)
                Assert.Equal(1, System.Numerics.BitOperations.PopCount((uint)(GrayCode.Encode(v) ^ GrayCode.Encode(v - 1))));
        }
        Assert.Equal(11, GrayCode.LevelsFor(1920));
        Assert.Equal(8, GrayCode.LevelsFor(256));
        Assert.Equal(8, GrayCode.LevelsFor(160));
        Assert.Equal(1, GrayCode.LevelsFor(1));
    }

    [Fact]
    public void FastModeShowsWhiteBlackAndEveryGrayLevelWithItsInverse()
    {
        var plan = ScanPlan.Create(256, 160, new ScanPlanOptions { Mode = ScanMode.Fast });
        Assert.Equal(2 + 2 * 8 + 2 * 8, plan.Count);
        Assert.False(plan.HasPhase);
        Assert.Equal(ScanFrameKind.White, plan.Frames[0].Kind);
        Assert.Equal(ScanFrameKind.Black, plan.Frames[1].Kind);
        Assert.Equal(new ScanFrame(2, ScanFrameKind.Gray, ScanAxis.Columns, 7, false, false, 0, 0, 0), plan.Frames[2]);
        Assert.Equal(new ScanFrame(3, ScanFrameKind.Gray, ScanAxis.Columns, 7, true, false, 0, 0, 0), plan.Frames[3]);
        Assert.Equal(0, plan.Frames[17].Level);
        Assert.Equal(ScanAxis.Rows, plan.Frames[18].Axis);
        Assert.Equal(0, plan.Columns!.LowestLevel);
        Assert.Equal(-1, plan.Columns.ComplementaryLevel);
        for (var i = 0; i < plan.Count; i++)
            Assert.Equal(i, plan.Frames[i].Index);
    }

    [Fact]
    public void RobustModeStopsTheGrayCodeAtThePeriodAndAddsTheComplementaryLevelAndFourPhaseSteps()
    {
        var plan = ScanPlan.Create(256, 160, new ScanPlanOptions { Mode = ScanMode.Robust });
        // Per axis: levels 7..4 (4 pairs) + complementary level 3 (1 pair) + 4 phase steps.
        Assert.Equal(2 + 2 * (2 * 5 + 4), plan.Count);
        Assert.True(plan.HasPhase);
        Assert.Equal(4, plan.Columns!.LowestLevel);
        Assert.Equal(3, plan.Columns.ComplementaryLevel);
        Assert.Equal(3, plan.Columns.FinestLevel);
        Assert.Equal(new[] { 16 }, plan.Columns.Periods.ToArray());
        var phase = plan.Frames.Where(f => f.Kind == ScanFrameKind.Phase).ToList();
        Assert.Equal(8, phase.Count);
        Assert.All(phase, f => Assert.Equal(16, f.Period));
        Assert.Equal([0, 1, 2, 3, 0, 1, 2, 3], phase.Select(f => f.PhaseStep));
        Assert.Equal("columns phase 16 px step 1/4", phase[0].Name);
        Assert.Equal(2, plan.Frames.Count(f => f.Kind == ScanFrameKind.Complementary && f.Axis == ScanAxis.Rows));
    }

    [Fact]
    public void DifficultModeXorCodesEveryLevelCoarserThanTheBase()
    {
        var plan = ScanPlan.Create(256, 160, new ScanPlanOptions { Mode = ScanMode.Difficult });
        Assert.Equal(3, plan.Columns!.XorBaseLevel);
        var gray = plan.Frames.Where(f => f.Kind == ScanFrameKind.Gray).ToList();
        Assert.All(gray, f => Assert.True(f.XorCoded));
        Assert.All(plan.Frames.Where(f => f.Kind == ScanFrameKind.Complementary), f => Assert.False(f.XorCoded));
        // Every XOR-coded column frame changes at least every 8 pixels: no long-range stripe is shown.
        foreach (var f in gray.Where(f => f.Axis == ScanAxis.Columns))
        {
            var row = plan.Render(f.Index).AsSpan(0, 256).ToArray();
            var run = 1;
            var longest = 1;
            for (var x = 1; x < row.Length; x++)
            {
                run = row[x] == row[x - 1] ? run + 1 : 1;
                longest = Math.Max(longest, run);
            }
            Assert.True(longest <= 16, $"{f.Name}: a run of {longest} pixels");
        }
    }

    [Fact]
    public void ColumnsOnlyAndMultiplePeriodsAreCountedPerAxis()
    {
        var plan = ScanPlan.Create(1920, 1080, new ScanPlanOptions { Mode = ScanMode.Robust, Axes = ScanAxes.Columns, PhasePeriods = [64, 16] });
        Assert.Null(plan.Rows);
        // Levels 10..6 (5 pairs) + complementary 5 + two 4-step sinusoids.
        Assert.Equal(2 + 2 * 6 + 8, plan.Count);
        Assert.Throws<ArgumentException>(() => ScanPlan.Create(256, 160, new ScanPlanOptions { PhasePeriods = [24] }));
        Assert.Throws<ArgumentException>(() => ScanPlan.Create(256, 160, new ScanPlanOptions { PhasePeriods = [16, 32] }));
    }

    [Theory]
    [InlineData(ScanMode.Fast)]
    [InlineData(ScanMode.Robust)]
    [InlineData(ScanMode.Difficult)]
    public void EachGrayFrameAndItsInverseAreComplementsInsideTheRegionAndBlackOutside(ScanMode mode)
    {
        var region = new PixelRect(37, 21, 150, 101);
        var plan = ScanPlan.Create(256, 160, new ScanPlanOptions { Mode = mode, Region = region });
        Assert.Equal(region, plan.Region);
        var pairs = 0;
        for (var i = 0; i < plan.Count; i++)
        {
            var f = plan.Frames[i];
            if (f.Kind is not (ScanFrameKind.Gray or ScanFrameKind.Complementary) || f.Inverse)
                continue;
            var inverse = plan.Frames[i + 1];
            Assert.True(inverse.Inverse && inverse.Level == f.Level && inverse.Axis == f.Axis);
            var a = plan.Render(i);
            var b = plan.Render(i + 1);
            for (var y = 0; y < 160; y++)
            {
                for (var x = 0; x < 256; x++)
                {
                    var k = y * 256 + x;
                    if (region.Contains(x, y))
                        Assert.Equal(255, a[k] + b[k]);
                    else
                        Assert.Equal(0, a[k] + b[k]);
                    Assert.Equal(a[k], plan.ValueAt(i, x, y));
                }
            }
            pairs++;
        }
        Assert.Equal(plan.Columns!.GrayLevelCount + plan.Rows!.GrayLevelCount, pairs);
        var white = plan.Render(plan.WhiteIndex);
        Assert.Equal(region.Width * region.Height * 255L, white.Sum(v => (long)v));
        Assert.All(plan.Render(plan.BlackIndex), v => Assert.Equal(0, v));
    }

    [Fact]
    public void ThePhaseFramesAreSinusoidsWhoseStepsSumToAConstant()
    {
        var plan = ScanPlan.Create(256, 160, new ScanPlanOptions { Mode = ScanMode.Robust, ProjectorGamma = 1 });
        var steps = plan.Frames.Where(f => f.Kind == ScanFrameKind.Phase && f.Axis == ScanAxis.Columns).Select(f => plan.Render(f.Index)).ToList();
        Assert.Equal(4, steps.Count);
        for (var x = 0; x < 256; x++)
        {
            // Σ cos(φ − 2πn/4) = 0, so the four levels sum to 2 × full scale (± rounding).
            var sum = steps.Sum(s => (int)s[x]);
            Assert.InRange(sum, 508, 512);
            var expected = 127.5 + 127.5 * Math.Cos(2 * Math.PI * (x + 0.5) / 16);
            Assert.InRange(steps[0][x], expected - 0.51, expected + 0.51);
        }
        // Rows frames are constant along a row.
        var rowFrame = plan.Frames.First(f => f.Kind == ScanFrameKind.Phase && f.Axis == ScanAxis.Rows);
        var image = plan.Render(rowFrame.Index);
        Assert.All(image.AsSpan(256 * 9, 256).ToArray(), v => Assert.Equal(image[256 * 9], v));
    }

    [Fact]
    public void ThePlanDescribesItselfInWords()
    {
        var plan = ScanPlan.Create(1920, 1080, new ScanPlanOptions { Mode = ScanMode.Robust });
        Assert.Equal("Robust scan of 1920x1080: 42 frames, columns 7 Gray levels + complementary + 4-step phase of 16 px; rows 7 Gray levels + complementary + 4-step phase of 16 px", plan.Describe());
        Assert.Equal("columns bit 10 inverse", plan.Frames[3].Name);
    }
}
