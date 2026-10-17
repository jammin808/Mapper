using System.Globalization;
using PlayMap.Core.Numerics;
using PlayMap.Core.Time;

namespace PlayMap.Core.Tests.Time;

/// <summary>The cycle rate x64 Windows converts a thread's cycles at, from the measuring's spins as the counts it might meet would give
/// them. A count brought up to date at each read gives a rate never above the counter's, near it where a quarter of the spins ran
/// whole, so a span converted at it reads at least its work, and an equal pair of reads now and then does not stop it; a spin stopped
/// for most of its millisecond reads the count too few times to be judged and neither sets nor stops the rate. A count brought up to
/// date at a clock tick or at a shorter interval (which moves on almost none of a spin's reads), a share over half judged on too few
/// passes, too few spins judged, spins that scatter, or a rate no x64 counter runs at give no rate, with the words why. The spins are
/// simulated: a 3 GHz counter, a 10 MHz stopwatch, and 100 ns for each read of either, so a spin's reads of the count are 0.2 µs
/// apart (as on a Hyper-V guest whose back-to-back cycle reads step about 0.1 µs).</summary>
public sealed class CycleRateTests
{
    private const double Rate = 3.0;
    private const double TickNs = 100;
    private const long SpinTicks = 10_000;
    private const double ReadNs = 100;
    private const int Spins = 16;

    /// <summary>What a spin stopped for most of its millisecond loses: it reads the count a handful of times.</summary>
    private const double StoppedNs = 999_000;

    [Fact]
    public void ACountBroughtUpToDateAtEachReadGivesTheFastestSpinsRateNeverAboveTheCounters()
    {
        var rate = ThreadTime.RateFrom(Measure((_, run) => Rate * run), TickNs);
        Assert.Equal("", rate.Why);
        Assert.Equal(Spins, rate.Judged);
        Assert.Equal(Spins, rate.Agreeing);
        Assert.Equal(1.0, rate.MovingShare);
        Assert.True(rate.FewestPasses >= ThreadTime.MinRatePasses, $"{rate.FewestPasses} passes");
        Assert.InRange(rate.CyclesPerNs, Rate * 0.999, Rate);
    }

    /// <summary>A count brought up to date at each read where now and then (2% of reads) a read finds it where the read before left
    /// it, the cycles between them not counted. Every spin sees such a pair, and one spin's first pass is one, so a rule that refuses
    /// the count for an equal pair anywhere in a spin, or for one straight after a spin's first read, refuses it; the count moves on
    /// nearly every read, and the share of moving reads gives the rate.</summary>
    [Fact]
    public void ACurrentCountWithAnEqualPairNowAndThenGivesTheRateThoughARuleOfOneEqualPairRefusesIt()
    {
        var rng = new Pcg32(17);
        var firstPassMoved = new bool[Spins];
        var spins = Measure((_, run) => Rate * run, equal: (spin, read) => (spin == 5 && read == 1) || rng.NextDouble() < 0.02,
            firstPassMoved: firstPassMoved);
        Assert.All(spins, s => Assert.InRange(s.Passes - s.Moved, 1, s.Passes / 25));
        Assert.False(firstPassMoved[5]);

        var rate = ThreadTime.RateFrom(spins, TickNs);
        Assert.Equal("", rate.Why);
        Assert.Equal(Spins, rate.Judged);
        Assert.InRange(rate.MovingShare, 0.96, 0.99);
        Assert.True(rate.Agreeing * 4 >= Spins, $"{rate.Agreeing} agreeing");
        // The reads' uncounted cycles only lower what a spin counts: the rate is never above the counter's.
        Assert.InRange(rate.CyclesPerNs, Rate * 0.97, Rate);
    }

    [Fact]
    public void AShareOfMovingReadsUnderHalfGivesNoRateAndHalfStands()
    {
        var spins = Enumerable.Repeat(new ThreadTime.RateSpin(3_000_000, 2000, 1000, 9_999), Spins).ToArray();
        var half = ThreadTime.RateFrom(spins, TickNs);
        Assert.Equal("", half.Why);
        Assert.Equal(0.5, half.MovingShare);
        Assert.Equal(Rate, half.CyclesPerNs, 9);

        spins[9] = spins[9] with { Moved = 999 };
        var under = ThreadTime.RateFrom(spins, TickNs);
        Assert.Equal(0, under.CyclesPerNs);
        Assert.Equal(0.4995, under.MovingShare);
        Assert.Contains("moved on 999 of a spin's 2000 reads (49.95%), not half", under.Why, StringComparison.Ordinal);
    }

    [Fact]
    public void AnInterruptedSpinOnlyLowersItsOwnRateAndAQuarterRunWholeStillGiveTheCounters()
    {
        // Twelve spins lose 20 µs to 600 µs of the processor; four run whole.
        var rate = ThreadTime.RateFrom(Measure((_, run) => Rate * run, i => i % 4 == 0 ? 0 : 20_000 + 50_000 * (i % 13)), TickNs);
        Assert.Equal("", rate.Why);
        Assert.Equal(Spins, rate.Judged);
        Assert.Equal(Spins / 4, rate.Agreeing);
        Assert.InRange(rate.CyclesPerNs, Rate * 0.999, Rate);
    }

