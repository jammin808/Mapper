using PlayMap.Core.Time;

namespace PlayMap.Core.Tests.Time;

/// <summary>The cycle rate x64 Windows converts a thread's cycles at, from the measuring's spins as the counts it might meet would give
/// them. A count brought up to date at each read gives a rate never above the counter's, near it where a quarter of the spins ran
/// whole, so a span converted at it reads at least its work; a count brought up to date at a clock tick or at a shorter interval, or
/// spins that scatter, or a rate no x64 counter runs at, give no rate, with the words why. The spins are simulated: a 3 GHz counter, a
/// 10 MHz stopwatch, and 300 ns for each read of either.</summary>
public sealed class CycleRateTests
{
    private const double Rate = 3.0;
    private const double TickNs = 100;
    private const long SpinTicks = 10_000;
    private const double ReadNs = 300;
    private const int Spins = 16;

    [Fact]
    public void ACountBroughtUpToDateAtEachReadGivesTheFastestSpinsRateNeverAboveTheCounters()
    {
        var rate = ThreadTime.RateFrom(Measure((_, run) => Rate * run), TickNs);
        Assert.Equal("", rate.Why);
        Assert.Equal(Spins, rate.Agreeing);
        Assert.InRange(rate.CyclesPerNs, Rate * 0.999, Rate);
    }

    [Fact]
    public void AnInterruptedSpinOnlyLowersItsOwnRateAndAQuarterRunWholeStillGiveTheCounters()
    {
        // Twelve spins lose 20 µs to 600 µs of the processor; four run whole.
        var rate = ThreadTime.RateFrom(Measure((_, run) => Rate * run, i => i % 4 == 0 ? 0 : 20_000 + 50_000 * (i % 13)), TickNs);
        Assert.Equal("", rate.Why);
        Assert.Equal(Spins / 4, rate.Agreeing);
        Assert.InRange(rate.CyclesPerNs, Rate * 0.999, Rate);
    }

    [Fact]
    public void SpinsThatScatterGiveNoRate()
    {
        // Three run whole, and each of the rest loses a different share of the processor: the fastest is not established.
        var rate = ThreadTime.RateFrom(Measure((_, run) => Rate * run, i => i < 3 ? 0 : 20_000 + 40_000 * i), TickNs);
        Assert.Equal(0, rate.CyclesPerNs);
        Assert.Equal(3, rate.Agreeing);
        Assert.Contains("only 3 of 16 spins", rate.Why, StringComparison.Ordinal);
    }

    [Fact]
    public void ACountBroughtUpToDateOnlyAtTheClockTickGivesNoRate()
    {
        var spins = Measure((_, run) => Rate * Math.Floor(run / 15_625_000) * 15_625_000);
        var rate = ThreadTime.RateFrom(spins, TickNs);
        Assert.Equal(0, rate.CyclesPerNs);
        Assert.Contains("did not move forward between two reads", rate.Why, StringComparison.Ordinal);
    }

    [Fact]
    public void ACountBroughtUpToDateAtAShorterIntervalGivesNoRateThoughItsFastestSpinCountsMoreThanTheCounter()
    {
        // Brought up to date every 0.3 ms, a spin of a millisecond counts three or four of those: the fastest counts 1.2 ms of the
        // counter in a millisecond, and a rate taken from it would read every span a sixth short. Two reads straight after each other
        // fall in one interval.
        var spins = Measure((_, run) => Rate * Math.Floor(run / 300_000) * 300_000);
        Assert.Contains(spins, s => s.Cycles / ((s.Ticks + 1) * TickNs) > Rate * 1.1);
        var rate = ThreadTime.RateFrom(spins, TickNs);
        Assert.Equal(0, rate.CyclesPerNs);
        Assert.Contains("did not move forward between two reads", rate.Why, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(20.0)]
    [InlineData(0.05)]
    public void ARateNoX64CounterRunsAtGivesNoRate(double cyclesPerNs)
    {
        var rate = ThreadTime.RateFrom(Measure((_, run) => cyclesPerNs * run), TickNs);
        Assert.Equal(0, rate.CyclesPerNs);
        Assert.Contains("is not an x64 time-stamp counter's", rate.Why, StringComparison.Ordinal);
    }

    [Fact]
    public void NoSpinGivesNoRate()
    {
        var rate = ThreadTime.RateFrom([], TickNs);
        Assert.Equal(0, rate.CyclesPerNs);
        Assert.NotEqual("", rate.Why);
    }

    /// <summary>The spins the measuring takes of a count (cycles, given the stopwatch's time and the time the thread has run, in ns),
    /// as <c>ThreadTime</c> takes them: a stopwatch read, two cycle reads, spinning on the stopwatch until it has moved a millisecond's
    /// ticks, a cycle read and a stopwatch read. <paramref name="interrupted"/> is how long the machine takes the processor in each
    /// spin: the stopwatch moves on, the thread does not run.</summary>
    private static ThreadTime.RateSpin[] Measure(Func<double, double, double> count, Func<int, double>? interrupted = null)
    {
        double wall = 1_234_567, run = 0;
        var spins = new ThreadTime.RateSpin[Spins];
        for (var i = 0; i < spins.Length; i++)
        {
            var w0 = Watch();
            var c0 = Read();
            var next = Read();
            wall += interrupted?.Invoke(i) ?? 0;
            while ((long)(wall / TickNs) - w0 < SpinTicks)
                Watch();
            var c1 = Read();
            var w1 = Watch();
            spins[i] = new ThreadTime.RateSpin(c1 > c0 ? c1 - c0 : 0, next > c0 ? next - c0 : 0, w1 - w0);
        }
        return spins;

        long Watch()
        {
            var w = (long)(wall / TickNs);
            wall += ReadNs;
            run += ReadNs;
            return w;
        }

        ulong Read()
        {
            var c = (ulong)count(wall, run);
            wall += ReadNs;
            run += ReadNs;
            return c;
        }
    }
}
