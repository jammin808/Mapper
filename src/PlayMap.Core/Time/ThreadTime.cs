using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;

namespace PlayMap.Core.Time;

/// <summary>What a <see cref="ThreadTime"/> read counts on this machine.</summary>
public enum ThreadClock
{
    /// <summary>The stopwatch: wall time, other threads' turns included (no thread clock is read on this platform).</summary>
    Stopwatch,

    /// <summary>The thread's CPU clock (Linux, macOS).</summary>
    CpuClock,

    /// <summary>The thread's cycles of the processor's invariant time-stamp counter, converted at the counter's rate as measured once
    /// against the stopwatch (Windows on an x64 kernel).</summary>
    Cycles,

    /// <summary>The thread's kernel and user time as Windows charges it, in whole clock ticks (Windows where the cycles are not
    /// converted).</summary>
    Ticks,
}

/// <summary>The work the calling thread has done, in nanoseconds of its own processor time: what it did between two reads, never the
/// time the machine gave other threads meanwhile (a preemption, a loaded machine) nor the time it was stopped (a collection, a page
/// brought in). <see cref="Clock"/> says what a read is here, <see cref="ResolutionNs"/> the finest step it moves in and
/// <see cref="Basis"/> both in words. Linux and macOS read the thread's CPU clock. Windows on an x64 kernel whose processor says its
/// time-stamp counter is invariant reads the thread's cycles (the counter's ticks while the thread ran, user and kernel mode) at the
/// counter's rate, measured once against the stopwatch when the type is first touched (<see cref="Warm"/>). Both are
/// <see cref="Precise"/>. Other Windows reads the thread's kernel and user time, which Windows charges in whole clock ticks: an ARM64
/// kernel counts a thread's cycles of the core's own clock, whose rate moves with the core's speed, and a counter the processor does
/// not say is invariant may change rate with it. Elsewhere the reads are the stopwatch's. What a read converts with is fixed before
/// the first read returns on any thread, so every read is thread-safe and no span two reads take holds the measuring.</summary>
public static partial class ThreadTime
{
    private const int LinuxThreadCpu = 3;
    private const int MacThreadCpu = 16;

    /// <summary>The calling thread's pseudo-handle on Windows.</summary>
    private const nint CurrentThread = -2;

    /// <summary>The time-stamp counter is invariant (CPUID 80000007h, EDX bit 8): it runs at one rate in every power state.</summary>
    private const int InvariantTscBit = 1 << 8;

    private const ushort MachineAmd64 = 0x8664;

    /// <summary>MACHINE_ATTRIBUTES.KernelEnabled: the kernel runs this machine type's code.</summary>
    private const int MachineKernelEnabled = 2;

    /// <summary>Windows' default clock interval (100 ns units), the step a thread's charged time moves in when the system does not say.</summary>
    private const uint DefaultTimeIncrement = 156_250;

    /// <summary>How many spins of about a millisecond the cycle rate is measured over.</summary>
    private const int RateSpins = 16;

    /// <summary>The range an x64 time-stamp counter's rate lies in (cycles a nanosecond: 100 MHz to 10 GHz); a measured rate outside
    /// it is not the counter's.</summary>
    private const double MinCyclesPerNs = 0.1;

    private const double MaxCyclesPerNs = 10;

    private static readonly Choice Chosen = Choose();

    /// <summary>What a read counts here.</summary>
    public static ThreadClock Clock => Chosen.Clock;

    /// <summary>True when a read is the thread's own processor time from a fine clock (the CPU clock, or Windows' converted cycles, in
    /// <see cref="ResolutionNs"/> steps), not Windows' clock ticks nor the stopwatch.</summary>
    public static bool Precise => Chosen.Clock is ThreadClock.CpuClock or ThreadClock.Cycles;

    /// <summary>True when a read is the thread's own processor time at all (precisely, or in Windows' ticks), not the stopwatch.</summary>
    public static bool Counted => Chosen.Clock != ThreadClock.Stopwatch;

