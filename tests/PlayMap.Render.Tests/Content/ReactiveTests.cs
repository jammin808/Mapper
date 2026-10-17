using PlayMap.Core.Content;
using PlayMap.Core.Model;
using PlayMap.Render.Engine;
using PlayMap.Render.Generators;
using PlayMap.Tests;

namespace PlayMap.Render.Tests.Content;

/// <summary>Reactive scenes and the fractals hear the node's sound through the frame's bus: at <c>sound</c> 0 (every project from
/// before round 2) they draw the picture they always drew; a bass burst lifts the brightness, never past the flash guard's audio
/// bound; a node with no sound draws neutral and says so; a bound number moves with its source on the render thread; and a warm
/// frame of the new kinds, their bindings and the content frame allocates nothing.</summary>
[Collection("gl")]
public class ReactiveTests(GlFixture gl)
{
    private const int W = 96;
    private const int H = 54;

    public static TheoryData<string> Listening => new() { "reactive.scene", "fractal.mandelbulb", "fractal.julia" };

    [Theory]
    [MemberData(nameof(Listening))]
    public void SoundZeroDrawsThePictureItAlwaysDrew(string kind)
    {
        var old = Scene.Gen(kind, null, "gn_old");
        var zero = Scene.Gen(kind, ParamSet.Empty.With("sound", 0.0), "gn_zero");
        var loud = new FixedBus(("audio:level", 1.0), ("audio:bass", 1.0));
        var (a, b) = gl.Run(c =>
        {
            using var services = new RenderServices(c.Device);
            using var host = new GeneratorHost(services);
            return (Scene.ReadTexture(c.Device, ContentGl.Draw(host, old, 3, 1, W, H)),
                Scene.ReadTexture(c.Device, ContentGl.Draw(host, zero, 3, 2, W, H, loud)));
        });
        Assert.True(a.AsSpan().SequenceEqual(b), $"{kind} at sound 0 hears the bus");
    }

    /// <summary>The kinds with a brightness of their own, against which the bound is drawn (a 2D fractal's bound is held on the view
    /// in Core's tests).</summary>
    public static TheoryData<string> Bright => new() { "reactive.scene", "fractal.mandelbulb", "fractal.kifs" };

    [Theory]
    [MemberData(nameof(Bright))]
    public void ABassBurstLiftsTheBrightnessWithinTheGuardsBound(string kind)
    {
        var listening = Scene.Gen(kind, ParamSet.Empty.With("sound", 1.0), "gn_ears");
        var capped = Scene.Gen(kind, ParamSet.Empty.With("brightness", 1.0 + FlashGuard.MaxAudioBrightnessSwing), "gn_cap");
        var neutral = Scene.Gen(kind, null, "gn_neutral");
        var bus = new FixedBus(("audio:level", 0.5), ("audio:bass", 0.5));
        var (quiet, burst, cap, plain) = gl.Run(c =>
        {
            using var services = new RenderServices(c.Device);
            using var host = new GeneratorHost(services);
            var q = Scene.ReadTexture(c.Device, ContentGl.Draw(host, listening, 3, 1, W, H, bus));
            bus.Set("audio:bass", 1.0);
            bus.Set("audio:level", 1.0);
            var b = Scene.ReadTexture(c.Device, ContentGl.Draw(host, listening, 3, 2, W, H, bus));
            return (q, b, Scene.ReadTexture(c.Device, ContentGl.Draw(host, capped, 3, 3, W, H)),
                Scene.ReadTexture(c.Device, ContentGl.Draw(host, neutral, 3, 4, W, H)));
        });
        // Neutral sound draws the scene as it is; the burst lifts it; nothing goes past the scene drawn at the bound's brightness.
        Assert.Equal(Mean(plain), Mean(quiet), 1);
        Assert.True(Mean(burst) > Mean(quiet) + 1, $"{kind}: the burst lifted nothing ({Mean(quiet):0.##} → {Mean(burst):0.##})");
        for (var i = 0; i < burst.Length; i++)
            Assert.True(burst[i] <= cap[i] + 1, $"{kind}: a value of {burst[i]} past the bound's {cap[i]} at byte {i}");
    }

    private static double Mean(byte[] bgra)
    {
        var sum = 0L;
        for (var i = 0; i < bgra.Length; i += 4)
            sum += bgra[i] + bgra[i + 1] + bgra[i + 2];
        return sum / (bgra.Length / 4 * 3.0);
    }

