using PlayMap.Render.Gpu;

namespace PlayMap.Render.Tests.Spine;

/// <summary>What a context can sample and render to, read from its version and extension list: fake lists for desktop Mesa, ES 3.0
/// Mesa and an ANGLE-like ES context; then the real core 3.3 context of the tests, where every block family reads true and a float
/// and a 16-bit target render a known colour.</summary>
[Collection("gl")]
public sealed class GlCapsTests(GlFixture gl)
{
    [Fact]
    public void ACoreMesaContextHasEveryFamilyFromItsVersionAndS3tc()
    {
        var caps = GlCaps.From("core330", 4, 5, new HashSet<string> { "GL_EXT_texture_compression_s3tc" });
        Assert.True(caps.Dxt1 && caps.Dxt5 && caps.Rgtc && caps.Bptc && caps.Norm16 && caps.FloatBlend && caps.ColorBufferFloat);
        Assert.True(caps.IsCore);
        // On core 3.3 BPTC needs its extension; RGTC is core from 3.0.
        var old = GlCaps.From("core330", 3, 3, new HashSet<string>());
        Assert.True(old.Rgtc);
        Assert.False(old.Bptc);
        Assert.False(old.Dxt1);
        Assert.False(old.Supports(TextureFormat.Bc1));
        Assert.True(old.Supports(TextureFormat.Bc4));
    }

    [Fact]
    public void AnEsMesaContextNeedsTheExtensionNames()
    {
        var ext = new HashSet<string>
        {
            "GL_EXT_texture_compression_s3tc", "GL_EXT_texture_compression_rgtc", "GL_EXT_texture_compression_bptc",
            "GL_EXT_texture_norm16", "GL_EXT_color_buffer_float", "GL_EXT_float_blend",
        };
        var caps = GlCaps.From("es300", 3, 0, ext);
        Assert.True(caps.Dxt1 && caps.Dxt5 && caps.Rgtc && caps.Bptc && caps.Norm16 && caps.FloatBlend && caps.ColorBufferFloat);
        Assert.False(caps.IsCore);
        var bare = GlCaps.From("es300", 3, 0, new HashSet<string>());
        Assert.False(bare.Rgtc || bare.Bptc || bare.Norm16 || bare.ColorBufferFloat);
        Assert.False(bare.SupportsRenderTarget(TextureFormat.Rgba16F));
    }

    [Fact]
    public void AnAngleLikeContextHasDxt1AndDxt5WithoutS3tcAndNoSixteenBitTarget()
    {
        var caps = GlCaps.From("es300", 3, 0, new HashSet<string> { "GL_EXT_texture_compression_dxt1", "GL_ANGLE_texture_compression_dxt5", "GL_EXT_color_buffer_float" });
        Assert.True(caps.Dxt1);
        Assert.True(caps.Dxt5);
        Assert.False(caps.Norm16);
        Assert.False(caps.SupportsRenderTarget(TextureFormat.Rgba16));
        Assert.True(caps.SupportsRenderTarget(TextureFormat.Rgba32F));
        Assert.False(caps.SupportsRenderTarget(TextureFormat.Rgba32F, blended: true));
        Assert.False(caps.SupportsRenderTarget(TextureFormat.Bc1));
    }

    [Fact]
    public void OnTheTestsCoreContextEveryBlockFamilyReadsTrue()
    {
        var caps = gl.Run(c => c.Device.Caps);
        Assert.Equal("core330", caps.Dialect);
        foreach (var f in new[] { TextureFormat.Bc1, TextureFormat.Bc3, TextureFormat.Bc4, TextureFormat.Bc7, TextureFormat.Bc6hUfloat })
            Assert.True(caps.Supports(f), $"{f} on {gl.Run(c => c.Device.Renderer)}");
    }

    [Theory]
    [InlineData(TextureFormat.Rgba32F)]
    [InlineData(TextureFormat.Rgba16)]
    public void AFloatAndASixteenBitTargetRenderAKnownColour(TextureFormat format)
    {
        var got = gl.Run(c =>
        {
            var d = c.Device;
            using var target = d.CreateRenderTarget(8, 8, format, filter: TextureFilter.Nearest);
            d.BindTarget(target);
            d.Clear(0.25f, 0.5f, 1f, 1f);
            var read = Pixels.Read(d, target);
            Assert.Empty(d.TakeErrors());
            return Pixels.At(read, 8, 3, 3);
        });
        Pixels.Near((64, 128, 255), got, 1, format.ToString());
    }

    [Fact]
    public void ABlockFormatIsNeverAPlainTextureOrATarget()
    {
        gl.Run(c =>
        {
            var plain = Assert.Throws<ArgumentException>(() => c.Device.CreateTexture(8, 8, TextureFormat.Bc1));
            Assert.Contains("compressed upload path", plain.Message, StringComparison.Ordinal);
            Assert.Throws<ArgumentException>(() => c.Device.CreateRenderTarget(8, 8, TextureFormat.Bc7));
        });
    }
}
