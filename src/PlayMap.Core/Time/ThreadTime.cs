using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace PlayMap.Core.Time;

/// <summary>What a <see cref="ThreadTime"/> read counts on this machine.</summary>
public enum ThreadClock
{
    /// <summary>The stopwatch: wall time, other threads' turns included (no thread clock is read on this platform).</summary>
    Stopwatch,

    /// <summary>The thread's CPU clock (Linux, macOS).</summary>
    CpuClock,

    /// <summary>The thread's kernel and user time as Windows charges it, in whole clock ticks (Windows).</summary>
    Ticks,
}

/// <summary>The work the calling thread has done, in nanoseconds of its own processor time: what it did between two reads, never the
/// time the machine gave other threads meanwhile (a preemption, a loaded machine) nor the time it was stopped (a collection, a page
/// brought in). <see cref="Clock"/> says what a read is here, <see cref="ResolutionNs"/> the finest step it moves in and
/// <see cref="Basis"/> both in words. Linux and macOS read the thread's CPU clock, which is <see cref="Precise"/>: a span's work is
/// within its read and the step. Windows reads the thread's kernel and user time, which it charges in whole clock ticks (15.625 ms
/// by default), a tick to the thread a clock interrupt finds running, so a read there is not precise and bounds no span's work from
/// above: a stretch the thread ran unbroken reads at most a tick over its work and at most a tick under it, and a tick further under
/// for each clock interrupt that found the processor in an interrupt handler or a DPC (Windows charges that tick to the processor,
/// not the thread); a span broken into stretches reads within a tick of the work of each. Windows' count of a thread's cycles is not
/// converted: Windows leaves interrupt time out of it, so its rate against the stopwatch moves with the machine's interrupts and load
/// (by more than a tenth on a Hyper-V guest), and no rate measured once converts it to the work done. Elsewhere the reads are the
/// stopwatch's. What a read is is fixed when the type is first touched, so every read is thread-safe.</summary>
public static partial class ThreadTime
{
    private const int LinuxThreadCpu = 3;
    private const int MacThreadCpu = 16;

    /// <summary>The calling thread's pseudo-handle on Windows.</summary>
    private const nint CurrentThread = -2;

    /// <summary>Windows' default clock interval (100 ns units), the step a thread's charged time moves in when the system does not say.</summary>
    private const uint DefaultTimeIncrement = 156_250;

    private static readonly Choice Chosen = Choose();

    /// <summary>What a read counts here.</summary>
    public static ThreadClock Clock => Chosen.Clock;

    /// <summary>True when a read is the thread's own processor time from a fine clock (the CPU clock, in <see cref="ResolutionNs"/>
    /// steps), not Windows' clock ticks nor the stopwatch.</summary>
    public static bool Precise => Chosen.Clock == ThreadClock.CpuClock;

    /// <summary>True when a read is the thread's own processor time at all (precisely, or in Windows' ticks), not the stopwatch.</summary>
    public static bool Counted => Chosen.Clock != ThreadClock.Stopwatch;

    /// <summary>The step a read moves in (ns), at most: a read tells nothing finer. Where the read is <see cref="Precise"/> the work
    /// done in a span is within the span's read plus this step. In Windows' ticks (<see cref="ThreadClock.Ticks"/>) that holds only
    /// for a span the thread ran in one stretch with no tick charged to an interrupt or a DPC, and a span the machine broke into
    /// stretches reads within a tick of the work of each.</summary>
    public static long ResolutionNs => Chosen.ResolutionNs;

    /// <summary>What a read is here, its resolution and, where it is not precise, why.</summary>
    public static string Basis => Chosen.Basis;

    /// <summary>The calling thread's processor time now (ns); only differences of two reads on one thread mean anything.</summary>
    public static long NowNs() => Chosen.Clock switch
    {
        ThreadClock.CpuClock when ReadCpuClock(out var ns) => ns,
        ThreadClock.Ticks when ReadTicks(out var charged) => charged,
        _ => StopwatchNs(),
    };