    [Fact]
    public void ASpinStoppedForMostOfItsMillisecondIsNotJudgedAndTheRestGiveTheRate()
    {
        // The first spin and the eighth are stopped from just after their first read to the end of their millisecond (the call's
        // binding, a collection, the processor taken): each reads the count a handful of times.
        var spins = Measure((_, run) => Rate * run, i => i is 0 or 7 ? StoppedNs : 0);
        Assert.All(new[] { spins[0], spins[7] }, s => Assert.InRange(s.Passes, 0, ThreadTime.MinRatePasses - 1));

        var rate = ThreadTime.RateFrom(spins, TickNs);
        Assert.Equal("", rate.Why);
        Assert.Equal(Spins - 2, rate.Judged);
        Assert.Equal(Spins - 2, rate.Agreeing);
        Assert.True(rate.FewestPasses >= ThreadTime.MinRatePasses, $"{rate.FewestPasses} passes");
        Assert.InRange(rate.CyclesPerNs, Rate * 0.999, Rate);
    }

    [Fact]
    public void SpinsStoppedForMostOfTheirMillisecondGiveNoRateWhereUnderAQuarterAreJudged()
    {
        var quarter = ThreadTime.RateFrom(Measure((_, run) => Rate * run, i => i < 12 ? StoppedNs : 0), TickNs);
        Assert.Equal("", quarter.Why);
        Assert.Equal(Spins / 4, quarter.Judged);
        Assert.Equal(Spins / 4, quarter.Agreeing);
        Assert.InRange(quarter.CyclesPerNs, Rate * 0.999, Rate);

        var under = ThreadTime.RateFrom(Measure((_, run) => Rate * run, i => i < 13 ? StoppedNs : 0), TickNs);
        Assert.Equal(0, under.CyclesPerNs);
        Assert.Equal(3, under.Judged);
        Assert.Contains(Words($"only 3 of 16 spins measuring the thread's cycle rate read the count on {ThreadTime.MinRatePasses} passes or more"),
            under.Why, StringComparison.Ordinal);

        var none = ThreadTime.RateFrom(Measure((_, run) => Rate * run, _ => StoppedNs), TickNs);
        Assert.Equal(0, none.CyclesPerNs);
        Assert.Equal(0, none.Judged);
        Assert.Equal(0, none.FewestPasses);
        Assert.Equal(0.0, none.MovingShare);
        Assert.Contains("only 0 of 16 spins", none.Why, StringComparison.Ordinal);
    }

    [Fact]
    public void AStaleCountIsRefusedOnTheSpinsJudgedThoughTheRestAreStopped()
    {
        // Brought up to date every 0.3 ms; twelve spins are stopped and the four that run whole are judged.
        var rate = ThreadTime.RateFrom(Measure((_, run) => Rate * Math.Floor(run / 300_000) * 300_000, i => i < 12 ? StoppedNs : 0),
            TickNs);
        Assert.Equal(0, rate.CyclesPerNs);
        Assert.Equal(Spins / 4, rate.Judged);
        Assert.InRange(rate.MovingShare, 0, 0.001);
        Assert.Contains("not half, so it is not brought up to date at each read", rate.Why, StringComparison.Ordinal);
    }

    [Fact]
    public void ASpinIsJudgedFromTheFewestPassesOnAndOneOfFewerNeitherSetsNorAgreesWithTheRate()
    {
        var floor = ThreadTime.MinRatePasses;
        var spins = Enumerable.Repeat(new ThreadTime.RateSpin(3_000_000, floor, floor, 9_999), Spins).ToArray();
        var all = ThreadTime.RateFrom(spins, TickNs);
        Assert.Equal("", all.Why);
        Assert.Equal(Spins, all.Judged);
        Assert.Equal(floor, all.FewestPasses);
        Assert.Equal(2.0 / (floor - 2), all.MostAbove);
        Assert.InRange(all.MostAbove, 0, 0.0021);
        Assert.Equal(Rate, all.CyclesPerNs, 9);

        // One pass short, counting twice the counter and never moving: judged, it would set the rate and refuse it twice over.
        spins[3] = new ThreadTime.RateSpin(6_000_000, floor - 1, 0, 9_999);
        var oneShort = ThreadTime.RateFrom(spins, TickNs);
        Assert.Equal("", oneShort.Why);
        Assert.Equal(Spins - 1, oneShort.Judged);
        Assert.Equal(Spins - 1, oneShort.Agreeing);
        Assert.Equal(1.0, oneShort.MovingShare);
        Assert.Equal(Rate, oneShort.CyclesPerNs, 9);
    }

