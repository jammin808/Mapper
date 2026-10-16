using PlayMap.Core.Numerics;
using PlayMap.Render.Platform;
using PlayMap.Render.Timing;

namespace PlayMap.Render.Tests.Presentation;

/// <summary>Vblank truth from synthetic samples (Xvfb's swaps are no real display, so the arithmetic is tested on sequences).</summary>
public sealed class VblankClockTests
{
    private const double Period60 = 1e9 / 60;
    private const long T0 = 5_000_000_000;

    private static long Jittered(ref Pcg32 rng, int k, double halfWidthNs = 500_000) =>
        T0 + (long)Math.Round(k * Period60 + rng.Range(-halfWidthNs, halfWidthNs));

    [Fact]
    public void SixHundredSwapReturnsWithHalfAMillisecondOfJitterGiveThePeriodWithinAMicrosecond()
    {
        var rng = new Pcg32(60);
        var clock = new VblankClock(60);
        for (var k = 0; k < 600; k++)
            Assert.Equal(1, clock.AddSwapReturn(Jittered(ref rng, k)));

        Assert.True(clock.Settled);
        Assert.InRange(clock.PeriodNs, Period60 - 1000, Period60 + 1000);
        Assert.InRange(clock.RefreshHz, 59.996, 60.004);
        Assert.Equal(599UL, clock.LastIndex);
        // The phase: the fitted instant of the last refresh against the true one.
        var truth = T0 + 599 * Period60;
        Assert.InRange(clock.TimeOf(599), truth - 200_000, truth + 200_000);
        Assert.InRange(clock.NextVblank((long)truth + 1_000_000), truth + Period60 - 200_000, truth + Period60 + 200_000);
        Assert.Equal(0, clock.Missed);
        Assert.Equal(0, clock.Outliers);
    }

    /// <summary>Swap returns as a loaded machine stamps them: one frame in twelve held up to 30 ms before its present (it then
    /// waits for the next vblank: a real miss) and one in twelve stamped up to 30 ms after its vblank (its thread waited for the
    /// CPU). A return is never early, so a late stamp shifts no later count and the median trim keeps it out of the rate: after
    /// 90 frames the fit is on the display's rate within 0.05 Hz and its next vblank within a millisecond, on every one of forty
    /// such machines.</summary>
    [Fact]
    public void SwapReturnsStampedLateByAStarvedThreadNeitherShiftTheCountNorBendTheRate()
    {
        for (ulong seed = 1; seed <= 40; seed++)
        {
            var rng = new Pcg32(seed);
            var clock = new VblankClock(60);
            var now = T0;
            long vblank = 0;
            for (var f = 0; f < 90; f++)
            {
                now += 1_000_000;
                if (rng.NextDouble() < 1.0 / 12)
                    now += (long)(rng.NextDouble() * 30_000_000);
                vblank = T0 + (long)Math.Round((Math.Floor((now - T0) / Period60) + 1) * Period60);
                now = Math.Max(now, vblank + 100_000);
                if (rng.NextDouble() < 1.0 / 12)
                    now += (long)(rng.NextDouble() * 30_000_000);
                clock.AddSwapReturn(now);
            }
            Assert.True(clock.RefreshHz is > 59.95 and < 60.05, $"seed {seed}: {clock.RefreshHz} Hz");
            var next = clock.NextVblank(vblank + 1_000_000) - (vblank + (long)Math.Round(Period60));
            Assert.True(Math.Abs(next) < 1_000_000, $"seed {seed}: next vblank {next} ns off");
        }
    }

    [Fact]
    public void DisplaySideSamplesFitTheSameAndOnlyANewerCountIsTaken()
    {
        var rng = new Pcg32(7);
        var clock = new VblankClock(60);
        for (var k = 0; k < 600; k++)
            Assert.True(clock.Add((ulong)(1000 + k), Jittered(ref rng, k, 50_000)));
        Assert.False(clock.Add(1599, T0 + (long)(599 * Period60)), "the same refresh twice");
        Assert.False(clock.Add(1200, T0), "an older refresh");
        Assert.InRange(clock.PeriodNs, Period60 - 100, Period60 + 100);
        Assert.Equal(1599UL, clock.IndexAt(T0 + (long)(599.5 * Period60)));
        Assert.Equal(1600UL, clock.FirstIndexAtOrAfter(T0 + (long)(599.5 * Period60)));
    }