    /// <summary>The calling thread's kernel and user time as Windows charges it (ns), or −1 off Windows: the read
    /// <see cref="ThreadClock.Ticks"/> makes.</summary>
    internal static long TicksNs() => OperatingSystem.IsWindows() && ReadTicks(out var ns) ? ns : -1;

    /// <summary>The step Windows charges a thread's kernel and user time in (ns): the system's clock increment, fixed at start.</summary>
    internal static long WindowsTickNs() =>
        GetSystemTimeAdjustment(out _, out var increment, out _) && increment > 0 ? increment * 100L : DefaultTimeIncrement * 100L;

    private sealed record Choice(ThreadClock Clock, long ResolutionNs, string Basis);

    private static Choice Choose()
    {
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            if (!ReadCpuClock(out _))
                return StopwatchChoice("the thread's CPU clock could not be read");
            var resolution = CpuClockResolutionNs();
            return new Choice(ThreadClock.CpuClock, resolution,
                string.Create(CultureInfo.InvariantCulture, $"the thread's CPU clock, in steps of {resolution} ns"));
        }
        if (!OperatingSystem.IsWindows())
            return StopwatchChoice("no thread clock is read on this platform");
        if (!ReadTicks(out _))
            return StopwatchChoice("the thread's times could not be read");
        var tick = WindowsTickNs();
        return new Choice(ThreadClock.Ticks, tick, string.Create(CultureInfo.InvariantCulture,
            $"the thread's kernel and user time, which Windows charges in whole {tick / 1e6:0.###} ms clock ticks, so a read is not precise and is within a tick of the work of each stretch the thread ran, less any tick an interrupt or a DPC took: the thread's cycles are not converted, for Windows leaves interrupt time out of them and their rate against the stopwatch moves with the machine's load (by more than a tenth on a Hyper-V guest), so no rate measured once converts them"));
    }

    private static Choice StopwatchChoice(string why) =>
        new(ThreadClock.Stopwatch, Math.Max(1, (long)Math.Ceiling(1e9 / Stopwatch.Frequency)),
            "the stopwatch, which counts other threads' turns too: " + why);

    private static long StopwatchNs() => (long)(Stopwatch.GetTimestamp() * (1e9 / Stopwatch.Frequency));

    private static bool ReadCpuClock(out long ns)
    {
        var t = default(Timespec);
        if (ClockGetTime(OperatingSystem.IsLinux() ? LinuxThreadCpu : MacThreadCpu, ref t) != 0)
        {
            ns = 0;
            return false;
        }
        ns = t.Seconds * 1_000_000_000L + t.Nanoseconds;
        return true;
    }

    /// <summary>The CPU clock's resolution as the system states it (ns), at least 1.</summary>
    private static long CpuClockResolutionNs()
    {
        var r = default(Timespec);
        return ClockGetRes(OperatingSystem.IsLinux() ? LinuxThreadCpu : MacThreadCpu, ref r) == 0
            ? Math.Max(1, r.Seconds * 1_000_000_000L + r.Nanoseconds)
            : 1;
    }

    private static bool ReadTicks(out long ns)
    {
        // The times are in 100 ns units.
        if (!GetThreadTimes(CurrentThread, out _, out _, out var kernel, out var user))
        {
            ns = 0;
            return false;
        }
        ns = (kernel + user) * 100;
        return true;
    }

    /// <summary>struct timespec on a 64-bit Unix (the platforms the node runs on).</summary>
    [StructLayout(LayoutKind.Sequential)]
    private record struct Timespec(long Seconds, long Nanoseconds);

    [LibraryImport("libc", EntryPoint = "clock_gettime")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static partial int ClockGetTime(int clock, ref Timespec time);

    [LibraryImport("libc", EntryPoint = "clock_getres")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static partial int ClockGetRes(int clock, ref Timespec resolution);

    [LibraryImport("kernel32", EntryPoint = "GetThreadTimes")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetThreadTimes(nint thread, out long creation, out long exit, out long kernel, out long user);

    [LibraryImport("kernel32", EntryPoint = "GetSystemTimeAdjustment")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetSystemTimeAdjustment(out uint adjustment, out uint increment, [MarshalAs(UnmanagedType.Bool)] out bool disabled);
}
