using PlayMap.Core.Media;
using PlayMap.Core.Model;
using PlayMap.Render.Engine;
using PlayMap.Render.Video;

namespace PlayMap.Render.Tests;

/// <summary>Video upload: YUV to RGB with the frame's matrix and range within two codes, any row stride,
/// 10-bit and planar layouts, and no upload when the generation is already on the GPU.</summary>
[Collection("gl")]
public class VideoTests(GlFixture gl)
{
    private const int W = 32;
    private const int H = 16;

    private static ArrayLease Nv12(float r, float g, float b, ColorMatrix matrix, ColorRange range, long generation = 1, int stride = W)
    {
        var k = YuvConstants.For(PixelLayout.Nv12, matrix, range, TransferFunction.Sdr);
        var (y, cb, cr) = k.FromRgb(r, g, b);
        var luma = new byte[stride * H];
        for (var row = 0; row < H; row++)
            for (var x = 0; x < W; x++)
                luma[row * stride + x] = (byte)Math.Round(y);
        var chroma = new byte[stride * (H / 2)];
        for (var row = 0; row < H / 2; row++)
        {
            for (var x = 0; x < W / 2; x++)
            {
                chroma[row * stride + x * 2] = (byte)Math.Round(cb);
                chroma[row * stride + x * 2 + 1] = (byte)Math.Round(cr);
            }
        }
        return new ArrayLease(PixelLayout.Nv12, W, H, generation, matrix, range, (luma, stride, W, H), (chroma, stride, W / 2, H / 2));
    }

    private (int R, int G, int B) Convert(VideoFrameLease lease) => gl.Run(c =>
    {
        using var set = new VideoTextureSet(c.Backend);
        Assert.True(set.Update(lease));
        var px = Scene.ReadTexture(c.Device, set.Picture!);
        Assert.Empty(c.Device.TakeErrors());
        return Pixels.At(px, W, W / 2, H / 2);
    });

    [Fact]
    public void AnNv12FrameInBt601LimitedRangeConvertsWithinTwoCodes()
    {
        using var lease = Nv12(0.8f, 0.3f, 0.2f, ColorMatrix.Bt601, ColorRange.Limited);
        Pixels.Near((204, 77, 51), Convert(lease), 2, "601 limited");
    }

    [Fact]
    public void AnNv12FrameInBt709FullRangeConvertsWithinTwoCodes()
    {
        using var lease = Nv12(0.1f, 0.6f, 0.9f, ColorMatrix.Bt709, ColorRange.Full);
        Pixels.Near((26, 153, 230), Convert(lease), 2, "709 full");
    }

    [Fact]
    public void APaddedRowStrideUploadsTheSamePicture()
    {
        using var lease = Nv12(0.5f, 0.5f, 0.5f, ColorMatrix.Bt709, ColorRange.Limited, stride: W + 24);
        Pixels.Near((128, 128, 128), Convert(lease), 2, "padded");
    }

    [Fact]
    public void AP010FrameConvertsItsTenBitCodes()
    {
        var k = YuvConstants.For(PixelLayout.P010, ColorMatrix.Bt709, ColorRange.Limited, TransferFunction.Sdr);
        var (y, cb, cr) = k.FromRgb(0.25f, 0.75f, 0.5f);
        static byte[] Plane(int samples, Func<int, double> code)
        {
            var bytes = new byte[samples * 2];
            for (var i = 0; i < samples; i++)
            {
                var v = (ushort)((int)Math.Round(code(i)) << 6);
                bytes[i * 2] = (byte)v;
                bytes[i * 2 + 1] = (byte)(v >> 8);
            }
            return bytes;
        }
        var luma = Plane(W * H, _ => y);
        var chroma = Plane(W / 2 * (H / 2) * 2, i => i % 2 == 0 ? cb : cr);
        using var lease = new ArrayLease(PixelLayout.P010, W, H, 1, ColorMatrix.Bt709, ColorRange.Limited, (luma, W * 2, W, H), (chroma, W * 2, W / 2, H / 2));
        Pixels.Near((64, 191, 128), Convert(lease), 2, "P010");
    }

