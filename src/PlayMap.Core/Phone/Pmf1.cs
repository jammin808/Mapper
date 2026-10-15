using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using PlayMap.Core.Scan;

namespace PlayMap.Core.Phone;

/// <summary>CRC-32 (IEEE 802.3, reflected, polynomial 0xEDB88320): the checksum a PMF1 header carries over its uncompressed
/// pixels, and the one the camera page computes with the same table.</summary>
public static class Crc32
{
    private static readonly uint[] Table = Make();

    private static uint[] Make()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }

    public static uint Of(ReadOnlySpan<byte> data)
    {
        var c = 0xFFFFFFFFu;
        foreach (var b in data)
            c = Table[(c ^ b) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }
}

/// <summary>A PMF1 frame's header: what the picture is (a pattern, a locate frame), the upload's sequence number and the cue it
/// answers, the stream's raster, the rows sent (<see cref="Roi"/>, inside the raster), the pixel format and codec, how many camera
/// frames were summed, the CRC-32 of the uncompressed pixels and when the picture was taken (page milliseconds).</summary>
public readonly record struct Pmf1Header(string Kind, long Seq, int? Cue, int Width, int Height, PixelRect Roi, string Format, string Codec,
    int Frames, uint Crc32, double Capture)
{
    /// <summary>Bytes per pixel of <see cref="Format"/>.</summary>
    public int BytesPerPixel => Format == Pmf1.Gray16 ? 2 : 1;

    /// <summary>The uncompressed payload's length: the ROI's rows, no padding.</summary>
    public long PixelBytes => (long)Roi.Width * Roi.Height * BytesPerPixel;
}

/// <summary>
/// The camera page's binary frame (<c>playmap.camera.v1</c>): the magic "PMF1", a little-endian uint32 header length H (≤ 4096), H
/// bytes of UTF-8 JSON, then the ROI's rows top to bottom with no padding (gray16 little-endian), compressed by the header's codec
/// (raw deflate or none). A frame is taken only whole: the header names every field, the ROI lies inside the raster, the inflated
/// pixels are exactly the ROI's size (never more than <see cref="MaxPixelBytes"/>) and their CRC-32 is the header's. Anything else
/// is refused with words, never half-read.
/// </summary>
public static class Pmf1
{
    public const uint Magic = 0x31464D50;

    public const int MaxHeaderBytes = 4096;

    /// <summary>The largest uncompressed picture taken (a 4K gray16 frame is 16.6 MB).</summary>
    public const long MaxPixelBytes = 64L << 20;

    public const string Gray8 = "gray8";

    public const string Gray16 = "gray16";

    public const string Deflate = "deflate-raw";

    public const string None = "none";

    private static readonly string[] Kinds = ["pattern", "locate", "still", "preview", "reference"];

