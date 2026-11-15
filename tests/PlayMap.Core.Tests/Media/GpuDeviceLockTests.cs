using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using PlayMap.Core.Media;
using PlayMap.Core.Time;
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

    /// <summary>Another thread takes the lock as a sharer does (<see cref="GpuDeviceLock.Enter"/>, no bound) and lets go at once. It is a
    /// thread of its own, never the pool's, so it starts however busy the pool is; what it threw reaches the test's thread at
    /// <see cref="Join"/>.</summary>
    private sealed class Sharer
    {
        private readonly Thread _thread;
        private volatile bool _took;
        private ExceptionDispatchInfo? _fault;

        public Sharer(GpuDeviceLock deviceLock)
        {
            _thread = new Thread(() =>
            {
                try
                {
                    deviceLock.Enter();
                    _took = true;
                    deviceLock.Exit();
                }
                catch (Exception e)
                {
                    Volatile.Write(ref _fault, ExceptionDispatchInfo.Capture(e));
                }
            })
            { IsBackground = true, Name = "device lock unbounded sharer" };
            _thread.Start();
        }

        /// <summary>The sharer has held the lock.</summary>
        public bool Took => _took;

        /// <summary>The sharer threw.</summary>
        public bool Faulted => Volatile.Read(ref _fault) is not null;

        /// <summary>Throws on the test's thread what the sharer threw, if it threw.</summary>
        public void ThrowIfFaulted() => Volatile.Read(ref _fault)?.Throw();

        /// <summary>Waits for the sharer to finish, within the test's patience, and throws on the test's thread what it threw.</summary>
        public void Join()
        {
            Assert.True(_thread.Join(Patience), "the sharer did not finish");
            ThrowIfFaulted();
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
    public void ASharerWaitsForTheHolderAndABoundedTakeGivesUpWithinItsBound()
    {
        var l = new GpuDeviceLock();
        using var holder = new Holder(l);
        Assert.Equal(0, l.LastTimedOutWaitNs);
        var watch = Stopwatch.StartNew();
        var from = MonoClock.Instance.NowNs;
        Assert.False(l.TryEnter(20));
        var span = MonoClock.Instance.NowNs - from;
        var waited = watch.Elapsed;
        Assert.True(waited >= TimeSpan.FromMilliseconds(10) && waited < TimeSpan.FromSeconds(2), $"the bounded take waited {waited.TotalMilliseconds:0.#} ms for a 20 ms bound");
        Assert.False(l.IsHeldByCurrentThread);
        Assert.Equal(1, l.TimedOut);
        // The take that gave up says how long it waited, read on its own thread: its bound at least in part, and never more than the
        // span around its call on the same clock.
        Assert.InRange(l.LastTimedOutWaitNs, 10_000_000, span);

        // A sharer's take has no bound: it waits, counted, while the holder holds, and gets the lock only once the holder lets go. The
        // holder lets go only once the sharer's own wait is counted (the bounded take above counted one already), so the sharer is
        // seen to wait however late its thread runs; it is a thread of its own, so its start waits on no pool worker.
        var waits = l.Waits;
        var sharer = new Sharer(l);
        _ = SpinWait.SpinUntil(() => l.Waits > waits || sharer.Faulted, Patience);
        sharer.ThrowIfFaulted();
        Assert.True(l.Waits > waits, $"the sharer never waited for the holder (waits {l.Waits})");
        Assert.False(sharer.Took, "the sharer took the lock while the holder held it");
        holder.Release();
        sharer.Join();
        Assert.True(sharer.Took, "the sharer never took the lock once the holder let go");
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
