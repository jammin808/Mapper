using System.Buffers.Binary;
using System.Globalization;

namespace PlayMap.Core.Media.Hap;

/// <summary>One chunk of a texture: where its stored bytes sit in the packet, how they are stored, and where its decoded
/// bytes land in the texture's plane.</summary>
public readonly record struct HapChunk(int SourceOffset, int SourceLength, HapCompressor Compressor, int TargetOffset, int TargetLength);

/// <summary>One texture (image) of a frame: its kind, its chunks (a range of <see cref="HapFrame.Chunks"/>) and its decoded size.</summary>
public readonly record struct HapTextureInfo(HapTexture Kind, int ChunkStart, int ChunkCount, int Bytes);

/// <summary>Why a frame was refused. Each has stable words (<see cref="HapFrame.Words"/>).</summary>
public enum HapRefusal
{
    None,
    Empty,
    Truncated,
    BadHeader,
    UnknownTexture,
    NotThisCodec,
    ChunkTables,
    ChunkOutOfBounds,
    SizeMismatch,
    TooManyChunks,
    NonStandardCompressor,
    BadSnappyLength,
}

/// <summary>Reads the uncompressed length a Snappy block starts with (a little-endian base-128 varint of at most five bytes).</summary>
public static class SnappyLength
{
    public static bool TryRead(ReadOnlySpan<byte> chunk, out uint length, out int preambleBytes)
    {
        length = 0;
        preambleBytes = 0;
        ulong value = 0;
        for (var i = 0; i < 5 && i < chunk.Length; i++)
        {
            var b = chunk[i];
            value |= (ulong)(b & 0x7F) << (7 * i);
            if ((b & 0x80) == 0)
            {
                if (value > uint.MaxValue)
                    return false;
                length = (uint)value;
                preambleBytes = i + 1;
                return true;
            }
        }
        return false;
    }
}

/// <summary>A HAP frame parsed in place: its textures and their chunk tables, checked against the picture's size and the
/// codec the container names. Parsing only reads lengths and tables; it never decompresses.
/// <para>Owned by one thread and refilled by each parse: the tables are fixed arrays, so a parse allocates nothing. A
/// refused frame keeps the facts its words need (<see cref="Words"/>).</para></summary>
public sealed class HapFrame
{
    /// <summary>The most chunks one texture may have.</summary>
    public const int MaxChunks = 256;

    private readonly HapChunk[] _chunks = new HapChunk[2 * MaxChunks];
    private readonly HapTextureInfo[] _textures = new HapTextureInfo[2];
    private int _chunkCount;
    private long _detail;
    private long _need;
    private HapTexture _refusedKind;
    private HapCodec _codec;
    private int _width;
    private int _height;

    public int TextureCount { get; private set; }

    /// <summary>Chunks stored without a second stage, across every texture of the frame.</summary>
    public int RawChunks { get; private set; }

    /// <summary>Every chunk of the frame, texture by texture.</summary>
    public ReadOnlySpan<HapChunk> AllChunks => _chunks.AsSpan(0, _chunkCount);

    public HapRefusal Refusal { get; private set; }

    /// <summary>Bytes of the packet the frame's sections cover.</summary>
    public int PacketBytes { get; private set; }

    /// <summary>Texture <paramref name="i"/> in plane order (HAP Q Alpha: the YCoCg image, then the alpha image).</summary>
    public HapTextureInfo Texture(int i) => i >= 0 && i < TextureCount
        ? _textures[i]
        : throw new ArgumentOutOfRangeException(nameof(i), i, "no such texture in this frame");

    public ReadOnlySpan<HapChunk> Chunks(int texture)
    {
        var t = Texture(texture);
        return _chunks.AsSpan(t.ChunkStart, t.ChunkCount);
    }

