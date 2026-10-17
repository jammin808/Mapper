using System.Diagnostics;
using PlayMap.Core.Time;

namespace PlayMap.Core.Tests.Time;

/// <summary>A thread's time counts the work it did, not the time it was kept from running: a thread blocked while the test's thread
/// spins counts almost none of that span, and a thread that spins counts about the span it spun (never more). Where the thread's
/// processor time is counted in Windows' clock ticks, each read is allowed a tick of rounding.</summary>
public sealed class ThreadTimeTests
{
    [Fact]
    public void AThreadsTimeCountsItsWorkNotItsWait()
    {
        Assert.Equal(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS(), ThreadTime.Precise);
        Assert.Equal(ThreadTime.Precise || OperatingSystem.IsWindows(), ThreadTime.Counted);
        if (!ThreadTime.Counted)
            return;
        var tick = ThreadTime.Precise ? 0 : ThreadTime.WindowsTickNs;
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
        Assert.True(waitedCpu < waitedWall / 5 + tick, $"blocked {waitedWall / 1e6:0.0} ms of the stopwatch, counted {waitedCpu / 1e6:0.0} ms of its own");

        var t0 = ThreadTime.NowNs();
        var w0 = Stopwatch.GetTimestamp();
        var spun = 0L;
        // At least 20 ms, and until the thread's clock has moved (a tick on Windows), so the read is of something.
        while (Stopwatch.GetElapsedTime(w0) < TimeSpan.FromMilliseconds(20) || ThreadTime.NowNs() == t0)
            spun++;
        var cpuSpin = ThreadTime.NowNs() - t0;
        var wallSpin = Stopwatch.GetElapsedTime(w0).Ticks * 100;
        Assert.True(spun > 0);
        // Never more than the span (a tenth over for the reads' own rounding, and a tick where time is counted in ticks).
        Assert.True(cpuSpin > 0 && cpuSpin <= wallSpin * 11 / 10 + tick, $"spun {wallSpin / 1e6:0.0} ms, counted {cpuSpin / 1e6:0.0} ms");
    }
}
