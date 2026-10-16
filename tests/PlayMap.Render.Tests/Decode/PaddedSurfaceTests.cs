using PlayMap.Core.Media;
using PlayMap.Render.Gpu;
using PlayMap.Render.Video;

namespace PlayMap.Render.Tests.Decode;

/// <summary>Padded decoder surfaces (zerocopy.md M6): H.264 1080p is coded 1088 high, and a D3D11 plane image covers the coded size.
/// The `yuv` pass samples only the visible part (uCrop = visible ÷ coded) and its chroma never filters into the padding (uMaxUv,
/// the last visible chroma texel's centre): a 1920×1088 NV12 frame whose last eight rows are magenta converts to a 1920×1080
/// picture with no magenta in its last row or column. The same planes passed as if they had no padding show it, so the crop is
/// what keeps it out.</summary>
[Collection("gl")]
public sealed class PaddedSurfaceTests(GlFixture gl)
{
    private const int W = 1920, H = 1080, CodedH = 1088;

    private static (GpuTexture Luma, GpuTexture Chroma) PaddedPlanes(GlDevice device)
    {
        var k = YuvConstants.For(PixelLayout.Nv12, ColorMatrix.Bt709, ColorRange.Limited, TransferFunction.Sdr);
        var grey = k.FromRgb(0.5f, 0.5f, 0.5f);
        var magenta = k.FromRgb(1, 0, 1);
        var luma = new byte[W * CodedH];
        for (var y = 0; y < CodedH; y++)
            Array.Fill(luma, (byte)MathF.Round(y < H ? grey.Y : magenta.Y), y * W, W);
        var chroma = new byte[W * (CodedH / 2)];
        for (var y = 0; y < CodedH / 2; y++)
            for (var x = 0; x < W / 2; x++)
            {
                var c = y < H / 2 ? grey : magenta;
                chroma[(y * W) + (2 * x)] = (byte)MathF.Round(c.Cb);
                chroma[(y * W) + (2 * x) + 1] = (byte)MathF.Round(c.Cr);
            }
        var l = device.CreateTexture(W, CodedH, TextureFormat.R8);
        var ch = device.CreateTexture(W / 2, CodedH / 2, TextureFormat.Rg8);
        device.Upload(l, luma, UploadFormat.R8);
        device.Upload(ch, chroma, UploadFormat.Rg8);
        return (l, ch);
    }

    private static float[] Convert(GlDevice device, VideoTextureSet set, GpuTexture luma, GpuTexture chroma, int codedHeight)
    {
        set.ConvertCoded(PixelLayout.Nv12, luma, chroma, W, H, W, codedHeight, ColorMatrix.Bt709, ColorRange.Limited, TransferFunction.Sdr);
        var px = new float[W * H * 4];
        device.ReadPixelsFloat(set.Converted!, px);
        return px;
    }

    // Grey has R = G = B; any magenta mixed in pulls G under R and B.
    private static float WorstTint(float[] px, IEnumerable<(int X, int Y)> where)
    {
        var worst = 0f;
        foreach (var (x, y) in where)
        {
            var i = ((y * W) + x) * 4;
            worst = MathF.Max(worst, MathF.Max(px[i] - px[i + 1], px[i + 2] - px[i + 1]));
        }
        return worst;
    }

    // The converted picture is stored top row first (texture row 0 is the first uploaded row), and read back the same way.
    private static IEnumerable<(int, int)> LastRow() => Enumerable.Range(0, W).Select(x => (x, H - 1));

    private static IEnumerable<(int, int)> LastColumn() => Enumerable.Range(0, H).Select(y => (W - 1, y));

    [Fact]
    public void ACodedFrameShowsNoPaddingRows() => gl.Run(ctx =>
    {
        var device = ctx.Device;
        var (luma, chroma) = PaddedPlanes(device);
        using var _ = luma;
        using var __ = chroma;
        using var set = new VideoTextureSet(device);
        var px = Convert(device, set, luma, chroma, CodedH);
        Assert.Equal((W, H), (set.Picture!.Width, set.Picture.Height));
        Assert.True(WorstTint(px, LastRow()) < 1 / 255f, $"magenta reached the last row ({WorstTint(px, LastRow())})");
        Assert.True(WorstTint(px, LastColumn()) < 1 / 255f, "magenta reached the last column");
        var mid = (((H / 2) * W) + (W / 2)) * 4;
        Assert.InRange(px[mid], 0.45f, 0.55f);
    });

    [Fact]
    public void ThePaddingShowsWhenThePlanesAreTakenForVisibleOnes() => gl.Run(ctx =>
    {
        var device = ctx.Device;
        var (luma, chroma) = PaddedPlanes(device);
        using var _ = luma;
        using var __ = chroma;
        using var set = new VideoTextureSet(device);
        var px = Convert(device, set, luma, chroma, H);
        // The luma padding is magenta's (darker than the grey): the last row is no longer grey.
        var i = (((H - 1) * W) + (W / 2)) * 4;
        Assert.True(MathF.Abs(px[i + 1] - 0.5f) > 0.1f, $"without the crop the padding rows are drawn (G {px[i + 1]})");
    });

    [Fact]
    public void TheCropAndTheChromaClampFollowTheVisibleAndCodedSizes() => gl.Run(ctx =>
    {
        using var chroma = ctx.Device.CreateTexture(W / 2, CodedH / 2, TextureFormat.Rg8);
        var (x, y, maxU, maxV) = VideoTextureSet.Crop(PixelLayout.Nv12, W, H, W, CodedH, chroma);
        Assert.Equal((1f, 1080f / 1088f, 959.5f / 960f, 539.5f / 544f), (x, y, maxU, maxV));
        using var tight = ctx.Device.CreateTexture(960, 540, TextureFormat.Rg8);
        Assert.Equal((1f, 1f, 959.5f / 960f, 539.5f / 540f), VideoTextureSet.Crop(PixelLayout.Nv12, W, H, W, H, tight));
        // An odd visible size: chroma rounds up, the clamp is the last chroma texel the picture covers.
        using var odd = ctx.Device.CreateTexture(960, 544, TextureFormat.Rg8);
        Assert.Equal((1919f / 1920f, 1079f / 1088f, 959.5f / 960f, 539.5f / 544f), VideoTextureSet.Crop(PixelLayout.P010, 1919, 1079, 1920, 1088, odd));
        // A small picture coded several times its width is still 4:2:0: the clamp stays inside its visible chroma.
        using var wide = ctx.Device.CreateTexture(64, 16, TextureFormat.Rg8);
        Assert.Equal((32f / 128f, 1f, 15.5f / 64f, 15.5f / 16f), VideoTextureSet.Crop(PixelLayout.Nv12, 32, 32, 128, 32, wide));
        // 4:4:4 and 4:2:2 planar keep the full chroma size where they do not halve it.
        using var full = ctx.Device.CreateTexture(32, 32, TextureFormat.R8);
        Assert.Equal((1f, 1f, 31.5f / 32f, 31.5f / 32f), VideoTextureSet.Crop(PixelLayout.Yuv444P, 32, 32, 32, 32, full));
        using var half = ctx.Device.CreateTexture(16, 32, TextureFormat.R8);
        Assert.Equal((1f, 1f, 15.5f / 16f, 31.5f / 32f), VideoTextureSet.Crop(PixelLayout.Yuv422P, 32, 32, 32, 32, half));
    });
}
