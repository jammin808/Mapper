using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
using PlayMap.Core.Time;
using Xunit.Abstractions;

namespace PlayMap.Core.Tests.Time;

/// <summary>A thread's time counts the work it did, not the time it was kept from running, to the measure's own precision. A thread
/// blocked while the test's thread spins counts almost none of that span and, where the measure is precise, the little it ran; a
/// thread that spins counts about the span it spun — never more than a tenth over, never less than half — in each 10 ms of it. A read
/// that moved in Windows' clock ticks (15.625 ms) can do neither: the blocked thread runs microseconds, so it reads nothing (a tick is
/// charged to it only when the clock interrupt lands in them, and then it reads a whole tick, fifteen times a fiftieth of its span);
/// and a 10 ms stretch holds at most one tick, so it reads nothing or a tick, never between half and 1.1 times its span (a 20 ms spin
/// read whole could read one tick, 15.6 ms, inside its bounds). Where the measure is Windows' ticks each read is allowed one, the
/// resolution it states. On Windows the measure is the thread's cycles exactly where their rate is the invariant time-stamp
/// counter's, and the fallback's ticks are as coarse as it says. The spins are timed by the stopwatch, so they run alone.</summary>
[Collection(ThreadClockCollection.Name)]
public sealed partial class ThreadTimeTests(ITestOutputHelper log)
{
    [Fact]
    public void AThreadsTimeCountsItsWorkNotItsWait()
    {
        log.WriteLine(ThreadTime.Basis);
        Assert.True(ThreadTime.Counted, ThreadTime.Basis);
        var step = ThreadTime.Precise ? 0 : ThreadTime.ResolutionNs;
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
        // ... and, where the measure is precise, the little it did run.
        Assert.True(!ThreadTime.Precise || waitedCpu > 0, Words($"blocked {waitedWall / 1e6:0.0} ms, counted nothing of the reads and the wait it ran"));

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

    /// <summary>On Windows the measure is the thread's cycles exactly where their rate is the time-stamp counter's and constant: an
    /// x64 kernel (it charges a thread the counter's ticks; an ARM64 kernel the core's own cycles) and a processor that says the
    /// counter is invariant. There it is precise, in nanosecond steps, at a measured rate; elsewhere it is the thread's ticks, not
    /// precise, and its resolution is the tick. Off Windows the CPU clock is read and no cycle rate is held.</summary>
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
        var constant = KernelIsX64() && CounterIsInvariant();
        Assert.True((constant ? ThreadClock.Cycles : ThreadClock.Ticks) == ThreadTime.Clock, ThreadTime.Basis);
        Assert.Equal(constant, ThreadTime.Precise);
        if (constant)
        {
            Assert.Equal(1, ThreadTime.ResolutionNs);
            Assert.True(ThreadTime.CycleNs > 0, ThreadTime.Basis);
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

    /// <summary>A spin counts about its span: never more than a tenth over, never less than half, to a step of the measure.</summary>
    private static void AboutItsSpan(string what, long cpuNs, long wallNs, long step) =>
        Assert.True(cpuNs >= wallNs / 2 - step && cpuNs <= wallNs * 11 / 10 + step,
            Words($"{what}: spun {wallNs / 1e6:0.00} ms of the stopwatch, counted {cpuNs / 1e6:0.00} ms ({ThreadTime.Basis})"));

    private static string Words(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);

    /// <summary>The processor says its time-stamp counter is invariant (CPUID 80000007h, EDX bit 8).</summary>
    private static bool CounterIsInvariant() =>
        X86Base.IsSupported
        && (uint)X86Base.CpuId(unchecked((int)0x8000_0000), 0).Eax >= 0x8000_0007
        && (X86Base.CpuId(unchecked((int)0x8000_0007), 0).Edx & (1 << 8)) != 0;

    /// <summary>The kernel runs x64 code (MACHINE_ATTRIBUTES.KernelEnabled for IMAGE_FILE_MACHINE_AMD64): an x64 process emulated on
    /// ARM64 Windows is told the machine is x64, and only the kernel's machine types say otherwise. A Windows without the query (before
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
