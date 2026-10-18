using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;
using PlayMap.Core.Model;
using PlayMap.Core.Threading;

namespace PlayMap.Core.Mapping;

/// <summary>A minimal PNG encoder for 16-bit greyscale images (blend and black-level maps): one IHDR, one
/// zlib-compressed IDAT of unfiltered rows, IEND, each chunk with its CRC-32. Big-endian samples, as PNG
/// stores them.</summary>
public static class PngWriter
{
    private static readonly byte[] Signature = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly uint[] CrcTable = BuildCrcTable();

    /// <summary>A width × height 16-bit greyscale PNG of values in 0..1 (clamped; NaN reads as 0).</summary>
    public static byte[] Gray16(ReadOnlySpan<float> values, int width, int height)
    {
        if (width <= 0 || height <= 0 || values.Length < width * height)
            throw new ArgumentException("The values do not fill the image.", nameof(values));
        var raw = new byte[height * (1 + width * 2)];
        for (var y = 0; y < height; y++)
        {
            var row = y * (1 + width * 2);
            raw[row] = 0; // filter: none
            for (var x = 0; x < width; x++)
            {
                var v = values[y * width + x];
                var s = float.IsNaN(v) ? (ushort)0 : (ushort)Math.Round(Math.Clamp(v, 0f, 1f) * 65535.0);
                BinaryPrimitives.WriteUInt16BigEndian(raw.AsSpan(row + 1 + x * 2, 2), s);
            }
        }
        byte[] compressed;
        using (var ms = new MemoryStream())
        {
            using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true))
                z.Write(raw);
            compressed = ms.ToArray();
        }
        using var png = new MemoryStream();
        png.Write(Signature);
        Span<byte> ihdr = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr[4..], height);
        ihdr[8] = 16; // bit depth
        ihdr[9] = 0;  // colour type: greyscale
        ihdr[10] = 0; // compression: deflate
        ihdr[11] = 0; // filter method
        ihdr[12] = 0; // no interlace
        Chunk(png, "IHDR", ihdr);
        Chunk(png, "IDAT", compressed);
        Chunk(png, "IEND", []);
        return png.ToArray();
    }

    private static void Chunk(Stream s, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(len, data.Length);
        s.Write(len);
        Span<byte> typeBytes = stackalloc byte[4];
        Encoding.ASCII.GetBytes(type, typeBytes);
        s.Write(typeBytes);
        s.Write(data);
        var crc = Crc32(Crc32(0xFFFFFFFFu, typeBytes), data) ^ 0xFFFFFFFFu;
        BinaryPrimitives.WriteUInt32BigEndian(len, crc);
        s.Write(len);
    }

    /// <summary>The PNG/zlib CRC-32 (reflected polynomial 0xEDB88320) continued over <paramref name="data"/>
    /// from register <paramref name="crc"/>; start at 0xFFFFFFFF and xor the end with it.</summary>
    public static uint Crc32(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (var b in data)
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc;
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (var n = 0u; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }
}

/// <summary>Portable float maps: "PF" (three channels), little-endian (scale −1), rows stored bottom to top as
/// the format orders them.</summary>
public static class PfmWriter
{
    /// <summary>A width × height three-channel PFM from row-major, top-first RGB triples.</summary>
    public static byte[] Rgb(ReadOnlySpan<float> rgb, int width, int height)
    {
        if (width <= 0 || height <= 0 || rgb.Length < width * height * 3)
            throw new ArgumentException("The values do not fill the image.", nameof(rgb));
        var header = Encoding.ASCII.GetBytes(string.Create(CultureInfo.InvariantCulture, $"PF\n{width} {height}\n-1.0\n"));
        var bytes = new byte[header.Length + width * height * 12];
        header.CopyTo(bytes, 0);
        var o = header.Length;
        for (var y = height - 1; y >= 0; y--)
            for (var i = 0; i < width * 3; i++)
            {
                BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(o, 4), rgb[y * width * 3 + i]);
                o += 4;
            }
        return bytes;
    }
}

/// <summary>One output's calibration as an MPCDI-style 2D package (in memory, file name → bytes, for the
/// caller to write or zip): the geometry warp as a PFM whose pixel (sampled across the output) holds the
/// content point it shows (normalised, NaN where no content lands), the blend map as a 16-bit alpha PNG
/// with its gamma embedded, the black pedestal as a 16-bit beta PNG, and an <c>mpcdi.xml</c> naming them.
/// The layout follows the 2D-media profile as media servers commonly read it; confirm against the target
/// reader before a show relies on it.</summary>
public static class MpcdiExport
{
    /// <summary>The package for one output.</summary>
    public static IReadOnlyDictionary<string, byte[]> Package(Output output, BlendMap? blend, DateTimeOffset date,
        int warpSamplesX = 128, int warpSamplesY = 72, PinWarp? pins = null)
    {
        ArgumentNullException.ThrowIfNull(output);
        var region = SafeName(output.Id);
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        warpSamplesX = Math.Clamp(warpSamplesX, 2, 4096);
        warpSamplesY = Math.Clamp(warpSamplesY, 2, 4096);
        files[$"{region}_warp.pfm"] = WarpPfm(output.Warp, warpSamplesX, warpSamplesY, pins);
        var gamma = Math.Clamp(output.DrawnBlendGamma(), BlendMath.MinGamma, BlendMath.MaxGamma);
        if (blend is not null)
        {
            files[$"{region}_alpha.png"] = PngWriter.Gray16(blend.Weights, blend.Width, blend.Height);
            files[$"{region}_beta.png"] = PngWriter.Gray16(blend.Pedestal, blend.Width, blend.Height);
        }
        files["mpcdi.xml"] = Xml(output, region, blend, gamma, date, warpSamplesX, warpSamplesY);
        return files;
    }

