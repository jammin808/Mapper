using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
using PlayMap.Core.Time;
using Xunit.Abstractions;

namespace PlayMap.Core.Tests.Time;

/// <summary>A thread's time counts the work it did, not the time it was kept from running. A thread blocked while the test's thread
/// spins counts almost none of that span and, where the measure must be precise, the little it ran; a thread that spins counts about
/// the span it spun — never more than a tenth over, never less than half (nine tenths where the reads are converted cycles) — in each
/// 10 ms of it. Whether the measure must be precise is the test's own reading of the platform, never the measure's word: Linux and
/// macOS (the CPU clock) and Windows on an x64 kernel whose processor says its time-stamp counter is invariant (the cycles) must be,
/// and there no read is allowed a tick. A read that moved in Windows' clock ticks (15.625 ms) fails there by the measure's word (it
/// is not precise) and by its reads, twice over: the blocked thread runs microseconds, so it reads nothing ("the little it ran") or, when the clock interrupt lands in them, a whole tick (fifteen
/// times a fiftieth of its span); and a 10 ms stretch holds at most one tick, so it reads nothing or a tick, never between half and
/// 1.1 times its span (a 20 ms spin read whole could read one tick, 15.6 ms, inside its bounds, so each half is read too). Where the
/// platform cannot be precise (an ARM64 kernel, a counter not said to be invariant) the measure must say it is not and each read is
/// allowed the tick it states; the blocked thread still refuses the stopwatch there, which reads its whole 50 ms. The spins are timed
/// by the stopwatch, so they run alone.</summary>
[Collection(ThreadClockCollection.Name)]
public sealed partial class ThreadTimeTests(ITestOutputHelper log)
{
    [Fact]
    public void AThreadsTimeCountsItsWorkNotItsWait()
    {
        log.WriteLine(ThreadTime.Basis);
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
        // Almost none of the span: a fiftieth of it at most, for it ran only to read the clock and to wait ...
        Assert.True(waitedCpu <= waitedWall / 50 + step,
            Words($"blocked {waitedWall / 1e6:0.0} ms of the stopwatch, counted {waitedCpu / 1e6:0.000} ms of its own"));
        // ... and, where the measure must be precise, the little it did run.
        Assert.True(!mustBePrecise || waitedCpu > 0, Words($"blocked {waitedWall / 1e6:0.0} ms, counted nothing of the reads and the wait it ran"));

        // Converted cycles read a span at least as long as the thread ran only while the rate they are converted at is not above the
        // counter's (a count that lagged the thread while the rate was measured makes it so, and reads short). The spins then run at
        // the highest priority, which the machine's ordinary work does not take the processor from, and read nine tenths of their
        // span at least.
        var cycles = ThreadTime.Clock == ThreadClock.Cycles;
        var floor = cycles ? 0.9 : 0.5;
        var thread = Thread.CurrentThread;
        var priority = thread.Priority;
        if (cycles)
            thread.Priority = ThreadPriority.Highest;
        long w0, w1, w2, t0, t1, t2, spun;
        try
        {
            w0 = Stopwatch.GetTimestamp();
            t0 = ThreadTime.NowNs();
            spun = Spin(10);
            t1 = ThreadTime.NowNs();
            w1 = Stopwatch.GetTimestamp();
            spun += Spin(10);
            t2 = ThreadTime.NowNs();
            w2 = Stopwatch.GetTimestamp();
        }
        finally
        {
            if (cycles)
                thread.Priority = priority;
        }
        Assert.True(spun > 0);
        log.WriteLine(Words($"blocked {waitedWall / 1e6:0.0} ms, counted {waitedCpu / 1e3:0.0} µs; spun {(w1 - w0) * 1e3 / Stopwatch.Frequency:0.00} + {(w2 - w1) * 1e3 / Stopwatch.Frequency:0.00} ms, counted {(t1 - t0) / 1e6:0.00} + {(t2 - t1) / 1e6:0.00} ms"));
        AboutItsSpan("the first 10 ms", t1 - t0, Stopwatch.GetElapsedTime(w0, w1).Ticks * 100, step, floor);
        AboutItsSpan("the second 10 ms", t2 - t1, Stopwatch.GetElapsedTime(w1, w2).Ticks * 100, step, floor);
        AboutItsSpan("the whole 20 ms", t2 - t0, Stopwatch.GetElapsedTime(w0, w2).Ticks * 100, step, floor);
    }

    /// <summary>A read moves in steps no finer than the step the measure states. Where the thread's time can be read precisely the
    /// finest step a spinning thread's reads move in is under 10 µs, a thousandth of the survey slice's 10 ms budget (a read's own
    /// cost, some hundreds of nanoseconds, bounds it from below, not the stated step); where it is Windows' ticks the finest is under
    /// two of them.</summary>
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