    /// <summary>Parses a packet of a <paramref name="width"/>×<paramref name="height"/> stream of <paramref name="expected"/>.
    /// False with a refusal when anything is not as the specification and the picture's size say: then the frame holds
    /// no textures.</summary>
    public bool TryParse(ReadOnlySpan<byte> packet, int width, int height, HapCodec expected, out HapRefusal refusal)
    {
        TextureCount = 0;
        RawChunks = 0;
        PacketBytes = 0;
        _chunkCount = 0;
        _detail = 0;
        _need = 0;
        _codec = expected;
        _width = width;
        _height = height;
        refusal = Parse(packet, width, height, expected);
        Refusal = refusal;
        if (refusal != HapRefusal.None)
        {
            TextureCount = 0;
            _chunkCount = 0;
            RawChunks = 0;
            return false;
        }
        return true;
    }

    private HapRefusal Parse(ReadOnlySpan<byte> packet, int width, int height, HapCodec expected)
    {
        if (packet.IsEmpty)
            return HapRefusal.Empty;
        if (width < 1 || height < 1)
            return Fail(HapRefusal.BadHeader, 0);
        var r = ReadHeader(packet, 0, out var type, out var start, out var size);
        if (r != HapRefusal.None)
            return r;
        PacketBytes = start + size;
        if (type == HapCodecs.MultipleImages)
        {
            if (expected != HapCodec.HapQAlpha)
                return Fail(HapRefusal.NotThisCodec, 0, kind: HapCodecs.MultipleImages);
            var at = start;
            var end = start + size;
            var images = 0;
            while (at < end)
            {
                if (images == 2)
                    return Fail(HapRefusal.BadHeader, 3);
                r = ReadHeader(packet[..end], at, out var inner, out var innerStart, out var innerSize);
                if (r != HapRefusal.None)
                    return r;
                r = Image(packet, inner, innerStart, innerSize, width, height, expected, images);
                if (r != HapRefusal.None)
                    return r;
                images++;
                at = innerStart + innerSize;
            }
            if (images != 2)
                return Fail(HapRefusal.BadHeader, images);
            if (_textures[0].Kind != HapTexture.Bc3YCoCg)
            {
                // The specification does not order the two images; planes are always the colour image, then the alpha one.
                (_textures[0], _textures[1]) = (_textures[1], _textures[0]);
            }
            return HapRefusal.None;
        }
        if (HapCodecs.Images(expected) != 1)
            return Fail(HapRefusal.NotThisCodec, 0, kind: type & 0x0F);
        return Image(packet, type, start, size, width, height, expected, 0);
    }