    [Fact]
    public void AMissedVblankIsCountedOnceAndDoesNotBendTheFit()
    {
        var rng = new Pcg32(3);
        var clock = new VblankClock(60);
        for (var k = 0; k <= 600; k++)
        {
            if (k == 300)
                continue;
            var step = clock.AddSwapReturn(Jittered(ref rng, k));
            Assert.Equal(k == 301 ? 2 : 1, step);
        }
        Assert.Equal(1, clock.Missed);
        Assert.Equal(600UL, clock.LastIndex);
        Assert.InRange(clock.PeriodNs, Period60 - 1000, Period60 + 1000);
    }

    [Fact]
    public void ARateOffTheNominalShowsItsPpm()
    {
        // A "60 Hz" display that really runs at 59.94 (60000/1001): about +1000 ppm long.
        var period = 1e9 * 1001 / 60000;
        var clock = new VblankClock(60);
        for (var k = 0; k < 400; k++)
            clock.AddSwapReturn(T0 + (long)Math.Round(k * period));
        Assert.InRange(clock.Ppm, 999, 1001);
        Assert.InRange(clock.RefreshHz, 59.9399, 59.9401);
        Assert.InRange(clock.PeriodPs, 16_683_331_333L, 16_683_335_333L);
    }

    [Fact]
    public void AnOutlierIsLeftOutOfTheFitAndCountedOnce()
    {
        var clock = new VblankClock(60);
        for (var k = 0; k < 200; k++)
            clock.Add((ulong)k, T0 + (long)Math.Round(k * Period60 + (k == 150 ? 6_000_000 : 0)));
        Assert.Equal(1, clock.Outliers);
        Assert.InRange(clock.PeriodNs, Period60 - 50, Period60 + 50);
    }

    [Fact]
    public void ADisjointStartsTheFitAgainAndANominalClockCountsAgainstItsOwnRate()
    {
        var clock = new VblankClock(60);
        for (var k = 0; k < 40; k++)
            clock.AddSwapReturn(T0 + (long)Math.Round(k * Period60));
        Assert.True(clock.Settled);
        clock.Disjoint();
        Assert.False(clock.Settled);
        Assert.False(clock.HasPhase);

        // A 50 Hz output with a fixed period: a 40 ms gap is one missed refresh, whatever the fit would say.
        var fixedClock = new VblankClock(50) { FixedPeriod = true };
        Assert.Equal(1, fixedClock.AddSwapReturn(T0));
        Assert.Equal(1, fixedClock.AddSwapReturn(T0 + 20_000_000));
        Assert.Equal(2, fixedClock.AddSwapReturn(T0 + 60_000_000));
        Assert.Equal(0, fixedClock.AddSwapReturn(T0 + 65_000_000));
        Assert.Equal(1, fixedClock.Missed);
        Assert.Equal(20_000_000, fixedClock.PeriodNs);
    }
}

/// <summary>Missed vblanks from the display's side, against the output's own refresh.</summary>
public sealed class PresentLedgerTests
{
    private const long Second = 1_000_000_000;

    [Fact]
    public void ALatePresentIsAGlitchAndItsMissedRefreshesAndTheGapsAreRepeats()
    {
        var ledger = new PresentLedger();
        for (ulong p = 1; p <= 4; p++)
        {
            ledger.OnPresent(p, 9 + p);
            ledger.OnShown(p, 9 + p, (long)p * 16_000_000);
        }
        // Present 5 was meant for refresh 14 and first showed at 16: two refreshes kept picture 4.
        ledger.OnPresent(5, 14);
        ledger.OnShown(5, 16, 80_000_000);
        Assert.Equal(5, ledger.Presented);
        Assert.Equal(5, ledger.Shown);
        Assert.Equal(1, ledger.Glitches);
        Assert.Equal(2, ledger.MissedVblanks);
        Assert.Equal(2, ledger.Repeats);
        Assert.Equal(0, ledger.Discarded);

        // Presents 6 and 7 both queued; only 7 showed (at 17): 6 was replaced before it was shown.
        ledger.OnPresent(6, 17);
        ledger.OnPresent(7, 17);
        ledger.OnShown(7, 17, 96_000_000);
        Assert.Equal(1, ledger.Discarded);
        // Shown twice, or an older present, changes nothing.
        ledger.OnShown(7, 18, 97_000_000);
        ledger.OnShown(3, 19, 98_000_000);
        Assert.Equal(6, ledger.Shown);
        ledger.OnHeld();
        Assert.Equal(1, ledger.Held);
    }

