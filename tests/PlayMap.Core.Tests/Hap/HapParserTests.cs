using System.Buffers.Binary;
using PlayMap.Core.Media;
using PlayMap.Core.Media.Hap;
using PlayMap.Tests;

namespace PlayMap.Core.Tests.Hap;

/// <summary>The HAP frame format (HAP specification, "Hap Video Codec Specification", sections "Frames" and "Decode
/// Instructions"): every codec parses from writer-made frames, the section-type table is the specification's, malformed
/// frames are refused with words, and writer → parser round trips are byte-exact.</summary>
public class HapParserTests
{
    /// <summary>A literal-only Snappy stream (the format's preamble, then literal elements): valid input to any Snappy
    /// decoder, made without one.</summary>
    internal static byte[] SnappyLiteral(ReadOnlySpan<byte> data)
    {
        var o = new List<byte>();
        var n = (uint)data.Length;
        while (n >= 0x80)
        {
            o.Add((byte)(n | 0x80));
            n >>= 7;
        }
        o.Add((byte)n);
        for (var at = 0; at < data.Length; at += 65536)
        {
            var len = Math.Min(65536, data.Length - at);
            if (len <= 60)
            {
                o.Add((byte)((len - 1) << 2));
            }
            else if (len <= 256)
            {
                o.Add(60 << 2);
                o.Add((byte)(len - 1));
            }
            else
            {
                o.Add(61 << 2);
                o.Add((byte)(len - 1));
                o.Add((byte)((len - 1) >> 8));
            }
            o.AddRange(data.Slice(at, len));
        }
        return [.. o];
    }

    internal static byte[] Plane(int width, int height, int blockBytes, int seed)
    {
        var p = new byte[HapCodecs.PlaneBytes(width, height, blockBytes)];
        var x = (uint)(seed * 2654435761u + 1);
        for (var i = 0; i < p.Length; i++)
        {
            x ^= x << 13;
            x ^= x >> 17;
            x ^= x << 5;
            p[i] = (byte)x;
        }
        return p;
    }

    /// <summary>A frame of one or two planes, cut into <paramref name="chunks"/> chunks each, stored raw or as Snappy.</summary>
    internal static byte[] Frame(HapCodec codec, int width, int height, int chunks, bool snappy, out byte[][] planes, HapTexture? first = null)
    {
        var kinds = codec == HapCodec.HapQAlpha
            ? new[] { HapTexture.Bc3YCoCg, HapTexture.Bc4Alpha }
            : [first ?? HapCodecs.FirstTexture(codec)];
        planes = new byte[kinds.Length][];
        var outs = new HapChunkOut[kinds.Length][];
        var datas = new byte[kinds.Length][];
        for (var t = 0; t < kinds.Length; t++)
        {
            var bb = HapCodecs.BlockBytes(kinds[t]);
            planes[t] = Plane(width, height, bb, t + 7);
            var plan = new HapChunkOut[HapFrame.MaxChunks];
            var count = HapChunking.Plan(planes[t].Length / bb, bb, chunks, plan);
            if (!snappy)
            {
                outs[t] = plan[..count];
                datas[t] = planes[t];
                continue;
            }
            var data = new List<byte>();
            outs[t] = new HapChunkOut[count];
            for (var i = 0; i < count; i++)
            {
                var z = SnappyLiteral(planes[t].AsSpan(plan[i].Offset, plan[i].Length));
                outs[t][i] = new HapChunkOut(data.Count, z.Length, HapCompressor.Snappy);
                data.AddRange(z);
            }
            datas[t] = [.. data];
        }
        var frame = new byte[HapFrameWriter.Measure(outs[0], kinds.Length > 1 ? outs[1] : default)];
        var written = kinds.Length > 1
            ? HapFrameWriter.Write(frame, kinds[0], outs[0], datas[0], kinds[1], outs[1], datas[1])
            : HapFrameWriter.Write(frame, kinds[0], outs[0], datas[0]);
        Assert.Equal(frame.Length, written);
        return frame;
    }

