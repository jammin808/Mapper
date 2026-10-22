using System.Diagnostics;
using System.Globalization;
using PlayMap.Core.Time;
using Xunit.Abstractions;

namespace PlayMap.Core.Tests.Time;

/// <summary>A thread's time counts the work it did, not the time it was kept from running. What the measure must be is the test's
/// own reading of the platform, never the measure's word: on Linux and macOS the thread's CPU clock, precise, and no read is allowed
/// a tick; on Windows the thread's kernel and user time in clock ticks, not precise, and each read is allowed the tick it states. A
/// thread blocked while the test's thread spins 50 ms counts a fiftieth of that span at most (and, where the measure is precise, the
/// little it ran); a thread that spins counts at least half the time it ran and at most 1.1 times the stopwatch's span of the spin,
/// in each 10 ms of it, in the whole 20 ms and in a spin of 125 ms or eight of the measure's steps, whichever is longer, within the
/// measure's step. A spin runs until it has run its span: what it ran is the sum of the stopwatch's steps between its consecutive
/// reads that are under 20 µs, and a longer step is time it was kept from running (another thread's turn on its processor, the
/// process stopped), which the lower bound does not ask the measure to count; the upper bound is on the stopwatch's span, which
/// holds every such step. So a machine that gives the spinning thread less than a whole processor, or stops the process, fails
/// neither bound while the spin runs its span within 10 s of the stopwatch, and a measure that counts a running thread too little
/// still fails the lower one. On Windows each bound is a tick wider, and the reads still refuse the stopwatch: the blocked thread
/// counts at most one tick (a fiftieth of its span is under one), never the 50 ms the stopwatch reads. A clock interrupt charges a
/// tick to the thread it finds running, and interrupts come a tick apart, so a span is charged at most a tick over its stopwatch
/// length and the 10 ms and 20 ms reads stay under their upper bounds (a 20 ms read of three ticks or more is not a tick clock's);
/// their lower bounds are below zero there, so the long spin is what shows that a spinning thread is charged its work: half of what
/// it ran less a tick is three of the eight ticks it runs, which a measure that charged the spin nothing, or only its kernel time,
/// never reads, and a tick an interrupt or a DPC takes costs one of the five to spare. A tick read on Linux or macOS fails by the
/// measure's word (it is not precise) and by its reads: the blocked thread runs microseconds, so it reads nothing ("the little it
/// ran") or, when the clock interrupt lands in them, a whole tick (fifteen times a fiftieth of its span); and a 10 ms stretch run
/// unbroken holds at most one tick, so it reads nothing or a tick, never between half and 1.1 times its span. The spins are timed by
/// the stopwatch, so they run alone.</summary>
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
        var first = Spin(10);
        var t1 = ThreadTime.NowNs();
        var w1 = Stopwatch.GetTimestamp();
        var second = Spin(10);
        var t2 = ThreadTime.NowNs();
        var w2 = Stopwatch.GetTimestamp();
        // Eight steps or more: on Windows half of what it ran less a tick is three of the ticks spun.
        var longMs = (int)Math.Max(125, Math.Ceiling(8.0 * step / 1e6));
        var w3 = Stopwatch.GetTimestamp();
        var t3 = ThreadTime.NowNs();
        var spin = Spin(longMs);
        var t4 = ThreadTime.NowNs();
        var w4 = Stopwatch.GetTimestamp();
        Assert.True(first.Reads + second.Reads + spin.Reads > 0);
        log.WriteLine(Words($"blocked {waitedWall / 1e6:0.0} ms, counted {waitedCpu / 1e3:0.0} µs; spun {(w1 - w0) * 1e3 / Stopwatch.Frequency:0.00} + {(w2 - w1) * 1e3 / Stopwatch.Frequency:0.00} ms, ran {first.RanNs / 1e6:0.00} + {second.RanNs / 1e6:0.00} ms, counted {(t1 - t0) / 1e6:0.00} + {(t2 - t1) / 1e6:0.00} ms; spun {(w4 - w3) * 1e3 / Stopwatch.Frequency:0.00} ms, ran {spin.RanNs / 1e6:0.00} ms, counted {(t4 - t3) / 1e6:0.00} ms"));
        RanItsSpan("the first 10 ms", first, 10);
        RanItsSpan("the second 10 ms", second, 10);
        RanItsSpan(Words($"the {longMs} ms spin"), spin, longMs);
        AboutItsSpan("the first 10 ms", t1 - t0, first.RanNs, Stopwatch.GetElapsedTime(w0, w1).Ticks * 100, step);
        AboutItsSpan("the second 10 ms", t2 - t1, second.RanNs, Stopwatch.GetElapsedTime(w1, w2).Ticks * 100, step);
        AboutItsSpan("the whole 20 ms", t2 - t0, first.RanNs + second.RanNs, Stopwatch.GetElapsedTime(w0, w2).Ticks * 100, step);
        AboutItsSpan(Words($"the {longMs} ms spin"), t4 - t3, spin.RanNs, Stopwatch.GetElapsedTime(w3, w4).Ticks * 100, step);
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

    /// <summary>The step of the stopwatch between a spin's consecutive reads under which the spin counts the step as its own running
    /// (ns): a read takes tens of nanoseconds, and a longer step is time the thread was kept from running (another thread's turn on its
    /// processor, the process stopped) or, rarely, an interrupt it waited out, which the spin then does not count either.</summary>
    private const long RunStepNs = 20_000;

    /// <summary>The longest a spin takes, by the stopwatch, to run its span before it gives up.</summary>
    private static readonly TimeSpan SpinPatience = TimeSpan.FromSeconds(10);

    /// <summary>What a spin did: how often it read the stopwatch, the time it ran (stopwatch ticks: the sum of the steps between its
    /// consecutive reads under <see cref="RunStepNs"/>) and the span it was asked to run (stopwatch ticks, rounded up).</summary>
    private readonly record struct Spun(long Reads, long Ran, long Span)
    {
        /// <summary>The time it ran (ns).</summary>
        public long RanNs => (long)(Ran * (1e9 / Stopwatch.Frequency));
    }

    /// <summary>Spins until it has run <paramref name="ms"/>, or until <see cref="SpinPatience"/> has passed by the stopwatch.</summary>
    private static Spun Spin(int ms)
    {
        var runStep = RunStepNs * Stopwatch.Frequency / 1_000_000_000;
        var span = (ms * Stopwatch.Frequency + 999) / 1000;
        var last = Stopwatch.GetTimestamp();
        var giveUp = last + (long)(SpinPatience.TotalSeconds * Stopwatch.Frequency);
        long reads = 0, ran = 0;
        while (ran < span && last < giveUp)
        {
            var now = Stopwatch.GetTimestamp();
            if (now - last < runStep)
                ran += now - last;
            last = now;
            reads++;
        }
        return new Spun(reads, ran, span);
    }

    /// <summary>A spin ran the span it was asked for, so its lower bound asks the measure for half of that span at least.</summary>
    private static void RanItsSpan(string what, Spun spin, int ms) =>
        Assert.True(spin.Ran >= spin.Span,
            Words($"{what}: ran {spin.RanNs / 1e6:0.00} ms of its {ms} ms in {SpinPatience.TotalSeconds:0} s of the stopwatch"));

    /// <summary>A spin counts about the time it ran: never more than a tenth over the stopwatch's span, never less than half of what it
    /// ran, within a step of the measure.</summary>
    private static void AboutItsSpan(string what, long cpuNs, long ranNs, long wallNs, long step) =>
        Assert.True(cpuNs >= ranNs / 2 - step && cpuNs <= wallNs * 11 / 10 + step,
            Words($"{what}: ran {ranNs / 1e6:0.00} ms of {wallNs / 1e6:0.00} ms of the stopwatch, counted {cpuNs / 1e6:0.00} ms, half of what it ran to 1.1 of the stopwatch's span allowed within {step / 1e6:0.###} ms ({ThreadTime.Basis})"));

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
