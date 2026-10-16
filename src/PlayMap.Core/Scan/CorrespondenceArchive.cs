using System.Buffers.Binary;
using System.IO.Compression;
using System.Runtime.InteropServices;

namespace PlayMap.Core.Scan;

/// <summary>
/// A decode as a file, so a scan's views outlive the node: "PMCM" and a version, the camera and projector rasters, the
/// scanned region, the axes, the decode's bit counts and noise, then every per-pixel array (projector x and y, confidence,
/// contrast, flags) and the white picture, little-endian, deflated. Reading it back gives the same map bit for bit, its
/// totals recounted from the flags; a file that is not one, or is cut short, is refused with words.
/// </summary>
public static class CorrespondenceArchive
{
    private const uint Magic = 0x4D434D50; // "PMCM" read little-endian
    private const int Version = 1;

    /// <summary>The largest camera raster a file may describe (64 Mpx): a header naming more is refused before anything is allocated.</summary>
    public const long MaxPixels = 64L << 20;

    public static void Write(Stream stream, CorrespondenceMap map)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(map);
        Span<byte> head = stackalloc byte[72];
        BinaryPrimitives.WriteUInt32LittleEndian(head, Magic);
        BinaryPrimitives.WriteInt32LittleEndian(head[4..], Version);
        BinaryPrimitives.WriteInt32LittleEndian(head[8..], map.Width);
        BinaryPrimitives.WriteInt32LittleEndian(head[12..], map.Height);
        BinaryPrimitives.WriteInt32LittleEndian(head[16..], map.ProjectorWidth);
        BinaryPrimitives.WriteInt32LittleEndian(head[20..], map.ProjectorHeight);
        BinaryPrimitives.WriteInt32LittleEndian(head[24..], map.Region.X);
        BinaryPrimitives.WriteInt32LittleEndian(head[28..], map.Region.Y);
        BinaryPrimitives.WriteInt32LittleEndian(head[32..], map.Region.Width);
        BinaryPrimitives.WriteInt32LittleEndian(head[36..], map.Region.Height);
        BinaryPrimitives.WriteInt32LittleEndian(head[40..], (int)map.Axes);
        head[44] = map.SubPixel ? (byte)1 : (byte)0;
        head[45] = map.White is null ? (byte)0 : (byte)1;
        head[46] = map.WhiteColour is null ? (byte)0 : (byte)1;
        head[47] = 0;
        BinaryPrimitives.WriteInt64LittleEndian(head[48..], map.Stats.UncertainBits);
        BinaryPrimitives.WriteInt64LittleEndian(head[56..], map.Stats.DecidedBits);
        BinaryPrimitives.WriteDoubleLittleEndian(head[64..], map.Stats.NoiseSigma);
        stream.Write(head);
        using var z = new ZLibStream(stream, CompressionLevel.Fastest, leaveOpen: true);
        WriteArray<float>(z, map.ProjectorX);
        WriteArray<float>(z, map.ProjectorY);
        WriteArray<float>(z, map.Confidence);
        WriteArray<float>(z, map.Contrast);
        z.Write(MemoryMarshal.AsBytes(map.Flags.AsSpan()));
        if (map.White is { } white)
            z.Write(white.AsSpan(0, map.Width * map.Height));
        if (map.WhiteColour is { } colour)
            WriteArray<int>(z, colour.AsSpan(0, map.Width * map.Height));
    }

    /// <summary>The decode a file holds.</summary>
    /// <exception cref="InvalidDataException">The stream is not a decode file this version reads, or it is cut short.</exception>
    public static CorrespondenceMap Read(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        Span<byte> head = stackalloc byte[72];
        stream.ReadExactly(head);
        if (BinaryPrimitives.ReadUInt32LittleEndian(head) != Magic)
            throw new InvalidDataException("not a decode file (no PMCM mark)");
        if (BinaryPrimitives.ReadInt32LittleEndian(head[4..]) != Version)
            throw new InvalidDataException("a decode file of another version");
        var w = BinaryPrimitives.ReadInt32LittleEndian(head[8..]);
        var h = BinaryPrimitives.ReadInt32LittleEndian(head[12..]);
        var pw = BinaryPrimitives.ReadInt32LittleEndian(head[16..]);
        var ph = BinaryPrimitives.ReadInt32LittleEndian(head[20..]);
        if (w < 1 || h < 1 || (long)w * h > MaxPixels || pw < 1 || ph < 1)
            throw new InvalidDataException("a decode file with an impossible raster");
        var region = new PixelRect(BinaryPrimitives.ReadInt32LittleEndian(head[24..]), BinaryPrimitives.ReadInt32LittleEndian(head[28..]),
            BinaryPrimitives.ReadInt32LittleEndian(head[32..]), BinaryPrimitives.ReadInt32LittleEndian(head[36..]));
        var axes = (ScanAxes)BinaryPrimitives.ReadInt32LittleEndian(head[40..]);
        var subPixel = head[44] != 0;
        var hasWhite = head[45] != 0;
        var hasColour = head[46] != 0;
        var uncertain = BinaryPrimitives.ReadInt64LittleEndian(head[48..]);
        var decided = BinaryPrimitives.ReadInt64LittleEndian(head[56..]);
        var noise = BinaryPrimitives.ReadDoubleLittleEndian(head[64..]);
        var n = w * h;
        try
        {
            using var z = new ZLibStream(stream, CompressionMode.Decompress, leaveOpen: true);
            var white = hasWhite ? new byte[n] : null;
            var colour = hasColour ? new int[n] : null;
            var map = new CorrespondenceMap(w, h, pw, ph, region, axes, subPixel) { White = white, WhiteColour = colour };
            ReadArray<float>(z, map.ProjectorX);
            ReadArray<float>(z, map.ProjectorY);
            ReadArray<float>(z, map.Confidence);
            ReadArray<float>(z, map.Contrast);
            z.ReadExactly(MemoryMarshal.AsBytes(map.Flags.AsSpan()));
            if (white is not null)
                z.ReadExactly(white);
            if (colour is not null)
                ReadArray<int>(z, colour);
            map.Recount(uncertain, decided, noise);
            return map;
        }
        catch (EndOfStreamException)
        {
            throw new InvalidDataException("the decode file is cut short");
        }
    }

    private static void WriteArray<T>(Stream s, ReadOnlySpan<T> values)
        where T : unmanaged
    {
        if (!BitConverter.IsLittleEndian)
            throw new PlatformNotSupportedException("decode files are written on little-endian machines");
        s.Write(MemoryMarshal.AsBytes(values));
    }

    private static void ReadArray<T>(Stream s, Span<T> values)
        where T : unmanaged
    {
        if (!BitConverter.IsLittleEndian)
            throw new PlatformNotSupportedException("decode files are read on little-endian machines");
        s.ReadExactly(MemoryMarshal.AsBytes(values));
    }
}