    /// <summary>Decodes a parsed frame's texture with the literal-only Snappy reader the tests own.</summary>
    internal static byte[] Unpack(HapFrame f, ReadOnlySpan<byte> packet, int texture)
    {
        var info = f.Texture(texture);
        var plane = new byte[info.Bytes];
        foreach (var c in f.Chunks(texture))
        {
            var src = packet.Slice(c.SourceOffset, c.SourceLength);
            if (c.Compressor == HapCompressor.None)
            {
                src.CopyTo(plane.AsSpan(c.TargetOffset));
                continue;
            }
            Assert.True(SnappyLength.TryRead(src, out var len, out var pre));
            var at = pre;
            var to = c.TargetOffset;
            while (at < src.Length)
            {
                var tag = src[at++];
                Assert.Equal(0, tag & 3);
                var l = (tag >> 2) + 1;
                if (l == 61)
                    l = src[at++] + 1;
                else if (l == 62)
                {
                    l = (src[at] | (src[at + 1] << 8)) + 1;
                    at += 2;
                }
                src.Slice(at, l).CopyTo(plane.AsSpan(to));
                at += l;
                to += l;
            }
            Assert.Equal(c.TargetOffset + (int)len, to);
        }
        return plane;
    }

    public static TheoryData<HapCodec, int, bool> Variants()
    {
        var d = new TheoryData<HapCodec, int, bool>();
        foreach (var codec in Enum.GetValues<HapCodec>())
            foreach (var chunks in new[] { 1, 4 })
                foreach (var snappy in new[] { false, true })
                    d.Add(codec, chunks, snappy);
        return d;
    }

    [Theory]
    [MemberData(nameof(Variants))]
    public void EveryCodecParsesAndRoundTripsByteExact(HapCodec codec, int chunks, bool snappy)
    {
        const int w = 70, h = 38;   // odd sizes: 18×10 blocks, padding in both directions
        var frame = Frame(codec, w, h, chunks, snappy, out var planes);
        var f = new HapFrame();
        Assert.True(f.TryParse(frame, w, h, codec, out var refusal), f.Words(1));
        Assert.Equal(HapRefusal.None, refusal);
        Assert.Equal(planes.Length, f.TextureCount);
        Assert.Equal(frame.Length, f.PacketBytes);
        for (var t = 0; t < planes.Length; t++)
        {
            var info = f.Texture(t);
            Assert.Equal(planes[t].Length, info.Bytes);
            Assert.Equal(chunks == 1 ? 1 : HapChunking.Plan(planes[t].Length / HapCodecs.BlockBytes(info.Kind), HapCodecs.BlockBytes(info.Kind), chunks, new HapChunkOut[256]), info.ChunkCount);
            Assert.Equal(planes[t], Unpack(f, frame, t));
        }
        Assert.Equal(snappy ? 0 : f.AllChunks.Length, f.RawChunks);
        Assert.Equal(HapCodecs.Layout(codec, f.Texture(0).Kind), HapCodecs.Layout(codec, HapCodecs.FirstTexture(codec)));
    }

    [Fact]
    public void HapHdrSignedFramesReadAsSignedBc6h()
    {
        var frame = Frame(HapCodec.HapHdr, 16, 16, 1, false, out _, HapTexture.Bc6hSigned);
        var f = new HapFrame();
        Assert.True(f.TryParse(frame, 16, 16, HapCodec.HapHdr, out _));
        Assert.Equal(HapTexture.Bc6hSigned, f.Texture(0).Kind);
        Assert.Equal(PixelLayout.Bc6hSfloat, HapCodecs.Layout(HapCodec.HapHdr, f.Texture(0).Kind));
        Assert.Equal(PixelLayout.Bc6hUfloat, HapCodecs.Layout(HapCodec.HapHdr, HapTexture.Bc6hUnsigned));
    }

