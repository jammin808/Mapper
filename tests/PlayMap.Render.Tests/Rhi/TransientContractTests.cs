using PlayMap.Core.Gpu;
using PlayMap.Render.Gl;

namespace PlayMap.Render.Tests.Rhi;

/// <summary>The render graph's transient contract on the GL backend: a plan's memory rents every placement from the backend's pool
/// (a block given back hands its placements back, and the next block of the same shapes gets the very same objects, making no GL
/// object), checks each offset against the block and keeps every shape apart; and aliasing barriers, discards and plan-managed
/// resources change no pixel — a scene drawn through placed transients with every one of them reads byte for byte what it reads
/// without them, which is what the device's own calls draw.</summary>
[Collection("gpu")]
public sealed class TransientContractTests(BackendFixture backends)
{
    private static readonly TextureDesc Picture = new(16, 16, TextureFormat.Rgba8, ResourcePoolUses);
    private static readonly TargetDesc Canvas = new(64, 32, TextureFormat.Rgba8);

    private const TextureUsage ResourcePoolUses = TextureUsage.Sampled | TextureUsage.CopySource | TextureUsage.CopyDest;

    [Fact]
    public void PlacementsComeFromThePoolAndGoBackToItWithTheirBlock()
    {
        backends.RunGl(c =>
        {
            var backend = c.Backend;
            var pool = backend.TransientPool;
            var lent = pool.Lent;
            int resting;
            GpuTexture placed;
            RenderTarget target;
            using (var memory = backend.CreateTransientMemory(1 << 20, "plan a"))
            {
                placed = memory.Place(Picture, 0);
                target = memory.PlaceTarget(Canvas, 65536);
                resting = pool.Resting;
                Assert.Equal(lent + 2, pool.Lent);
                Assert.Equal((Picture.Width, Picture.Height, Picture.Format, Picture.Usage), (placed.Width, placed.Height, placed.Format, placed.Usage));
                Assert.Equal((Canvas.Width, Canvas.Height, Canvas.Color0, false), (target.Width, target.Height, target.Format, target.HasDepth));
            }
            Assert.Equal(lent, pool.Lent);
            Assert.Equal(resting + 2, pool.Resting);
            Assert.False(placed.IsDisposed);
            Assert.False(target.IsDisposed);

            var textures = c.Device.LiveTextures;
            var targets = c.Device.LiveTargets;
            using (var memory = backend.CreateTransientMemory(1 << 20, "plan b"))
            {
                // The offsets differ from plan a's: GL places nothing, it rents by shape.
                Assert.Same(placed, memory.Place(Picture, 4096));
                Assert.Same(target, memory.PlaceTarget(Canvas, 0));
                Assert.Equal((textures, targets), (c.Device.LiveTextures, c.Device.LiveTargets));

                var nearest = memory.Place(Picture with { Filter = TextureFilter.Nearest }, 0);
                var deep = memory.PlaceTarget(Canvas with { Depth = true }, 0);
                Assert.NotSame(placed, nearest);
                Assert.Equal(TextureFilter.Nearest, nearest.Filter);
                Assert.NotSame(target, deep);
                Assert.True(deep.HasDepth);
                Assert.Equal(lent + 4, pool.Lent);
            }
            Assert.Equal(lent, pool.Lent);
            Assert.Empty(c.Device.TakeErrors());
        });
    }

    [Fact]
    public void APlacementIsSizedByItsTexelsAndOneThatDoesNotFitTheBlockIsRefused()
    {
        backends.RunGl(c =>
        {
            var backend = c.Backend;
            using var memory = backend.CreateTransientMemory(8192, "small");
            Assert.Equal((16L * 16 * 4, 1L), memory.SizeOf(Picture));
            Assert.Equal((64L * 32 * 8 + 64L * 32 * 4, 1L), memory.SizeOf(new TargetDesc(64, 32, TextureFormat.Rgba16F, Depth: true)));
            Assert.Equal((64L * 32 * 16 * 2, 1L), memory.SizeOf(new TargetDesc(64, 32, TextureFormat.Rgba32F, TextureFormat.Rgba32F)));
            Assert.Equal((8L * 4 * 8, 1L), memory.SizeOf(new TextureDesc(30, 16, TextureFormat.Bc1, TextureUsage.Sampled)));

            var lent = backend.TransientPool.Lent;
            Assert.Throws<ArgumentOutOfRangeException>(() => memory.Place(Picture, -1));
            Assert.Throws<ArgumentOutOfRangeException>(() => memory.Place(Picture, 8192 - 16 * 16 * 4 + 1));
            Assert.Throws<ArgumentOutOfRangeException>(() => memory.PlaceTarget(Canvas with { Depth = true }, 0));
            Assert.Equal(lent, backend.TransientPool.Lent);
            // The last offset that fits is taken, and a second placement at one offset is a second texture (GL never aliases).
            var last = memory.Place(Picture, 8192 - 16 * 16 * 4);
            Assert.NotSame(last, memory.Place(Picture, 8192 - 16 * 16 * 4));
            Assert.Equal(lent + 2, backend.TransientPool.Lent);
            Assert.Throws<ArgumentOutOfRangeException>(() => backend.CreateTransientMemory(0, "none"));
        });
    }

