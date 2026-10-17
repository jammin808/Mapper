using PlayMap.Core.Media;
using PlayMap.Media.Video;
using PlayMap.Tests;

namespace PlayMap.Media.Tests.Video;

/// <summary>The live frame pool: a frame a reader holds is never written; when every slot is held the writer drops and counts;
/// leases are counted and pooled; neither side allocates per frame once warm; generations never repeat across pools.</summary>
public sealed unsafe class LiveFramePoolTests
{
    private static void Write(LiveFramePool pool, byte value, int width = 64, int height = 8, PixelLayout layout = PixelLayout.Uyvy)
    {
        Assert.True(pool.TryBeginWrite(layout, width, height, out var planes));
        foreach (var p in planes)
            new Span<byte>((void*)p.Data, p.Stride * p.Height).Fill(value);
        pool.Publish(value, value);
    }

    private static byte First(VideoFrameLease lease) => *(byte*)lease.Planes[0].Data;

    [Fact]
    public void AWriterAndReadersOnTheirOwnThreadsNeverSeeAFrameChangeUnderALease()
    {
        // The writer fills each frame with one byte value and stamps it; three readers, all running before the writer starts,
        // acquire, hold the lease while the writer publishes two more frames, and check every byte is still the one stamped and that
        // stamps never go backwards. Fixed counts and a start barrier, no sleeps. The writer publishes at least its count of frames and
        // goes on until the readers have held three leases across two frames each, so the property is exercised whatever the
        // scheduler does with the readers' threads; a bound stops it, and the assertion says so, if they never run.
        // Eight slots: three readers each holding one and the newest still leave the writer free ones, so it never stalls.
        using var pool = new LiveFramePool(8);
        const int frames = 20_000;
        var failures = 0;
        var acquired = 0;
        var heldAcross = 0;
        var done = 0;
        using var ready = new CountdownEvent(3);
        static bool Same(VideoFrameLease lease, byte value)
        {
            var span = new ReadOnlySpan<byte>((void*)lease.Planes[0].Data, lease.Planes[0].Stride * lease.Planes[0].Height);
            return !span.ContainsAnyExcept(value);
        }
        var readers = Enumerable.Range(0, 3).Select(_ => new Thread(() =>
        {
            ready.Signal();
            long last = 0;
            while (Volatile.Read(ref done) == 0)
            {
                using var lease = pool.Acquire();
                if (lease is null)
                    continue;
                Interlocked.Increment(ref acquired);
                var value = (byte)(lease.SenderTimestampNs % 251 + 1);
                if (!Same(lease, value) || lease.SenderTimestampNs < last)
                    Interlocked.Increment(ref failures);
                last = lease.SenderTimestampNs;
                // Held while the writer moves on two frames: still the same frame.
                var published = pool.Published;
                var spin = new SpinWait();
                while (pool.Published < published + 2 && Volatile.Read(ref done) == 0)
                    spin.SpinOnce(-1);
                if (pool.Published >= published + 2)
                    Interlocked.Increment(ref heldAcross);
                if (!Same(lease, value))
                    Interlocked.Increment(ref failures);
            }
        })).ToList();
        readers.ForEach(r => r.Start());
        ready.Wait();
        for (var k = 1; k <= frames || (Volatile.Read(ref heldAcross) < 3 && k <= 50 * frames); k++)
        {
            if (!pool.TryBeginWrite(PixelLayout.Uyvy, 64, 8, out var planes))
                continue;
            var value = (byte)(k % 251 + 1);
            foreach (var p in planes)
                new Span<byte>((void*)p.Data, p.Stride * p.Height).Fill(value);
            pool.Publish(k, k);
        }
        Volatile.Write(ref done, 1);
        readers.ForEach(r => r.Join());
        Assert.Equal(0, failures);
        Assert.True(acquired > 0, "no reader acquired while the writer ran");
        Assert.True(heldAcross > 0, "no lease was held across the writer's frames");
        Assert.Equal(0, pool.Held);
    }

