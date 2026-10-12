using PlayMap.Core.Model;
using PlayMap.Core.Numerics;
using PlayMap.Core.Vision;
using PlayMap.Vision.Imaging;

namespace PlayMap.Vision.Tests;

/// <summary>The resampler and the model-input fill are exact where the maths says they must be: constants stay
/// constant, ramps stay ramps, an identity resize changes nothing, normalisation lands on the documented values
/// and a letterbox maps clicks there and back.</summary>
public class ImagingTests
{
    private static ImageRgb8 Ramp(int w, int h)
    {
        var img = ImageRgb8.Create(w, h);
        for (var y = 0; y < h; y++)
        {
            var row = img.MutableRow(y);
            for (var x = 0; x < w; x++)
            {
                row[x * 3] = (byte)(x * 2);
                row[x * 3 + 1] = (byte)(y * 3);
                row[x * 3 + 2] = 77;
            }
        }
        return img;
    }

    [Fact]
    public void AConstantPictureStaysConstantAtAnySize()
    {
        var img = ImageRgb8.Create(37, 23);
        img.Data.AsSpan().Fill(200);
        foreach (var (w, h) in new[] { (10, 7), (64, 64), (37, 23), (111, 5) })
        {
            var chw = new float[3 * w * h];
            Resampler.RgbToPlanar(img, new RectD(0, 0, 37, 23), chw, w, h, 0, 0, w, h, [1, 1, 1], [0, 0, 0]);
            Assert.All(chw, v => Assert.Equal(200f, v, 3));
        }
    }

    [Fact]
    public void ARampStaysARampWhenScaledUpOrDown()
    {
        var img = Ramp(64, 40);
        // Upscales and whole-number downscales are exact; a fractional downscale's discrete triangle is a hair
        // asymmetric, bounded by a tenth of a source pixel.
        foreach (var (w, h, tol) in new[] { (16, 10, 1e-3), (128, 80, 1e-3), (50, 31, 0.1) })
        {
            var chw = new float[3 * w * h];
            Resampler.RgbToPlanar(img, new RectD(0, 0, 64, 40), chw, w, h, 0, 0, w, h, [1, 1, 1], [0, 0, 0]);
            var sx = 64.0 / w;
            var sy = 40.0 / h;
            // Away from the clamped border the triangle filter reproduces an affine function exactly.
            for (var y = 3; y < h - 3; y++)
            {
                for (var x = 3; x < w - 3; x++)
                {
                    var srcX = (x + 0.5) * sx - 0.5;
                    var srcY = (y + 0.5) * sy - 0.5;
                    Assert.InRange(Math.Abs(srcX * 2 - chw[y * w + x]), 0, 2 * tol);
                    Assert.InRange(Math.Abs(srcY * 3 - chw[w * h + y * w + x]), 0, 3 * tol);
                    Assert.Equal(77, chw[2 * w * h + y * w + x], 3);
                }
            }
        }
    }

    [Fact]
    public void AnIdentityResizeChangesNothing()
    {
        var img = Ramp(20, 11);
        var chw = new float[3 * 20 * 11];
        Resampler.RgbToPlanar(img, new RectD(0, 0, 20, 11), chw, 20, 11, 0, 0, 20, 11, [1, 1, 1], [0, 0, 0]);
        for (var y = 0; y < 11; y++)
            for (var x = 0; x < 20; x++)
                Assert.Equal(img.Row(y)[x * 3], chw[y * 20 + x]);
    }

    [Fact]
    public void TheFloatChannelResizeSkipsHolesAndKeepsRamps()
    {
        var src = new float[32 * 32];
        for (var y = 0; y < 32; y++)
            for (var x = 0; x < 32; x++)
                src[y * 32 + x] = x + 10 * y;
        src[5 * 32 + 5] = float.NaN;
        var dst = new float[16 * 16];
        Resampler.Channel(src, 32, 32, new RectD(0, 0, 32, 32), dst, 16, 16);
        Assert.Equal(2 * 8 + 0.5 + 10 * (2 * 8 + 0.5), dst[8 * 16 + 8], 3);
        Assert.True(float.IsFinite(dst[2 * 16 + 2]));
        var all = new float[4];
        all.AsSpan().Fill(float.NaN);
        var one = new float[1];
        Resampler.Channel(all, 2, 2, new RectD(0, 0, 2, 2), one, 1, 1);
        Assert.True(float.IsNaN(one[0]));
    }

    [Fact]
    public void NormalisationLandsOnTheImageNetValues()
    {
        var img = ImageRgb8.Create(4, 4);
        for (var i = 0; i < 16; i++)
        {
            img.Data[i * 3] = 255;
            img.Data[i * 3 + 1] = 0;
            img.Data[i * 3 + 2] = 128;
        }
        var spec = ModelInputSpec.ImageNet(8, 8, FitMode.Stretch);
        var chw = new float[3 * 64];
        ModelInput.Fill(img, spec, chw);
        Assert.Equal((1f - 0.485f) / 0.229f, chw[0], 4);
        Assert.Equal((0f - 0.456f) / 0.224f, chw[64], 4);
        Assert.Equal((128f / 255f - 0.406f) / 0.225f, chw[128], 4);
    }

    [Fact]
    public void ALetterboxPadsTheRestAndMapsClicksThereAndBack()
    {
        var img = ImageRgb8.Create(40, 20);
        img.Data.AsSpan().Fill(255);
        var spec = ModelInputSpec.ImageNet(16, 16, FitMode.LetterboxTopLeft) with { PadValue = -9 };
        var chw = new float[3 * 256];
        var map = ModelInput.Fill(img, spec, chw);
        Assert.Equal(0.4, map.ScaleX, 9);
        Assert.Equal(0.4, map.ScaleY, 9);
        Assert.Equal((1f - 0.485f) / 0.229f, chw[3 * 16 + 5], 4);
        Assert.Equal(-9f, chw[12 * 16 + 5]);
        var click = new Vec2d(31.5, 7.25);
        var back = map.ToImage(map.ToModel(click));
        Assert.Equal(click.X, back.X, 9);
        Assert.Equal(click.Y, back.Y, 9);

        var centred = ImageToModel.For(40, 20, 16, 16, FitMode.LetterboxCentre);
        Assert.Equal(4, centred.OffsetY, 9);
        Assert.Equal(new RectD(0, 4, 16, 8), centred.Content);
    }

    [Fact]
    public void GrayConversionUsesTheLumaWeights()
    {
        var img = ImageRgb8.Create(3, 1);
        img.Data[0] = 255;
        img.Data[4] = 255;
        img.Data[8] = 255;
        var g = img.ToGray();
        Assert.Equal(77, g.Data[0]);
        Assert.Equal(149, g.Data[1]);
        Assert.Equal(29, g.Data[2]);
    }
}
