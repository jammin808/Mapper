using PlayMap.Render.Timing;

namespace PlayMap.Render.Tests;

/// <summary>Frame pacing from swap instants: the period is learned, a late present counts its missed slots,
/// the next vertical blank is predicted, and the 95th percentile reads the slow tail.</summary>
public class FramePacerTests
{
    [Fact]
    public void ALatePresentCountsTheSlotsItMissed()
    {
        var pacer = new FramePacer(60);
        var t = 1_000_000_000L;
        var period = 16_666_667L;
        for (var i = 0; i < 20; i++)
        {
            t += period;
            pacer.OnPresented(t);
        }
        Assert.Equal(0, pacer.Missed);
        pacer.OnPresented(t + 3 * period);
        Assert.Equal(2, pacer.Missed);
        Assert.InRange(pacer.PeriodNs, period - 1000, period + 1000);
    }

    [Fact]
    public void TheNextVblankIsPredictedFromTheLastPresent()
    {
        var pacer = new FramePacer(50);
        var t = 0L;
        for (var i = 0; i < 20; i++)
        {
            t += 20_000_000;
            pacer.OnPresented(t);
        }
        Assert.Equal(t + 20_000_000, pacer.PredictNextVblank(t + 5_000_000));
        Assert.Equal(t + 40_000_000, pacer.PredictNextVblank(t + 25_000_000));
    }

    [Fact]
    public void ThePercentileReadsTheSlowTail()
    {
        var pacer = new FramePacer(60);
        var t = 0L;
        for (var i = 0; i < 100; i++)
        {
            t += i % 10 == 0 ? 33_333_333 : 16_666_667;
            pacer.OnPresented(t);
        }
        Assert.InRange(pacer.FrameMsP95, 33, 34);
        Assert.InRange(pacer.FrameMs, 18, 19);
        Assert.True(pacer.Missed >= 9);
    }
}