    [Fact]
    public void HapQAlphaImagesInEitherOrderPlaneAsColourThenAlpha()
    {
        const int w = 16, h = 8;
        var colour = Plane(w, h, 16, 1);
        var alpha = Plane(w, h, 8, 2);
        HapChunkOut[] c1 = [new(0, colour.Length, HapCompressor.None)];
        HapChunkOut[] c2 = [new(0, alpha.Length, HapCompressor.None)];
        var frame = new byte[HapFrameWriter.Measure(c2, c1)];
        HapFrameWriter.Write(frame, HapTexture.Bc4Alpha, c2, alpha, HapTexture.Bc3YCoCg, c1, colour);
        var f = new HapFrame();
        Assert.True(f.TryParse(frame, w, h, HapCodec.HapQAlpha, out _), f.Words(0));
        Assert.Equal(HapTexture.Bc3YCoCg, f.Texture(0).Kind);
        Assert.Equal(HapTexture.Bc4Alpha, f.Texture(1).Kind);
        Assert.Equal(colour, Unpack(f, frame, 0));
        Assert.Equal(alpha, Unpack(f, frame, 1));
    }

    [Fact]
    public void TheSectionTypeTableIsTheSpecifications()
    {
        // The HAP specification's tables, typed here from the published text: decode instructions 0x01–0x04, multiple
        // images 0x0D, and for each texture format its type under the three second stages (0xA none, 0xB Snappy, 0xC complex).
        (byte, string)[] golden =
        [
            (0x01, "Decode Instructions Container"), (0x02, "Chunk Second-Stage Compressor Table"), (0x03, "Chunk Size Table"),
            (0x04, "Chunk Offset Table"), (0x0D, "Multiple Images"),
            (0xAB, "RGB DXT1, None"), (0xBB, "RGB DXT1, Snappy"), (0xCB, "RGB DXT1, Complex"),
            (0xAE, "RGBA DXT5, None"), (0xBE, "RGBA DXT5, Snappy"), (0xCE, "RGBA DXT5, Complex"),
            (0xAF, "Scaled YCoCg DXT5, None"), (0xBF, "Scaled YCoCg DXT5, Snappy"), (0xCF, "Scaled YCoCg DXT5, Complex"),
            (0xA1, "Alpha RGTC1, None"), (0xB1, "Alpha RGTC1, Snappy"), (0xC1, "Alpha RGTC1, Complex"),
            (0xAC, "RGBA BPTC Unorm, None"), (0xBC, "RGBA BPTC Unorm, Snappy"), (0xCC, "RGBA BPTC Unorm, Complex"),
            (0xA2, "RGB BPTC Unsigned Float, None"), (0xB2, "RGB BPTC Unsigned Float, Snappy"), (0xC2, "RGB BPTC Unsigned Float, Complex"),
            (0xA3, "RGB BPTC Signed Float, None"), (0xB3, "RGB BPTC Signed Float, Snappy"), (0xC3, "RGB BPTC Signed Float, Complex"),
        ];
        Assert.Equal(golden, HapCodecs.SectionTypes.Select(s => (s.Type, s.Meaning)).ToArray());
        Assert.Equal(0x0A, (int)HapCompressor.None);
        Assert.Equal(0x0B, (int)HapCompressor.Snappy);
    }