    /// <summary>The inverse warp sampled at pixel centres across the output: for each sample the content
    /// point (u, v) the warp puts there, in the R and G channels (B = 0); NaN where none does.</summary>
    public static byte[] WarpPfm(OutputWarp warp, int samplesX, int samplesY, PinWarp? pins = null)
    {
        ArgumentNullException.ThrowIfNull(warp);
        var field = new WarpField(warp, pins);
        var rgb = new float[samplesX * samplesY * 3];
        Parallel.For(0, samplesY, Compute.Options, y =>
        {
            for (var x = 0; x < samplesX; x++)
            {
                var k = (y * samplesX + x) * 3;
                var uv = field.Invert((x + 0.5) / samplesX, (y + 0.5) / samplesY);
                if (uv is { } p && p.X >= -1e-9 && p.Y >= -1e-9 && p.X <= 1 + 1e-9 && p.Y <= 1 + 1e-9)
                {
                    rgb[k] = (float)p.X;
                    rgb[k + 1] = (float)p.Y;
                }
                else
                {
                    rgb[k] = float.NaN;
                    rgb[k + 1] = float.NaN;
                }
            }
        });
        return PfmWriter.Rgb(rgb, samplesX, samplesY);
    }

    private static byte[] Xml(Output output, string region, BlendMap? blend, double gamma, DateTimeOffset date, int sx, int sy)
    {
        var settings = new XmlWriterSettings { Indent = true, Encoding = new UTF8Encoding(false) };
        using var ms = new MemoryStream();
        using (var w = XmlWriter.Create(ms, settings))
        {
            string I(double v) => v.ToString("0.######", CultureInfo.InvariantCulture);
            string N(int v) => v.ToString(CultureInfo.InvariantCulture);
            w.WriteStartDocument();
            w.WriteStartElement("MPCDI");
            w.WriteAttributeString("profile", "2d");
            w.WriteAttributeString("geometry", "1");
            w.WriteAttributeString("color", "1");
            w.WriteAttributeString("version", "2.0");
            w.WriteAttributeString("date", date.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
            w.WriteStartElement("display");
            w.WriteStartElement("buffer");
            w.WriteAttributeString("id", "Display");
            w.WriteAttributeString("Xresolution", N(output.Width));
            w.WriteAttributeString("Yresolution", N(output.Height));
            w.WriteStartElement("region");
            w.WriteAttributeString("id", region);
            w.WriteAttributeString("Xresolution", N(output.Width));
            w.WriteAttributeString("Yresolution", N(output.Height));
            w.WriteAttributeString("x", "0");
            w.WriteAttributeString("y", "0");
            w.WriteAttributeString("xsize", "1");
            w.WriteAttributeString("ysize", "1");
            w.WriteEndElement(); // region
            w.WriteEndElement(); // buffer
            w.WriteEndElement(); // display
            w.WriteStartElement("files");
            w.WriteStartElement("fileset");
            w.WriteAttributeString("region", region);
            w.WriteStartElement("geometryWarpFile");
            w.WriteElementString("path", $"{region}_warp.pfm");
            w.WriteElementString("interpolation", "linear");
            w.WriteElementString("componentDepth", "2");
            w.WriteElementString("bitDepth", "32");
            w.WriteElementString("xSamples", N(sx));
            w.WriteElementString("ySamples", N(sy));
            w.WriteElementString("origin", "topLeftCorner");
            w.WriteEndElement();
            if (blend is not null)
            {
                w.WriteStartElement("alphaMap");
                w.WriteElementString("path", $"{region}_alpha.png");
                w.WriteElementString("componentDepth", "1");
                w.WriteElementString("bitDepth", "16");
                w.WriteElementString("gammaEmbedded", I(gamma));
                w.WriteEndElement();
                w.WriteStartElement("betaMap");
                w.WriteElementString("path", $"{region}_beta.png");
                w.WriteElementString("componentDepth", "1");
                w.WriteElementString("bitDepth", "16");
                w.WriteEndElement();
            }
            w.WriteEndElement(); // fileset
            w.WriteEndElement(); // files
            w.WriteEndElement(); // MPCDI
            w.WriteEndDocument();
        }
        return ms.ToArray();
    }

    /// <summary>An id as a file-name-safe token (letters, digits, '-', '_').</summary>
    private static string SafeName(string id)
    {
        var sb = new StringBuilder(id.Length);
        foreach (var c in id)
            sb.Append(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '_');
        return sb.Length == 0 ? "output" : sb.ToString();
    }
}