    [Fact]
    public void AliasDiscardAndPlanManagedChangeNoPixel()
    {
        backends.RunGl(c =>
        {
            var plain = Draw(c, contract: false);
            var marked = Draw(c, contract: true);
            var device = DrawWithTheDevicesCalls(c);
            Assert.Equal(plain, marked);
            Assert.Equal(device, plain);
            // The scene draws something: the checker, the clear and the blended picture all show.
            Assert.True(plain.Distinct().Count() > 4);
            Assert.Empty(c.Device.TakeErrors());
        });
    }

    /// <summary>The scene through the backend: placed transients, a cleared pass, a checker, the picture blended over it. With
    /// <paramref name="contract"/>, every transient is plan-managed, discarded before its first use, aliased over the transient that
    /// held its memory before it, and moved by barriers.</summary>
    private static byte[] Draw(RenderContext c, bool contract)
    {
        var backend = c.Backend;
        var checker = backend.Programs.Id("checker");
        var quad = backend.Programs.Id("quad");
        using var memory = backend.CreateTransientMemory(1 << 20, contract ? "with the contract" : "without");
        var earlier = memory.Place(Picture with { Filter = TextureFilter.Nearest }, 0);
        var picture = memory.Place(Picture, 0);
        var canvas = memory.PlaceTarget(Canvas, 4096);
        if (contract)
        {
            foreach (var r in new GpuResource[] { earlier, picture, canvas })
                backend.SetPlanManaged(r, true);
        }
        BackendFrames.Run(backend, list =>
        {
            if (contract)
            {
                list.Alias(earlier, picture);
                list.Discard(picture);
                list.Barrier(picture, ResourceUse.CopyDest);
            }
            list.Upload(picture, Pattern(), UploadFormat.Rgba8);
            if (contract)
            {
                list.Alias(null, canvas);
                list.Discard(canvas);
                list.Barrier(picture, ResourceUse.Sampled);
                list.Barrier(canvas, ResourceUse.RenderTarget);
            }
            list.BeginPass(canvas, new PassDesc(LoadOp.Clear, new System.Numerics.Vector4(0.1f, 0.2f, 0.3f, 1)));
            list.Draw(checker, new DrawState(BlendMode.Opaque, DepthMode.Off, CullMode.None, Topology.Triangles))
                .Set("uCell", 8f)
                .Set("uColorA", 0.9f, 0.1f, 0.1f, 1)
                .Set("uColorB", 0.1f, 0.8f, 0.2f, 1)
                .SetInt("uSwap", 0)
                .Fullscreen();
            list.Draw(quad, new DrawState(BlendMode.Normal, DepthMode.Off, CullMode.None, Topology.TriangleStrip))
                .Set("uRect", 8, 4, 32, 16)
                .Set("uTarget", 64, 32)
                .Set("uUv", 0, 0, 1, 1)
                .SetInt("uMode", 0)
                .Set("uOpacity", 0.5f)
                .Set("uTint", 1, 1, 1, 1)
                .Texture("uTex", picture)
                .GeneratedQuads();
            list.EndPass();
            if (contract)
                list.Barrier(canvas, ResourceUse.CopySource);
        });
        var pixels = Pixels.Read(backend, canvas);
        if (contract)
        {
            foreach (var r in new GpuResource[] { earlier, picture, canvas })
                backend.SetPlanManaged(r, false);
        }
        return pixels;
    }

    /// <summary>The same scene through the device's own calls, as the frame code draws today.</summary>
    private static byte[] DrawWithTheDevicesCalls(RenderContext c)
    {
        var d = c.Device;
        using var picture = d.CreateTexture(Picture.Width, Picture.Height, Picture.Format);
        using var canvas = d.CreateRenderTarget(Canvas.Width, Canvas.Height, Canvas.Color0);
        d.Upload(picture, Pattern(), UploadFormat.Rgba8);
        d.BindTarget(canvas);
        d.SetBlend(BlendMode.Opaque);
        d.SetDepth(false, false);
        d.SetCull(CullMode.None);
        d.Clear(0.1f, 0.2f, 0.3f, 1);
        var checker = d.Program("checker");
        checker.Set("uCell", 8f);
        checker.Set("uColorA", 0.9f, 0.1f, 0.1f, 1);
        checker.Set("uColorB", 0.1f, 0.8f, 0.2f, 1);
        checker.SetInt("uSwap", 0);
        d.DrawFullscreen(checker);
        d.SetBlend(BlendMode.Normal);
        var quad = d.Program("quad");
        quad.Set("uRect", 8, 4, 32, 16);
        quad.Set("uTarget", 64, 32);
        quad.Set("uUv", 0, 0, 1, 1);
        quad.SetInt("uMode", 0);
        quad.Set("uOpacity", 0.5f);
        quad.Set("uTint", 1, 1, 1, 1);
        d.BindSampler(quad, "uTex", 0, picture);
        d.DrawGeneratedQuads(quad);
        d.SetBlend(BlendMode.Opaque);
        return Pixels.Read(d.Backend, canvas);
    }

    /// <summary>A 16×16 RGBA picture with a gradient and a half-transparent band.</summary>
    private static byte[] Pattern()
    {
        var bytes = new byte[16 * 16 * 4];
        for (var y = 0; y < 16; y++)
        {
            for (var x = 0; x < 16; x++)
            {
                var i = (y * 16 + x) * 4;
                bytes[i] = (byte)(x * 16);
                bytes[i + 1] = (byte)(y * 16);
                bytes[i + 2] = (byte)(255 - x * 8);
                bytes[i + 3] = (byte)(y < 8 ? 255 : 128);
            }
        }
        return bytes;
    }
}