    [Fact]
    public void ANodeWithNoSoundDrawsNeutralAndSaysSo()
    {
        var g = Scene.Gen("reactive.scene", ParamSet.Empty.With("sound", 1.0), "gn_deaf");
        var content = new ContentFrame();
        var words = gl.Run(c =>
        {
            using var services = new RenderServices(c.Device);
            using var host = new GeneratorHost(services);
            ContentGl.Draw(host, g, 3, 1, W, H, content.Over(null));
            return host.RendererOf(g.Id, W, H)!.Words;
        });
        Assert.Equal(GeneratorRenderer.NoSoundWords, words);
        Assert.True(content.Stats.TryGet(g.Id, out var live));
        Assert.Equal(GeneratorRenderer.NoSoundWords, live.Words);
    }

    [Fact]
    public void ABoundNumberMovesWithItsSourceOnTheRenderThreadAndTheProjectKeepsItsBase()
    {
        var binding = new ParamBinding("speed", "lfo:test", 1, 0, BindingCurve.Linear, 0) { Mode = BindingMode.Replace };
        var g = Scene.Gen("reactive.scene", null, "gn_bound") with { Bindings = [binding] };
        var bus = new FixedBus(("lfo:test", 0.0));
        var content = new ContentFrame();
        var (low, high, live) = gl.Run(c =>
        {
            using var services = new RenderServices(c.Device);
            using var host = new GeneratorHost(services);
            var a = Scene.ReadTexture(c.Device, ContentGl.Draw(host, g, 3, 1, W, H, content.Over(bus)));
            bus.Set("lfo:test", 1.0);
            var b = Scene.ReadTexture(c.Device, ContentGl.Draw(host, g, 3, 2, W, H, content.Over(bus)));
            content.Stats.TryGet(g.Id, out var l);
            return (a, b, l!.Live());
        });
        Assert.False(low.AsSpan().SequenceEqual(high), "the bound speed moved nothing");
        Assert.Equal(["speed"], live.Names);
        Assert.Equal(3.0, live.Values[0], 6);
        Assert.Null(g.Params.Get("speed"));
    }

    [Fact]
    public void AWarmFrameOfTheNewKindsAllocatesNothing()
    {
        var generators = new[]
        {
            Scene.Gen("particles.flow", ParamSet.Empty.With("count", 4096), "gn_flow") with
            {
                Bindings = [new ParamBinding("curl", "lfo:test", 0.5, 0, BindingCurve.Linear, 0)],
            },
            Scene.Gen("fractal.mandelbulb", ParamSet.Empty.With("sound", 0.5).With("steps", 32), "gn_bulb"),
            Scene.Gen("fractal.mandelbox", ParamSet.Empty.With("steps", 32), "gn_box"),
            Scene.Gen("reactive.scene", ParamSet.Empty.With("sound", 1.0), "gn_scene") with
            {
                Bindings = [new ParamBinding("speed", "lfo:test", 0.5, 0, BindingCurve.Linear, 0)],
            },
        };
        var outputs = generators.Select(g => Scene.SourceOutput("op_" + g.Id[3..], SourceRef.Generator(g.Id), 64, 36)).ToArray();
        var project = Scene.Project([.. generators], [.. outputs]);
        var bus = new FixedBus(("lfo:test", 0.3), ("audio:level", 0.4), ("audio:bass", 0.6));
        var content = new ContentFrame();
        var allocated = gl.Run(c =>
        {
            // The device is the fixture's: what earlier tests left in its error log is theirs.
            c.Device.TakeErrors();
            using var engine = new RenderEngine(c, new RenderEngineOptions(GpuTiming: true));
            FrameInputs Inputs(int f) => Scene.Inputs(project, 10 + f / 60.0) with
            {
                Modulation = content.Over(bus),
                Impulse = new EffectImpulse(PulsePreset.Bloom, 10.2, 0.5),
            };
            for (var f = 0; f < 30; f++)
            {
                content.BeginItems();
                content.SetItemTime("gn_box", f * 16_666_667L);
                engine.Render(Inputs(f));
            }
            var before = Allocations.Start();
            for (var f = 30; f < 90; f++)
            {
                content.BeginItems();
                content.SetItemTime("gn_box", f * 16_666_667L);
                engine.Render(Inputs(f));
            }
            var bytes = Allocations.Since(before);
            Assert.Empty(c.Device.TakeErrors());
            Assert.Empty(engine.GeneratorFaults());
            return bytes;
        });
        Assert.True(allocated == 0, $"sixty warm frames allocated {allocated} bytes");
    }
}
