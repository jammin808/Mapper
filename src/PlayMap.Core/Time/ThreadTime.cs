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
/// counter's rate, measured once against the stopwatch when the type is first touched (<see cref="Warm"/>), where the measuring's
/// spins say the count is brought up to date at each read (it moves on at least half the reads of every spin that read it
/// <see cref="MinRatePasses"/> times or more). Both are <see cref="Precise"/>. Other Windows reads the thread's kernel and user
/// time, which Windows charges in whole clock ticks: an ARM64 kernel counts a thread's cycles of the core's own clock, whose rate
/// moves with the core's speed, a counter the processor does not say is invariant may change rate with it, and a count the spins do
/// not agree on is not converted. Elsewhere the reads are the stopwatch's. What a read converts with is fixed before the first read
/// returns on any thread, so every read is thread-safe and no span two reads take holds the measuring.</summary>
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

    /// <summary>The share of an agreeing spin's rate in the fastest spin's (within 1% of it); a quarter of the spins must agree.</summary>
    private const double RateAgreement = 0.99;

    /// <summary>The fewest passes a spin of the measuring reads the cycle count on for it to be judged. A spin of a millisecond makes
    /// thousands where a read costs a fraction of a microsecond (about 5000 on a Hyper-V guest); one of fewer was stopped for most of
    /// its millisecond, or its reads are slow. A count brought up to date at an interval moves on half a spin's passes only where the
    /// interval is under two passes' span, and the fastest spin it gives then counts at most 2/(passes − 2) above the counter's: 0.2%
    /// at this floor.</summary>
    internal const int MinRatePasses = 1000;

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

    /// <summary>The step a read moves in (ns), at most: a read tells nothing finer. A span read from the cycles
    /// (<see cref="ThreadClock.Cycles"/>) also reads long by the measured rate's shortfall from the counter's; it reads short only
    /// where the count is brought up to date at an interval the measuring cannot tell from each read, and then by at most the share
    /// the rate can be above the counter's (<see cref="Basis"/> states it) and that interval, under two of the measuring's
    /// passes.</summary>
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
            var rate = MeasureCycleRate();
            if (rate.CyclesPerNs > 0)
            {
                var cycleNs = 1 / rate.CyclesPerNs;
                // A read is a whole nanosecond count of whole cycles: it moves in a cycle's span rounded up, and in 1 ns at least.
                var step = Math.Max(1, (long)Math.Ceiling(cycleNs));
                return new Choice(ThreadClock.Cycles, step, cycleNs, string.Create(CultureInfo.InvariantCulture,
                    $"the thread's cycles of the invariant time-stamp counter, in steps of {step} ns, at {cycleNs:0.00000} ns a cycle measured against the stopwatch ({rate.Agreeing} of {RateSpins} spins within 1% of the fastest; in the {rate.Judged} that read the count {MinRatePasses} times or more, it moved on {rate.MovingShare * 100:0.##}% of a spin's reads at least, over {rate.FewestPasses} passes at least, so the rate is at most {rate.MostAbove * 100:0.###}% above the counter's)"));
            }
            why = rate.Why;
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

    /// <summary>Whether the Windows kernel is x64, the kernel that counts a thread's cycles from the time-stamp counter. The runtime
    /// reports the native machine, an emulating process's included; the kernel's machine types confirm the kernel runs x64 code.</summary>
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

    /// <summary>The calling thread's cycle rate, from <see cref="RateSpins"/> spins of about a millisecond at the highest thread
    /// priority, so the machine's ordinary work rarely takes their processor (<see cref="RateFrom"/>). The count is read once before
    /// the spins, so binding the call is no spin's. A spin reads the stopwatch and the cycle count, then on every pass the stopwatch
    /// and, until it has moved a millisecond's ticks, the count: its cycle reads lie inside its first and last stopwatch reads.</summary>
    private static CycleRate MeasureCycleRate()
    {
        var thread = Thread.CurrentThread;
        var priority = thread.Priority;
        thread.Priority = ThreadPriority.Highest;
        try
        {
            var spin = Math.Max(1, Stopwatch.Frequency / 1000);
            if (!QueryThreadCycleTime(CurrentThread, out _))
                return Unread;
            var spins = new RateSpin[RateSpins];
            for (var i = 0; i < spins.Length; i++)
            {
                var t0 = Stopwatch.GetTimestamp();
                if (!QueryThreadCycleTime(CurrentThread, out var c0))
                    return Unread;
                int passes = 0, moved = 0;
                var last = c0;
                var t1 = Stopwatch.GetTimestamp();
                while (t1 - t0 < spin)
                {
                    // Spinning: the thread runs the whole span unless the machine takes it away.
                    if (!QueryThreadCycleTime(CurrentThread, out var c))
                        return Unread;
                    passes++;
                    if (c > last)
                        moved++;
                    last = c;
                    t1 = Stopwatch.GetTimestamp();
                }
                spins[i] = new RateSpin(last > c0 ? last - c0 : 0, passes, moved, t1 - t0);
            }
            return RateFrom(spins, 1e9 / Stopwatch.Frequency);
        }
        finally
        {
            thread.Priority = priority;
        }
    }

    private static CycleRate Unread => new(0, 0, 0, 0, 0, "the thread's cycles could not be read");

    /// <summary>One spin of the cycle rate's measuring: the cycles the thread counted in it (its last cycle read less its first, 0
    /// where the count did not move forward), the passes it read the count on and how many of them found the count moved on from the
    /// read before, and the stopwatch ticks between its first stopwatch read and its last.</summary>
    internal readonly record struct RateSpin(ulong Cycles, int Passes, int Moved, long Ticks);

    /// <summary>A measured cycle rate (cycles a nanosecond), how many spins agreed on it, how many were judged (read the count on
    /// <see cref="MinRatePasses"/> passes or more), the least share of a judged spin's passes the count moved on, the fewest passes a
    /// judged spin read it on (each 0 where none was judged), and, where the rate does not stand (0), why.</summary>
    internal readonly record struct CycleRate(double CyclesPerNs, int Agreeing, int Judged, double MovingShare, int FewestPasses, string Why)
    {
        /// <summary>How far above the counter's the rate can be, as a share of it: 2/(P − 2) for the fewest passes P a judged spin
        /// made, where the count is brought up to date at an interval its share of moving reads does not tell from each read (none
        /// where it is brought up to date at each read).</summary>
        public double MostAbove => FewestPasses > 2 ? 2.0 / (FewestPasses - 2) : double.PositiveInfinity;
    }

    /// <summary>The counter's rate from spins timed in stopwatch ticks of <paramref name="tickNs"/>: the fastest judged spin's, its
    /// cycles over its span taken one tick longer than read (a read is the tick it falls in). No spin whose cycle reads lie inside its
    /// stopwatch reads counts more cycles than the counter's rate gives that span, and a preemption, an interrupt or a stop in a spin
    /// only lowers what it counts, so the rate is never above the counter's and a span converted at it reads at least the work done in
    /// it. That holds only for a count brought up to date at each read: one brought up to date at a clock tick, a switch or any
    /// interval lags the thread by an amount that varies, and a spin's difference of two such reads can count more than the counter
    /// gave the spin. A spin is judged where it read the count on <see cref="MinRatePasses"/> passes or more; one of fewer was stopped
    /// for most of its millisecond (or its reads are slow), says nothing of the count, and neither sets nor agrees with the rate. The
    /// rate stands only where every judged spin found the count moved on at least half of its passes. Half lies far from both counts:
    /// one brought up to date at each read moves on nearly every pass (96 to 100% of reads straight after each other on a Hyper-V
    /// guest, where now and then a read finds the count where the read before left it, and such an equal pair is a read, not a stale
    /// count), and one brought up to date at an interval moves at most once an interval, on well under 1% of passes a fraction of a
    /// microsecond apart where the interval is 0.3 ms and almost never at the 15.625 ms tick. A count brought up to date at an
    /// interval moves on half a spin's passes only where the interval is under two passes' span; a spin's reads then lag the thread by
    /// less than the interval, and one that moved on M passes counts at most 1/(M − 1) above the counter's, so the rate is at most
    /// <see cref="CycleRate.MostAbove"/> above it. The rate stands, too, only where a quarter of the spins are judged and count within
    /// 1% of the fastest (a stop or an interrupt only lowers one spin or leaves it unjudged; spins that scatter say the rate is not
    /// established), and where it is an x64 counter's rate.</summary>
    internal static CycleRate RateFrom(ReadOnlySpan<RateSpin> spins, double tickNs)
    {
        if (spins.IsEmpty)
            return new CycleRate(0, 0, 0, 0, 0, "no spin measured the thread's cycle rate");
        int judged = 0, fewest = 0;
        var least = default(RateSpin);
        foreach (var s in spins)
        {
            if (s.Passes < MinRatePasses)
                continue;
            if (judged == 0 || s.Passes < fewest)
                fewest = s.Passes;
            if (judged == 0 || ShareOf(s) < ShareOf(least))
                least = s;
            judged++;
        }
        var share = judged > 0 ? ShareOf(least) : 0;
        if (judged > 0 && least.Moved * 2L < least.Passes)
            return new CycleRate(0, 0, judged, share, fewest, string.Create(CultureInfo.InvariantCulture,
                $"the thread's cycle count moved on {least.Moved} of a spin's {least.Passes} reads ({share * 100:0.##}%), not half, so it is not brought up to date at each read"));
        if (judged * 4 < spins.Length)
            return new CycleRate(0, 0, judged, share, fewest, string.Create(CultureInfo.InvariantCulture,
                $"only {judged} of {spins.Length} spins measuring the thread's cycle rate read the count on {MinRatePasses} passes or more, the fewest a spin is judged on, so the rate is not established"));
        var best = 0.0;
        foreach (var s in spins)
        {
            if (s.Passes >= MinRatePasses)
                best = Math.Max(best, RateOf(s, tickNs));
        }
        var agreeing = 0;
        foreach (var s in spins)
        {
            if (s.Passes >= MinRatePasses && RateOf(s, tickNs) >= best * RateAgreement)
                agreeing++;
        }
        if (agreeing * 4 < spins.Length)
            return new CycleRate(0, agreeing, judged, share, fewest, string.Create(CultureInfo.InvariantCulture,
                $"only {agreeing} of {spins.Length} spins measuring the thread's cycle rate counted within 1% of the fastest, so the rate is not established"));
        if (best is < MinCyclesPerNs or > MaxCyclesPerNs)
            return new CycleRate(0, agreeing, judged, share, fewest, string.Create(CultureInfo.InvariantCulture,
                $"the measured cycle rate, {best:0.###} a nanosecond, is not an x64 time-stamp counter's"));
        return new CycleRate(best, agreeing, judged, share, fewest, "");
    }

    /// <summary>The share of a spin's passes the count moved on (0 where it read the count on none).</summary>
    private static double ShareOf(RateSpin s) => s.Passes > 0 ? (double)s.Moved / s.Passes : 0;

    private static double RateOf(RateSpin s, double tickNs) => s.Cycles / ((s.Ticks + 1) * tickNs);

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