    [Fact]
    public void AShareOverHalfOnTooFewPassesIsNotJudgedThoughItsFastestSpinCountsMoreThanAJudgedSpinCan()
    {
        // Reads of 2.4 µs, so a spin reads the count every 4.8 µs, about 200 times; the count is brought up to date every 9.5 µs of
        // the stopwatch. It moves on just over half of a spin's reads, and its fastest spin counts more than 0.3% above the counter,
        // where a spin of the fewest passes judged counts at most 0.2% above it.
        var spins = Measure((wall, run) => run > wall % 9_500 ? Rate * (run - wall % 9_500) : 0, readNs: 2_400);
        Assert.All(spins, s => Assert.True(s.Moved * 2 >= s.Passes, $"{s.Moved} of {s.Passes}"));
        Assert.All(spins, s => Assert.InRange(s.Passes, 200, 250));
        Assert.Contains(spins, s => s.Cycles / ((s.Ticks + 1) * TickNs) > Rate * 1.003);

        var rate = ThreadTime.RateFrom(spins, TickNs);
        Assert.Equal(0, rate.CyclesPerNs);
        Assert.Equal(0, rate.Judged);
        Assert.Contains("only 0 of 16 spins", rate.Why, StringComparison.Ordinal);
    }

    [Fact]
    public void SpinsThatScatterBelowTheFastestStillGiveTheCountersRate()
    {
        // Two run whole and each of the rest loses a different share of the processor, as the windows-latest runner's measuring did
        // beside the suite (2 of 16 within 1% of the fastest): a loaded machine only lowers a spin, so the fastest stands.
        var rate = ThreadTime.RateFrom(Measure((_, run) => Rate * run, i => i < 2 ? 0 : 20_000 + 40_000 * i), TickNs);
        Assert.Equal("", rate.Why);
        Assert.Equal(Spins, rate.Judged);
        Assert.Equal(2, rate.Agreeing);
        Assert.InRange(rate.CyclesPerNs, Rate * (1 - 1e-3), Rate);
    }

    [Fact]
    public void OneSpinRunWholeAmongSlowedOnesGivesTheCountersRate()
    {
        // Every spin but one lost part of its millisecond: the one run whole is the counter's rate, and none counts above it.
        var rate = ThreadTime.RateFrom(Measure((_, run) => Rate * run, i => i == 7 ? 0 : 30_000 + 10_000 * i), TickNs);
        Assert.Equal("", rate.Why);
        Assert.Equal(1, rate.Agreeing);
        Assert.InRange(rate.CyclesPerNs, Rate * (1 - 1e-3), Rate);
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

    /// <summary>The spins the measuring takes of a count (cycles, given the stopwatch's time and the time the thread has run, in ns),
    /// as <c>ThreadTime</c> takes them: a stopwatch read and a cycle read, then on every pass a stopwatch read and, until the stopwatch
    /// has moved a millisecond's ticks, a cycle read, each read taking <paramref name="readNs"/>. <paramref name="interrupted"/> is how
    /// long the machine takes the processor in each spin, just after its first cycle read: the stopwatch moves on, the thread does not
    /// run. Where <paramref name="equal"/> says so of a spin's read (0 its first cycle read, then each pass's), the read finds the count
    /// where the read before left it: the thread's run between the two is not counted. <paramref name="firstPassMoved"/>, where given,
    /// takes whether each spin's first pass found the count moved on from its first cycle read.</summary>
    private static ThreadTime.RateSpin[] Measure(Func<double, double, double> count, Func<int, double>? interrupted = null,
        Func<int, int, bool>? equal = null, double readNs = ReadNs, bool[]? firstPassMoved = null)
    {
        double wall = 1_234_567, run = 0, uncounted = 0, runAtRead = 0;
        var spins = new ThreadTime.RateSpin[Spins];
        for (var i = 0; i < spins.Length; i++)
        {
            var w0 = Watch();
            var c0 = Read(i, 0);
            wall += interrupted?.Invoke(i) ?? 0;
            int passes = 0, moved = 0;
            var last = c0;
            var w1 = Watch();
            while (w1 - w0 < SpinTicks)
            {
                var c = Read(i, passes + 1);
                passes++;
                if (c > last)
                    moved++;
                if (passes == 1 && firstPassMoved is not null)
                    firstPassMoved[i] = c > last;
                last = c;
                w1 = Watch();
            }
            spins[i] = new ThreadTime.RateSpin(last > c0 ? last - c0 : 0, passes, moved, w1 - w0);
        }
        return spins;

        long Watch()
        {
            var w = (long)(wall / TickNs);
            wall += readNs;
            run += readNs;
            return w;
        }

        ulong Read(int spin, int read)
        {
            if (equal?.Invoke(spin, read) == true)
                uncounted += run - runAtRead;
            runAtRead = run;
            var c = (ulong)count(wall, run - uncounted);
            wall += readNs;
            run += readNs;
            return c;
        }
    }

    private static string Words(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);
}
