using System.Diagnostics;
using System.Globalization;
using PlayMap.Core.Time;
using Xunit.Abstractions;

namespace PlayMap.Core.Tests.Time;

/// <summary>A thread's time counts the work it did, not the time it was kept from running. What the measure must be is the test's
/// own reading of the platform, never the measure's word: on Linux and macOS the thread's CPU clock, precise, and no read is
/// allowed a tick; on Windows the thread's kernel and user time in clock ticks, not precise, and each read is allowed the tick it
/// states. A thread blocked while the test's thread spins 50 ms counts a fiftieth of that span at most (and, where the measure is
/// precise, the little it ran); a thread that spins counts half to 1.1 times the span it spun, in each 10 ms of it and in the whole
/// 20 ms, within the measure's step. On Windows each bound is a tick wider, and the reads still refuse the stopwatch: the blocked
/// thread counts at most one tick (a fiftieth of its span is under one), never the 50 ms the stopwatch reads. A spinning thread's
/// ticks fall inside its bounds, for each clock interrupt charges a tick to the thread it finds running, so a stretch spun unbroken
/// is charged within a tick of its length (a 20 ms read of three ticks or more, past its bounds, is not a tick clock's). A tick
/// read on Linux or macOS fails by the measure's word (it is not precise) and by its reads: the blocked thread runs microseconds,
/// so it reads nothing ("the little it ran") or, when the clock interrupt lands in them, a whole tick (fifteen times a fiftieth of
/// its span); and a 10 ms stretch holds at most one tick, so it reads nothing or a tick, never between half and 1.1 times its span.
/// The spins are timed by the stopwatch, so they run alone.</summary>
[Collection(ThreadClockCollection.Name)]
public sealed class ThreadTimeTests(ITestOutputHelper log)
{
    [Fact]
    public void AThreadsTimeCountsItsWorkNotItsWait()
    {
        log.WriteLine(ThreadTime.Basis);
        Assert.True(ExpectedClock() == ThreadTime.Clock, Words($"the measure here must be {ExpectedClock()}, and it is {ThreadTime.Basis}"));
        Assert.True(ThreadTime.Counted, ThreadTime.Basis);
        var mustBePrecise = PreciseIsPossible();
        Assert.True(mustBePrecise == ThreadTime.Precise,
            Words($"the thread's time can {(mustBePrecise ? "" : "not ")}be read precisely here, and the measure is {ThreadTime.Basis}"));
        var step = mustBePrecise ? 0 : ThreadTime.ResolutionNs;
        using var go = new ManualResetEventSlim(false);
        long waitedCpu = 0, waitedWall = 0;
        var blocked = new Thread(() =>
        {
            var cpu = ThreadTime.NowNs();
            var wall = Stopwatch.GetTimestamp();
            go.Wait();
            waitedCpu = ThreadTime.NowNs() - cpu;
            waitedWall = Stopwatch.GetElapsedTime(wall).Ticks * 100;
        }) { IsBackground = true };
        blocked.Start();
        // Held until it is blocked, and then while this thread does 50 ms of the stopwatch's work.
        while ((blocked.ThreadState & System.Threading.ThreadState.WaitSleepJoin) == 0)
            Thread.Yield();
        var held = Stopwatch.GetTimestamp();
        while (Stopwatch.GetElapsedTime(held) < TimeSpan.FromMilliseconds(50))
            Thread.Yield();
        go.Set();
        blocked.Join();
        Assert.True(waitedWall >= 50_000_000, $"{waitedWall} ns");
        // Almost none of the span: a fiftieth of it at most, for it ran only to read the clock and to wait (and a tick at most where
        // the measure is Windows' ticks) ...
        Assert.True(waitedCpu <= waitedWall / 50 + step,
            Words($"blocked {waitedWall / 1e6:0.0} ms of the stopwatch, counted {waitedCpu / 1e6:0.000} ms of its own ({ThreadTime.Basis})"));
        // ... and, where the measure must be precise, the little it did run.
        Assert.True(!mustBePrecise || waitedCpu > 0, Words($"blocked {waitedWall / 1e6:0.0} ms, counted nothing of the reads and the wait it ran"));

        var w0 = Stopwatch.GetTimestamp();
        var t0 = ThreadTime.NowNs();
        var spun = Spin(10);
        var t1 = ThreadTime.NowNs();
        var w1 = Stopwatch.GetTimestamp();
        spun += Spin(10);
        var t2 = ThreadTime.NowNs();
        var w2 = Stopwatch.GetTimestamp();
        Assert.True(spun > 0);
        log.WriteLine(Words($"blocked {waitedWall / 1e6:0.0} ms, counted {waitedCpu / 1e3:0.0} µs; spun {(w1 - w0) * 1e3 / Stopwatch.Frequency:0.00} + {(w2 - w1) * 1e3 / Stopwatch.Frequency:0.00} ms, counted {(t1 - t0) / 1e6:0.00} + {(t2 - t1) / 1e6:0.00} ms"));
        AboutItsSpan("the first 10 ms", t1 - t0, Stopwatch.GetElapsedTime(w0, w1).Ticks * 100, step);
        AboutItsSpan("the second 10 ms", t2 - t1, Stopwatch.GetElapsedTime(w1, w2).Ticks * 100, step);
        AboutItsSpan("the whole 20 ms", t2 - t0, Stopwatch.GetElapsedTime(w0, w2).Ticks * 100, step);
    }

