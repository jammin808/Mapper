using PlayMap.Core.Content;
using PlayMap.Core.Model;
using PlayMap.Render.Generators;
using PlayMap.Render.Gpu;
using SkiaSharp;

namespace PlayMap.Render.Tests.Content;

/// <summary>The GPU particle field: after a seek it is, bit for bit, the field that played from the start (bounded memory, at the
/// 64 k a software renderer steps); every slot's birth, life and age on the GPU are the C# schedule's; a picture emitter gives birth
/// only where its source is bright; a sting surges the field.</summary>
[Collection("gl")]
public class ParticleDeterminismTests(GlFixture gl)
{
    [Fact]
    public void AFieldAfterASeekIsTheFieldThatPlayedFromTheStartByteForByte()
    {
        const int w = 64;
        const int h = 36;
        var g = Scene.Gen("particles.flow", ParamSet.Empty.With("count", ParticleFlowSettings.SoftwareCount).With("emitter", "disc")
            .With("vortex", 0.4).With("gravity", 0.05).With("edges", "bounce"), "gn_flow");
        var (played, sought) = gl.Run(c =>
        {
            using var services = new RenderServices(c.Device);
            using var a = new GeneratorHost(services);
            using var b = new GeneratorHost(services);
            // Played from 0 s to 10 s at 60 frames a second.
            long frame = 1;
            for (var f = 0; f <= 600; f++)
                ContentGl.Draw(a, g, f / 60.0, frame++, w, h);
            // Sought straight to 10 s, settled there.
            var fb = ContentGl.Settle(b, g, 10, 1, w, h);
            var ra = (FlowGenerator)a.RendererOf(g.Id, w, h)!;
            var rb = (FlowGenerator)b.RendererOf(g.Id, w, h)!;
            Assert.Equal(1200, ra.SimStep);
            Assert.Equal(1200, rb.SimStep);
            Assert.Equal(ParticleFlowSettings.SoftwareCount, rb.Count);
            var pa = Scene.ReadTexture(c.Device, ContentGl.Draw(a, g, 10, frame, w, h));
            var pb = Scene.ReadTexture(c.Device, ContentGl.Draw(b, g, 10, fb, w, h));
            return ((ContentGl.ReadState(c.Device, ra.State!, 0), ContentGl.ReadState(c.Device, ra.State!, 1), pa),
                (ContentGl.ReadState(c.Device, rb.State!, 0), ContentGl.ReadState(c.Device, rb.State!, 1), pb));
        });
        var alive = 0;
        for (var i = 3; i < played.Item1.Length; i += 4)
            if (played.Item1[i] > 0)
                alive++;
        Assert.True(alive > ParticleFlowSettings.SoftwareCount / 2, $"only {alive} particles alive");
        Assert.True(Bits(played.Item1).SequenceEqual(Bits(sought.Item1)), "positions differ after the seek");
        Assert.True(Bits(played.Item2).SequenceEqual(Bits(sought.Item2)), "velocities differ after the seek");
        Assert.True(played.Item3.AsSpan().SequenceEqual(sought.Item3), "the drawn field differs after the seek");
    }

    private static int[] Bits(float[] values) => [.. values.Select(BitConverter.SingleToInt32Bits)];

    [Fact]
    public void EverySlotsBirthLifeAndAgeOnTheGpuAreTheCSharpSchedules()
    {
        const int count = 4096;
        const double rate = 0.7;
        var ps = ParamSet.Empty.With("count", count).With("rate", rate).With("lifeMin", 0.5).With("lifeMax", 1.5).With("seed", 77);
        var g = Scene.Gen("particles.flow", ps, "gn_sched");
        var settings = ParticleFlowSettings.Resolve(ps);
        var s0 = gl.Run(c =>
        {
            using var services = new RenderServices(c.Device);
            using var host = new GeneratorHost(services);
            ContentGl.Settle(host, g, 3, 1, 32, 32);
            var r = (FlowGenerator)host.RendererOf(g.Id, 32, 32)!;
            Assert.Equal(360, r.SimStep);
            return ContentGl.ReadState(c.Device, r.State!, 0);
        });
        var side = ParticleFlowSettings.Side(count);
        var seedHash = ParticleSchedule.Pcg(settings.Seed);
        var step = ParticleSchedule.Biased(360);
        var (lit, dark) = (0, 0);
        for (var idx = 0; idx < count; idx++)
        {
            var key = (uint)idx ^ seedHash;
            var (life, _) = ParticleSchedule.Slot(key, settings.LifeMinSteps, settings.LifeMaxSteps);
            var (_, k, since) = ParticleSchedule.At(key, step, settings.LifeMinSteps, settings.LifeMaxSteps);
            var o = (idx / side * side + idx % side) * 4;
            if (ParticleSchedule.Alive(key, k, rate))
            {
                lit++;
                Assert.Equal(life * (1f / ParticleSchedule.StepsPerSecond), s0[o + 3]);
                Assert.Equal(since, (int)Math.Round(s0[o + 2] * ParticleSchedule.StepsPerSecond));
            }
            else
            {
                dark++;
                Assert.Equal(0f, s0[o + 3]);
            }
        }
        // The rate lights about its share of the slots.
        Assert.InRange(lit / (double)count, rate - 0.05, rate + 0.05);
        Assert.True(dark > 0);
    }

