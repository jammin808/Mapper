using System.Numerics;
using PlayMap.Core.Content;
using PlayMap.Core.Model;
using PlayMap.Core.Playback;

namespace PlayMap.Core.Tests.Content;

/// <summary>Content's pure parts: a generator's own time in double (no drift days on), the precision warning past 2¹⁶ s, an item's
/// clip time for a routed generator; the particle schedule (a birth every life, a phase in it, the alive share at the rate); the
/// distance estimators (zero on the surface the marcher finds, about the distance off it); the stings; the traits; the new kinds'
/// parameters resolving; and the sound's swing held to the flash guard's bound on every listening view.</summary>
public sealed class ContentCoreTests
{
    [Fact]
    public void ContentTimeIsDoubleSecondsFromTheOriginAndStaysExactDaysOn()
    {
        Assert.Equal(2.5, ContentTime.Seconds(3_500_000_000, 1_000_000_000));
        Assert.Equal(-1, ContentTime.Seconds(0, 1_000_000_000));
        const long week = 7L * 86_400 * 1_000_000_000;
        Assert.Equal(10.000000001, ContentTime.Seconds(week + 10_000_000_001, week), 12);
        Assert.Equal(0.25, ContentTime.Phase(10.25, 1), 12);
        Assert.Equal(0, ContentTime.Phase(4, 0.5), 12);
        Assert.Equal(1.5, ContentTime.Wrap(7.5, 2), 12);
        Assert.Equal(7.5, ContentTime.Wrap(7.5, 0), 12);
    }

    [Fact]
    public void ThePrecisionWarningIsForNonPeriodicKindsPastTwoToTheSixteenSeconds()
    {
        Assert.False(ContentTime.PrecisionLost("fractal.mandelbulb", 65_535));
        Assert.True(ContentTime.PrecisionLost("fractal.mandelbulb", 65_537));
        Assert.True(ContentTime.PrecisionLost("particles.flow", 70_000));
        // The sync marks have a period: their float time is wrapped, so it never loses precision.
        Assert.False(ContentTime.PrecisionLost("pattern.syncmarks", 1e6));
    }

    [Fact]
    public void ARoutedGeneratorsItemTimeIsItsClipsPositionPastItsInPoint()
    {
        var clip = ClipTransport.Load("it1", 2_000_000_000, 12_000_000_000, loop: true, 0);
        // Cued: held at its in point.
        Assert.Equal(0, PlaylistCursor.ItemTimeNs(clip, 50_000_000_000));
        var playing = clip.Play(100_000_000_000).Transport;
        Assert.Equal(3_000_000_000, PlaylistCursor.ItemTimeNs(playing, 103_000_000_000));
        // Wrapped by the loop: 10 s long, 13 s in is 3 s in.
        Assert.Equal(3_000_000_000, PlaylistCursor.ItemTimeNs(playing, 113_000_000_000));
        var paused = playing.Pause(104_000_000_000).Transport;
        Assert.Equal(4_000_000_000, PlaylistCursor.ItemTimeNs(paused, 200_000_000_000));
        Assert.Equal(0, PlaylistCursor.ItemTimeNs(ClipTransport.Empty, 5));
    }

    [Fact]
    public void EverySlotIsBornOnceEveryLifeAtItsPhase()
    {
        for (uint key = 0; key < 2000; key += 7)
        {
            var (life, phase) = ParticleSchedule.Slot(key, 60, 180);
            Assert.InRange(life, 60, 180);
            Assert.InRange(phase, 0, life - 1);
            var births = new List<long>();
            for (long n = 0; n < 3 * life; n++)
            {
                var (birth, k, since) = ParticleSchedule.At(key, ParticleSchedule.Biased(n), 60, 180);
                Assert.InRange(since, 0, life - 1);
                if (birth)
                    births.Add(n);
                if (n > 0 && !birth)
                    Assert.Equal(ParticleSchedule.At(key, ParticleSchedule.Biased(n - 1), 60, 180).LifeIndex, k);
            }
            Assert.True(births.Count is 3, $"slot {key}: {births.Count} births in three lives");
            Assert.All(births.Zip(births.Skip(1)), p => Assert.Equal(life, p.Second - p.First));
        }
    }

    [Fact]
    public void TheAliveShareIsTheRateAndTheHashIsTheShadersWords()
    {
        var lit = Enumerable.Range(0, 20_000).Count(i => ParticleSchedule.Alive((uint)i, 3, 0.3));
        Assert.InRange(lit / 20_000.0, 0.28, 0.32);
        Assert.Equal(0, Enumerable.Range(0, 1000).Count(i => ParticleSchedule.Alive((uint)i, 3, 0)));
        Assert.Equal(1000, Enumerable.Range(0, 1000).Count(i => ParticleSchedule.Alive((uint)i, 3, 1)));
        // PCG's output hash, known values (the shader computes the same words).
        Assert.Equal(129708002u, ParticleSchedule.Pcg(0));
        Assert.Equal(2831084092u, ParticleSchedule.Pcg(1));
        Assert.InRange(ParticleSchedule.U01(uint.MaxValue), 0.999f, 1f);
        Assert.True(ParticleSchedule.U01(uint.MaxValue) < 1f);
    }

    public static TheoryData<string> Raymarched => new() { "fractal.mandelbulb", "fractal.mandelbox", "fractal.kifs" };

