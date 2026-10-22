using System.Diagnostics;
using System.Runtime.CompilerServices;
using PlayMap.Core.Media;
using PlayMap.Tests;

namespace PlayMap.Core.Tests.Media;

/// <summary>The renderer's device lock, as a shared device's sharers and the render thread use it: recursive on one thread and
/// exclusive across threads; a sharer waits for the holder; a bounded take gives up within its bound holding nothing, and counts it;
/// the render thread lets go of every hold for a wait and takes back all of them or none; nothing allocates once warm.</summary>
public sealed class GpuDeviceLockTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    /// <summary>Another thread takes the lock and keeps it until <see cref="Release"/>.</summary>
    private sealed class Holder : IDisposable
    {
        private readonly ManualResetEventSlim _held = new();
        private readonly ManualResetEventSlim _release = new();
        private readonly Thread _thread;

        public Holder(GpuDeviceLock deviceLock)
        {
            _thread = new Thread(() =>
            {
                deviceLock.Enter();
                _held.Set();
                _release.Wait();
                deviceLock.Exit();
            })
            { IsBackground = true, Name = "device lock holder" };
            _thread.Start();
            _held.Wait();
        }

        public void Release()
        {
            _release.Set();
            _thread.Join();
        }

        public void Dispose()
        {
            Release();
            _held.Dispose();
            _release.Dispose();
        }
    }

    /// <summary>Runs <paramref name="body"/> on a thread of its own and answers its result (the lock is the thread's, so a test that
    /// holds it never awaits: it would come back on another thread).</summary>
    private static bool OnOtherThread(Func<bool> body)
    {
        var result = false;
        var thread = new Thread(() => result = body()) { IsBackground = true, Name = "device lock sharer" };
        thread.Start();
        Assert.True(thread.Join(Patience), "the other thread did not finish");
        return result;
    }

    [Fact]
    public void ItIsRecursiveOnOneThreadAndExclusiveAcrossThreads()
    {
        var l = new GpuDeviceLock();
        l.Enter();
        Assert.True(l.TryEnter(0));
        Assert.True(l.IsHeldByCurrentThread);
        Assert.False(OnOtherThread(() => l.TryEnter(0)));
        Assert.Equal(1, l.TimedOut);
        l.Exit();
        Assert.True(l.IsHeldByCurrentThread);
        l.Exit();
        Assert.False(l.IsHeldByCurrentThread);
        Assert.Throws<SynchronizationLockException>(l.Exit);
        Assert.True(OnOtherThread(() =>
        {
            var took = l.TryEnter(0);
            if (took)
                l.Exit();
            return took;
        }));
    }

    [Fact]
    public async Task ASharerWaitsForTheHolderAndABoundedTakeGivesUpWithinItsBound()
    {
        var l = new GpuDeviceLock();
        using var holder = new Holder(l);
        var watch = Stopwatch.StartNew();
        Assert.False(l.TryEnter(20));
        var waited = watch.Elapsed;
        Assert.True(waited >= TimeSpan.FromMilliseconds(10) && waited < TimeSpan.FromSeconds(2), $"the bounded take waited {waited.TotalMilliseconds:0.#} ms for a 20 ms bound");
        Assert.False(l.IsHeldByCurrentThread);
        Assert.Equal(1, l.TimedOut);

        // A sharer's take has no bound: it waits, counted, while the holder holds, and gets the lock only once the holder lets go. The
        // holder lets go only once the sharer's own wait is counted (the bounded take above counted one already), so the sharer is
        // seen to wait however late its thread runs.
        var waits = l.Waits;
        var sharer = Task.Run(() =>
        {
            l.Enter();
            l.Exit();
        });
        Assert.True(SpinWait.SpinUntil(() => l.Waits > waits, Patience), $"the sharer never waited for the holder (waits {l.Waits})");
        Assert.False(sharer.IsCompleted, "the sharer took the lock while the holder held it");
        holder.Release();
        await sharer.WaitAsync(Patience);
        Assert.Equal(waits + 1, l.Waits);
        Assert.Equal(1, l.TimedOut);
    }

    [Fact]
    public void TheRenderThreadLetsGoOfEveryHoldForAWaitAndTakesThemAllBack()
    {
        var l = new GpuDeviceLock();
        l.Enter();
        l.Enter();
        Assert.True(l.TryEnter(GpuDeviceLock.FrameBoundMs));
        var holds = l.ExitAll();
        Assert.Equal(3, holds);
        Assert.False(l.IsHeldByCurrentThread);

        // While the render thread waits on its display, a decoder gets the device.
        Assert.True(OnOtherThread(() =>
        {
            l.Enter();
            l.Exit();
            return true;
        }));

        Assert.True(l.Reenter(holds, GpuDeviceLock.FrameBoundMs));
        for (var i = 0; i < holds; i++)
            l.Exit();
        Assert.False(l.IsHeldByCurrentThread);
        Assert.Throws<SynchronizationLockException>(l.Exit);
        Assert.Equal(0, l.ExitAll());
        Assert.True(l.Reenter(0, 0));
        Assert.False(l.IsHeldByCurrentThread);
        Assert.Equal(0, l.TimedOut);
    }

    [Fact]
    public void ATakeBackPastTheBoundTakesNothing()
    {
        var l = new GpuDeviceLock();
        l.Enter();
        l.Enter();
        var holds = l.ExitAll();
        using (new Holder(l))
        {
            Assert.False(l.Reenter(holds, 10));
            Assert.False(l.IsHeldByCurrentThread);
            Assert.Equal(0, l.ExitAll());
        }
        Assert.Equal(1, l.TimedOut);
        Assert.True(l.Reenter(holds, GpuDeviceLock.FrameBoundMs));
        Assert.Equal(2, l.ExitAll());
    }

    [Fact]
    public void NothingAllocatesOnceWarmWhetherTheTakeWinsOrTimesOut()
    {
        var l = new GpuDeviceLock();
        Cycle(l, 10);
        using (new Holder(l))
            TimedOut(l, 2);
        Assert.Equal(0, Cycle(l, 1000));
        using (new Holder(l))
            Assert.Equal(0, TimedOut(l, 20));
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization | MethodImplOptions.NoInlining)]
    private static long Cycle(GpuDeviceLock l, int rounds)
    {
        var before = Allocations.Start();
        for (var i = 0; i < rounds; i++)
        {
            l.Enter();
            _ = l.TryEnter(GpuDeviceLock.FrameBoundMs);
            var holds = l.ExitAll();
            _ = l.Reenter(holds, GpuDeviceLock.FrameBoundMs);
            l.Exit();
            l.Exit();
        }
        return Allocations.Since(before);
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization | MethodImplOptions.NoInlining)]
    private static long TimedOut(GpuDeviceLock l, int rounds)
    {
        var before = Allocations.Start();
        for (var i = 0; i < rounds; i++)
            Assert.False(l.TryEnter(1));
        return Allocations.Since(before);
    }
}