    // A section header: three bytes of little-endian size and a type byte; a size of zero means the real size follows in
    // four more bytes (an eight-byte header).
    private HapRefusal ReadHeader(ReadOnlySpan<byte> span, int at, out byte type, out int start, out int size)
    {
        type = 0;
        start = 0;
        size = 0;
        if (span.Length - at < 4)
            return Fail(HapRefusal.Truncated, 4 - (span.Length - at));
        long length = span[at] | (span[at + 1] << 8) | (span[at + 2] << 16);
        type = span[at + 3];
        var header = 4;
        if (length == 0)
        {
            if (span.Length - at < 8)
                return Fail(HapRefusal.Truncated, 8 - (span.Length - at));
            length = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(at + 4, 4));
            header = 8;
        }
        var left = span.Length - at - header;
        if (length > left)
            return Fail(HapRefusal.Truncated, length - left);
        start = at + header;
        size = (int)length;
        return HapRefusal.None;
    }

    private HapRefusal Image(ReadOnlySpan<byte> packet, byte type, int start, int size, int width, int height, HapCodec expected, int index)
    {
        var stage = type >> 4;
        var nibble = type & 0x0F;
        if (stage is not (HapCodecs.StageNone or HapCodecs.StageSnappy or HapCodecs.StageComplex))
            return Fail(HapRefusal.BadHeader, type);
        if (!HapCodecs.IsTexture(nibble))
            return Fail(HapRefusal.UnknownTexture, nibble);
        var kind = (HapTexture)nibble;
        // HAP Q Alpha's two images may come in either order; every other codec has one.
        var allowed = expected == HapCodec.HapQAlpha
            ? HapCodecs.Allowed(expected, kind, 0) || HapCodecs.Allowed(expected, kind, 1)
            : HapCodecs.Allowed(expected, kind, index);
        if (!allowed)
            return Fail(HapRefusal.NotThisCodec, 0, kind: nibble);
        if (index == 1 && kind == _textures[0].Kind)
            return Fail(HapRefusal.NotThisCodec, 0, kind: nibble);
        var first = _chunkCount;
        var payload = packet.Slice(start, size);
        long total;
        switch (stage)
        {
            case HapCodecs.StageNone:
                Add(start, size, HapCompressor.None, 0, size);
                total = size;
                break;
            case HapCodecs.StageSnappy:
                if (!SnappyLength.TryRead(payload, out var len, out _) || len > int.MaxValue)
                    return Fail(HapRefusal.BadSnappyLength, 0);
                Add(start, size, HapCompressor.Snappy, 0, (int)len);
                total = len;
                break;
            default:
                var r = Complex(packet, start, size, out total);
                if (r != HapRefusal.None)
                    return r;
                break;
        }
        var need = HapCodecs.PlaneBytes(width, height, HapCodecs.BlockBytes(kind));
        if (total != need)
        {
            _refusedKind = kind;
            return Fail(HapRefusal.SizeMismatch, total, need);
        }
        _textures[index] = new HapTextureInfo(kind, first, _chunkCount - first, (int)total);
        TextureCount = index + 1;
        return HapRefusal.None;
    }

    // Stage 0xC: a decode-instructions container (compressor table, size table, an optional offset table; unknown
    // sub-sections are skipped), then the chunks' bytes.
    private HapRefusal Complex(ReadOnlySpan<byte> packet, int start, int size, out long total)
    {
        total = 0;
        var section = packet.Slice(0, start + size);
        var r = ReadHeader(section, start, out var type, out var cStart, out var cSize);
        if (r != HapRefusal.None)
            return r;
        if (type != HapCodecs.DecodeInstructions)
            return Fail(HapRefusal.ChunkTables, type);
        int compStart = -1, compCount = 0, sizeStart = -1, sizeCount = 0, offStart = -1, offCount = 0;
        var at = cStart;
        var cEnd = cStart + cSize;
        while (at < cEnd)
        {
            r = ReadHeader(section[..cEnd], at, out var sub, out var sStart, out var sSize);
            if (r != HapRefusal.None)
                return r;
            switch (sub)
            {
                case HapCodecs.CompressorTable:
                    compStart = sStart;
                    compCount = sSize;
                    break;
                case HapCodecs.SizeTable:
                    if (sSize % 4 != 0)
                        return Fail(HapRefusal.ChunkTables, sSize);
                    sizeStart = sStart;
                    sizeCount = sSize / 4;
                    break;
                case HapCodecs.OffsetTable:
                    if (sSize % 4 != 0)
                        return Fail(HapRefusal.ChunkTables, sSize);
                    offStart = sStart;
                    offCount = sSize / 4;
                    break;
            }
            at = sStart + sSize;
        }
        if (compStart < 0 || sizeStart < 0 || compCount == 0)
            return Fail(HapRefusal.ChunkTables, 0);
        if (compCount != sizeCount || (offStart >= 0 && offCount != compCount))
            return Fail(HapRefusal.ChunkTables, compCount);
        if (compCount > MaxChunks)
            return Fail(HapRefusal.TooManyChunks, compCount);
        var dataStart = cEnd;
        var dataLength = start + size - dataStart;
        long running = 0, target = 0;
        for (var i = 0; i < compCount; i++)
        {
            var comp = packet[compStart + i];
            var length = BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(sizeStart + 4 * i, 4));
            long offset = offStart >= 0 ? BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(offStart + 4 * i, 4)) : running;
            running = offset + length;
            if (offset + length > dataLength)
                return Fail(HapRefusal.ChunkOutOfBounds, i);
            var src = dataStart + (int)offset;
            int targetLength;
            HapCompressor how;
            if (comp == (byte)HapCompressor.None)
            {
                how = HapCompressor.None;
                targetLength = (int)length;
            }
            else if (comp == (byte)HapCompressor.Snappy)
            {
                how = HapCompressor.Snappy;
                if (!SnappyLength.TryRead(packet.Slice(src, (int)length), out var len, out _) || len > int.MaxValue)
                    return Fail(HapRefusal.BadSnappyLength, i);
                targetLength = (int)len;
            }
            else
            {
                return Fail(HapRefusal.NonStandardCompressor, comp);
            }
            if (target + targetLength > int.MaxValue)
                return Fail(HapRefusal.SizeMismatch, target + targetLength, 0);
            Add(src, (int)length, how, (int)target, targetLength);
            target += targetLength;
        }
        total = target;
        return HapRefusal.None;
    }

    private void Add(int src, int length, HapCompressor how, int target, int targetLength)
    {
        _chunks[_chunkCount++] = new HapChunk(src, length, how, target, targetLength);
        if (how == HapCompressor.None)
            RawChunks++;
    }

    private HapRefusal Fail(HapRefusal refusal, long detail, long need = 0, int kind = -1)
    {
        _detail = detail;
        _need = need;
        if (kind >= 0)
            _refusedKind = (HapTexture)kind;
        return refusal;
    }

    /// <summary>The words for the last refusal of this frame object, for frame number <paramref name="frame"/>.</summary>
    public string Words(long frame) => Words(Refusal, frame, _detail, _need, _refusedKind, _codec, _width, _height);

    /// <summary>The words for a refusal. <paramref name="detail"/> and <paramref name="need"/> are the facts the parser kept:
    /// the missing bytes, the decoded and needed sizes, the chunk, the compressor byte.</summary>
    public static string Words(HapRefusal refusal, long frame, long detail, long need, HapTexture kind, HapCodec codec, int width, int height)
    {
        var f = "frame " + frame.ToString(CultureInfo.InvariantCulture);
        var c = CultureInfo.InvariantCulture;
        return refusal switch
        {
            HapRefusal.None => "",
            HapRefusal.Empty => f + " is empty",
            HapRefusal.Truncated => string.Create(c, $"{f} is {detail:N0} bytes shorter than its header says"),
            HapRefusal.BadHeader => string.Create(c, $"{f} has a section header the HAP specification does not define (0x{detail:X2})"),
            HapRefusal.UnknownTexture => string.Create(c, $"{f} names texture format 0x{detail:X} that HAP does not define"),
            HapRefusal.NotThisCodec => string.Create(c,
                $"{f} holds {((int)kind == HapCodecs.MultipleImages ? "two images" : TextureName((int)kind) + " blocks")} but the file says {HapCodecs.Words(codec)}"),
            HapRefusal.ChunkTables => string.Create(c, $"{f} has chunk tables that do not agree ({detail})"),
            HapRefusal.ChunkOutOfBounds => string.Create(c, $"{f}'s chunk {detail} runs past the end of the frame"),
            HapRefusal.SizeMismatch => string.Create(c,
                $"{f} decodes to {detail:N0} bytes; {width}×{height} {TextureName((int)kind)} needs {need:N0}"),
            HapRefusal.TooManyChunks => string.Create(c, $"{f} has {detail} chunks; HAP allows at most {MaxChunks}"),
            HapRefusal.NonStandardCompressor => detail == 0xD0
                ? string.Create(c, $"{f} uses compressor 0xD0 (GDeflate), which is not part of the HAP spec")
                : string.Create(c, $"{f} uses compressor 0x{detail:X2}, which is not part of the HAP spec"),
            HapRefusal.BadSnappyLength => string.Create(c, $"{f}'s chunk {detail} does not start with a Snappy length"),
            _ => f + " was refused",
        };
    }

    private static string TextureName(int kind) => kind switch
    {
        0x01 => "BC4",
        0x02 or 0x03 => "BC6H",
        0x0B => "BC1",
        0x0C => "BC7",
        0x0E => "BC3",
        0x0F => "YCoCg BC3",
        _ => "unknown",
    };
}