    [Fact]
    public void FourccsCodecsAndLayoutsAreTheSpecifications()
    {
        (string Tag, HapCodec Codec, string Name, PixelLayout Layout, int Images)[] golden =
        [
            ("Hap1", HapCodec.Hap, "hap", PixelLayout.Bc1, 1),
            ("Hap5", HapCodec.HapAlpha, "hap_alpha", PixelLayout.Bc3, 1),
            ("HapY", HapCodec.HapQ, "hap_q", PixelLayout.Bc3YCoCg, 1),
            ("HapM", HapCodec.HapQAlpha, "hap_q_alpha", PixelLayout.Bc3YCoCgBc4, 2),
            ("HapA", HapCodec.HapAlphaOnly, "hap_alpha_only", PixelLayout.Bc4, 1),
            ("Hap7", HapCodec.HapR, "hap_r", PixelLayout.Bc7, 1),
            ("HapH", HapCodec.HapHdr, "hap_hdr", PixelLayout.Bc6hUfloat, 1),
        ];
        foreach (var g in golden)
        {
            var tag = BinaryPrimitives.ReadUInt32LittleEndian(System.Text.Encoding.ASCII.GetBytes(g.Tag));
            Assert.True(HapCodecs.TryFromTag(tag, out var codec));
            Assert.Equal(g.Codec, codec);
            Assert.Equal(tag, HapCodecs.Tag(codec));
            Assert.Equal(g.Name, HapCodecs.Name(codec));
            Assert.True(HapCodecs.TryFromName(g.Name, out var back));
            Assert.Equal(codec, back);
            Assert.Equal(g.Layout, HapCodecs.Layout(codec, HapCodecs.FirstTexture(codec)));
            Assert.Equal(g.Images, HapCodecs.Images(codec));
        }
        Assert.False(HapCodecs.TryFromTag(0x31637661, out _));   // 'avc1'
        Assert.Equal(1_036_800, HapCodecs.FrameBytes(HapCodec.Hap, 1920, 1080));
        Assert.Equal(8_294_400, HapCodecs.FrameBytes(HapCodec.HapQ, 3840, 2160));
        Assert.Equal(8_294_400 + 4_147_200, HapCodecs.FrameBytes(HapCodec.HapQAlpha, 3840, 2160));
        Assert.True(HapCodecs.FfmpegDecodes(HapCodec.HapQAlpha));
        Assert.False(HapCodecs.FfmpegDecodes(HapCodec.HapR));
        Assert.False(HapCodecs.FfmpegDecodes(HapCodec.HapHdr));
        Assert.Equal("BPTC", HapCodecs.Missing(HapCodec.HapR, BlockFormats.Bc1 | BlockFormats.Bc3 | BlockFormats.Bc4));
        Assert.Equal("RGTC", HapCodecs.Missing(HapCodec.HapQAlpha, BlockFormats.Bc1 | BlockFormats.Bc3));
        Assert.Equal("", HapCodecs.Missing(HapCodec.HapQ, BlockFormats.Bc3));
    }

    [Fact]
    public void TruncatedFramesAreRefusedWithTheBytesMissing()
    {
        var frame = Frame(HapCodec.HapQ, 32, 16, 1, false, out _);
        var f = new HapFrame();
        Assert.False(f.TryParse(frame.AsSpan(0, frame.Length - 3), 32, 16, HapCodec.HapQ, out var refusal));
        Assert.Equal(HapRefusal.Truncated, refusal);
        Assert.Equal(0, f.TextureCount);
        Assert.Equal("frame 812 is 3 bytes shorter than its header says", f.Words(812));
        Assert.False(f.TryParse(frame.AsSpan(0, 2), 32, 16, HapCodec.HapQ, out refusal));
        Assert.Equal(HapRefusal.Truncated, refusal);
        Assert.False(f.TryParse([], 32, 16, HapCodec.HapQ, out refusal));
        Assert.Equal(HapRefusal.Empty, refusal);
        Assert.Equal("frame 3 is empty", f.Words(3));
    }

