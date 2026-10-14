using System.Buffers.Binary;

namespace PlayMap.Core.Media.Hap;

/// <summary>One chunk as stored: its bytes at <see cref="Offset"/> in the caller's data, <see cref="Length"/> long, and how
/// they are stored. Uncompressed plans (<see cref="HapChunking.Plan"/>) name ranges of the plane itself.</summary>
public readonly record struct HapChunkOut(int Offset, int Length, HapCompressor Compressor);

/// <summary>Where a texture's chunk boundaries fall.</summary>
public static class HapChunking
{
    /// <summary>Splits <paramref name="blocks"/> blocks of <paramref name="blockBytes"/> bytes into at most
    /// <paramref name="wanted"/> chunks on block boundaries: the largest count not above the wish that divides the block
    /// count (equal chunks, the reference encoder's rule); when only one chunk would divide evenly, near-equal chunks
    /// (sizes differing by at most one block). Answers the count; the chunks name byte ranges of the plane, stored raw.</summary>
    public static int Plan(int blocks, int blockBytes, int wanted, Span<HapChunkOut> chunks)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(blocks, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(blockBytes, 1);
        wanted = Math.Clamp(wanted, 1, Math.Min(HapFrame.MaxChunks, blocks));
        if (chunks.Length < wanted)
            throw new ArgumentException("The span holds fewer chunks than wanted.", nameof(chunks));
        var count = 1;
        for (var c = wanted; c >= 1; c--)
        {
            if (blocks % c == 0)
            {
                count = c;
                break;
            }
        }
        if (count == 1 && wanted > 1)
            count = wanted;
        var baseBlocks = blocks / count;
        var extra = blocks % count;
        var at = 0;
        for (var i = 0; i < count; i++)
        {
            var n = baseBlocks + (i < extra ? 1 : 0);
            chunks[i] = new HapChunkOut(at * blockBytes, n * blockBytes, HapCompressor.None);
            at += n;
        }
        return count;
    }
}

/// <summary>Writes HAP frames: one image section (one chunk stored raw or Snappy, or several chunks behind a
/// decode-instructions container with a compressor table and a size table), or for two textures (HAP Q Alpha) both inside
/// a multiple-images section. Headers take the four-byte form when the size fits in 24 bits and is not zero, else the
/// eight-byte form; chunks follow one another in order, so no offset table is written.</summary>
public static class HapFrameWriter
{
    /// <summary>Bytes of the frame <see cref="Write"/> makes for these chunks (<paramref name="chunks2"/> empty for one texture).</summary>
    public static int Measure(ReadOnlySpan<HapChunkOut> chunks1, ReadOnlySpan<HapChunkOut> chunks2 = default)
    {
        var one = ImageBytes(chunks1);
        if (chunks2.IsEmpty)
            return Header(one) + one;
        var two = ImageBytes(chunks2);
        var inner = Header(one) + one + Header(two) + two;
        return Header(inner) + inner;
    }

    /// <summary>Writes a frame into <paramref name="frame"/> and answers its length. Each chunk's stored bytes are read from
    /// its data span at the chunk's offset.</summary>
    public static int Write(Span<byte> frame, HapTexture first, ReadOnlySpan<HapChunkOut> chunks1, ReadOnlySpan<byte> data1,
        HapTexture second = 0, ReadOnlySpan<HapChunkOut> chunks2 = default, ReadOnlySpan<byte> data2 = default)
    {
        if (chunks1.IsEmpty)
            throw new ArgumentException("A texture has at least one chunk.", nameof(chunks1));
        var total = Measure(chunks1, chunks2);
        if (frame.Length < total)
            throw new ArgumentException($"The frame needs {total} bytes; {frame.Length} given.", nameof(frame));
        if (chunks2.IsEmpty)
            return WriteImage(frame, first, chunks1, data1);
        if (!HapCodecs.IsTexture((int)second))
            throw new ArgumentException("A second texture needs its kind.", nameof(second));
        var inner = Header(ImageBytes(chunks1)) + ImageBytes(chunks1) + Header(ImageBytes(chunks2)) + ImageBytes(chunks2);
        var at = WriteHeader(frame, inner, HapCodecs.MultipleImages);
        at += WriteImage(frame[at..], first, chunks1, data1);
        at += WriteImage(frame[at..], second, chunks2, data2);
        return at;
    }

    private static int WriteImage(Span<byte> frame, HapTexture kind, ReadOnlySpan<HapChunkOut> chunks, ReadOnlySpan<byte> data)
    {
        var size = ImageBytes(chunks);
        if (chunks.Length == 1)
        {
            var stage = chunks[0].Compressor == HapCompressor.Snappy ? HapCodecs.StageSnappy : HapCodecs.StageNone;
            var at = WriteHeader(frame, size, (byte)((stage << 4) | (byte)kind));
            data.Slice(chunks[0].Offset, chunks[0].Length).CopyTo(frame[at..]);
            return at + size;
        }
        var p = WriteHeader(frame, size, (byte)((HapCodecs.StageComplex << 4) | (byte)kind));
        var tables = TablesBytes(chunks.Length);
        p += WriteHeader(frame[p..], tables, HapCodecs.DecodeInstructions);
        p += WriteHeader(frame[p..], chunks.Length, HapCodecs.CompressorTable);
        foreach (var c in chunks)
            frame[p++] = (byte)c.Compressor;
        p += WriteHeader(frame[p..], 4 * chunks.Length, HapCodecs.SizeTable);
        foreach (var c in chunks)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(frame[p..], (uint)c.Length);
            p += 4;
        }
        foreach (var c in chunks)
        {
            data.Slice(c.Offset, c.Length).CopyTo(frame[p..]);
            p += c.Length;
        }
        return p;
    }

    private static int ImageBytes(ReadOnlySpan<HapChunkOut> chunks)
    {
        long data = 0;
        foreach (var c in chunks)
            data += c.Length;
        if (chunks.Length > 1)
        {
            var tables = TablesBytes(chunks.Length);
            data += Header(tables) + tables;
        }
        return data > int.MaxValue ? throw new ArgumentException("A HAP frame is smaller than 2 GB.", nameof(chunks)) : (int)data;
    }

    private static int TablesBytes(int chunks) => Header(chunks) + chunks + Header(4 * chunks) + 4 * chunks;

    private static int Header(int size) => size is > 0 and < 1 << 24 ? 4 : 8;

    private static int WriteHeader(Span<byte> frame, int size, byte type)
    {
        if (Header(size) == 4)
        {
            frame[0] = (byte)size;
            frame[1] = (byte)(size >> 8);
            frame[2] = (byte)(size >> 16);
            frame[3] = type;
            return 4;
        }
        frame[0] = 0;
        frame[1] = 0;
        frame[2] = 0;
        frame[3] = type;
        BinaryPrimitives.WriteUInt32LittleEndian(frame[4..], (uint)size);
        return 8;
    }
}