    [Fact]
    public void AReaderGetsTheNewestFrameAndAHeldFrameIsNeverOverwritten()
    {
        using var pool = new LiveFramePool(3);
        Assert.Null(pool.Acquire());
        Write(pool, 1);
        using var held = pool.Acquire()!;
        Assert.Equal(1, First(held));
        // Two more frames: with three slots, the held one is skipped by the writer.
        Write(pool, 2);
        Write(pool, 3);
        Assert.Equal(1, First(held));
        using (var newest = pool.Acquire()!)
            Assert.Equal(3, First(newest));
        Assert.Equal(1, pool.Held);
        Assert.Equal(0, pool.Dropped);
    }

    [Fact]
    public void WhenEverySlotIsHeldTheWriterDropsAndCountsInsteadOfWaiting()
    {
        using var pool = new LiveFramePool(3);
        var held = new List<VideoFrameLease>();
        for (byte i = 1; i <= 3; i++)
        {
            Write(pool, i);
            held.Add(pool.Acquire()!);
        }
        Assert.False(pool.TryBeginWrite(PixelLayout.Uyvy, 64, 8, out _));
        Assert.Equal(1, pool.Dropped);
        Assert.Equal([1, 2, 3], held.Select(First));
        held[0].Dispose();
        Write(pool, 4);
        using (var newest = pool.Acquire()!)
            Assert.Equal(4, First(newest));
        foreach (var l in held.Skip(1))
            l.Dispose();
        Assert.Equal(0, pool.Held);
    }

    [Fact]
    public void PlanesFollowTheLayoutTightRowsAndStampsRide()
    {
        using var pool = new LiveFramePool();
        Assert.True(pool.TryBeginWrite(PixelLayout.Pa16, 1920, 1080, out var planes));
        Assert.Equal(3, planes.Length);
        Assert.All(planes.ToArray(), p => Assert.Equal(3840, p.Stride));
        Assert.Equal(0, planes[0].Data % LiveFramePool.Alignment);
        pool.Publish(123_400, 999, ColorMatrix.Bt2020, ColorRange.Limited, TransferFunction.Pq);
        using var lease = pool.Acquire()!;
        Assert.Equal(PixelLayout.Pa16, lease.Layout);
        Assert.Equal(123_400, lease.SenderTimestampNs);
        Assert.Equal(999, lease.ArrivalNs);
        Assert.Equal(TransferFunction.Pq, lease.Transfer);
        Assert.True(lease.HasAlpha);
    }

    [Fact]
    public void GenerationsNeverRepeatAcrossPools()
    {
        using var a = new LiveFramePool();
        using var b = new LiveFramePool();
        Write(a, 1);
        Write(b, 1);
        using var la = a.Acquire()!;
        using var lb = b.Acquire()!;
        Assert.NotEqual(la.Generation, lb.Generation);
    }

    [Fact]
    public void NeitherSideAllocatesPerFrameOnceWarm()
    {
        using var pool = new LiveFramePool(4);
        for (byte i = 0; i < 20; i++)
        {
            Write(pool, i);
            pool.Acquire()!.Dispose();
        }
        var before = Allocations.Start();
        for (byte i = 0; i < 200; i++)
        {
            Assert.True(pool.TryBeginWrite(PixelLayout.Uyvy, 64, 8, out _));
            pool.Publish(i, i);
            var lease = pool.Acquire()!;
            _ = lease.Generation;
            lease.Dispose();
        }
        Assert.Equal(0, Allocations.Since(before));
    }

    [Fact]
    public void ALeaseHeldPastThePoolsDisposalStillReadsAndThenFrees()
    {
        var pool = new LiveFramePool();
        Write(pool, 7);
        var lease = pool.Acquire()!;
        pool.Dispose();
        Assert.Equal(7, First(lease));
        lease.Dispose();
        Assert.Null(pool.Acquire());
        Assert.False(pool.TryBeginWrite(PixelLayout.Uyvy, 64, 8, out _));
    }
}
