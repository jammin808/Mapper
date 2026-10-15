using System.Buffers;
using PlayMap.Core.Media.Hap;
using PlayMap.Core.Model;

namespace PlayMap.Core.Media.Optimise;

/// <summary>The block planes of one picture, in row order of 4×4 blocks: one plane, or two for HAP Q Alpha (YCoCg BC3, then BC4).
/// The buffers are rented; <see cref="Dispose"/> returns them. Reused across frames of one size (<see cref="Reset"/>).</summary>
public sealed class HapBlocks : IDisposable
{
    private readonly byte[]?[] _planes = new byte[2][];
    private readonly int[] _bytes = new int[2];
    private readonly HapTexture[] _kinds = new HapTexture[2];

    public int Planes { get; private set; }
    public int BlocksWide { get; private set; }
    public int BlocksHigh { get; private set; }
    public HapCodec Codec { get; private set; }

    /// <summary>Sizes the planes for a codec and a picture (sizes round up to whole blocks).</summary>
    public void Reset(HapCodec codec, int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        Codec = codec;
        BlocksWide = (width + 3) / 4;
        BlocksHigh = (height + 3) / 4;
        Planes = HapCodecs.Images(codec);
        _kinds[0] = HapCodecs.FirstTexture(codec);
        _kinds[1] = HapTexture.Bc4Alpha;
        for (var p = 0; p < Planes; p++)
        {
            var bytes = BlocksWide * BlocksHigh * HapCodecs.BlockBytes(_kinds[p]);
            if (_planes[p] is not { } have || have.Length < bytes)
            {
                if (_planes[p] is { } old)
                    ArrayPool<byte>.Shared.Return(old);
                _planes[p] = ArrayPool<byte>.Shared.Rent(bytes);
            }
            _bytes[p] = bytes;
        }
    }

    public HapTexture Kind(int plane) => _kinds[plane];

    public int BlockBytes(int plane) => HapCodecs.BlockBytes(_kinds[plane]);

    public Span<byte> Plane(int plane) => plane < Planes ? _planes[plane].AsSpan(0, _bytes[plane]) : throw new ArgumentOutOfRangeException(nameof(plane));

    public void Dispose()
    {
        for (var p = 0; p < _planes.Length; p++)
        {
            if (_planes[p] is { } b)
                ArrayPool<byte>.Shared.Return(b);
            _planes[p] = null;
        }
        Planes = 0;
    }
}

/// <summary>Turns a picture into HAP's block planes. The GL implementation (Render's <c>GlHapEncoder</c>) runs its programs on the
/// thread whose context it was made on; every call is on that thread.</summary>
public interface IHapBlockEncoder : IDisposable
{
    /// <summary>"gl-frag/1 · llvmpipe (LLVM 20.1.2)": the quality report and a version's encoder field say it.</summary>
    string Id { get; }

    /// <summary>Whether this encoder makes the codec at the quality, and the words why not.</summary>
    bool Supports(HapCodec codec, EncodeQuality quality, out string why);

    /// <summary>Encodes a picture of straight-alpha RGBA, 8 bits a channel, rows tight (width × 4 bytes), into
    /// <paramref name="into"/> (reset for the codec and size here).</summary>
    void Encode(ReadOnlySpan<byte> rgba, int width, int height, HapCodec codec, EncodeQuality quality, HapBlocks into);
}

/// <summary>The second stage and the frame: each plane split into chunks on block boundaries (<see cref="HapChunking"/>), each chunk
/// Snappy-compressed and kept raw when Snappy saves less than the recipe's gain (or by the policy), then written as one HAP frame
/// (<see cref="HapFrameWriter"/>). One packer per thread: its buffers are reused, so a frame of a size seen before allocates nothing.</summary>
public sealed class HapPacker
{
    private readonly SnappyCompressor _snappy = new();
    private readonly HapChunkOut[] _plan = new HapChunkOut[HapFrame.MaxChunks];
    private readonly HapChunkOut[][] _stored = [new HapChunkOut[HapFrame.MaxChunks], new HapChunkOut[HapFrame.MaxChunks]];
    private byte[][] _data = [[], []];
    private byte[] _scratch = [];
    private int _rawChunks;
    private int _allChunks;

    /// <summary>Chunks stored raw in the frames packed so far, of all of them.</summary>
    public (int Raw, int All) ChunkMix => (_rawChunks, _allChunks);

    /// <summary>Packs one picture's planes into a HAP frame in <paramref name="frame"/> (grown as needed) and answers its length.</summary>
    public int Pack(HapBlocks blocks, int chunks, SecondStagePolicy policy, double rawBelowGain, ref byte[] frame)
    {
        ArgumentNullException.ThrowIfNull(blocks);
        Span<int> counts = stackalloc int[2];
        for (var p = 0; p < blocks.Planes; p++)
            counts[p] = Stage(blocks, p, chunks, policy, rawBelowGain);
        var first = _stored[0].AsSpan(0, counts[0]);
        var second = blocks.Planes > 1 ? _stored[1].AsSpan(0, counts[1]) : default;
        var size = HapFrameWriter.Measure(first, second);
        if (frame.Length < size)
            frame = new byte[size];
        return blocks.Planes > 1
            ? HapFrameWriter.Write(frame, blocks.Kind(0), first, _data[0], blocks.Kind(1), second, _data[1])
            : HapFrameWriter.Write(frame, blocks.Kind(0), first, _data[0]);
    }

    private int Stage(HapBlocks blocks, int plane, int chunks, SecondStagePolicy policy, double rawBelowGain)
    {
        var bytes = blocks.Plane(plane);
        var blockBytes = blocks.BlockBytes(plane);
        var count = HapChunking.Plan(bytes.Length / blockBytes, blockBytes, chunks, _plan);
        var bound = 0;
        for (var i = 0; i < count; i++)
            bound += SnappyCompressor.MaxCompressedLength(_plan[i].Length);
        if (_data[plane].Length < bound)
            _data[plane] = new byte[bound];
        var data = _data[plane];
        var at = 0;
        for (var i = 0; i < count; i++)
        {
            var src = bytes.Slice(_plan[i].Offset, _plan[i].Length);
            var raw = policy == SecondStagePolicy.None;
            if (!raw)
            {
                var need = SnappyCompressor.MaxCompressedLength(src.Length);
                if (_scratch.Length < need)
                    _scratch = new byte[need];
                var n = _snappy.Compress(src, _scratch);
                raw = policy == SecondStagePolicy.Auto && n > src.Length * (1 - rawBelowGain);
                if (!raw)
                {
                    _scratch.AsSpan(0, n).CopyTo(data.AsSpan(at));
                    _stored[plane][i] = new HapChunkOut(at, n, HapCompressor.Snappy);
                    at += n;
                }
            }
            if (raw)
            {
                src.CopyTo(data.AsSpan(at));
                _stored[plane][i] = new HapChunkOut(at, src.Length, HapCompressor.None);
                at += src.Length;
                _rawChunks++;
            }
            _allChunks++;
        }
        return count;
    }
}
