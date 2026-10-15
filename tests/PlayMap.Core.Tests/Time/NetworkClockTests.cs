using PlayMap.Core.Numerics;
using PlayMap.Core.Time;

namespace PlayMap.Core.Tests.Time;

/// <summary>The leader clock's corrections: one under a millisecond is slewed at 200 ppm (continuous, never backwards, never faster
/// than 1 + 200 ppm + drift), a larger one steps where its owner says and is announced with its Δ, a promoted node keeps the time
/// line it read, a silent leader is free-wheeled on, and a pinned instant is now on one thread only.</summary>
public sealed class NetworkClockTests
{
    private const long Ms = 1_000_000;

    /// <summary>An estimate that reads the leader <paramref name="offsetNs"/> ahead (and <paramref name="drift"/> fast) of the local
    /// clock, from exchanges with no round trip around <paramref name="atLocalNs"/>.</summary>
    private static ClockOffsetEstimator Estimate(double offsetNs, long atLocalNs, double drift = 0)
    {
        var e = new ClockOffsetEstimator();
        for (var i = 7; i >= 0; i--)
        {
            var t = atLocalNs - i * 250 * Ms;
            var o = (long)Math.Round(offsetNs + drift * (t - atLocalNs));
            e.Add(new ClockSample(t, t + o, t + o, t));
        }
        return e;
    }

    [Fact]
    public void ACorrectionUnderAMillisecondIsSlewedAt200PpmAndTimeStaysContinuous()
    {
        var local = new ManualClock(10_000 * Ms);
        var clock = new NetworkClock(local);
        var steps = 0;
        clock.Stepped += _ => steps++;
        clock.Follow(Estimate(5 * Ms, local.NowNs));
        Assert.Equal(1, steps);   // from its own clock to a leader 5 ms ahead: a step
        Assert.Equal(local.NowNs + 5 * Ms, clock.NowNs);

        // The leader is read 0.8 ms further ahead: no jump at the swap, then 200 ppm until it is absorbed (4 s).
        var before = clock.NowNs;
        clock.Follow(Estimate(5.8 * Ms, local.NowNs));
        Assert.Equal(before, clock.NowNs);
        Assert.True(clock.IsSlewing);
        Assert.Equal(1, steps);
        local.AdvanceMs(1000);
        Assert.Equal(before + 1000 * Ms + 200_000, clock.NowNs);   // 1 s of local time, 0.2 ms of the correction
        local.AdvanceMs(3000);
        Assert.Equal(before + 4000 * Ms + 800_000, clock.NowNs);
        Assert.False(clock.IsSlewing);
        local.AdvanceMs(1000);
        Assert.Equal(local.NowNs + (long)(5.8 * Ms), clock.NowNs);
        Assert.Equal(0, clock.LastStepNs - 5 * Ms);
    }

    [Fact]
    public void LeaderTimeReadInATightLoopNeverRunsBackwardsNorFasterThanTheSlewAllows()
    {
        var local = new ManualClock(1_000 * Ms);
        var clock = new NetworkClock(local);
        const double drift = 30e-6;
        const double truth = 2_000.0 * Ms;
        clock.Follow(Estimate(truth, local.NowNs, drift));
        var rng = new Pcg32(42);
        var last = clock.NowNs;
        var lastLocal = local.NowNs;
        var reads = 0;
        var worst = 0.0;
        for (var exchange = 0; exchange < 400; exchange++)
        {
            // A fresh estimate every 250 ms, its offset off the truth by up to ±0.45 ms (a noisy network): never 1 ms from what the
            // clock reads, so slewed every time.
            var noise = (rng.NextDouble() * 2 - 1) * 0.45 * Ms;
            clock.Follow(Estimate(truth + drift * (local.NowNs - 1_000 * Ms) + noise, local.NowNs, drift));
            for (var i = 0; i < 250; i++)
            {
                local.Advance(1 * Ms);
                var now = clock.NowNs;
                Assert.True(now >= last, $"read {reads}: leader time ran backwards by {last - now} ns");
                var rate = (now - last) / (double)(local.NowNs - lastLocal);
                worst = Math.Max(worst, rate);
                Assert.True(rate <= 1 + 200e-6 + drift + 2e-6, $"read {reads}: leader time ran at {rate:0.0000000} of local");
                last = now;
                lastLocal = local.NowNs;
                reads++;
            }
        }
        Assert.Equal(0, clock.Steps - 1);
        Assert.True(worst > 1 + drift, $"the slew was never used (worst rate {worst})");
    }

