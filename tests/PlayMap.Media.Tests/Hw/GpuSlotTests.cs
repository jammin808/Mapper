using System.Runtime.InteropServices;
using PlayMap.Core.Media;
using PlayMap.Core.Media.Decode;
using PlayMap.Core.Model;
using PlayMap.Core.Time;
using PlayMap.Media.Ffmpeg;
using PlayMap.Media.Frames;
using PlayMap.Media.Hap;
using PlayMap.Media.Sources;
using PlayMap.Media.Tests.Hap;

namespace PlayMap.Media.Tests.Hw;

/// <summary>An upload arena over native memory, as the renderer's would be over its mapped buffer: returns go straight back.</summary>
internal sealed unsafe class NativeArena : IUploadArena, IDisposable
{
    private readonly ArenaAllocator _allocator;
    private nint _memory;

    public NativeArena(long bytes, int maxBlocks = 64)
    {
        _allocator = new ArenaAllocator(99, bytes, maxBlocks);
        _memory = (nint)NativeMemory.AlignedAlloc((nuint)_allocator.CapacityBytes, ArenaAllocator.Alignment);
    }

    public nint Base => _memory;
    public long Epoch => _allocator.Epoch;
    public long CapacityBytes => _allocator.CapacityBytes;
    public long FreeBytes => _allocator.FreeBytes;
    public int Outstanding => _allocator.Outstanding;
    public int Returns { get; private set; }

    public bool TryRent(long bytes, out UploadBlock block) => _allocator.TryRent(bytes, _memory, out block);

    public void Return(in UploadBlock block)
    {
        if (_allocator.Return(block))
            Returns++;
    }

    public void Dispose()
    {
        if (_memory != 0)
            NativeMemory.AlignedFree((void*)_memory);
        _memory = 0;
    }
}