    [Theory]
    [MemberData(nameof(Raymarched))]
    public void ADistanceEstimateIsZeroOnTheSurfaceAndAboutTheDistanceOffIt(string kind)
    {
        var s = Fractal3DSettings.Resolve(kind, ParamSet.Empty);
        var family = s.Kind;
        var rng = new Core.Numerics.Pcg32(42);
        var camera = Fractal3DCamera.Orbit(0, 1, 3.2, 0.5, 45);
        var hits = 0;
        for (var i = 0; i < 200; i++)
        {
            var dir = camera.Ray((float)(rng.NextDouble() * 64), (float)(rng.NextDouble() * 36), 64, 36);
            var t = Fractal3D.March(family, s.De, camera.Eye, dir, 128, 0.002f);
            if (t < 0)
                continue;
            hits++;
            // On the surface the estimate is all but zero; a step back along the ray it is positive and about the step (an estimate
            // may run a little long — the Mandelbox's does — never by a quarter).
            var on = camera.Eye + dir * t;
            Assert.True(Fractal3D.Distance(family, on, s.De) < 0.002f * Math.Max(t, 0.05f) + 1e-6f);
            var back = camera.Eye + dir * (t * 0.9f);
            var d = Fractal3D.Distance(family, back, s.De);
            Assert.True(d > 0 && d <= 1.25f * t * 0.1f + 1e-4f, $"{kind}: an estimate of {d} where the surface is {t * 0.1f} away along the ray");
        }
        Assert.True(hits > 20, $"{kind}: only {hits} rays met it");
        // Far away the estimate is positive.
        Assert.True(Fractal3D.Distance(family, new Vector3(0, 0, 14), s.De) > 0);
    }

    [Fact]
    public void TheNewKindsResolveTheirDefaultsAndTheirTraits()
    {
        var flow = ParticleFlowSettings.Resolve(ParamSet.Empty);
        Assert.Equal((262_144, FlowEmitter.Area, 4.0, 480), (flow.Count, flow.Emitter, flow.LifeMaxSeconds, flow.LifeMaxSteps));
        Assert.Equal(512, ParticleFlowSettings.Side(flow.Count));
        Assert.Equal(65_536, ParticleFlowSettings.Resolve(ParamSet.Empty, ParticleFlowSettings.SoftwareCount).Count);
        var box = Fractal3DSettings.Resolve("fractal.mandelbox", ParamSet.Empty.With("scale", -2.0));
        Assert.Equal((Fractal3DKind.Mandelbox, -2f, 12), (box.Kind, box.De.Scale, box.De.Iterations));
        Assert.Equal(KifsFold.Menger, Fractal3DSettings.Resolve("fractal.kifs", ParamSet.Empty.With("fold", "menger")).De.Fold);
        Assert.Throws<ArgumentException>(() => Fractal3DSettings.Resolve("fractal.julia", ParamSet.Empty));
        foreach (var kind in GeneratorCatalog.All.Where(k => k.Family is "fractal" || k.Id == "reactive.scene"))
            Assert.Equal("0", kind.Param("sound")!.Default);
        Assert.Equal(TimeClass.Bounded, GeneratorTraits.Of("particles.flow").Time);
        Assert.True(GeneratorTraits.Of("fractal.kifs").DrawsBindings);
        Assert.False(GeneratorTraits.Of("particles.field").DrawsBindings);
        Assert.False(GeneratorTraits.Of("pattern.testcard").DrawsBindings);
        Assert.True(GeneratorTraits.OnLeaderClock("pattern.syncmarks"));
        Assert.Equal(TimeClass.Live, GeneratorTraits.Of("reactive.scene").With(listens: true));
    }

    [Fact]
    public void EveryPulsePresetIsAStingWithItsChannels()
    {
        Assert.Equal(Enum.GetValues<PulsePreset>().Length, Stings.All.Count);
        Assert.Equal(PulsePreset.Supernova, Stings.Find(" supernova ")!.Preset);
        Assert.Null(Stings.Find("Fireworks"));
        var flash = Stings.Find("Flash")!;
        Assert.False(flash.Scored);
        Assert.Contains("flash", flash.Channels);
        Assert.Equal(900, flash.DefaultMs);
        Assert.True(Stings.Find("Rainbow")!.Scored);
        Assert.Contains("hue", Stings.Find("Rainbow")!.Channels);
        Assert.All(Stings.All, s => Assert.InRange(s.DefaultMs, Stings.MinMs, Stings.MaxMs));
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(0.5f)]
    [InlineData(1f)]
    public void TheSoundsSwingIsHeldToTheGuardsBound(float low)
    {
        var scene = ReactiveSettings.Resolve(ParamSet.Empty);
        var quiet = ReactiveView.Of(scene, ReactiveNumbers.Of(scene), 3, default, ReactiveSound.None);
        Assert.Equal(ReactiveView.Of(scene, 3), quiet);
        var heard = ReactiveView.Of(scene, ReactiveNumbers.Of(scene), 3, default, new ReactiveSound(low, low, 1));
        Assert.InRange(heard.Brightness - quiet.Brightness, -FlashGuard.MaxAudioBrightnessSwing - 1e-6, FlashGuard.MaxAudioBrightnessSwing + 1e-6);
        Assert.Equal(low > 0.5f, heard.Brightness > quiet.Brightness);
        // A 2D fractal's view takes the same bound.
        var julia = FractalSettings.Resolve("fractal.julia", ParamSet.Empty);
        var view = FractalView.Of(julia, FractalNumbers.Of(julia), 3);
        Assert.Equal(FractalView.Of(julia, 3), view);
        var bright = FlashGuard.Brightness((float)view.Brightness, low, 1);
        Assert.InRange(bright - view.Brightness, -FlashGuard.MaxAudioBrightnessSwing - 1e-6, FlashGuard.MaxAudioBrightnessSwing + 1e-6);
    }
}
