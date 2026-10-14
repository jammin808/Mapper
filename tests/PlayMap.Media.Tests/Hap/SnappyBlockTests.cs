using PlayMap.Media.Hap;

namespace PlayMap.Media.Tests.Hap;

/// <summary>The HAP second stage's Snappy decoder against Snappier, the reference codec: every block Snappier makes decodes
/// byte-exact, and malformed blocks are refused, never read past.</summary>
public class SnappyBlockTests
{
    private static byte[] Sample(int length, int seed, int alphabet)
    {
        var p = new byte[length];
        var x = (uint)seed * 2654435761u + 7;
        for (var i = 0; i < p.Length; i++)
        {
            x ^= x << 13;
            x ^= x >> 17;
            x ^= x << 5;
            // Runs and repeats as texture blocks have them: a small alphabet, with stretches copied from earlier.
            p[i] = i > 64 && x % 5 == 0 ? p[i - 1 - (int)(x >> 8) % 64] : (byte)(x % (uint)alphabet);
        }
        return p;
    }

    [Theory]
    [InlineData(1, 1, 256)]
    [InlineData(100, 2, 256)]
    [InlineData(70_000, 3, 4)]
    [InlineData(1_048_576, 4, 16)]
    [InlineData(300_000, 5, 1)]
    [InlineData(65_536 * 3 + 17, 6, 256)]
    public void EveryBlockSnappierMakesDecodesByteExact(int length, int seed, int alphabet)
    {
        var data = Sample(length, seed, alphabet);
        var z = Snappier.Snappy.CompressToArray(data);
        var output = new byte[length];
        Assert.True(SnappyBlock.TryDecompress(z, output, out var written));
        Assert.Equal(length, written);
        Assert.Equal(data, output);
    }

    [Fact]
    public void MalformedBlocksAreRefused()
    {
        var data = Sample(4096, 9, 8);
        var z = Snappier.Snappy.CompressToArray(data);
        var output = new byte[data.Length];
        Assert.False(SnappyBlock.TryDecompress(z.AsSpan(0, z.Length - 1), output, out _));   // ends short of its length
        Assert.False(SnappyBlock.TryDecompress(z, new byte[data.Length - 1], out _));      // output smaller than the preamble
        Assert.False(SnappyBlock.TryDecompress([0x04, 0x01, 0x10], output, out _));        // a reference before the start
        Assert.False(SnappyBlock.TryDecompress([0x04, 0x00, 0x41, 0x02, 0x00, 0x00], output, out _));   // offset zero
        Assert.False(SnappyBlock.TryDecompress([0x02, 0xF0, 0xFF, 0xFF, 0xFF, 0xFF], output, out _));   // a literal past the input
        Assert.False(SnappyBlock.TryDecompress([0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x01], output, out _));   // no preamble
        Assert.True(SnappyBlock.TryDecompress([0x00], [], out var none));
        Assert.Equal(0, none);
    }

    [Fact]
    public void DecodingAllocatesNothing()
    {
        var data = Sample(200_000, 11, 16);
        var z = Snappier.Snappy.CompressToArray(data);
        var output = new byte[data.Length];
        Assert.True(SnappyBlock.TryDecompress(z, output, out _));
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 20; i++)
            Assert.True(SnappyBlock.TryDecompress(z, output, out _));
        Assert.Equal(before, GC.GetAllocatedBytesForCurrentThread());
    }
}
