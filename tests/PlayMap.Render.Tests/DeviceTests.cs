using PlayMap.Core.Gpu;
using PlayMap.Render.Engine;

namespace PlayMap.Render.Tests;

/// <summary>The GPU seam: every pipeline the frame's passes list is made by the device's warm-up, targets clear and read back top row
/// first, and pooled resources come back.</summary>
[Collection("gpu")]
public class DeviceTests(BackendFixture backends)
{
    [Fact]
    public void EveryPipelineThePassesListIsMadeByTheWarmUp()
    {
        backends.Run(leg =>
        {
            var d = leg.Device;
            var keys = FramePipelines.Of(d).Keys.ToArray();
            var warm = d.Pipelines.WarmAsync(keys, CancellationToken.None);
            Assert.True(BackendFrames.Until(d, () => warm.IsCompleted), "the warm-up did not answer");
            var result = warm.GetAwaiter().GetResult();
            Assert.Empty(result.Failed);
            Assert.Equal(keys.Length, result.Asked);
            Assert.Equal(keys.Length, result.Made + result.FromCache);
            foreach (var key in keys)
                Assert.True(d.Pipelines.TryGet(key, out _), $"no pipeline for {d.Programs.Layout(key.Program).Name}");
            Assert.Empty(leg.TakeErrors());
        });
    }

    [Fact]
    public void AnUploadReadsBackTopRowFirst()
    {
        backends.Run(leg =>
        {
            var d = leg.Device;
            using var target = d.CreateTarget(new TargetDesc(4, 2, TextureFormat.Rgba8));
            var rgba = new byte[4 * 2 * 4];
            for (var i = 0; i < 4; i++)
            {
                rgba[i * 4] = 255;          // top row red
                rgba[(4 + i) * 4 + 2] = 255; // bottom row blue
            }
            for (var i = 0; i < 8; i++)
                rgba[i * 4 + 3] = 255;
            using var tex = Scene.Texture(d, 4, 2, TextureFormat.Rgba8, rgba, UploadFormat.Rgba8, TextureFilter.Nearest);
            var blit = d.Programs.Id("blit");
            BackendFrames.Pass(d, target, PassDesc.Keep, list => list.Draw(blit, DrawState.Opaque)
                .Set("uSrc", 0, 0, 1, 1)
                .Texture("uTex", tex)
                .Fullscreen());
            var px = Pixels.Read(d, target);
            Assert.Equal((255, 0, 0), Pixels.At(px, 4, 0, 0));
            Assert.Equal((0, 0, 255), Pixels.At(px, 4, 3, 1));
            Assert.Empty(leg.TakeErrors());
        });
    }

    [Fact]
    public void APooledTargetComesBackForTheSameShape()
    {
        backends.Run(leg =>
        {
            using var pool = new ResourcePool(leg.Device);
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