    [Fact]
    public void APlanar420FrameConvertsLikeItsSemiPlanarTwin()
    {
        var k = YuvConstants.For(PixelLayout.Yuv420P, ColorMatrix.Bt601, ColorRange.Limited, TransferFunction.Sdr);
        var (y, cb, cr) = k.FromRgb(0.9f, 0.9f, 0.1f);
        var luma = Enumerable.Repeat((byte)Math.Round(y), W * H).ToArray();
        var u = Enumerable.Repeat((byte)Math.Round(cb), W / 2 * (H / 2)).ToArray();
        var v = Enumerable.Repeat((byte)Math.Round(cr), W / 2 * (H / 2)).ToArray();
        using var lease = new ArrayLease(PixelLayout.Yuv420P, W, H, 1, ColorMatrix.Bt601, ColorRange.Limited, (luma, W, W, H), (u, W / 2, W / 2, H / 2), (v, W / 2, W / 2, H / 2));
        Pixels.Near((230, 230, 26), Convert(lease), 2, "420P");
    }

    [Fact]
    public void PqReferenceWhiteIsToneMappedToTheSdrGreyItsCurveGives()
    {
        // 203 cd/m² (HDR reference white) encoded with the ST 2084 curve, neutral, in 10-bit BT.2020 limited.
        const double m1 = 0.1593017578125, m2 = 78.84375, c1 = 0.8359375, c2 = 18.8515625, c3 = 18.6875;
        var l = Math.Pow(203 / 10000.0, m1);
        var e = Math.Pow((c1 + c2 * l) / (1 + c3 * l), m2);
        var yCode = 64 + 876 * e;
        static byte[] Plane(int samples, Func<int, double> code)
        {
            var bytes = new byte[samples * 2];
            for (var i = 0; i < samples; i++)
            {
                var v = (ushort)((int)Math.Round(code(i)) << 6);
                bytes[i * 2] = (byte)v;
                bytes[i * 2 + 1] = (byte)(v >> 8);
            }
            return bytes;
        }
        using var lease = new ArrayLease(PixelLayout.P010, W, H, 1, ColorMatrix.Bt2020, ColorRange.Limited,
            (Plane(W * H, _ => yCode), W * 2, W, H), (Plane(W / 2 * (H / 2) * 2, _ => 512), W * 2, W / 2, H / 2))
        {
            TransferOverride = TransferFunction.Pq,
        };
        // The shader's tone map: x = nits / 203 = 1 → x(1 + x/w²)/(1 + x) with w = 1000/203, then 1/2.4.
        var white = 1000 / 203.0;
        var expected = (int)Math.Round(Math.Pow(1 * (1 + 1 / (white * white)) / 2, 1 / 2.4) * 255);
        Pixels.Near((expected, expected, expected), Convert(lease), 3, "PQ 203 nits");
    }

    [Fact]
    public void ABgraFrameUploadsItsBytesExactly()
    {
        var bgra = new byte[W * H * 4];
        for (var i = 0; i < W * H; i++)
        {
            bgra[i * 4] = 10;
            bgra[i * 4 + 1] = 20;
            bgra[i * 4 + 2] = 30;
            bgra[i * 4 + 3] = 255;
        }
        using var lease = new ArrayLease(PixelLayout.Bgra8, W, H, 1, ColorMatrix.Bt709, ColorRange.Full, (bgra, W * 4, W, H));
        Assert.Equal((30, 20, 10), Convert(lease));
    }

    [Fact]
    public void AFrameWhoseGenerationIsOnTheGpuIsNotUploadedAgain()
    {
        var generation = 7L;
        var leases = new List<ArrayLease>();
        var sources = new FuncSources((_, _) =>
        {
            var lease = Nv12(0.4f, 0.4f, 0.4f, ColorMatrix.Bt709, ColorRange.Limited, generation);
            leases.Add(lease);
            return lease;
        });
        var media = SourceRef.Media("md1");
        var project = Scene.Project([], [Scene.SourceOutput("op1", media, 64, 36)]);
        var (afterThree, afterNew) = gl.Run(c =>
        {
            using var engine = new RenderEngine(c.Backend, new RenderEngineOptions(GpuTiming: false));
            for (var f = 0; f < 3; f++)
                engine.Render(Scene.Inputs(project, 1 + f / 60.0, sources));
            var three = engine.VideoUploads;
            generation = 8;
            engine.Render(Scene.Inputs(project, 1.1, sources));
            return (three, engine.VideoUploads);
        });
        Assert.Equal(1, afterThree);
        Assert.Equal(2, afterNew);
        Assert.Equal(4, sources.Calls);
        Assert.All(leases, l => Assert.Equal(1, l.Releases));
    }
}
