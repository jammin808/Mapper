using PlayMap.Core.Gpu;
using PlayMap.Render.Gl;

namespace PlayMap.Render.Tests.Spine;

/// <summary>A target read back as floats gives the same values in either dialect: a fixed-point target its codes over 2^bits − 1
/// (on ES, which reads such a buffer only as integers, the device reads the codes and scales them), a float target its floats, and a
/// channel the format lacks 0 (alpha 1). The ES leg runs this through the ES branch, the core leg through the float read.</summary>
[Collection("gl")]
public sealed class FloatReadTests(GlFixture gl)
{
    // No component times 255, 1023 or 65535 lands on a half code, so the clear's rounding cannot move the expected code.
    private static readonly float[] Colour = [0.2f, 0.6f, 0.85f, 1f];

    [Theory]
    [InlineData(TextureFormat.Rgba8, 4, 8)]
    [InlineData(TextureFormat.R8, 1, 8)]
    [InlineData(TextureFormat.Rg8, 2, 8)]
    [InlineData(TextureFormat.Rgba16, 4, 16)]
    [InlineData(TextureFormat.Rgb10A2, 4, 10)]
    [InlineData(TextureFormat.Rgba16F, 4, 0)]
    [InlineData(TextureFormat.Rgba32F, 4, 0)]
    public void ATargetReadsBackAsItsOwnValues(TextureFormat format, int channels, int bits)
    {
        const int Size = 4;
        var (px, errors, renders) = gl.Run(c =>
        {
            var d = c.Device;
            using var target = d.CreateRenderTarget(Size, Size, format, filter: TextureFilter.Nearest);
            d.BindTarget(target);
            d.Clear(Colour[0], Colour[1], Colour[2], Colour[3]);
            var read = new float[Size * Size * 4];
            d.ReadPixelsFloat(target, read);
            return (read, d.TakeErrors(), d.Caps.SupportsRenderTarget(format));
        });
        Assert.True(renders, $"{format} is a render target on {GlDialects.Word(GlFixture.Dialect)}");
        Assert.Empty(errors);
        for (var i = 0; i < Size * Size; i++)
        {
            for (var ch = 0; ch < 4; ch++)
            {
                var got = px[i * 4 + ch];
                var want = ch < channels ? Colour[ch] : ch == 3 ? 1f : 0f;
                // Rgb10A2's alpha has two bits.
                var max = bits == 0 ? 0 : (1 << (format == TextureFormat.Rgb10A2 && ch == 3 ? 2 : bits)) - 1;
                if (max == 0)
                {
                    Assert.True(Math.Abs(got - want) <= 1e-3, $"{format} texel {i} channel {ch}: {got} against {want}");
                    continue;
                }
                var code = got * max;
                Assert.True(Math.Abs(code - Math.Round(code)) <= 1e-2, $"{format} texel {i} channel {ch}: {got} is not a code over {max}");
                Assert.Equal((int)Math.Round(want * max), (int)Math.Round(code));
            }
        }
    }
}
