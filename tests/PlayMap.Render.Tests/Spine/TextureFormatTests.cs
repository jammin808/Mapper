using PlayMap.Core.Gpu;

namespace PlayMap.Render.Tests.Spine;

/// <summary>Every texture format has its block class by name, and a block-compressed level's size is whole 4×4 blocks (the bench's
/// HAP frames: 1920×1080 BC1 and BC3); a 16-bit upload is eight bytes a pixel. The GL backend's row for each format is GlCapsTests'.</summary>
public sealed class TextureFormatTests
{
    private static readonly HashSet<TextureFormat> Blocks =
    [
        TextureFormat.Bc1, TextureFormat.Bc1Alpha, TextureFormat.Bc3, TextureFormat.Bc4, TextureFormat.Bc5, TextureFormat.Bc7,
        TextureFormat.Bc6hUfloat, TextureFormat.Bc6hSfloat,
    ];

    [Fact]
    public void EveryFormatHasTheExpectedBlockClass()
    {
        foreach (var f in Enum.GetValues<TextureFormat>())
        {
            Assert.Equal(Blocks.Contains(f), TextureFormats.IsBlockCompressed(f));
            var expected = f switch
            {
                TextureFormat.Bc1 or TextureFormat.Bc1Alpha or TextureFormat.Bc4 => 8,
                TextureFormat.Bc3 or TextureFormat.Bc5 or TextureFormat.Bc7 or TextureFormat.Bc6hUfloat or TextureFormat.Bc6hSfloat => 16,
                _ => 0,
            };
            Assert.Equal(expected, TextureFormats.BlockBytes(f));
        }
        Assert.Equal(Blocks.Count, Enum.GetValues<TextureFormat>().Count(TextureFormats.IsBlockCompressed));
    }

    [Fact]
    public void ABlockCompressedLevelIsWholeBlocks()
    {
        Assert.Equal(1_036_800, TextureFormats.CompressedSize(TextureFormat.Bc1, 1920, 1080));
        Assert.Equal(2_073_600, TextureFormats.CompressedSize(TextureFormat.Bc3, 1920, 1080));
        Assert.Equal(8, TextureFormats.CompressedSize(TextureFormat.Bc1, 1, 1));
        Assert.Equal(4 * 16, TextureFormats.CompressedSize(TextureFormat.Bc7, 5, 5));
        Assert.Equal(0, TextureFormats.CompressedSize(TextureFormat.Rgba8, 1920, 1080));
    }

    [Fact]
    public void TheSixteenBitUploadIsEightBytesAPixel() => Assert.Equal(8, UploadFormats.BytesPerPixel(UploadFormat.Rgba16));
}