    /// <summary>On Windows the measure is the thread's cycles exactly where their rate is the time-stamp counter's and constant: an
    /// x64 kernel (it charges a thread the counter's ticks; an ARM64 kernel the core's own cycles) and a processor that says the
    /// counter is invariant. There it is precise, at a measured rate, in steps of a cycle rounded up to a whole nanosecond; elsewhere
    /// it is the thread's ticks, not precise, and its resolution is the tick. Off Windows the CPU clock is read and no cycle rate is
    /// held.</summary>
    [Fact]
    [Trait("Category", "Windows")]
    public void OnWindowsTheThreadsCyclesAreReadWhereTheirRateIsConstant()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() ? ThreadClock.CpuClock : ThreadClock.Stopwatch, ThreadTime.Clock);
            Assert.Equal(0, ThreadTime.CycleNs);
            return;
        }
        var constant = PreciseIsPossible();
        Assert.True((constant ? ThreadClock.Cycles : ThreadClock.Ticks) == ThreadTime.Clock, ThreadTime.Basis);
        Assert.Equal(constant, ThreadTime.Precise);
        if (constant)
        {
            Assert.True(ThreadTime.CycleNs > 0, ThreadTime.Basis);
            Assert.Equal(Math.Max(1, (long)Math.Ceiling(ThreadTime.CycleNs)), ThreadTime.ResolutionNs);
        }
        else
        {
            Assert.Equal(ThreadTime.WindowsTickNs(), ThreadTime.ResolutionNs);
            Assert.Equal(0, ThreadTime.CycleNs);
        }
    }

    /// <summary>The read Windows falls back to (the thread's kernel and user time) moves in steps no finer than the resolution it
    /// states, Windows' clock increment, and the smallest of them is that increment: a caller told the fallback's resolution knows a
    /// read there tells nothing finer. Read here on whichever Windows the job runs; off Windows there is no such read.</summary>
    [Fact]
    [Trait("Category", "Windows")]
    public void WindowsThreadTimesMoveInStepsOfTheTickTheyState()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(-1, ThreadTime.TicksNs());
            return;
        }
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

    private static long Spin(int ms)
    {
        var spun = 0L;
        var w = Stopwatch.GetTimestamp();
        while (Stopwatch.GetElapsedTime(w) < TimeSpan.FromMilliseconds(ms))
            spun++;
        return spun;
    }

    /// <summary>A spin counts about its span: never more than a tenth over, never less than <paramref name="floor"/> of it, to a step
    /// of the measure.</summary>
    private static void AboutItsSpan(string what, long cpuNs, long wallNs, long step, double floor) =>
        Assert.True(cpuNs >= wallNs * floor - step && cpuNs <= wallNs * 11 / 10 + step,
            Words($"{what}: spun {wallNs / 1e6:0.00} ms of the stopwatch, counted {cpuNs / 1e6:0.00} ms, {floor:0.0} to 1.1 of it allowed ({ThreadTime.Basis})"));

    private static string Words(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);

    /// <summary>Whether the thread's own time can be read precisely here, by the test's own reading of the platform: the CPU clock on
    /// Linux and macOS, and on Windows the cycles where the kernel is x64 and the processor says the counter is invariant.</summary>
    private static bool PreciseIsPossible() =>
        OperatingSystem.IsWindows() ? KernelIsX64() && CounterIsInvariant() : OperatingSystem.IsLinux() || OperatingSystem.IsMacOS();

    /// <summary>The processor says its time-stamp counter is invariant (CPUID 80000007h, EDX bit 8).</summary>
    private static bool CounterIsInvariant() =>
        X86Base.IsSupported
        && (uint)X86Base.CpuId(unchecked((int)0x8000_0000), 0).Eax >= 0x8000_0007
        && (X86Base.CpuId(unchecked((int)0x8000_0007), 0).Edx & (1 << 8)) != 0;

    /// <summary>The kernel runs x64 code: the runtime reports the native machine, an emulating process's included, and the kernel's
    /// machine types (MACHINE_ATTRIBUTES.KernelEnabled for IMAGE_FILE_MACHINE_AMD64) confirm it. A Windows without the query (before
    /// build 22000) has no x64 emulation on ARM64, so the machine it reports stands.</summary>
    private static bool KernelIsX64()
    {
        if (RuntimeInformation.OSArchitecture != Architecture.X64)
            return false;
        try
        {
            return GetMachineTypeAttributes(0x8664, out var attributes) != 0 || (attributes & 2) != 0;
        }
        catch (EntryPointNotFoundException)
        {
            return true;
        }
    }

    [LibraryImport("kernel32", EntryPoint = "GetMachineTypeAttributes")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int GetMachineTypeAttributes(ushort machine, out int attributes);
}

/// <summary>The thread clock's tests spin against the stopwatch: they run alone, after the parallel tests.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ThreadClockCollection
{
    public const string Name = "Thread clocks";
}