    [Fact]
    public void ALargeCorrectionStepsWhereItsOwnerTakesItAndSaysByHowMuch()
    {
        var local = new ManualClock(50 * Ms);
        var clock = new NetworkClock(local);
        var taken = new List<Action>();
        var deltas = new List<long>();
        clock.StepOn = taken.Add;
        clock.Stepped += deltas.Add;
        var ahead = (3 * 3600 + 12 * 60) * 1_000_000_000L;
        clock.Follow(Estimate(ahead, local.NowNs));

        // Until its owner takes the step, the clock reads the time line it had.
        Assert.Single(taken);
        Assert.Equal(local.NowNs, clock.NowNs);
        local.AdvanceMs(7);
        taken[0]();
        Assert.Equal([ahead], deltas);
        Assert.Equal(local.NowNs + ahead, clock.NowNs);
        Assert.True(clock.IsFollowing);
        Assert.Equal(1, clock.Steps);

        // Taken again later, a step that a newer estimate already made is a slew of what is left (here nothing).
        clock.Follow(Estimate(ahead + 0.5 * Ms, local.NowNs));
        Assert.Single(taken);
        clock.Lead();
        Assert.Equal(2, taken.Count);
        taken[1]();
        Assert.Equal(2, deltas.Count);
        Assert.Equal(-ahead, deltas[1]);   // the half millisecond was still being slewed: leader time read the old line
        Assert.Equal(local.NowNs, clock.NowNs);
        Assert.False(clock.IsFollowing);
    }

    [Fact]
    public void APromotedNodeKeepsTheTimeLineItReadAndASilentLeaderIsFreeWheeledOn()
    {
        var local = new ManualClock(0);
        var clock = new NetworkClock(local);
        clock.Follow(Estimate(42 * Ms, 0, 20e-6));
        local.AdvanceMs(10_000);
        var read = clock.NowNs;

        clock.Hold();
        Assert.True(clock.IsHolding);
        local.AdvanceMs(2_500);
        Assert.Equal(2_500 * Ms, clock.HoldingNs);
        Assert.Equal(read + 2_500 * Ms + 50_000, clock.NowNs);   // the drift still applies while free-wheeling

        var before = clock.NowNs;
        clock.Promote();
        Assert.False(clock.IsFollowing);
        Assert.False(clock.IsHolding);
        Assert.Equal(before, clock.NowNs);
        local.AdvanceMs(1_000);
        Assert.Equal(before + 1_000 * Ms, clock.NowNs);
        Assert.Equal(0, clock.Steps - 1);
    }

    [Fact]
    public void APinnedInstantIsNowOnItsOwnThreadOnly()
    {
        var local = new ManualClock(1_000 * Ms);
        var clock = new NetworkClock(local);
        using (clock.Pin(77))
        {
            Assert.Equal(77, clock.NowNs);
            using (clock.Pin(99))
                Assert.Equal(99, clock.NowNs);
            Assert.Equal(77, clock.NowNs);
            // Another thread reads the clock (a thread of its own: a pin is synchronous work's, never held across an await).
            long other = 0;
            var reader = new Thread(() => other = clock.NowNs);
            reader.Start();
            reader.Join();
            Assert.Equal(1_000 * Ms, other);
            Assert.Equal(1_000 * Ms, new NetworkClock(local).NowNs);
        }
        Assert.Equal(1_000 * Ms, clock.NowNs);
    }
}
