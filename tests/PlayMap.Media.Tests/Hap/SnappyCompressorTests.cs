using PlayMap.Core.Media.Hap;
using PlayMap.Core.Media.Optimise;
using PlayMap.Core.Numerics;
using PlayMap.Media.Hap;

namespace PlayMap.Media.Tests.Hap;

/// <summary>The encoder's Snappy compressor against two decoders — Snappier (the reference codec) and the node's own — on runs,
/// repeats, noise and long matches: every block decodes byte-exact in both, stays within the format's bound and shrinks what
/// repeats. And the packer's frames parse as HAP with their chunks raw where Snappy saves too little.</summary>
public class SnappyCompressorTests
{
    private static byte[] Sample(int length, ulong seed, int alphabet, int repeatEvery)
    {
        var rng = new Pcg32(seed);
        var p = new byte[length];
        for (var i = 0; i < p.Length; i++)
            p[i] = i > 70 && repeatEvery > 0 && rng.NextInt(repeatEvery) == 0 ? p[i - 1 - rng.NextInt(70_000 < i ? 70_000 : i - 1)] : (byte)rng.NextInt(alphabet);
        return p;
    }

    [Theory]
    [InlineData(0, 1, 256, 0)]
    [InlineData(7, 2, 256, 0)]
    [InlineData(100, 3, 256, 3)]
    [InlineData(70_000, 4, 4, 2)]
    [InlineData(300_000, 5, 1, 0)]
    [InlineData(1_048_576, 6, 16, 4)]
    [InlineData(200_000, 7, 256, 0)]
    public void EveryBlockDecodesByteExactInBothDecoders(int length, ulong seed, int alphabet, int repeatEvery)
    {
        var input = Sample(length, seed, alphabet, repeatEvery);
        var packed = new byte[SnappyCompressor.MaxCompressedLength(length)];
        var n = new SnappyCompressor().Compress(input, packed);
        Assert.InRange(n, 1, SnappyCompressor.MaxCompressedLength(length));
        Assert.Equal(input, Snappier.Snappy.DecompressToArray(packed.AsSpan(0, n)));
        var mine = new byte[length];
        Assert.True(SnappyBlock.TryDecompress(packed.AsSpan(0, n), mine, out var written));
        Assert.Equal(length, written);
        Assert.Equal(input, mine);
        if (alphabet == 1 && length > 0)
            Assert.True(n < length / 20, $"{n} bytes for {length} of one value");
    }

    [Fact]
    public void ThePackerStoresChunksRawWhereSnappySavesTooLittleAndTheFrameParses()
    {
        using var blocks = new HapBlocks();
        blocks.Reset(HapCodec.HapQ, 64, 32);
        var plane = blocks.Plane(0);
        var rng = new Pcg32(9);
        // The top half repeats one block, the bottom half is noise.
        for (var i = 0; i < plane.Length; i++)
            plane[i] = i < plane.Length / 2 ? (byte)(i % 16) : (byte)rng.NextInt(256);
        var packer = new HapPacker();
        byte[] frame = [];
        var length = packer.Pack(blocks, 2, SecondStagePolicy.Auto, 0.10, ref frame);
        var parsed = new HapFrame();
        Assert.True(parsed.TryParse(frame.AsSpan(0, length), 64, 32, HapCodec.HapQ, out var refusal), refusal.ToString());
        var chunks = parsed.Chunks(0);
        Assert.Equal(2, chunks.Length);
        Assert.Equal((HapCompressor.Snappy, HapCompressor.None), (chunks[0].Compressor, chunks[1].Compressor));
        Assert.Equal((1, 2), packer.ChunkMix);
    }
}
