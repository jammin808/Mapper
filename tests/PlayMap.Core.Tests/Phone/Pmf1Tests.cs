using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using PlayMap.Core.Numerics;
using PlayMap.Core.Phone;
using PlayMap.Core.Scan;

namespace PlayMap.Core.Tests.Phone;

/// <summary>The camera page's binary frame: what goes in comes out bit-identical, checked by its CRC; a frame that lies about its
/// size, its ROI, its checksum or its header is refused with words and never half-read.</summary>
public sealed class Pmf1Tests
{
    private static byte[] Noise(int n, ulong seed)
    {
        var rng = new Pcg32(seed);
        var b = new byte[n];
        for (var i = 0; i < n; i++)
            b[i] = (byte)(rng.NextUInt() & 0xFF);
        return b;
    }

    private static Pmf1Header Header(int w, int h, PixelRect roi, string format = Pmf1.Gray8, string codec = Pmf1.Deflate) =>
        new("pattern", 41, 7, w, h, roi, format, codec, 1, 0, 1234.5);

    [Theory]
    [InlineData(Pmf1.Gray8, Pmf1.Deflate)]
    [InlineData(Pmf1.Gray8, Pmf1.None)]
    [InlineData(Pmf1.Gray16, Pmf1.Deflate)]
    public void AFrameRoundTripsBitIdenticalWithItsHeader(string format, string codec)
    {
        var roi = new PixelRect(16, 8, 300, 200);
        var header = Header(320, 240, roi, format, codec);
        var pixels = Noise((int)header.PixelBytes, 9);
        var message = Pmf1.Write(header, pixels);

        Assert.True(Pmf1.TryParse(message, out var read, out var payload, out var words), words);
        Assert.Equal("pattern", read.Kind);
        Assert.Equal(41, read.Seq);
        Assert.Equal(7, read.Cue);
        Assert.Equal(roi, read.Roi);
        Assert.Equal(format, read.Format);
        Assert.Equal(Crc32.Of(pixels), read.Crc32);
        Assert.Equal(1234.5, read.Capture);
        Assert.True(Pmf1.TryDecode(read, payload, out var back, out words), words);
        Assert.Equal(pixels, back);
    }

    [Fact]
    public void Crc32IsTheStandardOne() =>
        // The check value of CRC-32/ISO-HDLC.
        Assert.Equal(0xCBF43926u, Crc32.Of(Encoding.ASCII.GetBytes("123456789")));

    [Fact]
    public void PixelsThatDoNotMatchTheirCrcAreRefused()
    {
        var header = Header(64, 48, new PixelRect(0, 0, 64, 48), codec: Pmf1.None);
        var pixels = Noise(64 * 48, 3);
        var message = Pmf1.Write(header, pixels);
        message[^10] ^= 0x01; // one bit of one pixel on the way
        Assert.True(Pmf1.TryParse(message, out var read, out var payload, out _));
        Assert.False(Pmf1.TryDecode(read, payload, out var none, out var words));
        Assert.Empty(none);
        Assert.Contains("CRC-32", words, StringComparison.Ordinal);
    }

    [Fact]
    public void AFrameThatInflatesBeyondItsRoiIsRefusedWithoutInflatingIt()
    {
        var header = Header(64, 48, new PixelRect(0, 0, 64, 48));
        var big = new byte[64 * 48 * 50];
        using var ms = new MemoryStream();
        using (var z = new DeflateStream(ms, CompressionLevel.Fastest, leaveOpen: true))
            z.Write(big);
        var json = Encoding.UTF8.GetBytes("{\"kind\":\"pattern\",\"seq\":1,\"capture\":0,\"w\":64,\"h\":48,\"roi\":[0,0,64,48],\"format\":\"gray8\",\"codec\":\"deflate-raw\",\"frames\":1,\"crc32\":0}");
        var message = Frame(json, ms.ToArray());
        Assert.True(Pmf1.TryParse(message, out var read, out var payload, out _));
        Assert.Equal(header.PixelBytes, read.PixelBytes);
        Assert.False(Pmf1.TryDecode(read, payload, out _, out var words));
        Assert.Contains("inflate to other than", words, StringComparison.Ordinal);
    }

    private static byte[] Frame(byte[] json, byte[] payload)
    {
        var m = new byte[8 + json.Length + payload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(m, Pmf1.Magic);
        BinaryPrimitives.WriteUInt32LittleEndian(m.AsSpan(4), (uint)json.Length);
        json.CopyTo(m, 8);
        payload.CopyTo(m, 8 + json.Length);
        return m;
    }

    [Theory]
    [InlineData("{\"kind\":\"pattern\",\"seq\":1,\"w\":64,\"h\":48,\"roi\":[0,0,65,48],\"format\":\"gray8\",\"codec\":\"none\",\"crc32\":0}", "outside")]
    [InlineData("{\"kind\":\"pattern\",\"seq\":1,\"w\":64,\"h\":48,\"roi\":[0,0,64,48],\"format\":\"rgb\",\"codec\":\"none\",\"crc32\":0}", "format")]
    [InlineData("{\"kind\":\"pattern\",\"seq\":1,\"w\":64,\"h\":48,\"roi\":[0,0,64,48],\"format\":\"gray8\",\"codec\":\"zip\",\"crc32\":0}", "coding")]
    [InlineData("{\"kind\":\"selfie\",\"seq\":1,\"w\":64,\"h\":48,\"roi\":[0,0,64,48],\"format\":\"gray8\",\"codec\":\"none\",\"crc32\":0}", "kind")]
    [InlineData("{\"kind\":\"pattern\",\"w\":64,\"h\":48,\"roi\":[0,0,64,48],\"format\":\"gray8\",\"codec\":\"none\",\"crc32\":0}", "does not read")]
    [InlineData("[1,2,3]", "JSON object")]
    public void AHeaderThatIsNotWholeIsRefusedWithWords(string json, string words)
    {
        var message = Frame(Encoding.UTF8.GetBytes(json), new byte[64 * 48]);
        Assert.False(Pmf1.TryParse(message, out _, out _, out var said));
        Assert.Contains(words, said, StringComparison.Ordinal);
    }

    [Fact]
    public void TheMagicAndTheHeaderLengthAreChecked()
    {
        Assert.False(Pmf1.TryParse("PMF2...."u8, out _, out _, out var w1));
        Assert.Equal("not a PMF1 frame", w1);
        var m = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(m, Pmf1.Magic);
        BinaryPrimitives.WriteUInt32LittleEndian(m.AsSpan(4), 5000);
        Assert.False(Pmf1.TryParse(m, out _, out _, out var w2));
        Assert.Contains("at most 4096", w2, StringComparison.Ordinal);
    }
}
