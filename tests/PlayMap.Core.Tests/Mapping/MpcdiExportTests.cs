using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using PlayMap.Core.Mapping;
using PlayMap.Core.Model;
using PlayMap.Core.Numerics;

namespace PlayMap.Core.Tests.Mapping;

/// <summary>The export package: a 16-bit PNG whose chunks check and whose samples read back, a PFM laid out
/// bottom row first, a warp file holding the inverse warp, and the XML naming every file.</summary>
public class MpcdiExportTests
{
    [Fact]
    public void ASixteenBitPngReadsBackSampleForSampleAndEveryChunkChecks()
    {
        const int w = 7, h = 5;
        var values = Enumerable.Range(0, w * h).Select(i => i / (float)(w * h - 1)).ToArray();
        var png = PngWriter.Gray16(values, w, h);
        Assert.Equal([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A], png[..8]);
        var idat = new MemoryStream();
        var o = 8;
        var types = new List<string>();
        while (o < png.Length)
        {
            var len = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(o));
            var type = Encoding.ASCII.GetString(png, o + 4, 4);
            types.Add(type);
            var data = png.AsSpan(o + 8, len);
            var crc = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(o + 8 + len));
            Assert.Equal(crc, PngWriter.Crc32(PngWriter.Crc32(0xFFFFFFFFu, png.AsSpan(o + 4, 4)), data) ^ 0xFFFFFFFFu);
            if (type == "IHDR")
            {
                Assert.Equal(w, BinaryPrimitives.ReadInt32BigEndian(data));
                Assert.Equal(h, BinaryPrimitives.ReadInt32BigEndian(data[4..]));
                Assert.Equal(16, data[8]);
                Assert.Equal(0, data[9]);
            }
            if (type == "IDAT")
                idat.Write(data);
            o += 12 + len;
        }
        Assert.Equal(new[] { "IHDR", "IDAT", "IEND" }, types);
        idat.Position = 0;
        using var z = new ZLibStream(idat, CompressionMode.Decompress);
        var raw = new MemoryStream();
        z.CopyTo(raw);
        var bytes = raw.ToArray();
        Assert.Equal(h * (1 + w * 2), bytes.Length);
        for (var y = 0; y < h; y++)
        {
            Assert.Equal(0, bytes[y * (1 + w * 2)]);
            for (var x = 0; x < w; x++)
            {
                var s = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(y * (1 + w * 2) + 1 + x * 2));
                Assert.Equal((ushort)Math.Round(values[y * w + x] * 65535.0), s);
            }
        }
        // The CRC of the standard check string.
        Assert.Equal(0xCBF43926u, PngWriter.Crc32(0xFFFFFFFFu, "123456789"u8) ^ 0xFFFFFFFFu);
    }

    [Fact]
    public void APfmStoresItsBottomRowFirstAfterALittleEndianHeader()
    {
        var rgb = new float[2 * 2 * 3];
        for (var i = 0; i < rgb.Length; i++)
            rgb[i] = i;
        var pfm = PfmWriter.Rgb(rgb, 2, 2);
        var header = "PF\n2 2\n-1.0\n";
        Assert.Equal(header, Encoding.ASCII.GetString(pfm, 0, header.Length));
        // The first stored pixel is the bottom-left: row 1, values 6, 7, 8.
        Assert.Equal(6f, BinaryPrimitives.ReadSingleLittleEndian(pfm.AsSpan(header.Length)));
        Assert.Equal(0f, BinaryPrimitives.ReadSingleLittleEndian(pfm.AsSpan(header.Length + 6 * 4)));
    }

    [Fact]
    public void ThePackageHoldsTheInverseWarpTheMapsAndTheXmlThatNamesThem()
    {
        var pin = new CornerPin(new Vec2d(0.1, 0.05), Vec2d.Zero, new Vec2d(-0.05, 0), Vec2d.Zero);
        var output = BlendSolver3DTests.OutputFor("left", "p") with { Warp = new OutputWarp(true, pin, null) };
        var blend = BlendMath.EdgeBlendMap("left", 64, 36, new BlendSettings(false, new EdgeBlend(0, 0, 400, 0), BlendCurve.Cosine, 2.2, 0.01, false), 1920, 1080);
        var files = MpcdiExport.Package(output, blend, new DateTimeOffset(2026, 10, 12, 20, 0, 0, TimeSpan.Zero), 32, 18);
        Assert.Equal(new[] { "left_alpha.png", "left_beta.png", "left_warp.pfm", "mpcdi.xml" }, files.Keys.Order(StringComparer.Ordinal));

        // The warp file: a sample inside the pinned quad holds the content point that lands there.
        var pfm = files["left_warp.pfm"];
        var header = "PF\n32 18\n-1.0\n";
        Assert.Equal(header, Encoding.ASCII.GetString(pfm, 0, header.Length));
        var field = new WarpField(output.Warp);
        const int sx = 16, sy = 9;
        var stored = header.Length + ((18 - 1 - sy) * 32 + sx) * 12;
        var u = BinaryPrimitives.ReadSingleLittleEndian(pfm.AsSpan(stored));
        var v = BinaryPrimitives.ReadSingleLittleEndian(pfm.AsSpan(stored + 4));
        var shown = field.Evaluate(u, v);
        Assert.Equal((sx + 0.5) / 32, shown.X, 1e-5);
        Assert.Equal((sy + 0.5) / 18, shown.Y, 1e-5);
        // The top-left sample is outside the pinned quad: no content lands there.
        var corner = header.Length + (18 - 1) * 32 * 12;
        Assert.True(float.IsNaN(BinaryPrimitives.ReadSingleLittleEndian(pfm.AsSpan(corner))));

        var xml = XDocument.Parse(Encoding.UTF8.GetString(files["mpcdi.xml"]));
        var root = xml.Root!;
        Assert.Equal("MPCDI", root.Name.LocalName);
        Assert.Equal("2026-10-12 20:00:00", root.Attribute("date")!.Value);
        Assert.Equal("1920", root.Descendants("buffer").Single().Attribute("Xresolution")!.Value);
        Assert.Equal("left_warp.pfm", root.Descendants("geometryWarpFile").Single().Element("path")!.Value);
        Assert.Equal("2.2", root.Descendants("alphaMap").Single().Element("gammaEmbedded")!.Value);
        Assert.Equal("left_beta.png", root.Descendants("betaMap").Single().Element("path")!.Value);
        Assert.Equal(32, int.Parse(root.Descendants("geometryWarpFile").Single().Element("xSamples")!.Value, CultureInfo.InvariantCulture));
    }
}
