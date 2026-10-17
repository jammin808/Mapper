using System.Globalization;
using PlayMap.Core.Numerics;
using PlayMap.Core.Time;

namespace PlayMap.Core.Tests.Time;

/// <summary>The cycle rate x64 Windows converts a thread's cycles at, from the measuring's spins as the counts it might meet would give
/// them. A count brought up to date at each read gives a rate never above the counter's, near it where a quarter of the spins ran
/// whole, so a span converted at it reads at least its work, and one equal pair of reads now and then does not stop it; a count
/// brought up to date at a clock tick or at a shorter interval (which moves on almost none of a spin's reads), a spin that read the
/// count too few times for its share to say anything, spins that scatter, or a rate no x64 counter runs at give no rate, with the
/// words why. The spins are simulated: a 3 GHz counter, a 10 MHz stopwatch, and 100 ns for each read of either, so a spin's reads of
/// the count are 0.2 µs apart (as on the windows-latest runner, a Hyper-V guest whose back-to-back cycle reads step about 0.1 µs).</summary>
public sealed class CycleRateTests
{
    private const double Rate = 3.0;
    private const double TickNs = 100;
    private const long SpinTicks = 10_000;
    private const double ReadNs = 100;
    private const int Spins = 16;

    [Fact]
    public void ACountBroughtUpToDateAtEachReadGivesTheFastestSpinsRateNeverAboveTheCounters()
    {
        var rate = ThreadTime.RateFrom(Measure((_, run) => Rate * run), TickNs);
        Assert.Equal("", rate.Why);
        Assert.Equal(Spins, rate.Agreeing);
        Assert.Equal(1.0, rate.MovingShare);
        Assert.True(rate.FewestPasses >= ThreadTime.MinRatePasses, $"{rate.FewestPasses} passes");
        Assert.InRange(rate.CyclesPerNs, Rate * 0.999, Rate);
    }

    /// <summary>The runner's count: brought up to date at each read, and now and then (2% of reads) a read finds it where the read
    /// before left it, the cycles between them not counted. Every spin sees such pairs, so a rule that refuses the count for one equal
    /// pair in any spin refuses it (the runner, reading one pair a spin, saw one in about one process in four); the count moves on
    /// nearly every read, and the share of moving reads gives the rate.</summary>
    [Fact]
    public void ACurrentCountWithAnEqualPairNowAndThenGivesTheRateThoughEverySpinSeesOne()
    {
        var rng = new Pcg32(308);
        var spins = Measure((_, run) => Rate * run, equal: () => rng.NextDouble() < 0.02);
        Assert.All(spins, s => Assert.InRange(s.Passes - s.Moved, 1, s.Passes / 25));
        Assert.True(RefusedForOneEqualPair(spins));

        var rate = ThreadTime.RateFrom(spins, TickNs);
        Assert.Equal("", rate.Why);
        Assert.InRange(rate.MovingShare, 0.96, 0.99);
        Assert.True(rate.Agreeing * 4 >= Spins, $"{rate.Agreeing} agreeing");
        // The reads' uncounted cycles only lower what a spin counts: the rate is never above the counter's.
        Assert.InRange(rate.CyclesPerNs, Rate * 0.97, Rate);
    }

