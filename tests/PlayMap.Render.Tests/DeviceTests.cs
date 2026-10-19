using PlayMap.Core.Gpu;
using PlayMap.Render.Gpu;
using PlayMap.Render.Shaders;

namespace PlayMap.Render.Tests;

/// <summary>The GPU seam: programs compile on the headless driver, targets clear and read back top row first,
/// pooled resources come back, and the census sees what is alive.</summary>
[Collection("gl")]
public class DeviceTests(GlFixture gl)
{
    [Fact]
    public void EveryProgramInTheLibraryCompilesAndLinks()
    {
        gl.Run(c =>
        {
            foreach (var name in ShaderLibrary.Names)
                Assert.NotNull(c.Device.Program(name));
            Assert.Empty(c.Device.TakeErrors());
        });
    }

    [Fact]
    public void AnUploadReadsBackTopRowFirst()
    {
        gl.Run(c =>
        {
            var d = c.Device;
            using var target = d.CreateRenderTarget(4, 2, TextureFormat.Rgba8);
            using var tex = d.CreateTexture(4, 2, TextureFormat.Rgba8, TextureFilter.Nearest);
            var rgba = new byte[4 * 2 * 4];
            for (var i = 0; i < 4; i++)
            {
                rgba[i * 4] = 255;          // top row red
                rgba[(4 + i) * 4 + 2] = 255; // bottom row blue
            }
            for (var i = 0; i < 8; i++)
                rgba[i * 4 + 3] = 255;
            d.Upload(tex, rgba, UploadFormat.Rgba8);
            d.BindTarget(target);
            var blit = d.Program("blit");
            blit.Set("uSrc", 0, 0, 1, 1);
            d.BindSampler(blit, "uTex", 0, tex);
            d.DrawFullscreen(blit);
            var px = Pixels.Read(d, target);
            Assert.Equal((255, 0, 0), Pixels.At(px, 4, 0, 0));
            Assert.Equal((0, 0, 255), Pixels.At(px, 4, 3, 1));
            Assert.Empty(d.TakeErrors());
        });
    }

    [Fact]
    public void APooledTargetComesBackForTheSameShape()
    {
        gl.Run(c =>
        {
            using var pool = new ResourcePool(c.Device);
            var a = pool.RentTarget(32, 16, TextureFormat.Rgba16F);
            pool.Return(a);
            var b = pool.RentTarget(32, 16, TextureFormat.Rgba16F);
            Assert.Same(a, b);
            var other = pool.RentTarget(32, 16, TextureFormat.Rgba8);
            Assert.NotSame(a, other);
            Assert.Equal(2, pool.Lent);
        });
    }
}