    [Fact]
    public void CountersStayRightAcrossTheWrapOfThePresentAndRefreshCounters()
    {
        var ledger = new PresentLedger();
        var p = ulong.MaxValue - 2;
        var r = ulong.MaxValue - 1;
        for (var i = 0; i < 8; i++)
        {
            var late = i == 5 ? 1UL : 0UL;
            r += 1 + late;
            ledger.OnPresent(p, r - late);
            ledger.OnShown(p, r, i * 16_000_000L);
            p++;
        }
        Assert.Equal(8, ledger.Shown);
        Assert.Equal(1, ledger.Glitches);
        Assert.Equal(1, ledger.MissedVblanks);
        Assert.Equal(1, ledger.Repeats);
        Assert.Equal(0, ledger.Discarded);
    }

    [Fact]
    public void TheLastSixtyAndTenSecondsComeFromPerSecondBuckets()
    {
        var ledger = new PresentLedger();
        ulong r = 0;
        ulong p = 0;
        // 70 s at 60 Hz; a late present (one missed refresh) in seconds 5, 30 and 65.
        for (var s = 0; s < 70; s++)
        {
            for (var f = 0; f < 60; f++)
            {
                var late = f == 10 && s is 5 or 30 or 65 ? 1UL : 0UL;
                r += 1 + late;
                p++;
                ledger.OnPresent(p, r - late);
                ledger.OnShown(p, r, s * Second + f * (Second / 60));
            }
        }
        var now = 69 * Second + Second / 2;
        Assert.Equal(3, ledger.MissedVblanks);
        Assert.Equal(2, ledger.Missed60s(now));
        Assert.Equal(1, ledger.MissedIn(now, 10));
        Assert.InRange(ledger.RefreshesIn(now, 10), 600, 601);
        Assert.InRange(ledger.LateShare(now), 1 / 601.0, 1 / 600.0);
        // Long after, nothing is recent.
        Assert.Equal(0, ledger.Missed60s(200 * Second));
        Assert.Equal(0, ledger.LateShare(200 * Second));

        ledger.Reset();
        Assert.Equal(0, ledger.MissedVblanks);
        Assert.Equal(0, ledger.Missed60s(now));
        Assert.Equal(-1, ledger.LastShownNs);
    }

    [Fact]
    public void AMeasuredClockCountsAFrameThatMissedItsRefreshOnce()
    {
        var clock = new PresentClock(60, measured: true, PresentPath.GlWindow);
        const double period = 1e9 / 60;
        long Vblank(int k) => 2 * Second + (long)Math.Round(k * period) + 200_000;
        // Each frame starts just after a vblank and its swap returns at the next — except frame 50, which took two refreshes.
        var k = 0;
        for (var f = 0; f < 100; f++)
        {
            clock.BeginFrame(Vblank(k) + 1_000_000);
            k += f == 50 ? 2 : 1;
            clock.OnPresented(PresentResult.Done(Vblank(k)));
        }
        var snap = clock.Snapshot(Vblank(k) + 1_000_000);
        Assert.Equal(100, snap.Presented);
        Assert.Equal(1, snap.MissedVblanks);
        Assert.Equal(1, snap.Glitches);
        Assert.Equal(1, snap.Missed60s);
        Assert.Equal(1, clock.Vblank.Missed);
        Assert.True(snap.Settled);
        Assert.True(snap.Measured);
        Assert.InRange(snap.RefreshHz, 59.99, 60.01);
        Assert.Equal("gl", snap.ModeWord);
        Assert.Equal("gl-window", snap.PathWord);
        Assert.StartsWith("60.000000 Hz · 1 missed in 60 s", snap.Words(), StringComparison.Ordinal);

        var before = clock.Reset(Vblank(k) + 2_000_000);
        Assert.Equal(1, before.MissedVblanks);
        Assert.Equal(0, clock.Snapshot(Vblank(k) + 2_000_000).Presented);
    }