    /// <summary>A read moves in steps no finer than the step the measure states. Where the thread's time can be read precisely the
    /// finest step a spinning thread's reads move in is under 10 µs, a thousandth of the survey slice's 10 ms budget (a read's own
    /// cost, some hundreds of nanoseconds, bounds it from below, not the stated step); where it is Windows' ticks the finest is under
    /// two of them (reads a fraction of a microsecond apart see one tick charged at a time).</summary>
    [Fact]
    public void AReadMovesInTheStepItStates()
    {
        var mustBePrecise = PreciseIsPossible();
        var steps = new List<long>();
        var last = ThreadTime.NowNs();
        var watch = Stopwatch.GetTimestamp();
        while (steps.Count < 1000 && Stopwatch.GetElapsedTime(watch) < TimeSpan.FromSeconds(2))
        {
            var now = ThreadTime.NowNs();
            if (now != last)
                steps.Add(now - last);
            last = now;
        }
        Assert.NotEmpty(steps);
        var finest = steps.Min();
        log.WriteLine(Words($"{steps.Count} steps, the finest {finest} ns ({ThreadTime.Basis})"));
        Assert.True(finest >= ThreadTime.ResolutionNs, Words($"a step of {finest} ns against the stated {ThreadTime.ResolutionNs} ns ({ThreadTime.Basis})"));
        Assert.True(mustBePrecise ? finest < 10_000 : finest < 2 * ThreadTime.ResolutionNs,
            Words($"the finest step {finest} ns, where the thread's time can {(mustBePrecise ? "" : "not ")}be read precisely ({ThreadTime.Basis})"));
    }

    /// <summary>On Windows the measure is the thread's kernel and user time in Windows' clock ticks, not precise, its resolution the
    /// tick the system states, and its basis says the thread's cycles are not converted and why; off Windows it is the CPU clock
    /// (Linux, macOS) and there is no tick read.</summary>
    [Fact]
    [Trait("Category", "Windows")]
    public void OnWindowsTheThreadsTimeIsItsChargedTicks()
    {
        Assert.Equal(ExpectedClock(), ThreadTime.Clock);
        if (OperatingSystem.IsWindows())
        {
            Assert.False(ThreadTime.Precise, ThreadTime.Basis);
            Assert.True(ThreadTime.Counted, ThreadTime.Basis);
            Assert.Equal(ThreadTime.WindowsTickNs(), ThreadTime.ResolutionNs);
            Assert.True(ThreadTime.TicksNs() >= 0, ThreadTime.Basis);
            Assert.Contains("cycles are not converted", ThreadTime.Basis, StringComparison.Ordinal);
        }
        else
        {
            Assert.Equal(-1, ThreadTime.TicksNs());
        }
    }

    /// <summary>Windows' thread times (the thread's kernel and user time) move in steps no finer than the resolution they state,
    /// Windows' clock increment, and the smallest of them is that increment: a caller told the resolution knows a read tells nothing
    /// finer. Read here on whichever Windows the job runs; off Windows there is no such read.</summary>
    [Fact]
    [Trait("Category", "Windows")]
    public void WindowsThreadTimesMoveInStepsOfTheTickTheyState()
    {
        if (OperatingSystem.IsWindows())
        {
            var tick = ThreadTime.WindowsTickNs();
            Assert.True(tick >= 1_000_000, $"{tick} ns");
            var steps = new List<long>();
            var last = ThreadTime.TicksNs();
            var watch = Stopwatch.GetTimestamp();
            // Spinning until the thread has been charged three times (a tick lands on a running thread every clock interval).
            while (steps.Count < 3 && Stopwatch.GetElapsedTime(watch) < TimeSpan.FromSeconds(2))
            {
                var now = ThreadTime.TicksNs();
                if (now != last)
                    steps.Add(now - last);
                last = now;
            }
            Assert.NotEmpty(steps);
            Assert.All(steps, s => Assert.True(s >= tick, $"a step of {s} ns against the stated {tick} ns"));
            Assert.True(steps.Min() < 2 * tick, $"steps {string.Join(", ", steps)} ns against the stated {tick} ns");
        }
        else
        {
            Assert.Equal(-1, ThreadTime.TicksNs());
        }
    }

    private static long Spin(int ms)
    {
        var spun = 0L;
        var w = Stopwatch.GetTimestamp();
        while (Stopwatch.GetElapsedTime(w) < TimeSpan.FromMilliseconds(ms))
            spun++;
        return spun;
    }

    /// <summary>A spin counts about its span: never more than a tenth over, never less than half of it, within a step of the
    /// measure.</summary>
    private static void AboutItsSpan(string what, long cpuNs, long wallNs, long step) =>
        Assert.True(cpuNs >= wallNs / 2 - step && cpuNs <= wallNs * 11 / 10 + step,
            Words($"{what}: spun {wallNs / 1e6:0.00} ms of the stopwatch, counted {cpuNs / 1e6:0.00} ms, 0.5 to 1.1 of it allowed within {step / 1e6:0.###} ms ({ThreadTime.Basis})"));

    private static string Words(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);

    /// <summary>What a read must count here, by the test's own reading of the platform: the CPU clock on Linux and macOS, Windows'
    /// charged ticks on Windows.</summary>
    private static ThreadClock ExpectedClock() =>
        OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() ? ThreadClock.CpuClock
        : OperatingSystem.IsWindows() ? ThreadClock.Ticks
        : ThreadClock.Stopwatch;

    /// <summary>Whether the thread's own time can be read precisely here, by the test's own reading of the platform: the CPU clock on
    /// Linux and macOS.</summary>
    private static bool PreciseIsPossible() => ExpectedClock() == ThreadClock.CpuClock;
}

/// <summary>The thread clock's tests spin against the stopwatch: they run alone, after the parallel tests.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ThreadClockCollection
{
    public const string Name = "Thread clocks";
}
