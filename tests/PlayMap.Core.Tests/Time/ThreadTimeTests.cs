using System.Diagnostics;
using System.Runtime.InteropServices;
using PlayMap.Core.Time;

namespace PlayMap.Core.Tests.Time;

/// <summary>A thread's held time is the stopwatch's less its waits for a processor: two threads made to share one processor (Linux,
/// each pinned to the same one) each spin for the same stretch of the stopwatch and together hold no more than that processor gave
/// them. Elsewhere the held time is the stopwatch's.</summary>
public sealed partial class ThreadTimeTests
{
    [Fact]
    public void TwoThreadsSharingOneProcessorHoldOnlyWhatItGaveThem()
    {
        Assert.Equal(OperatingSystem.IsLinux(), ThreadTime.Precise);
        if (!OperatingSystem.IsLinux())
            return;
        // The first processor this process may run on.
        ulong allowed = 0;
        Assert.Equal(0, SchedGetAffinity(0, sizeof(ulong), ref allowed));
        var one = allowed & (~allowed + 1);
        using var start = new Barrier(2);
        var held = new long[2];
        var wall = new long[2];
        var pinned = new int[2];
        var threads = Enumerable.Range(0, 2).Select(i => new Thread(() =>
        {
            var mask = one;
            pinned[i] = SchedSetAffinity(0, sizeof(ulong), ref mask);
            start.SignalAndWait();
            var h = ThreadTime.NowNs();
            var w = Stopwatch.GetTimestamp();
            while (Stopwatch.GetElapsedTime(w) < TimeSpan.FromMilliseconds(300))
            {
                // Spinning: runnable the whole span, so all it does not hold it waited for the shared processor.
            }
            held[i] = ThreadTime.NowNs() - h;
            wall[i] = Stopwatch.GetElapsedTime(w).Ticks * 100;
        }) { IsBackground = true }).ToList();
        threads.ForEach(t => t.Start());
        threads.ForEach(t => t.Join());
        Assert.Equal([0, 0], pinned);
        // Both spun 300 ms of the stopwatch at once on one processor: together they held about one stretch of it, not two.
        Assert.True(held.Sum() < wall.Sum() * 3 / 4, $"held {held[0] / 1e6:0.0} + {held[1] / 1e6:0.0} ms over {wall[0] / 1e6:0.0} + {wall[1] / 1e6:0.0} ms");
    }

    [LibraryImport("libc", EntryPoint = "sched_setaffinity", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static partial int SchedSetAffinity(int pid, nint size, ref ulong mask);

    [LibraryImport("libc", EntryPoint = "sched_getaffinity", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static partial int SchedGetAffinity(int pid, nint size, ref ulong mask);
}