    [Fact]
    public void APictureEmitterGivesBirthOnlyWhereThePictureIsBright()
    {
        const int size = 64;
        var g = Scene.Gen("particles.flow", ParamSet.Empty.With("count", 16384).With("emitter", "picture").With("threshold", 0.2)
            .With("lifeMin", 0.5).With("lifeMax", 1), "gn_pic") with
        {
            Inputs = System.Collections.Immutable.ImmutableSortedDictionary.CreateRange(StringComparer.Ordinal,
                [new KeyValuePair<string, SourceRef>("source", SourceRef.Canvas("cv_disk"))]),
        };
        var (s0, words) = gl.Run(c =>
        {
            using var disk = DiskTexture(c.Device, 256, 0.2f);
            using var services = new RenderServices(c.Device);
            using var host = new GeneratorHost(services);
            var content = new ContentFrame { Pictures = new OnePicture(disk) };
            ContentGl.Settle(host, g, 4, 1, size, size, content);
            var r = (FlowGenerator)host.RendererOf(g.Id, size, size)!;
            return (ContentGl.ReadState(c.Device, r.State!, 0), r.Words);
        });
        Assert.Equal("", words);
        var newborn = 0;
        for (var i = 0; i < s0.Length; i += 4)
        {
            if (s0[i + 3] <= 0 || s0[i + 2] > 0.5f / ParticleSchedule.StepsPerSecond)
                continue;
            newborn++;
            var dx = s0[i] - 0.5;
            var dy = s0[i + 1] - 0.5;
            Assert.True(Math.Sqrt(dx * dx + dy * dy) <= 0.2 + 2.0 / 256, $"a particle born at ({s0[i]:0.###}, {s0[i + 1]:0.###}), outside the disk");
        }
        Assert.True(newborn > 0, "nothing was born on the step read");
        // With nothing to read the emitter says so and nothing is born.
        var (dark, why) = gl.Run(c =>
        {
            using var services = new RenderServices(c.Device);
            using var host = new GeneratorHost(services);
            ContentGl.Settle(host, g, 4, 1, size, size, new ContentFrame());
            var r = (FlowGenerator)host.RendererOf(g.Id, size, size)!;
            return (ContentGl.ReadState(c.Device, r.State!, 0), r.Words);
        });
        Assert.Contains("no picture", why, StringComparison.Ordinal);
        Assert.All(Enumerable.Range(0, dark.Length / 4), i => Assert.Equal(0f, dark[i * 4 + 3]));
    }

    [Fact]
    public void AStingSurgesTheField()
    {
        var g = Scene.Gen("particles.flow", ParamSet.Empty.With("count", 8192), "gn_sting");
        var (calm, stung) = gl.Run(c =>
        {
            using var services = new RenderServices(c.Device);
            using var a = new GeneratorHost(services);
            using var b = new GeneratorHost(services);
            var sting = new EffectImpulse(PulsePreset.Gust, 4.5, 1.0);
            var fa = ContentGl.Settle(a, g, 5, 1, 64, 36);
            var fb = ContentGl.Settle(b, g, 5, 1, 64, 36, impulse: sting);
            return (Scene.ReadTexture(c.Device, ContentGl.Draw(a, g, 5, fa, 64, 36)),
                Scene.ReadTexture(c.Device, ContentGl.Draw(b, g, 5, fb, 64, 36, impulse: sting)));
        });
        Assert.False(calm.AsSpan().SequenceEqual(stung), "the sting moved nothing");
    }

    private sealed class OnePicture(GpuTexture texture) : IContentPictures
    {
        public GpuTexture? Picture(SourceRef source, int width, int height) => texture;
    }

    /// <summary>A white disk of a radius (a share of the side) on black, centred.</summary>
    private static GpuTexture DiskTexture(GlDevice d, int side, float radius)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(side, side, SKColorType.Rgba8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bitmap))
        using (var paint = new SKPaint { IsAntialias = false, Color = SKColors.White })
        {
            canvas.Clear(SKColors.Black);
            canvas.DrawCircle(side / 2f, side / 2f, radius * side, paint);
        }
        var tex = d.CreateTexture(side, side, TextureFormat.Rgba8);
        d.UploadPointer(tex, bitmap.GetPixels(), UploadFormat.Rgba8, bitmap.RowBytes / 4);
        return tex;
    }
}
