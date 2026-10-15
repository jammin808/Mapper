using PlayMap.Core.Media;
using PlayMap.Core.Video;

namespace PlayMap.Core.Tests.Video;

/// <summary>The packed network layouts are sized as NDI and OMT size them (tight rows), and the CPU twin of the GPU passes encodes and
/// decodes BT.709 limited range as the standard says.</summary>
public sealed class LayoutSizeTests
{
    [Theory]
    [InlineData(PixelLayout.Uyvy, 4_147_200L)]
    [InlineData(PixelLayout.Uyva, 6_220_800L)]
    [InlineData(PixelLayout.P216, 8_294_400L)]
    [InlineData(PixelLayout.Pa16, 12_441_600L)]
    public void FrameSizesAt1080pAreTheSendersOwn(PixelLayout layout, long bytes)
    {
        Assert.Equal(bytes, LivePlanes.FrameBytes(layout, 1920, 1080));
    }

    [Fact]
    public void StridesPerPlaneAndOddWidthsAreRefusedWithWords()
    {
        Assert.Equal(3840, LivePlanes.Stride(PixelLayout.Uyvy, 0, 1920));
        Assert.Equal(1920, LivePlanes.Stride(PixelLayout.Uyva, 1, 1920));
        Assert.Equal(3840, LivePlanes.Stride(PixelLayout.Pa16, 2, 1920));
        Assert.Equal(3, LivePlanes.PlaneCount(PixelLayout.Pa16));
        Assert.Equal(0, LivePlanes.PlaneCount(PixelLayout.Nv12));
        Assert.Equal("a uyvy frame needs an even width (4:2:2 pairs), not 1919", LivePlanes.Refuse(PixelLayout.Uyvy, 1919, 1080));
        Assert.Null(LivePlanes.Refuse(PixelLayout.P216, 1920, 1080));
    }

    [Fact]
    public void TheTwinEncodesBt709LimitedRangeAtBothDepths()
    {
        Assert.Equal((235f, 128f, 128f), Rounded(PackReference.Encode(1, 1, 1)));
        Assert.Equal((16f, 128f, 128f), Rounded(PackReference.Encode(0, 0, 0)));
        // Pure red: Y = 16 + 219·0.2126, Cr at its top.
        var (y, cb, cr) = PackReference.Encode(1, 0, 0);
        Assert.Equal(62.56, y, 2);
        Assert.Equal(102.0, cb, 0);
        Assert.Equal(240.0, cr, 2);
        var (y16, _, _) = PackReference.Encode(1, 1, 1, 256);
        Assert.Equal(60160, y16, 1);
        var (r, g, b) = PackReference.Decode(y, cb, cr);
        Assert.Equal(1, r, 4);
        Assert.Equal(0, g, 4);
        Assert.Equal(0, b, 4);
    }

    [Theory]
    [InlineData(PixelLayout.Uyvy, 1.5f)]
    [InlineData(PixelLayout.Uyva, 1.5f)]
    [InlineData(PixelLayout.P216, 0.02f)]
    [InlineData(PixelLayout.Pa16, 0.02f)]
    public void AFlatPicturePacksAndUnpacksToItself(PixelLayout layout, float codes8)
    {
        const int w = 16, h = 4;
        var rgba = new float[w * h * 4];
        for (var i = 0; i < w * h; i++)
        {
            rgba[i * 4] = 0.8f;
            rgba[i * 4 + 1] = 0.3f;
            rgba[i * 4 + 2] = 0.55f;
            rgba[i * 4 + 3] = 0.5f;
        }
        var packed = PackReference.Pack(layout, rgba, w, h);
        Assert.Equal(LivePlanes.FrameBytes(layout, w, h), packed.Length);
        var back = PackReference.Unpack(layout, packed, w, h);
        for (var i = 0; i < back.Length; i++)
        {
            if (i % 4 == 3)
                Assert.Equal(LivePlanes.HasAlpha(layout) ? 0.5f : 1f, back[i], LivePlanes.IsDeep(layout) ? 0.0001f : 0.003f);
            else
                Assert.InRange(Math.Abs(back[i] - rgba[i]) * 255, 0, codes8);
        }
    }

    private static (float, float, float) Rounded((float A, float B, float C) v) => (MathF.Round(v.A), MathF.Round(v.B), MathF.Round(v.C));
}
