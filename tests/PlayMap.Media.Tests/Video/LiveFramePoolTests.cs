using PlayMap.Core.Media;
using PlayMap.Media.Video;

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
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (byte i = 0; i < 200; i++)
        {
            Assert.True(pool.TryBeginWrite(PixelLayout.Uyvy, 64, 8, out _));
            pool.Publish(i, i);
            var lease = pool.Acquire()!;
            _ = lease.Generation;
            lease.Dispose();
        }
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
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
