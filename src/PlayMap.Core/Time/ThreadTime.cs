using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PlayMap.Core.Time;

/// <summary>The work the calling thread has done, in nanoseconds of its own processor time: what it did between two reads, never the
/// time the machine gave other threads meanwhile (a preemption, a loaded machine) nor the time it was stopped (a collection, a page
/// brought in). Linux and macOS read the thread's CPU clock (<see cref="Precise"/>); elsewhere the reads are the stopwatch's.</summary>
public static partial class ThreadTime
{
    private const int LinuxThreadCpu = 3;
    private const int MacThreadCpu = 16;

    /// <summary>True when <see cref="NowNs"/> reads the thread's own processor time on this platform.</summary>
    public static bool Precise { get; } = (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()) && Read(out _);

    /// <summary>The calling thread's processor time now (ns); only differences of two reads on one thread mean anything.</summary>
    public static long NowNs() =>
        Precise && Read(out var ns) ? ns : (long)(Stopwatch.GetTimestamp() * (1e9 / Stopwatch.Frequency));

    private static bool Read(out long ns)
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

    /// <summary>struct timespec on a 64-bit Unix (the platforms the node runs on).</summary>
    [StructLayout(LayoutKind.Sequential)]
    private record struct Timespec(long Seconds, long Nanoseconds);

    [LibraryImport("libc", EntryPoint = "clock_gettime")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static partial int ClockGetTime(int clock, ref Timespec time);
}
