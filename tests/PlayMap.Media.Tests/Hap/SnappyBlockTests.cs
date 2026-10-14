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
    public void EveryShortPeriodAndLengthRepeatsAsTheFormatSays()
    {
        // Hand-made blocks (a compressor never writes most of these): a 9-byte literal, then a back-reference of every
        // length 1–64 at every distance 1–9, then (or not) a literal after it, so references run with and without slack
        // behind them. The expected bytes follow the format's own meaning, one byte at a time.
        byte[] head = [11, 22, 33, 44, 55, 66, 77, 88, 99];
        byte[] tail = [.. Enumerable.Range(0, 20).Select(i => (byte)(200 + i))];
        foreach (var withTail in new[] { false, true })
        {
            for (var off = 1; off <= 9; off++)
            {
                for (var len = 1; len <= 64; len++)
                {
                    var expected = new List<byte>(head);
                    for (var i = 0; i < len; i++)
                        expected.Add(expected[^off]);
                    if (withTail)
                        expected.AddRange(tail);
                    var block = new List<byte>();
                    for (var n = (uint)expected.Count; ; n >>= 7)
                    {
                        block.Add((byte)(n < 0x80 ? n : (n & 0x7F) | 0x80));
                        if (n < 0x80)
                            break;
                    }
                    block.Add((byte)((head.Length - 1) << 2));
                    block.AddRange(head);
                    block.Add((byte)(((len - 1) << 2) | 2));
                    block.Add((byte)off);
                    block.Add(0);
                    if (withTail)
                    {
                        block.Add((byte)((tail.Length - 1) << 2));
                        block.AddRange(tail);
                    }
                    var output = new byte[expected.Count];
                    Assert.True(SnappyBlock.TryDecompress(block.ToArray(), output, out var written), $"distance {off}, length {len}");
                    Assert.Equal(expected.Count, written);
                    Assert.True(expected.SequenceEqual(output), $"distance {off}, length {len}, tail {withTail}");
                }
            }
        }
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
