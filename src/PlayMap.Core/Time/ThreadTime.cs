using System.Diagnostics;
using System.Globalization;

namespace PlayMap.Core.Time;

/// <summary>How long the calling thread has held its place, in nanoseconds: the stopwatch's time less the time the thread waited
/// runnable for a processor (the scheduler gave the processor to other threads — a preemption on a loaded machine). Time the thread
/// spends blocked of its own accord (a lock, a read, a page fault) counts: it holds what it holds meanwhile. Linux reads the waits
/// from the thread's scheduler statistics (<see cref="Precise"/>); elsewhere none are read and the reads are the stopwatch's.</summary>
public static class ThreadTime
{
    /// <summary>True when the thread's run-queue waits are read (Linux with scheduler statistics): a preempted span counts only
    /// what the thread held.</summary>
    public static bool Precise { get; } = OperatingSystem.IsLinux() && RunQueueWaitNs() >= 0;

    /// <summary>The calling thread's held time now (ns); only differences of two reads on one thread mean anything.</summary>
    public static long NowNs()
    {
        var wall = (long)(Stopwatch.GetTimestamp() * (1e9 / Stopwatch.Frequency));
        if (!Precise)
            return wall;
        var waited = RunQueueWaitNs();
        return waited >= 0 ? wall - waited : wall;
    }

    /// <summary>The calling thread's total wait on the run queue (ns, the second field of /proc/thread-self/schedstat), or −1.</summary>
    private static long RunQueueWaitNs()
    {
        try
        {
            var fields = File.ReadAllText("/proc/thread-self/schedstat").Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return fields.Length >= 2 && long.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out var ns) ? ns : -1;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // No scheduler statistics here: the stopwatch alone.
            return -1;
        }
    }
}
