using System.Diagnostics;
using PlayMap.Core.Time;

namespace PlayMap.Core.Tests.Time;

/// <summary>A thread's own time counts the work it did, not the time it waited: a thread blocked while the test's thread spins
/// counts almost none of that span, and a thread that spins counts about the span it spun (never more).</summary>
public sealed class ThreadTimeTests
{
    [Fact]
    public void AThreadsTimeCountsItsWorkNotItsWait()
    {
        Assert.True(ThreadTime.Precise);
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
        // Held until it is blocked, and then for at least 50 ms of the stopwatch.
        while ((blocked.ThreadState & System.Threading.ThreadState.WaitSleepJoin) == 0)
            Thread.Yield();
        var held = Stopwatch.GetTimestamp();
        while (Stopwatch.GetElapsedTime(held) < TimeSpan.FromMilliseconds(50))
            Thread.Yield();
        go.Set();
        blocked.Join();
        Assert.True(waitedWall >= 50_000_000, $"{waitedWall} ns");
        Assert.True(waitedCpu < waitedWall / 5, $"blocked {waitedWall / 1e6:0.0} ms of the stopwatch, counted {waitedCpu / 1e6:0.0} ms of its own");

        var t0 = ThreadTime.NowNs();
        var w0 = Stopwatch.GetTimestamp();
        var spun = 0L;
        while (Stopwatch.GetElapsedTime(w0) < TimeSpan.FromMilliseconds(20))
            spun++;
        var cpuSpin = ThreadTime.NowNs() - t0;
        var wallSpin = Stopwatch.GetElapsedTime(w0).Ticks * 100;
        Assert.True(spun > 0);
        // Never more than the span (a tenth over for the reads' own rounding), and something of it.
        Assert.True(cpuSpin > 0 && cpuSpin <= wallSpin * 11 / 10, $"spun {wallSpin / 1e6:0.0} ms, counted {cpuSpin / 1e6:0.0} ms");
    }
}
