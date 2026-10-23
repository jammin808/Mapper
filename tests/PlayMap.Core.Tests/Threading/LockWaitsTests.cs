using PlayMap.Core.Content;
using PlayMap.Core.Led;
using PlayMap.Core.Machine;
using PlayMap.Core.Media;
using PlayMap.Core.Threading;
using PlayMap.Tests;

namespace PlayMap.Core.Tests.Threading;

/// <summary>A lock's first wait makes its wait event on the thread that waits, and a thread's first wait its wait record, at whatever
/// moment it comes — on a loaded machine any frame of a show, when a reader holding it is preempted. A prepared lock's first wait on a
/// thread with its record makes nothing; the locks the render, decode and LED threads share in Core are made prepared (the device
/// lock, the upload arena's ranges, the LED frame pool and the sting channel), and a thread that claims its role has its record from
/// its claim.</summary>
public sealed class LockWaitsTests
{
    [Fact]
    public void APreparedLocksFirstWaitAllocatesNothingWhereABareOnesDoes()
    {
        var bare = new Lock();
        var prepared = new Lock();
        LockWaits.Prepare(prepared);
        Assert.True(LockWaitProbe.FirstWait(bare) > 0, "a first wait on a bare lock makes its event");
        Assert.Equal(0, LockWaitProbe.FirstWait(prepared));
        Assert.Equal(0, LockWaitProbe.FirstWait(LockWaits.Prepared()));
    }

    [Fact]
    public void TheLocksTheRenderDecodeAndLedThreadsShareAreMadeWithTheirWaitEvents()
    {
        Assert.Empty(LockWaitProbe.Unprepared(new GpuDeviceLock()));
        Assert.Empty(LockWaitProbe.Unprepared(new ArenaAllocator(1, 64 * 1024)));
        Assert.Empty(LockWaitProbe.Unprepared(new LedFramePool()));
        Assert.Empty(LockWaitProbe.Unprepared(new EffectImpulseChannel()));
    }

    /// <summary>A thread that claims its role (the render, decode, sound, LED and control threads, each as its work starts) waits
    /// for the first time on a prepared lock another thread holds: the wait allocates nothing, the thread's record made with its
    /// claim.</summary>
    [Fact]
    public void AClaimedThreadsFirstWaitOnAPreparedLockAllocatesNothing()
    {
        var gate = LockWaits.Prepared();
        long allocated = -1;
        gate.Enter();
        try
        {
            var claimed = new Thread(() =>
            {
                using var claim = ThreadClaims.Claim(ThreadRole.Render);
                var before = Allocations.Start();
                if (gate.TryEnter(1))
                    gate.Exit();
                allocated = Allocations.Since(before);
            })
            { IsBackground = true, Name = "claimed" };
            claimed.Start();
            claimed.Join();
        }
        finally
        {
            gate.Exit();
        }
        Assert.Equal(0, allocated);
    }

    /// <summary>The device lock's bounded take that waits for a sharer, timing out or not, allocates nothing from its first.</summary>
    [Fact]
    public void TheDeviceLocksFirstBoundedTakeBehindASharerAllocatesNothing()
    {
        var deviceLock = new GpuDeviceLock();
        using var held = new ManualResetEventSlim();
        using var letGo = new ManualResetEventSlim();
        var sharer = new Thread(() =>
        {
            deviceLock.Enter();
            held.Set();
            letGo.Wait();
            deviceLock.Exit();
        })
        { IsBackground = true, Name = "sharer" };
        sharer.Start();
        Assert.True(held.Wait(TimeSpan.FromSeconds(10)));
        long allocated = -1;
        var took = true;
        var taker = new Thread(() =>
        {
            using (var once = new AutoResetEvent(false))
                once.WaitOne(0);
            var before = Allocations.Start();
            took = deviceLock.TryEnter(GpuDeviceLock.FrameBoundMs);
            allocated = Allocations.Since(before);
        })
        { IsBackground = true, Name = "frame" };
        try
        {
            taker.Start();
            taker.Join();
        }
        finally
        {
            letGo.Set();
            sharer.Join();
        }
        Assert.Equal((false, 0L, 1L), (took, allocated, deviceLock.TimedOut));
    }
}