    /// <summary>The finest step a read moves in (ns): a span read here is within one of these of the work done in it.</summary>
    public static long ResolutionNs => Chosen.ResolutionNs;

    /// <summary>Nanoseconds a cycle where the cycles are read (<see cref="ThreadClock.Cycles"/>), else 0.</summary>
    public static double CycleNs => Chosen.CycleNs;

    /// <summary>What a read is here, its resolution and, where it is not precise, why.</summary>
    public static string Basis => Chosen.Basis;

    /// <summary>Fixes what the reads convert with (on x64 Windows the cycle rate's measuring, about 16 ms of spinning) on the calling
    /// thread. The engine calls it as the node starts, off the control lane and the render thread, so neither ever spins for it.</summary>
    public static void Warm() => _ = Chosen.Clock;

    /// <summary>The calling thread's processor time now (ns); only differences of two reads on one thread mean anything.</summary>
    public static long NowNs()
    {
        var chosen = Chosen;
        switch (chosen.Clock)
        {
            case ThreadClock.CpuClock when ReadCpuClock(out var ns):
                return ns;
            case ThreadClock.Cycles when QueryThreadCycleTime(CurrentThread, out var cycles):
                return (long)(cycles * chosen.CycleNs);
            case ThreadClock.Ticks when ReadTicks(out var charged):
                return charged;
            default:
                return StopwatchNs();
        }
    }

    /// <summary>The calling thread's kernel and user time as Windows charges it (ns), or −1 off Windows: the read
    /// <see cref="ThreadClock.Ticks"/> makes.</summary>
    internal static long TicksNs() => OperatingSystem.IsWindows() && ReadTicks(out var ns) ? ns : -1;

    /// <summary>The step Windows charges a thread's kernel and user time in (ns): the system's clock increment, fixed at start.</summary>
    internal static long WindowsTickNs() =>
        GetSystemTimeAdjustment(out _, out var increment, out _) && increment > 0 ? increment * 100L : DefaultTimeIncrement * 100L;

    private sealed record Choice(ThreadClock Clock, long ResolutionNs, double CycleNs, string Basis);

