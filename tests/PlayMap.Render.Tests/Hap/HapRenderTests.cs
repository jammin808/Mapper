using System.Diagnostics;
using System.Runtime.InteropServices;
using BCnEncoder.Decoder;
using BCnEncoder.Encoder;
using BCnEncoder.Shared;
using CommunityToolkit.HighPerformance;
using PlayMap.Core.Media;
using PlayMap.Core.Media.Hap;
using PlayMap.Render.Gpu;
using PlayMap.Render.Hap;
using PlayMap.Render.Video;

namespace PlayMap.Render.Tests.Hap;

/// <summary>HAP on llvmpipe (Mesa exposes S3TC, RGTC and BPTC in its 3.3 core context): (a) raw planes of every block family
/// against BCnEncoder.NET's CPU decode; (b) the `hap` pass's YCoCg maths against its CPU twin; (c) whole frames FFmpeg's
/// encoder made against FFmpeg's own CPU decode, with hap.md M4's bounds; an odd picture size shows no padding texel; a
/// context lacking a family refuses with words.</summary>
[Collection("gl")]
public class HapRenderTests(GlFixture gl)
{
    private static byte[] Image(int w, int h, int seed)
    {
        var p = new byte[w * h * 4];
        var x = (uint)seed * 2654435761u + 99;
        for (var y = 0; y < h; y++)
        {
            for (var i = 0; i < w; i++)
            {
                x ^= x << 13;
                x ^= x >> 17;
                x ^= x << 5;
                var o = (y * w + i) * 4;
                p[o] = (byte)(i * 255 / Math.Max(1, w - 1));
                p[o + 1] = (byte)(y * 255 / Math.Max(1, h - 1));
                p[o + 2] = (byte)(128 + 100 * Math.Sin((i + y) / 5.0) + x % 16);
                p[o + 3] = (byte)(x % 256);
            }
        }
        return p;
    }

    private static byte[] Encode(CompressionFormat format, byte[] rgba, int w, int h)
    {
        var e = new BcEncoder(format);
        e.OutputOptions.GenerateMipMaps = false;
        e.OutputOptions.Quality = CompressionQuality.Fast;
        return e.EncodeToRawBytes(rgba, w, h, PixelFormat.Rgba32)[0];
    }

    private static ColorRgbFloat[] HdrImage(int w, int h, bool signed)
    {
        var p = new ColorRgbFloat[w * h];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
                p[y * w + x] = new ColorRgbFloat(x / (float)w * 6f - (signed ? 2f : 0f), y / (float)h * 3f, 0.25f + (x ^ y) % 7 * 0.5f);
        return p;
    }

    private static byte[] EncodeHdr(CompressionFormat format, ColorRgbFloat[] pixels, int w, int h)
    {
        var e = new BcEncoder(format);
        e.OutputOptions.GenerateMipMaps = false;
        e.OutputOptions.Quality = CompressionQuality.Fast;
        return e.EncodeToRawBytesHdr(new ReadOnlyMemory2D<ColorRgbFloat>(pixels, h, w))[0];
    }

    // Uploads one plane of blocks and runs the pass into a target of the given format; answers the target read back as floats.
    private static float[] Pass(GlDevice d, TextureFormat format, byte[] blocks, int w, int h, int mode, TextureFormat target,
        TextureFormat? secondFormat = null, byte[]? second = null, double scale = 1, int transfer = 0)
    {
        using var tex = d.CreateCompressedTexture(w, h, format);
        Upload(d, tex, blocks);
        using var tex2 = secondFormat is { } f2 ? d.CreateCompressedTexture(w, h, f2) : null;
        if (tex2 is not null)
            Upload(d, tex2, second!);
        using var rt = d.CreateRenderTarget(w, h, target, filter: TextureFilter.Nearest);
        HapPass.Draw(d, rt, tex, tex2, mode, scale, transfer);
        var px = new float[w * h * 4];
        d.ReadPixelsFloat(rt, px);
        return px;
    }