    [Fact]
    public void AFrameOfTheWrongSizeOrCodecIsRefusedWithWords()
    {
        var frame = Frame(HapCodec.HapQ, 3840, 2160, 1, false, out _);
        var f = new HapFrame();
        Assert.False(f.TryParse(frame, 3840, 2164, HapCodec.HapQ, out var refusal));
        Assert.Equal(HapRefusal.SizeMismatch, refusal);
        Assert.Equal("frame 812 decodes to 8,294,400 bytes; 3840×2164 YCoCg BC3 needs 8,309,760", f.Words(812));

        var bc7 = Frame(HapCodec.HapR, 16, 16, 1, false, out _);
        Assert.False(f.TryParse(bc7, 16, 16, HapCodec.HapQ, out refusal));
        Assert.Equal(HapRefusal.NotThisCodec, refusal);
        Assert.Equal("frame 812 holds BC7 blocks but the file says HAP Q", f.Words(812));

        var two = Frame(HapCodec.HapQAlpha, 16, 16, 1, false, out _);
        Assert.False(f.TryParse(two, 16, 16, HapCodec.HapQ, out refusal));
        Assert.Equal(HapRefusal.NotThisCodec, refusal);
        Assert.Equal("frame 9 holds two images but the file says HAP Q", f.Words(9));
        Assert.False(f.TryParse(Frame(HapCodec.HapQ, 16, 16, 1, false, out _), 16, 16, HapCodec.HapQAlpha, out refusal));
        Assert.Equal(HapRefusal.NotThisCodec, refusal);

        var unknown = (byte[])bc7.Clone();
        unknown[3] = 0xA5;
        Assert.False(f.TryParse(unknown, 16, 16, HapCodec.HapR, out refusal));
        Assert.Equal(HapRefusal.UnknownTexture, refusal);
        Assert.Contains("texture format 0x5", f.Words(1), StringComparison.Ordinal);
        unknown[3] = 0x02;
        Assert.False(f.TryParse(unknown, 16, 16, HapCodec.HapR, out refusal));
        Assert.Equal(HapRefusal.BadHeader, refusal);
    }

    [Fact]
    public void BadChunkTablesAreRefusedWithWords()
    {
        const int w = 64, h = 16;
        var frame = Frame(HapCodec.Hap, w, h, 4, false, out _);
        var f = new HapFrame();
        Assert.True(f.TryParse(frame, w, h, HapCodec.Hap, out _));
        // Layout of a 4-chunk frame: image header (4), container header (4), compressor table header (4) + 4 bytes,
        // size table header (4) + 16 bytes, then the data.
        var compressorAt = 4 + 4 + 4;
        var sizeAt = compressorAt + 4 + 4;

        var gdeflate = (byte[])frame.Clone();
        gdeflate[compressorAt + 2] = 0xD0;
        Assert.False(f.TryParse(gdeflate, w, h, HapCodec.Hap, out var refusal));
        Assert.Equal(HapRefusal.NonStandardCompressor, refusal);
        Assert.Equal("frame 812 uses compressor 0xD0 (GDeflate), which is not part of the HAP spec", f.Words(812));

        var past = (byte[])frame.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(past.AsSpan(sizeAt + 12), 100_000);
        Assert.False(f.TryParse(past, w, h, HapCodec.Hap, out refusal));
        Assert.Equal(HapRefusal.ChunkOutOfBounds, refusal);
        Assert.Equal("frame 2's chunk 3 runs past the end of the frame", f.Words(2));

        var shortSizes = (byte[])frame.Clone();
        shortSizes[sizeAt - 4] = 12;   // the size table says three entries while the compressor table has four
        Assert.False(f.TryParse(shortSizes, w, h, HapCodec.Hap, out refusal));
        Assert.True(refusal is HapRefusal.ChunkTables or HapRefusal.Truncated, refusal.ToString());

        var moved = (byte[])frame.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(moved.AsSpan(sizeAt + 4), 200);   // chunks no longer add up to the texture
        Assert.False(f.TryParse(moved, w, h, HapCodec.Hap, out refusal));
        Assert.True(refusal is HapRefusal.SizeMismatch or HapRefusal.ChunkOutOfBounds, refusal.ToString());
        Assert.NotEqual("", f.Words(5));
    }

    [Fact]
    public void SnappyLengthsThatOverrunTheTextureAreRefused()
    {
        var frame = Frame(HapCodec.Hap, 16, 16, 1, true, out _);
        var f = new HapFrame();
        Assert.True(f.TryParse(frame, 16, 16, HapCodec.Hap, out _));
        var over = (byte[])frame.Clone();
        over[4] = (byte)(over[4] + 8);   // the preamble now says one block more than the picture holds
        Assert.False(f.TryParse(over, 16, 16, HapCodec.Hap, out var refusal));
        Assert.Equal(HapRefusal.SizeMismatch, refusal);
        Assert.Equal("frame 4 decodes to 136 bytes; 16×16 BC1 needs 128", f.Words(4));

        var noVarint = (byte[])frame.Clone();
        for (var i = 4; i < 9; i++)
            noVarint[i] = 0xFF;
        Assert.False(f.TryParse(noVarint, 16, 16, HapCodec.Hap, out refusal));
        Assert.Equal(HapRefusal.BadSnappyLength, refusal);
        Assert.Equal("frame 4's chunk 0 does not start with a Snappy length", f.Words(4));
    }

