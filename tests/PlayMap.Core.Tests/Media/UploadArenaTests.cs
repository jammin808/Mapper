using System.Runtime.CompilerServices;
using PlayMap.Core.Media;
using PlayMap.Tests;

namespace PlayMap.Core.Tests.Media;

/// <summary>The upload arena's bookkeeping: aligned first-fit ranges, coalesced on return, bounded by capacity and by block count;
/// a rebuild moves the epoch and a block of an older epoch is never freed into the new arena; renting and returning allocate
/// nothing.</summary>
public sealed class UploadArenaTests
{
    [Fact]
    public void BlocksAreAlignedAndFirstFit()
    {
        var a = new ArenaAllocator(7, 4096, maxBlocks: 8);
        Assert.True(a.TryRent(1, 0x10000, out var b1));
        Assert.Equal(new UploadBlock(7, 0, 256, 0x10000, 1), b1);
        Assert.True(a.TryRent(300, 0x10000, out var b2));
        Assert.Equal((256L, 512L, (nint)0x10100), (b2.Offset, b2.Bytes, b2.Pointer));
        Assert.Equal(4096 - 768, a.FreeBytes);
        Assert.Equal(2, a.Outstanding);
        Assert.True(a.Return(b1));
        // The hole at the start is the first fit for a block that fits it.
        Assert.True(a.TryRent(200, 0, out var b3));
        Assert.Equal(0, b3.Offset);
    }

    [Fact]
    public void ReturnedNeighboursCoalesceSoTheWholeArenaRentsAgain()
    {
        var a = new ArenaAllocator(1, 1024 * 256, maxBlocks: 16);
        var blocks = new UploadBlock[4];
        for (var i = 0; i < 4; i++)
            Assert.True(a.TryRent(64 * 1024, 0, out blocks[i]));
        Assert.False(a.TryRent(1, 0, out _), "full");
        foreach (var i in new[] { 1, 3, 0, 2 })
            Assert.True(a.Return(blocks[i]));
        Assert.Equal(a.CapacityBytes, a.FreeBytes);
        Assert.True(a.TryRent(a.CapacityBytes, 0, out var whole));
        Assert.Equal((0L, a.CapacityBytes), (whole.Offset, whole.Bytes));
    }

    [Fact]
    public void TheBlockCountIsBoundedAndAFullArenaAnswersFalse()
    {
        var a = new ArenaAllocator(1, 1 << 20, maxBlocks: 3);
        for (var i = 0; i < 3; i++)
            Assert.True(a.TryRent(256, 0, out _));
        Assert.False(a.TryRent(256, 0, out var none));
        Assert.True(none.IsEmpty);
        Assert.False(a.TryRent(0, 0, out _));
        Assert.False(new ArenaAllocator(2, 1024).TryRent(2048, 0, out _));
    }

    [Fact]
    public void ARebuildMovesTheEpochAndOldBlocksAreIgnored()
    {
        var a = new ArenaAllocator(1, 4096);
        Assert.True(a.TryRent(1024, 0, out var old));
        Assert.Equal(1, old.Epoch);
        Assert.Equal(2, a.Rebuild());
        Assert.Equal(a.CapacityBytes, a.FreeBytes);
        Assert.True(a.TryRent(1024, 0, out var fresh));
        Assert.Equal((0L, 2L), (fresh.Offset, fresh.Epoch));
        Assert.False(a.Return(old), "a block of the arena before the rebuild");
        Assert.Equal(1, a.Outstanding);
        Assert.True(a.Return(fresh));
    }

    [Fact]
    public void ABlockReturnedTwiceOrOfAnotherArenaIsIgnored()
    {
        var a = new ArenaAllocator(1, 4096);
        Assert.True(a.TryRent(512, 0, out var b));
        Assert.True(a.TryRent(512, 0, out _));
        Assert.True(a.Return(b));
        Assert.False(a.Return(b));
        Assert.False(a.Return(b with { Arena = 2 }));
        Assert.False(a.Return(default));
        Assert.Equal(1, a.Outstanding);
        Assert.Equal(4096 - 512, a.FreeBytes);
    }

    [Fact]
    public void RentingAndReturningAllocateNothing()
    {
        var a = new ArenaAllocator(1, 64L << 20, maxBlocks: 64);
        var blocks = new UploadBlock[32];
        Cycle(a, blocks, 50);
        var bytes = Cycle(a, blocks, 2000);
        Assert.Equal(0, bytes);
        Assert.Equal(0, a.Outstanding);
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization | MethodImplOptions.NoInlining)]
    private static long Cycle(ArenaAllocator a, UploadBlock[] blocks, int rounds)
    {
        var before = Allocations.Start();
        for (var r = 0; r < rounds; r++)
        {
            for (var i = 0; i < blocks.Length; i++)
                a.TryRent(8192 + (i * 4096), 0, out blocks[i]);
            for (var i = blocks.Length - 1; i >= 0; i -= 2)
                a.Return(blocks[i]);
            for (var i = blocks.Length - 2; i >= 0; i -= 2)
                a.Return(blocks[i]);
        }
        return Allocations.Since(before);
    }

    [Fact]
    public async Task RentersOnManyThreadsNeverShareARange()
    {
        var a = new ArenaAllocator(1, 1 << 22, maxBlocks: 64);
        var owner = new int[(1 << 22) / ArenaAllocator.Alignment];
        var overlaps = 0;
        await Task.WhenAll(Enumerable.Range(1, 4).Select(t => Task.Run(() =>
        {
            for (var r = 0; r < 2000; r++)
            {
                if (!a.TryRent(256 * (1 + (r % 7)), 0, out var b))
                    continue;
                var first = (int)(b.Offset / ArenaAllocator.Alignment);
                var count = (int)(b.Bytes / ArenaAllocator.Alignment);
                for (var k = first; k < first + count; k++)
                    if (Interlocked.CompareExchange(ref owner[k], t, 0) != 0)
                        Interlocked.Increment(ref overlaps);
                for (var k = first; k < first + count; k++)
                    Interlocked.CompareExchange(ref owner[k], 0, t);
                Assert.True(a.Return(b));
            }
        })));
        Assert.Equal(0, overlaps);
        Assert.Equal(a.CapacityBytes, a.FreeBytes);
    }
}
