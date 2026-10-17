using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace PlayMap.Core.Time;

/// <summary>The processor time the calling thread has run, in nanoseconds: the work a thread did between two reads, never the time
/// the machine gave other threads meanwhile (a preemption, a loaded machine). Linux and macOS read the thread's CPU clock; Windows
/// counts the thread's cycles and converts them at the processor's cycle rate, measured once against the stopwatch. Where none can
/// be read, <see cref="Precise"/> is false and the reads are the stopwatch's (wall time).</summary>
public static partial class ThreadTime
{
    private const int LinuxThreadCpu = 3;
    private const int MacThreadCpu = 16;

    private static readonly Lazy<double> CycleNs = new(MeasureCycleNs, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>True when <see cref="NowNs"/> counts the thread's own time on this platform.</summary>
    public static bool Precise { get; } = OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() || OperatingSystem.IsWindows();

    /// <summary>Measures what the first read would (Windows' cycle rate, a few milliseconds' spin) on the calling thread: called off any
    /// lane before the first read on one, so no lane spins for it.</summary>
    public static void Warm() => _ = NowNs();

    /// <summary>The calling thread's processor time now (ns); only differences of two reads on one thread mean anything.</summary>
    public static long NowNs()
    {
        if (OperatingSystem.IsWindows())
        {
            // The rate first: its one measuring spin is never inside a span two reads take.
            var ns = CycleNs.Value;
            return (long)(Cycles() * ns);
        }
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            var t = default(Timespec);
            if (ClockGetTime(OperatingSystem.IsLinux() ? LinuxThreadCpu : MacThreadCpu, ref t) == 0)
                return t.Seconds * 1_000_000_000L + t.Nanoseconds;
        }
        return (long)(Stopwatch.GetTimestamp() * (1e9 / Stopwatch.Frequency));
    }

    [SupportedOSPlatform("windows")]
    private static ulong Cycles() => QueryThreadCycleTime(-2, out var cycles) ? cycles : 0;

    /// <summary>Nanoseconds a cycle, Windows only: the cycles a spin on this thread counts against the stopwatch over the same span,
    /// the fastest of a few spins (a spin the machine interrupted counts fewer cycles for its span, never more).</summary>
    private static double MeasureCycleNs()
    {
        if (!OperatingSystem.IsWindows())
            return 0;
        var best = 0.0;
        for (var i = 0; i < 5; i++)
        {
            var c0 = Cycles();
            var t0 = Stopwatch.GetTimestamp();
            while (Stopwatch.GetElapsedTime(t0).Ticks < TimeSpan.TicksPerMillisecond * 2)
            {
                // Spinning: the thread runs the whole span unless the machine takes it away.
            }
            var cycles = Cycles() - c0;
            var ns = Stopwatch.GetElapsedTime(t0).Ticks * 100.0;
            best = Math.Max(best, cycles / ns);
        }
        return best > 0 ? 1 / best : 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private record struct Timespec(long Seconds, long Nanoseconds);

    [LibraryImport("libc", EntryPoint = "clock_gettime")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static partial int ClockGetTime(int clock, ref Timespec time);

    [LibraryImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryThreadCycleTime(nint thread, out ulong cycles);
}