    [Fact]
    public void AShareOfMovingReadsUnderHalfGivesNoRateAndHalfStands()
    {
        var spins = Enumerable.Repeat(new ThreadTime.RateSpin(3_000_000, 1000, 500, 9_999), Spins).ToArray();
        var half = ThreadTime.RateFrom(spins, TickNs);
        Assert.Equal("", half.Why);
        Assert.Equal(0.5, half.MovingShare);
        Assert.Equal(Rate, half.CyclesPerNs, 9);

        spins[9] = spins[9] with { Moved = 499 };
        var under = ThreadTime.RateFrom(spins, TickNs);
        Assert.Equal(0, under.CyclesPerNs);
        Assert.Equal(0.499, under.MovingShare);
        Assert.Contains("moved on 499 of a spin's 1000 reads (49.9%), not half", under.Why, StringComparison.Ordinal);
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
    public void ASpinThatReadTheCountOnTooFewPassesGivesNoRate()
    {
        // One spin loses all but a few microseconds of its millisecond: it reads the count a handful of times, too few to judge.
        var rate = ThreadTime.RateFrom(Measure((_, run) => Rate * run, i => i == 7 ? 999_000 : 0), TickNs);
        Assert.Equal(0, rate.CyclesPerNs);
        Assert.InRange(rate.FewestPasses, 0, ThreadTime.MinRatePasses - 1);
        Assert.Contains(Words($"read the count on {rate.FewestPasses} passes, fewer than the {ThreadTime.MinRatePasses}"), rate.Why,
            StringComparison.Ordinal);
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
        // Sixteen spins of a millisecond hold one tick at most: no spin's count moves on more than one of its reads.
        var spins = Measure((_, run) => Rate * Math.Floor(run / 15_625_000) * 15_625_000);
        Assert.All(spins, s => Assert.InRange(s.Moved, 0, 1));
        var rate = ThreadTime.RateFrom(spins, TickNs);
        Assert.Equal(0, rate.CyclesPerNs);
        Assert.Equal(0.0, rate.MovingShare);
        Assert.Contains(Words($"({rate.MovingShare * 100:0.##}%), not half, so it is not brought up to date at each read"), rate.Why,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ACountBroughtUpToDateAtAShorterIntervalGivesNoRateThoughItsFastestSpinCountsMoreThanTheCounter()
    {
        // Brought up to date every 0.3 ms, a spin of a millisecond counts three or four of those: the fastest counts 1.2 ms of the
        // counter in a millisecond, and a rate taken from it would read every span a sixth short. The count moves on three or four of
        // a spin's five thousand reads.
        var spins = Measure((_, run) => Rate * Math.Floor(run / 300_000) * 300_000);
        Assert.Contains(spins, s => s.Cycles / ((s.Ticks + 1) * TickNs) > Rate * 1.1);
        var rate = ThreadTime.RateFrom(spins, TickNs);
        Assert.Equal(0, rate.CyclesPerNs);
        Assert.InRange(rate.MovingShare, 0, 0.001);
        Assert.Contains(Words($"({rate.MovingShare * 100:0.##}%), not half, so it is not brought up to date at each read"), rate.Why,
            StringComparison.Ordinal);
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

    /// <summary>Whether a rule that refuses the count where any spin's count stood still between two of its reads refuses these.</summary>
    private static bool RefusedForOneEqualPair(ThreadTime.RateSpin[] spins) => spins.Any(s => s.Moved < s.Passes);

    /// <summary>The spins the measuring takes of a count (cycles, given the stopwatch's time and the time the thread has run, in ns),
    /// as <c>ThreadTime</c> takes them: a stopwatch read and a cycle read, then on every pass a stopwatch read and, until the stopwatch
    /// has moved a millisecond's ticks, a cycle read. <paramref name="interrupted"/> is how long the machine takes the processor in
    /// each spin: the stopwatch moves on, the thread does not run. Where <paramref name="equal"/> says so, a cycle read finds the
    /// count where the read before left it: the thread's run between the two is not counted.</summary>
    private static ThreadTime.RateSpin[] Measure(Func<double, double, double> count, Func<int, double>? interrupted = null,
        Func<bool>? equal = null)
    {
        double wall = 1_234_567, run = 0, uncounted = 0, runAtRead = 0;
        var spins = new ThreadTime.RateSpin[Spins];
        for (var i = 0; i < spins.Length; i++)
        {
            var w0 = Watch();
            var c0 = Read();
            wall += interrupted?.Invoke(i) ?? 0;
            int passes = 0, moved = 0;
            var last = c0;
            var w1 = Watch();
            while (w1 - w0 < SpinTicks)
            {
                var c = Read();
                passes++;
                if (c > last)
                    moved++;
                last = c;
                w1 = Watch();
            }
            spins[i] = new ThreadTime.RateSpin(last > c0 ? last - c0 : 0, passes, moved, w1 - w0);
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
            if (equal?.Invoke() == true)
                uncounted += run - runAtRead;
            runAtRead = run;
            var c = (ulong)count(wall, run - uncounted);
            wall += ReadNs;
            run += ReadNs;
            return c;
        }
    }

    private static string Words(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);
}
