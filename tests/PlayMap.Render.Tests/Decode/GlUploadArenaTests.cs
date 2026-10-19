using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using PlayMap.Core.Media;
using PlayMap.Core.Media.Decode;
using PlayMap.Core.Numerics;
using PlayMap.Render.Decode;
using PlayMap.Render.Gl;
using PlayMap.Render.Video;
using PlayMap.Tests;

namespace PlayMap.Render.Tests.Decode;

/// <summary>The upload arena on a real context (llvmpipe maps persistent, coherent buffers): a HAP plane a producer wrote into a
/// block uploads from the arena's buffer and draws the same picture as the copy path; a returned block is free again only after a
/// fence the GPU has passed; an arena that is gone is never read (the last picture holds); the per-frame fence and the returns
/// allocate nothing once warm.</summary>
[Collection("gl")]
public sealed class GlUploadArenaTests(GlFixture gl)
{
    private const int W = 64, H = 32;
    private const int Bytes = (W / 4) * (H / 4) * 8;

    private static unsafe UploadBlock WriteBc1(GlUploadArena arena, ulong seed)
    {
        Assert.True(arena.TryRent(Bytes, out var block));
        var rng = new Pcg32(seed, 3);
        var p = (byte*)block.Pointer;
        for (var i = 0; i < Bytes; i++)
            p[i] = (byte)rng.NextInt(256);
        return block;
    }

    private static TestLease Staged(UploadBlock block, long generation) =>
        new(PixelLayout.Bc1, W, H, generation, [new FramePlane(block.Pointer, (W / 4) * 8, W / 4, H / 4)], staged: block);

    private static float[] Picture(GlDevice device, VideoTextureSet set)
    {
        var px = new float[W * H * 4];
        device.ReadPixelsFloat(set.Converted!, px);
        return px;
    }

    private static GlUploadArena Make(GlDevice device)
    {
        var arena = GlUploadArena.Create(device, 4, out var words);
        Assert.True(arena is not null, "no arena: " + words);
        device.DecodeContext = new DecodeContext(null, ZeroCopyPlatform.None, "glx", arena, "");
        return arena;
    }

    [Fact]
    public void ABlockUploadsFromTheArenaAndDrawsWhatTheCopyPathDraws() => gl.Run(ctx =>
    {
        var device = ctx.Device;
        Assert.True(device.Caps.Dxt1, "this GL samples no BC1: the arena's HAP upload cannot be drawn here");
        using var arena = Make(device);
        try
        {
            var block = WriteBc1(arena, 11);
            using var staged = new VideoTextureSet(device);
            Assert.True(staged.Update(Staged(block, 1)), staged.Refusal);
            Assert.Equal(1, staged.StagedUploads);
            var copy = new byte[Bytes];
            Marshal.Copy(block.Pointer, copy, 0, Bytes);
            var pin = GCHandle.Alloc(copy, GCHandleType.Pinned);
            try
            {
                using var plain = new VideoTextureSet(device);
                Assert.True(plain.Update(new TestLease(PixelLayout.Bc1, W, H, 1, [new FramePlane(pin.AddrOfPinnedObject(), (W / 4) * 8, W / 4, H / 4)])));
                Assert.Equal(0, plain.StagedUploads);
                Assert.Equal(Picture(device, plain), Picture(device, staged));
            }
            finally
            {
                pin.Free();
            }
            arena.Return(block);
        }
        finally
        {
            device.DecodeContext = null;
        }
    });

    [Fact]
    public void AReturnedBlockIsFreeOnlyAfterTheGpuPassedItsFence() => gl.Run(ctx =>
    {
        var device = ctx.Device;
        using var arena = Make(device);
        try
        {
            var block = WriteBc1(arena, 1);
            Assert.Equal(1, arena.Outstanding);
            arena.Return(block);
            Assert.Equal(1, arena.Outstanding);   // returned, not yet fenced
            arena.EndFrame();                     // fenced now
            device.Finish();
            arena.EndFrame();                     // the fence passed
            Assert.Equal(0, arena.Outstanding);
            Assert.Equal(arena.CapacityBytes, arena.FreeBytes);
        }
        finally
        {
            device.DecodeContext = null;
        }
    });

    [Fact]
    public void AFrameInAnArenaThatIsGoneIsNeverReadAndTheLastPictureHolds() => gl.Run(ctx =>
    {
        var device = ctx.Device;
        Assert.True(device.Caps.Dxt1, "this GL samples no BC1: the arena's HAP upload cannot be drawn here");
        var arena = Make(device);
        try
        {
            using var set = new VideoTextureSet(device);
            var first = WriteBc1(arena, 2);
            Assert.True(set.Update(Staged(first, 1)));
            var before = Picture(device, set);
            var late = WriteBc1(arena, 3);
            var epoch = arena.Epoch;
            arena.Dispose();
            Assert.NotEqual(epoch, arena.Epoch);
            Assert.False(arena.Owns(late));
            Assert.False(arena.TryRent(16, out _));
            // The lease still points at memory that went with the arena: refused before anything reads it.
            Assert.False(set.Update(Staged(late, 2)));
            Assert.Equal("the frame was written into an upload arena this renderer no longer has", set.Refusal);
            Assert.Equal(before, Picture(device, set));
            arena.Return(late);   // ignored
        }
        finally
        {
            device.DecodeContext = null;
            arena.Dispose();
        }
    });

    [Fact]
    public void RentReturnAndTheFrameFenceAllocateNothingOnceWarm() => gl.Run(ctx =>
    {
        var device = ctx.Device;
        using var arena = Make(device);
        try
        {
            var blocks = new UploadBlock[8];
            Cycle(device, arena, blocks, 20);
            var allocated = Cycle(device, arena, blocks, 200);
            Assert.Equal(0, allocated);
            device.Finish();
            arena.EndFrame();
            arena.EndFrame();
            Assert.Equal(0, arena.Outstanding);
        }
        finally
        {
            device.DecodeContext = null;
        }
    });

    [MethodImpl(MethodImplOptions.AggressiveOptimization | MethodImplOptions.NoInlining)]
    private static long Cycle(GlDevice device, GlUploadArena arena, UploadBlock[] blocks, int frames)
    {
        var before = Allocations.Start();
        for (var f = 0; f < frames; f++)
        {
            for (var i = 0; i < blocks.Length; i++)
                arena.TryRent(4096 * (i + 1), out blocks[i]);
            for (var i = 0; i < blocks.Length; i++)
                arena.Return(blocks[i]);
            arena.EndFrame();
            if (f % 4 == 3)
                device.Finish();
        }
        return Allocations.Since(before);
    }
}