    private static Choice Choose()
    {
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            if (!ReadCpuClock(out _))
                return StopwatchChoice("the thread's CPU clock could not be read");
            var resolution = CpuClockResolutionNs();
            return new Choice(ThreadClock.CpuClock, resolution, 0,
                string.Create(CultureInfo.InvariantCulture, $"the thread's CPU clock, in steps of {resolution} ns"));
        }
        return OperatingSystem.IsWindows() ? ChooseOnWindows() : StopwatchChoice("no thread clock is read on this platform");
    }

    private static Choice ChooseOnWindows()
    {
        var why = WhyCyclesAreNotConverted();
        if (why.Length == 0)
        {
            var cycleNs = MeasureCycleNs();
            if (cycleNs > 0)
                return new Choice(ThreadClock.Cycles, 1, cycleNs, string.Create(CultureInfo.InvariantCulture,
                    $"the thread's cycles of the invariant time-stamp counter, at {cycleNs:0.00000} ns a cycle measured against the stopwatch"));
            why = "the thread's cycles could not be measured against the stopwatch";
        }
        if (!ReadTicks(out _))
            return StopwatchChoice("the thread's times could not be read: " + why);
        var tick = WindowsTickNs();
        return new Choice(ThreadClock.Ticks, tick, 0, string.Create(CultureInfo.InvariantCulture,
            $"the thread's kernel and user time, which Windows charges in {tick / 1e6:0.###} ms ticks: {why}"));
    }

    private static Choice StopwatchChoice(string why) =>
        new(ThreadClock.Stopwatch, Math.Max(1, (long)Math.Ceiling(1e9 / Stopwatch.Frequency)), 0,
            "the stopwatch, which counts other threads' turns too: " + why);

    /// <summary>"" when the thread's cycles are the invariant time-stamp counter's ticks, else why they are not converted.</summary>
    private static string WhyCyclesAreNotConverted()
    {
        if (!KernelRunsX64())
            return "the kernel is not x64, and an ARM64 kernel counts a thread's cycles of the core's own clock, whose rate moves with the core's speed";
        if (!X86Base.IsSupported)
            return "the processor's identification cannot be read from this process";
        var top = (uint)X86Base.CpuId(unchecked((int)0x8000_0000), 0).Eax;
        if (top < 0x8000_0007 || (X86Base.CpuId(unchecked((int)0x8000_0007), 0).Edx & InvariantTscBit) == 0)
            return "the processor does not say its time-stamp counter is invariant (CPUID 80000007h EDX bit 8), so the counter may change rate with the processor's speed";
        return "";
    }

    /// <summary>Whether the Windows kernel is x64, the kernel that counts a thread's cycles from the time-stamp counter. The process's
    /// own view cannot say: an x64 process emulated on ARM64 Windows is told the machine is x64; the kernel's machine types say which
    /// it runs.</summary>
    private static bool KernelRunsX64()
    {
        if (RuntimeInformation.OSArchitecture != Architecture.X64)
            return false;
        try
        {
            // A query that fails says nothing against the machine the system reports.
            return GetMachineTypeAttributes(MachineAmd64, out var attributes) != 0 || (attributes & MachineKernelEnabled) != 0;
        }
        catch (EntryPointNotFoundException)
        {
            // Windows before 11 has no machine-type query, and no x64 emulation on ARM64 to see through.
            return true;
        }
    }

    /// <summary>Nanoseconds a cycle of the calling thread, from the highest rate any of 16 spins of about a millisecond counts: its
    /// cycles over the stopwatch's span around it. Each spin's two cycle reads lie inside its two stopwatch reads, and the span is taken one stopwatch
    /// tick longer than read (a read is the tick it falls in), so no spin counts more cycles than the counter's rate gives its span; a
    /// preemption or an interrupt in a spin only lowers what it counts. The rate taken is therefore never above the counter's, and a
    /// span converted at it reads at least the work done in it: a measuring spin the machine interrupted makes reads long, never
    /// short. The spins run at the highest thread priority, so the machine's ordinary work rarely takes their processor. 0 when the
    /// cycles cannot be read or the rate is not an x64 counter's.</summary>
    private static double MeasureCycleNs()
    {
        var thread = Thread.CurrentThread;
        var priority = thread.Priority;
        thread.Priority = ThreadPriority.Highest;
        try
        {
            var spin = Math.Max(1, Stopwatch.Frequency / 1000);
            var tickNs = 1e9 / Stopwatch.Frequency;
            var best = 0.0;
            for (var i = 0; i < RateSpins; i++)
            {
                var t0 = Stopwatch.GetTimestamp();
                if (!QueryThreadCycleTime(CurrentThread, out var c0))
                    return 0;
                while (Stopwatch.GetTimestamp() - t0 < spin)
                {
                    // Spinning: the thread runs the whole span unless the machine takes it away.
                }
                if (!QueryThreadCycleTime(CurrentThread, out var c1))
                    return 0;
                var t1 = Stopwatch.GetTimestamp();
                best = Math.Max(best, (c1 - c0) / ((t1 - t0 + 1) * tickNs));
            }
            return best is >= MinCyclesPerNs and <= MaxCyclesPerNs ? 1 / best : 0;
        }
        finally
        {
            thread.Priority = priority;
        }
    }

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

    [LibraryImport("kernel32", EntryPoint = "QueryThreadCycleTime")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryThreadCycleTime(nint thread, out ulong cycles);

    [LibraryImport("kernel32", EntryPoint = "GetThreadTimes")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetThreadTimes(nint thread, out long creation, out long exit, out long kernel, out long user);

    [LibraryImport("kernel32", EntryPoint = "GetSystemTimeAdjustment")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetSystemTimeAdjustment(out uint adjustment, out uint increment, [MarshalAs(UnmanagedType.Bool)] out bool disabled);

    [LibraryImport("kernel32", EntryPoint = "GetMachineTypeAttributes")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int GetMachineTypeAttributes(ushort machine, out int attributes);
}