    [Fact]
    public void LongHeadersAndUnknownSubSectionsAreRead()
    {
        // FFmpeg's encoder writes every header in the eight-byte form; the parser reads either.
        var plane = Plane(8, 8, 8, 3);
        var frame = new byte[8 + plane.Length];
        frame[3] = 0xAB;
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(4), (uint)plane.Length);
        plane.CopyTo(frame, 8);
        var f = new HapFrame();
        Assert.True(f.TryParse(frame, 8, 8, HapCodec.Hap, out _));
        Assert.Equal(plane, Unpack(f, frame, 0));

        // A container with an offset table and an unknown sub-section (skipped) in front of the tables.
        var data = Plane(8, 4, 8, 4);   // 2 blocks: two chunks of one block each, stored second-first
        var tables = new List<byte>();
        var body = tables;
        void Section(int size, byte type, params byte[] bytes)
        {
            body.Add((byte)size);
            body.Add((byte)(size >> 8));
            body.Add((byte)(size >> 16));
            body.Add(type);
            body.AddRange(bytes);
        }
        Section(2, 0x7E, 1, 2);
        Section(2, 0x02, 0x0A, 0x0A);
        Section(8, 0x03, 8, 0, 0, 0, 8, 0, 0, 0);
        Section(8, 0x04, 8, 0, 0, 0, 0, 0, 0, 0);
        var container = new List<byte>();
        body = container;
        Section(tables.Count, 0x01, [.. tables]);
        container.AddRange(data.AsSpan(8, 8).ToArray());
        container.AddRange(data.AsSpan(0, 8).ToArray());
        var image = new List<byte>();
        body = image;
        Section(container.Count, 0xCB, [.. container]);
        var packet = image.ToArray();
        Assert.True(f.TryParse(packet, 8, 4, HapCodec.Hap, out var refusal), f.Words(0) + refusal);
        Assert.Equal(data, Unpack(f, packet, 0));
    }

    [Fact]
    public void ChunkingFollowsTheReferenceEncodersRule()
    {
        var plan = new HapChunkOut[HapFrame.MaxChunks];
        // 3840×2160 BC3: 518 400 blocks; 16 divides it.
        Assert.Equal(16, HapChunking.Plan(518_400, 16, 16, plan));
        Assert.All(plan[..16], c => Assert.Equal(518_400 / 16 * 16, c.Length));
        // 7 blocks, 3 wanted: no divisor above 1, so three near-equal chunks (3, 2, 2 blocks).
        Assert.Equal(3, HapChunking.Plan(7, 8, 3, plan));
        Assert.Equal(new HapChunkOut[] { new(0, 24, HapCompressor.None), new(24, 16, HapCompressor.None), new(40, 16, HapCompressor.None) }, plan[..3]);
        // 12 blocks, 5 wanted: 4 divides it.
        Assert.Equal(4, HapChunking.Plan(12, 8, 5, plan));
        Assert.Equal(1, HapChunking.Plan(5, 8, 1, plan));
        Assert.Equal(2, HapChunking.Plan(2, 8, 64, plan));
    }

    [Fact]
    public void AParseAllocatesNothingOnceWarm()
    {
        var frame = Frame(HapCodec.HapQAlpha, 64, 32, 8, true, out _);
        var f = new HapFrame();
        Assert.True(f.TryParse(frame, 64, 32, HapCodec.HapQAlpha, out _));
        var before = Allocations.Start();
        for (var i = 0; i < 100; i++)
            Assert.True(f.TryParse(frame, 64, 32, HapCodec.HapQAlpha, out _));
        Assert.Equal(0, Allocations.Since(before));
    }
}