    private static void Upload(GlDevice d, GpuTexture tex, byte[] blocks)
    {
        var pin = GCHandle.Alloc(blocks, GCHandleType.Pinned);
        try
        {
            d.UploadCompressed(tex, pin.AddrOfPinnedObject(), blocks.Length);
        }
        finally
        {
            pin.Free();
        }
    }

    [Theory]
    [InlineData(CompressionFormat.Bc1, TextureFormat.Bc1)]
    [InlineData(CompressionFormat.Bc3, TextureFormat.Bc3)]
    [InlineData(CompressionFormat.Bc4, TextureFormat.Bc4)]
    [InlineData(CompressionFormat.Bc7, TextureFormat.Bc7)]
    public void RawPlanesMatchTheCpuDecodeWithinOneCode(CompressionFormat bc, TextureFormat format)
    {
        const int w = 64, h = 32;
        var rgba = Image(w, h, (int)bc);
        var blocks = Encode(bc, rgba, w, h);
        Assert.Equal(TextureFormats.CompressedSize(format, w, h), blocks.Length);
        var reference = new BcDecoder().DecodeRaw(blocks, w, h, bc);
        var px = gl.Run(c => Pass(c.Device, format, blocks, w, h, HapPass.ModeRaw, TextureFormat.Rgba8));
        var worst = 0;
        for (var i = 0; i < w * h; i++)
        {
            var r = reference[i];
            int[] want = bc == CompressionFormat.Bc4 ? [r.r] : bc == CompressionFormat.Bc1 ? [r.r, r.g, r.b, 255] : [r.r, r.g, r.b, r.a];
            for (var ch = 0; ch < want.Length; ch++)
                worst = Math.Max(worst, Math.Abs((int)Math.Round(px[i * 4 + ch] * 255) - want[ch]));
        }
        Assert.InRange(worst, 0, 1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Bc6hPlanesMatchTheCpuDecodeWithinOneHalfUlp(bool signed)
    {
        const int w = 32, h = 16;
        var bc = signed ? CompressionFormat.Bc6S : CompressionFormat.Bc6U;
        var format = signed ? TextureFormat.Bc6hSfloat : TextureFormat.Bc6hUfloat;
        var blocks = EncodeHdr(bc, HdrImage(w, h, signed), w, h);
        var reference = new BcDecoder().DecodeRawHdr(blocks, w, h, bc);
        var px = gl.Run(c => Pass(c.Device, format, blocks, w, h, HapPass.ModeRaw, TextureFormat.Rgba16F));
        for (var i = 0; i < w * h; i++)
        {
            float[] want = [reference[i].r, reference[i].g, reference[i].b];
            for (var ch = 0; ch < 3; ch++)
            {
                var got = px[i * 4 + ch];
                var ulp = Math.Abs(BitConverter.HalfToInt16Bits((System.Half)got) - BitConverter.HalfToInt16Bits((System.Half)want[ch]));
                Assert.True(ulp <= 1 || Math.Abs(got - want[ch]) <= 1e-3 * Math.Max(1, Math.Abs(want[ch])),
                    $"texel {i} channel {ch}: {got} against {want[ch]}");
            }
        }
    }

    [Fact]
    public void TheYCoCgMathsMatchTheCpuTwin()
    {
        // The twin is fed the texels the GPU itself sampled (the raw pass into a 32-bit float target), so only the maths differ.
        const int w = 32, h = 32;
        var blocks = Encode(CompressionFormat.Bc3, Image(w, h, 5), w, h);
        var alphaBlocks = Encode(CompressionFormat.Bc4, Image(w, h, 6), w, h);
        var (texels, alpha, q, qa, only) = gl.Run(c => (
            Pass(c.Device, TextureFormat.Bc3, blocks, w, h, HapPass.ModeRaw, TextureFormat.Rgba32F),
            Pass(c.Device, TextureFormat.Bc4, alphaBlocks, w, h, HapPass.ModeRaw, TextureFormat.Rgba32F),
            Pass(c.Device, TextureFormat.Bc3, blocks, w, h, HapPass.ModeYCoCg, TextureFormat.Rgba16F),
            Pass(c.Device, TextureFormat.Bc3, blocks, w, h, HapPass.ModeYCoCgAlpha, TextureFormat.Rgba16F, TextureFormat.Bc4, alphaBlocks),
            Pass(c.Device, TextureFormat.Bc4, alphaBlocks, w, h, HapPass.ModeAlphaOnly, TextureFormat.Rgba16F)));
        const float tol = 1e-3f;
        for (var i = 0; i < w * h; i++)
        {
            var (r, g, b) = HapPass.YCoCgToRgb(texels[i * 4], texels[i * 4 + 1], texels[i * 4 + 2], texels[i * 4 + 3]);
            var a = alpha[i * 4];
            Assert.InRange(q[i * 4] - r, -tol, tol);
            Assert.InRange(q[i * 4 + 1] - g, -tol, tol);
            Assert.InRange(q[i * 4 + 2] - b, -tol, tol);
            Assert.Equal(1f, q[i * 4 + 3]);
            Assert.InRange(qa[i * 4] - r, -tol, tol);
            Assert.InRange(qa[i * 4 + 3] - a, -tol, tol);
            Assert.InRange(only[i * 4] - a, -tol, tol);
            Assert.InRange(only[i * 4 + 2] - a, -tol, tol);
            Assert.Equal(1f, only[i * 4 + 3]);
        }
    }

    [Fact]
    public void HapHdrIsReadLinearWithOneAt203NitsAndTheItemsWhite()
    {
        // A flat 1.0 (SDR white under the default reading) and the same at a 406-nit white (twice as bright before tone mapping).
        const int w = 8, h = 8;
        var flat = Enumerable.Repeat(new ColorRgbFloat(1f, 1f, 1f), w * h).ToArray();
        var blocks = EncodeHdr(CompressionFormat.Bc6U, flat, w, h);
        var v = new BcDecoder().DecodeRawHdr(blocks, w, h, CompressionFormat.Bc6U)[0].r;
        Assert.InRange(v, 0.95f, 1.05f);   // the fast BC6H encoder quantises 1.0 a little
        var (one, two, pq) = gl.Run(c => (
            Pass(c.Device, TextureFormat.Bc6hUfloat, blocks, w, h, HapPass.ModeHdr, TextureFormat.Rgba16F, scale: 1, transfer: 0),
            Pass(c.Device, TextureFormat.Bc6hUfloat, blocks, w, h, HapPass.ModeHdr, TextureFormat.Rgba16F, scale: 2, transfer: 0),
            Pass(c.Device, TextureFormat.Bc6hUfloat, blocks, w, h, HapPass.ModeHdr, TextureFormat.Rgba16F, scale: 1, transfer: 1)));
        static double Curve(double x)
        {
            const double white = 1000.0 / 203.0;
            return Math.Pow(Math.Clamp(x * (1 + x / (white * white)) / (1 + x), 0, 1), 1 / 2.4);
        }
        Assert.InRange(one[0], Curve(v) - 2e-3, Curve(v) + 2e-3);
        Assert.InRange(two[0], Curve(2 * v) - 2e-3, Curve(2 * v) + 2e-3);
        Assert.True(two[0] > one[0]);
        // PQ code 1.0 is 10 000 nits: far brighter than white, clipped by the curve's ceiling.
        Assert.True(pq[0] > two[0]);
        Assert.Equal(1.0, new HapHdrReading(TransferFunction.Linear, 203, "default").Scale);
    }

    // ---- (c) whole frames against FFmpeg's CPU decode -------------------------------------------------------------------

    private static string Tool()
    {
        var libs = Environment.GetEnvironmentVariable("PLAYMAP_FFMPEG_DIR") ?? "/opt/ffmpeg9/lib";
        foreach (var candidate in new[] { Path.Combine(libs, "..", "bin", "ffmpeg"), "/opt/ffmpeg9/bin/ffmpeg" })
        {
            if (File.Exists(candidate))
                return Path.GetFullPath(candidate);
        }
        throw new InvalidOperationException("the ffmpeg tool was not found beside " + libs);
    }

    private static void Run(params string[] args)
    {
        var psi = new ProcessStartInfo(Tool()) { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
        foreach (var a in (string[])["-hide_banner", "-loglevel", "error", "-y", .. args])
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEndAsync();
        p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, "ffmpeg failed: " + err.Result);
    }

    public static TheoryData<string, PixelLayout, int, double> FfmpegFamilies() => new()
    {
        { "hap", PixelLayout.Bc1, 2, 60 },
        { "hap_alpha", PixelLayout.Bc3, 2, 60 },
        { "hap_q", PixelLayout.Bc3YCoCg, 4, 50 },
    };

    [Theory]
    [MemberData(nameof(FfmpegFamilies))]
    public void WholeFramesMatchFfmpegsCpuDecode(string format, PixelLayout layout, int maxDiff, double psnrFloor)
    {
        const int w = 256, h = 144, frames = 3;
        var dir = Directory.CreateTempSubdirectory("playmap-hap-render-").FullName;
        try
        {
            var mov = Path.Combine(dir, "clip.mov");
            Run("-f", "lavfi", "-i", $"testsrc2=s={w}x{h}:r=10:d=1,format=rgba", "-frames:v", "3", "-c:v", "hap", "-format", format,
                "-compressor", "none", mov);
            Run("-i", mov, "-map", "0:v", "-c", "copy", "-f", "rawvideo", Path.Combine(dir, "packets.bin"));
            Run("-i", mov, "-f", "rawvideo", "-pix_fmt", "rgba", Path.Combine(dir, "ffmpeg.rgba"));
            var packets = File.ReadAllBytes(Path.Combine(dir, "packets.bin"));
            var reference = File.ReadAllBytes(Path.Combine(dir, "ffmpeg.rgba"));
            Assert.Equal(frames * w * h * 4, reference.Length);
            Assert.True(HapCodecs.TryFromName(format, out var codec));
            var parsed = new HapFrame();
            var at = 0;
            for (var n = 0; n < frames; n++)
            {
                // Packets were written back to back: each frame's length is its top section's header plus its size.
                Assert.True(parsed.TryParse(packets.AsSpan(at), w, h, codec, out _), parsed.Words(n));
                var packet = packets.AsSpan(at, parsed.PacketBytes);
                at += parsed.PacketBytes;
                var plane = new byte[parsed.Texture(0).Bytes];
                foreach (var ch in parsed.Chunks(0))
                    packet.Slice(ch.SourceOffset, ch.SourceLength).CopyTo(plane.AsSpan(ch.TargetOffset));
                var bpr = (w / 4) * HapCodecs.BlockBytes(parsed.Texture(0).Kind);
                using var lease = new ArrayLease(layout, w, h, n + 1, ColorMatrix.Bt709, ColorRange.Full, (plane, bpr, w / 4, h / 4));
                var got = gl.Run(c =>
                {
                    using var set = new VideoTextureSet(c.Device);
                    Assert.True(set.Update(lease));
                    Assert.Equal((w, h), (set.Picture!.Width, set.Picture.Height));
                    return Pixels.Read(c.Device, set.Converted!);
                });
                double sum = 0;
                var worst = 0;
                var channels = layout == PixelLayout.Bc3 ? 4 : 3;
                for (var i = 0; i < w * h; i++)
                {
                    var o = n * w * h * 4 + i * 4;
                    int[] mine = [got[i * 4 + 2], got[i * 4 + 1], got[i * 4], got[i * 4 + 3]];
                    for (var c = 0; c < channels; c++)
                    {
                        var d = mine[c] - reference[o + c];
                        worst = Math.Max(worst, Math.Abs(d));
                        sum += d * d;
                    }
                }
                var mse = sum / (w * h * channels);
                var psnr = mse == 0 ? 99 : 10 * Math.Log10(255.0 * 255.0 / mse);
                Assert.True(worst <= maxDiff, $"{format} frame {n}: max diff {worst} > {maxDiff}");
                Assert.True(psnr >= psnrFloor, $"{format} frame {n}: PSNR {psnr:0.0} dB < {psnrFloor}");
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void AnOddSizeShowsNoPaddingTexel()
    {
        // 18×10 visible inside 20×12 coded: the padding column and rows are magenta and must never be shown.
        const int vw = 18, vh = 10, cw = 20, ch = 12;
        var rgba = new byte[cw * ch * 4];
        for (var y = 0; y < ch; y++)
        {
            for (var x = 0; x < cw; x++)
            {
                var o = (y * cw + x) * 4;
                var pad = x >= vw || y >= vh;
                rgba[o] = (byte)(pad ? 255 : 40);
                rgba[o + 1] = (byte)(pad ? 0 : 160);
                rgba[o + 2] = (byte)(pad ? 255 : 90);
                rgba[o + 3] = 255;
            }
        }
        var blocks = Encode(CompressionFormat.Bc1, rgba, cw, ch);
        using var lease = new ArrayLease(PixelLayout.Bc1, vw, vh, 1, ColorMatrix.Bt709, ColorRange.Full, (blocks, cw / 4 * 8, cw / 4, ch / 4));
        var px = gl.Run(c =>
        {
            using var set = new VideoTextureSet(c.Device, streaming: false);
            Assert.True(set.Update(lease));
            Assert.Equal((vw, vh), (set.Picture!.Width, set.Picture.Height));
            return Pixels.Read(c.Device, set.Converted!);
        });
        Assert.Equal(vw * vh * 4, px.Length);
        for (var i = 0; i < vw * vh; i++)
            Assert.False(px[i * 4 + 2] > 200 && px[i * 4 + 1] < 60 && px[i * 4] > 200, $"pixel {i % vw},{i / vw} shows the padding");
    }

    [Fact]
    public void AContextLackingAFamilyRefusesWithWordsNeverABlackFrame()
    {
        var blocks = Encode(CompressionFormat.Bc7, Image(8, 8, 3), 8, 8);
        using var lease = new ArrayLease(PixelLayout.Bc7, 8, 8, 1, ColorMatrix.Bt709, ColorRange.Full, (blocks, 2 * 16, 2, 2));
        var (words, refusal, picture) = gl.Run(c =>
        {
            using var set = new VideoTextureSet(c.Device);
            // An ES 3.0 context with S3TC and RGTC and no BPTC (an ANGLE-like list).
            set.Caps = GlCaps.From("es300", 3, 0, new HashSet<string> { "GL_EXT_texture_compression_s3tc", "GL_EXT_texture_compression_rgtc" });
            var e = Assert.Throws<NotSupportedException>(() => set.Update(lease));
            return (e.Message, set.Refusal, set.Picture);
        });
        Assert.Equal("this GPU's GL (es300) does not offer BPTC textures, which Bc7 needs", words);
        Assert.Equal(words, refusal);
        Assert.Null(picture);

        // On llvmpipe's own core context every family is there.
        var formats = gl.Run(c => c.Device.BlockFormats);
        Assert.Equal(BlockFormats.Bc1 | BlockFormats.Bc3 | BlockFormats.Bc4 | BlockFormats.Bc7 | BlockFormats.Bc6h, formats);
        Assert.Null(gl.Run(c => c.Device.RefuseBlockFormat(TextureFormat.Bc6hSfloat)));
    }
}