    /// <summary>A frame: header and pixels, compressed by the header's codec. The header's CRC is computed here.</summary>
    public static byte[] Write(Pmf1Header header, ReadOnlySpan<byte> pixels)
    {
        if (pixels.Length != header.PixelBytes)
            throw new ArgumentException("The pixels are not the ROI's size.", nameof(pixels));
        header = header with { Crc32 = Crc32.Of(pixels) };
        byte[] payload;
        if (header.Codec == Deflate)
        {
            using var ms = new MemoryStream();
            using (var z = new DeflateStream(ms, CompressionLevel.Fastest, leaveOpen: true))
                z.Write(pixels);
            payload = ms.ToArray();
        }
        else
        {
            payload = pixels.ToArray();
        }
        var json = HeaderJson(header);
        var message = new byte[8 + json.Length + payload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(message, Magic);
        BinaryPrimitives.WriteUInt32LittleEndian(message.AsSpan(4), (uint)json.Length);
        json.CopyTo(message, 8);
        payload.CopyTo(message, 8 + json.Length);
        return message;
    }

    private static byte[] HeaderJson(Pmf1Header h)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("kind", h.Kind);
            w.WriteNumber("seq", h.Seq);
            if (h.Cue is { } cue)
                w.WriteNumber("cue", cue);
            w.WriteNumber("capture", h.Capture);
            w.WriteNumber("w", h.Width);
            w.WriteNumber("h", h.Height);
            w.WriteStartArray("roi");
            w.WriteNumberValue(h.Roi.X);
            w.WriteNumberValue(h.Roi.Y);
            w.WriteNumberValue(h.Roi.Width);
            w.WriteNumberValue(h.Roi.Height);
            w.WriteEndArray();
            w.WriteString("format", h.Format);
            w.WriteString("codec", h.Codec);
            w.WriteNumber("frames", h.Frames);
            w.WriteNumber("crc32", h.Crc32);
            w.WriteEndObject();
        }
        return ms.ToArray();
    }

    /// <summary>Reads a frame's header and its still-compressed payload; false with the words when the frame is not one.</summary>
    public static bool TryParse(ReadOnlySpan<byte> message, out Pmf1Header header, out ReadOnlySpan<byte> payload, out string words)
    {
        header = default;
        payload = default;
        if (message.Length < 8 || BinaryPrimitives.ReadUInt32LittleEndian(message) != Magic)
        {
            words = "not a PMF1 frame";
            return false;
        }
        var length = BinaryPrimitives.ReadUInt32LittleEndian(message[4..]);
        if (length is 0 or > MaxHeaderBytes || 8 + length > message.Length)
        {
            words = string.Create(CultureInfo.InvariantCulture, $"a PMF1 header of {length} bytes (at most {MaxHeaderBytes}, inside the frame)");
            return false;
        }
        if (!TryReadHeader(message.Slice(8, (int)length), out header, out words))
            return false;
        payload = message[(8 + (int)length)..];
        return true;
    }

    private static bool TryReadHeader(ReadOnlySpan<byte> json, out Pmf1Header header, out string words)
    {
        header = default;
        try
        {
            var reader = new Utf8JsonReader(json, new JsonReaderOptions { MaxDepth = 4 });
            using var doc = JsonDocument.ParseValue(ref reader);
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object)
            {
                words = "a PMF1 header is a JSON object";
                return false;
            }
            var kind = Text(r, "kind");
            var format = Text(r, "format");
            var codec = Text(r, "codec");
            if (Array.IndexOf(Kinds, kind) < 0)
            {
                words = $"a PMF1 frame of kind '{kind}'";
                return false;
            }
            if (format is not (Gray8 or Gray16))
            {
                words = $"a PMF1 frame in format '{format}' (gray8 or gray16)";
                return false;
            }
            if (codec is not (Deflate or None))
            {
                words = $"a PMF1 frame coded '{codec}' ({Deflate} or {None})";
                return false;
            }
            if (!r.TryGetProperty("roi", out var roi) || roi.ValueKind != JsonValueKind.Array || roi.GetArrayLength() != 4)
            {
                words = "a PMF1 header without its roi [x, y, w, h]";
                return false;
            }
            var w = Int(r, "w");
            var h = Int(r, "h");
            var rect = new PixelRect(roi[0].GetInt32(), roi[1].GetInt32(), roi[2].GetInt32(), roi[3].GetInt32());
            if (w is < 1 or > 16384 || h is < 1 or > 16384 || rect.X < 0 || rect.Y < 0 || rect.Width < 1 || rect.Height < 1
                || rect.Right > w || rect.Bottom > h)
            {
                words = string.Create(CultureInfo.InvariantCulture, $"a PMF1 roi {rect} outside its {w}x{h} raster");
                return false;
            }
            int? cue = r.TryGetProperty("cue", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetInt32() : null;
            var frames = r.TryGetProperty("frames", out var f) && f.ValueKind == JsonValueKind.Number ? f.GetInt32() : 1;
            if (frames is < 1 or > 64)
            {
                words = string.Create(CultureInfo.InvariantCulture, $"a PMF1 frame summing {frames} pictures (1–64)");
                return false;
            }
            header = new Pmf1Header(kind, Long(r, "seq"), cue, w, h, rect, format, codec, frames, Uint(r, "crc32"), Number(r, "capture"));
            if (header.PixelBytes > MaxPixelBytes)
            {
                words = string.Create(CultureInfo.InvariantCulture, $"a PMF1 picture of {header.PixelBytes} bytes (at most {MaxPixelBytes})");
                return false;
            }
            words = "";
            return true;
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            words = "a PMF1 header that does not read: " + ex.Message;
            return false;
        }
    }

    /// <summary>The frame's pixels, inflated and checked against the header's size and CRC; false with the words otherwise.</summary>
    public static bool TryDecode(Pmf1Header header, ReadOnlySpan<byte> payload, out byte[] pixels, out string words)
    {
        pixels = [];
        var size = header.PixelBytes;
        if (size > MaxPixelBytes)
        {
            words = "the picture is larger than a PMF1 frame may be";
            return false;
        }
        var into = new byte[size];
        if (header.Codec == None)
        {
            if (payload.Length != size)
            {
                words = string.Create(CultureInfo.InvariantCulture, $"{payload.Length} bytes of pixels where the roi holds {size}");
                return false;
            }
            payload.CopyTo(into);
        }
        else
        {
            try
            {
                using var source = new MemoryStream(payload.ToArray(), writable: false);
                using var z = new DeflateStream(source, CompressionMode.Decompress);
                var read = 0;
                while (read < into.Length)
                {
                    var n = z.Read(into, read, into.Length - read);
                    if (n == 0)
                        break;
                    read += n;
                }
                // One byte more than the roi holds is a frame that lies about its size: it is not inflated further.
                if (read != into.Length || z.ReadByte() >= 0)
                {
                    words = string.Create(CultureInfo.InvariantCulture, $"the pixels inflate to other than the roi's {size} bytes");
                    return false;
                }
            }
            catch (InvalidDataException ex)
            {
                words = "the pixels do not inflate: " + ex.Message;
                return false;
            }
        }
        var crc = Crc32.Of(into);
        if (crc != header.Crc32)
        {
            words = string.Create(CultureInfo.InvariantCulture, $"the pixels' CRC-32 is {crc:x8}, the header says {header.Crc32:x8}");
            return false;
        }
        pixels = into;
        words = "";
        return true;
    }

    private static string Text(JsonElement r, string name) =>
        r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static int Int(JsonElement r, string name) => r.GetProperty(name).GetInt32();

    private static long Long(JsonElement r, string name) => r.GetProperty(name).GetInt64();

    private static uint Uint(JsonElement r, string name) => r.GetProperty(name).GetUInt32();

    private static double Number(JsonElement r, string name) =>
        r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : double.NaN;

    /// <summary>The header in words for a log.</summary>
    public static string Describe(Pmf1Header h) =>
        string.Create(CultureInfo.InvariantCulture, $"{h.Kind} #{h.Seq} {h.Width}x{h.Height} roi {h.Roi} {h.Format}/{h.Codec}");

}
