using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace PlayMap.Core.Align;

/// <summary>A float map as a portable float map file (the format MPCDI carries warps in): "PF" (three channels) or "Pf" (one), the
/// size, the scale (negative: little-endian), then the rows bottom-up. In memory the values are rows top-down, channels interleaved.</summary>
public sealed record FloatMap(int Width, int Height, int Channels, float[] Values)
{
    public float At(int x, int y, int channel) => Values[(y * Width + x) * Channels + channel];
}

/// <summary>Reads and writes portable float maps, and names a file's content by its hash.</summary>
public static class Pfm
{
    /// <summary>The file's bytes: header, then the rows bottom-up as little-endian float32.</summary>
    public static byte[] Write(FloatMap map)
    {
        ArgumentNullException.ThrowIfNull(map);
        if (map.Channels is not (1 or 3))
            throw new ArgumentException("A PFM carries one or three channels.", nameof(map));
        if (map.Width < 1 || map.Height < 1 || map.Values.Length != map.Width * map.Height * map.Channels)
            throw new ArgumentException("The values do not fill the map.", nameof(map));
        var header = Encoding.ASCII.GetBytes(string.Create(CultureInfo.InvariantCulture,
            $"{(map.Channels == 3 ? "PF" : "Pf")}\n{map.Width} {map.Height}\n-1.0\n"));
        var row = map.Width * map.Channels;
        var bytes = new byte[header.Length + map.Values.Length * 4];
        header.CopyTo(bytes, 0);
        var at = header.Length;
        for (var y = map.Height - 1; y >= 0; y--)
        {
            for (var i = 0; i < row; i++)
            {
                BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(at, 4), map.Values[y * row + i]);
                at += 4;
            }
        }
        return bytes;
    }

    /// <summary>A map from a file's bytes (either byte order); throws <see cref="InvalidDataException"/> with words for anything
    /// else.</summary>
    public static FloatMap Read(ReadOnlySpan<byte> bytes)
    {
        var pos = 0;
        var magic = Token(bytes, ref pos);
        var channels = magic switch
        {
            "PF" => 3,
            "Pf" => 1,
            _ => throw new InvalidDataException("not a portable float map"),
        };
        if (!int.TryParse(Token(bytes, ref pos), NumberStyles.Integer, CultureInfo.InvariantCulture, out var w)
            || !int.TryParse(Token(bytes, ref pos), NumberStyles.Integer, CultureInfo.InvariantCulture, out var h)
            || !double.TryParse(Token(bytes, ref pos), NumberStyles.Float, CultureInfo.InvariantCulture, out var scale)
            || w < 1 || h < 1 || w > 16384 || h > 16384 || scale == 0)
            throw new InvalidDataException("the float map's header is not a size and a scale");
        pos++; // the single whitespace after the scale
        var row = w * channels;
        if (bytes.Length - pos < (long)row * h * 4)
            throw new InvalidDataException("the float map is shorter than its size");
        var values = new float[row * h];
        var little = scale < 0;
        for (var y = h - 1; y >= 0; y--)
        {
            for (var i = 0; i < row; i++)
            {
                var span = bytes.Slice(pos, 4);
                values[y * row + i] = little ? BinaryPrimitives.ReadSingleLittleEndian(span) : BinaryPrimitives.ReadSingleBigEndian(span);
                pos += 4;
            }
        }
        return new FloatMap(w, h, channels, values);
    }

    /// <summary>The content's name: the first 16 hex digits of its SHA-256.</summary>
    public static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes))[..16];

    private static string Token(ReadOnlySpan<byte> bytes, ref int pos)
    {
        while (pos < bytes.Length && IsSpace(bytes[pos]))
            pos++;
        var start = pos;
        while (pos < bytes.Length && !IsSpace(bytes[pos]) && pos - start < 32)
            pos++;
        return Encoding.ASCII.GetString(bytes[start..pos]);
    }

    private static bool IsSpace(byte b) => b is (byte)' ' or (byte)'\n' or (byte)'\r' or (byte)'\t';
}