/// <summary>The pooled slot of a GPU picture and of an arena-staged one: a decoder surface goes back to its decoder only when every
/// hold on it is gone (the lease's and a renderer's that recorded a read), never at the lease's end alone; a staged picture's planes
/// are inside its arena block and the block goes back with the slot. A HAP clip writes its frames into the arena, says so, and lets
/// go of the arena when told (its queued frames in it are made again elsewhere). An FFmpeg clip opened for a renderer whose device
/// cannot be used reports the path its frames actually come by, with the ladder's words.</summary>
public sealed class GpuSlotTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    [Fact]
    public void AGpuSlotStaysOutUntilTheRenderersHoldIsReleased()
    {
        using var pool = new FramePool(2);
        var layout = FrameLayout.For(PixelLayout.Nv12, 1920, 1080);
        var slot = pool.TryRentGpu(layout, out var surface);
        Assert.NotNull(slot);
        Assert.True(slot.IsGpu);
        Assert.Same(slot, surface.Owner);
        Assert.Empty(slot.Planes);
        surface.Retain();       // the renderer recorded a read of it
        slot.Release();         // the lease (and the queue) let go
        Assert.Equal(1, pool.InUse);
        surface.Release();      // the GPU passed the fence
        Assert.Equal(0, pool.InUse);
        Assert.False(slot.IsGpu);
    }

    [Fact]
    public void AGpuLeaseCarriesTheSurfaceAndNoPlanes()
    {
        var stats = new SourceStats();
        using var queue = new FrameQueue(new QueueSettings { QueueFrames = 4 }, stats, () => { });
        using var pool = new FramePool(6);
        var slot = pool.TryRentGpu(FrameLayout.For(PixelLayout.P010, 3840, 2160), out var surface)!;
        surface.Api = GpuApi.DmaBuf;
        surface.PlaneCount = 2;
        slot.PtsNs = 0;
        slot.DurationNs = 40_000_000;
        slot.Generation = 1;
        Assert.True(queue.Publish(slot));
        using (var lease = queue.Acquire(10_000_000))
        {
            Assert.NotNull(lease);
            Assert.Same(surface, lease.Surface);
            Assert.Empty(lease.Planes);
            Assert.Equal((PixelLayout.P010, 3840, 2160), (lease.Layout, lease.Width, lease.Height));
            Assert.True(lease.Staged.IsEmpty);
            surface.Retain();
        }
        queue.Close();
        Assert.Equal(1, pool.InUse);
        surface.Release();
        Assert.Equal(0, pool.InUse);
    }

    [Fact]
    public void AStagedSlotWritesIntoItsBlockAndReturnsIt()
    {
        using var arena = new NativeArena(1 << 20);
        using var pool = new FramePool(2);
        var layout = FrameLayout.For(PixelLayout.Bc3, 256, 128);
        var slot = pool.TryRentStaged(layout, arena);
        Assert.NotNull(slot);
        Assert.False(slot.Staged.IsEmpty);
        Assert.Equal(arena.Base + (nint)slot.Staged.Offset, slot.Planes[0].Data);
        Assert.True((long)layout.TotalBytes <= slot.Staged.Bytes);
        Assert.Equal(1, arena.Outstanding);
        slot.Release();
        Assert.Equal((0, 1), (arena.Outstanding, arena.Returns));
        // The same slot rented plainly afterwards has its own buffer again.
        var plain = pool.TryRent(layout)!;
        Assert.True(plain.Staged.IsEmpty);
        Assert.NotEqual(0, plain.Planes[0].Data);
        plain.Release();
    }

    [Fact]
    public void AFullArenaRentsNoStagedSlotAndLeavesThePoolWhole()
    {
        using var arena = new NativeArena(4096, maxBlocks: 1);
        using var pool = new FramePool(2);
        var layout = FrameLayout.For(PixelLayout.Bc1, 64, 64);
        var first = pool.TryRentStaged(layout, arena)!;
        Assert.Null(pool.TryRentStaged(layout, arena));
        Assert.Equal(1, pool.InUse);
        first.Release();
        Assert.Equal(0, pool.InUse);
    }

    [Fact]
    public async Task AHapClipWritesItsFramesIntoTheArenaAndLetsGoWhenTold()
    {
        using var arena = new NativeArena(64L << 20);
        using var source = new HapVideoSource(HapClips.Ffmpeg("hap_q", 4, "snappy"), new HapSourceOptions { Arena = arena });
        Assert.True(await source.Opened, source.Health.Status);
        var rate = new FrameRate(HapClips.Fps, 1);
        for (var n = 0; n < 10; n++)
        {
            var t = rate.TimeOf(n) + rate.FrameDurationNs / 2;
            source.Prepare(t, true, 1);
            Assert.True(source.WaitUntilReady(t, Wait), source.Health.Status);
            using var lease = source.Acquire(t)!;
            Assert.False(lease.Staged.IsEmpty);
            Assert.InRange(lease.Planes[0].Data, arena.Base, arena.Base + (nint)arena.CapacityBytes - 1);
            Assert.Equal(n, HapClips.ReadIndex(lease));
        }
        Assert.Equal("persistent", source.Health.Hap!.Staging);
        Assert.True(arena.Outstanding > 0);

        source.UseArena(null);
        var after = rate.TimeOf(11) + rate.FrameDurationNs / 2;
        source.Prepare(after, true, 1);
        Assert.True(source.WaitUntilReady(after, Wait), source.Health.Status);
        using (var lease = source.Acquire(after)!)
        {
            Assert.True(lease.Staged.IsEmpty, "a frame made after the arena went is in the source's own buffer");
            Assert.Equal(11, HapClips.ReadIndex(lease));
        }
        Assert.Equal(0, arena.Outstanding);
        Assert.Equal("copy", source.Health.Hap!.Staging);
    }

    // An mpeg4 clip (an LGPL-compatible encoder), made once by the tests' ffmpeg tool: two seconds of FFmpeg's synthetic pattern at 25 fps.
    private static readonly Lazy<string> Mpeg4 = new(() =>
    {
        var path = Path.Combine(TestMedia.Dir, "zerocopy-mpeg4.mp4");
        TestMedia.Run("-f", "lavfi", "-i", "testsrc2=s=320x240:r=25:d=2", "-c:v", "mpeg4", "-q:v", "4", path);
        Assert.True(File.Exists(path), "ffmpeg made no " + path);
        return path;
    });

    private static string Mpeg4Clip()
    {
        TestMedia.Ensure();
        return Mpeg4.Value;
    }

    // VA-API is reached through libva and a DRM render node, and its frames shared as dmabufs: Linux's only (the guard that asks
    // for libva is not asked elsewhere).
    [LinuxFact]
    [Trait("Category", "Linux")]
    public async Task AClipForARendererWhoseVaapiCannotBeUsedSaysThePathItsFramesComeBy()
    {
        TestMedia.Ensure();
        var share = new FakeShare(GpuApi.DmaBuf);
        var options = new VideoSourceOptions
        {
            Decoder = new DecoderOptions
            {
                Gpu = share,
                Ceiling = DecodePath.HwDirect,
                // This machine as CI has it: no libva at all.
                LibraryProbe = _ => LibraryFound.Missing,
                HardwareDevice = "vaapi",
            },
        };
        using var source = new FfmpegVideoSource(Mpeg4Clip(), options);
        Assert.True(await source.Opened, source.Health.Status);
        const long t = 1_000_000_000;
        source.Prepare(t, false, 1);
        Assert.True(source.WaitUntilReady(t, Wait), source.Health.Status);
        using var lease = source.Acquire(t)!;
        Assert.Null(lease.Surface);
        var h = source.Health;
        Assert.Equal(DecodePath.Software, h.Path);
        Assert.False(h.ZeroCopy);
        Assert.Equal("", h.Device);
        Assert.StartsWith("vaapi skipped: libva.so.2 is not on this machine", h.Why, StringComparison.Ordinal);
        Assert.Equal(1, h.Why.Split("vaapi skipped").Length - 1);
        Assert.True(h.BusBytesPerFrame > 0, "a software frame is uploaded once");
        Assert.Equal(0, h.Surfaces);
    }

    [Fact]
    public async Task APolicyOfSoftwareOpensNoDevice()
    {
        TestMedia.Ensure();
        using var source = new FfmpegVideoSource(Mpeg4Clip(), new VideoSourceOptions
        {
            Decoder = new DecoderOptions { AllowHardware = false, Ceiling = DecodePath.Software, Gpu = null },
        });
        Assert.True(await source.Opened, source.Health.Status);
        source.Prepare(0, false, 1);
        Assert.True(source.WaitUntilReady(0, Wait), source.Health.Status);
        using var lease = source.Acquire(0)!;
        Assert.Equal((DecodePath.Software, "", false), (source.Health.Path, source.Health.Device, source.Health.Hardware));
        Assert.Equal("software", DecodeLadder.Word(source.Health.Path));
    }
}