    [Fact]
    public void AnOffscreenOutputIsCountedAgainstItsOwnRefreshNotTheShowsFrameRate()
    {
        // A 50 Hz output fed by a loop that finishes a frame every 20 ms, then stalls for 60 ms once.
        var clock = new PresentClock(50, measured: false, PresentPath.Offscreen);
        var t = 3 * Second;
        for (var f = 0; f < 200; f++)
        {
            t += f == 100 ? 60_000_000 : 20_000_000;
            clock.OnPresented(PresentResult.Done(t));
        }
        var snap = clock.Snapshot(t + 1_000_000);
        Assert.Equal(2, snap.MissedVblanks);
        Assert.Equal(50, snap.RefreshHz);
        Assert.False(snap.Settled);
        Assert.Contains("50 Hz target · offscreen · 2 missed in 60 s", snap.Words(), StringComparison.Ordinal);
    }
}

/// <summary>The clock timed by the display's own statistics (DXGI): the first statistics switch it to display timing in the display's
/// refresh numbering; a present shown later than the refresh it was meant for is a glitch with its missed vblanks; 32-bit counters wrap
/// with unsigned differences; disjoint statistics restart the fit and stale ones go back to swap returns.</summary>
public sealed class DisplayStatisticsClockTests
{
    private const long Period = 16_666_667;

    [Fact]
    public void StatisticsTimeTheClockAndCountALatePresentAcrossTheCounterWrap()
    {
        var clock = new PresentClock(60, measured: true, PresentPath.DxgiFlip);
        Assert.False(clock.DisplayTimed);
        // Counters 10 below the u32 wrap; vblank k at t0 + k·T.
        var sync = uint.MaxValue - 10;
        var present = uint.MaxValue - 5;
        const long T0 = 1_000_000_000;
        clock.OnDisplayStatistics(present, sync, sync, T0);
        Assert.True(clock.DisplayTimed);
        Assert.True(clock.Measured);
        long t = T0;
        for (var f = 1; f <= 60; f++)
        {
            t += Period;
            present++;
            sync++;
            // The present is queued just after the vblank before the one it is meant for.
            clock.OnPresented(PresentResult.Done(t - Period + 1_000_000) with { PresentCount = present });
            // Frame 40 is shown one refresh late (the previous picture stayed up one more vblank).
            var shownAt = f == 40 ? sync + 1 : sync;
            if (f == 40)
            {
                sync++;
                t += Period;
            }
            clock.OnDisplayStatistics(present, shownAt, sync, t);
        }
        Assert.True(sync < 100, "the refresh counter wrapped");
        var s = clock.Snapshot(t);
        Assert.Equal(60, s.Presented);
        Assert.Equal(1, s.Glitches);
        Assert.Equal(1, s.MissedVblanks);
        Assert.True(s.Settled);
        Assert.InRange(s.RefreshHz, 59.99, 60.01);

        // Disjoint (a mode change): the fit restarts, the ledger keeps its counts.
        clock.OnDisplayDisjoint();
        Assert.False(clock.Vblank.HasPhase);
        Assert.Equal(1, clock.Snapshot(t).Glitches);

        // Stale statistics: back to swap returns, no longer display timed.
        clock.OnDisplayStale();
        Assert.False(clock.DisplayTimed);
        clock.OnPresented(PresentResult.Done(t + Period));
        Assert.Equal(61, clock.Snapshot(t + Period).Presented);
    }
}

/// <summary>The statistics name the last present the display showed, which lags the newest queued one: the two counts are unwrapped
/// apart, so a shown present is matched with the one queued under its number and a lag of one or two presents is no glitch.</summary>
public sealed class LaggingStatisticsTests
{
    private const long Period = 16_666_667;

    [Fact]
    public void AShownCountBehindTheQueuedOneMatchesItsOwnPresent()
    {
        var clock = new PresentClock(60, measured: true, PresentPath.DxgiFlip);
        var sync = uint.MaxValue - 20;
        var queued = uint.MaxValue - 30;
        long t = 1_000_000_000;
        clock.OnDisplayStatistics(queued, sync, sync, t);
        for (var f = 1; f <= 90; f++)
        {
            t += Period;
            sync++;
            queued++;
            clock.OnPresented(PresentResult.Done(t - Period + 1_000_000) with { PresentCount = queued });
            // The display shows the present queued one frame before, at the refresh it was meant for.
            if (f >= 2)
                clock.OnDisplayStatistics(queued - 1, sync - 1, sync, t);
        }
        var s = clock.Snapshot(t);
        Assert.Equal(90, s.Presented);
        Assert.Equal(0, s.Glitches);
        Assert.Equal(0, s.MissedVblanks);
        Assert.Equal(0, clock.Ledger.Discarded);
        Assert.True(clock.Ledger.Shown >= 88);
    }
}
