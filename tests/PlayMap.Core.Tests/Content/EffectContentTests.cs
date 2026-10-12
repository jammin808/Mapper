using PlayMap.Core.Content;
using PlayMap.Core.Model;

namespace PlayMap.Core.Tests.Content;

/// <summary>The flash guard, the sync marks, the sting envelopes, the fractal and reactive maths and their
/// presets: the pure content the generators draw from.</summary>
public class EffectContentTests
{
    [Fact]
    public void TheFlashGuardLetsThroughNoMoreThanThreeFlashesASecond()
    {
        var guard = new FlashGuard();
        var lit = 0;
        var wasLit = false;
        // A strobe at 10 Hz for two seconds, sampled at 120 Hz.
        for (var i = 0; i < 240; i++)
        {
            var t = i / 120.0;
            var wanted = (t * 10) % 1 < 0.5 ? 1f : 0f;
            var drawn = guard.Limit(wanted, t);
            Assert.True(drawn <= FlashGuard.MaxLevel);
            var isLit = drawn > FlashGuard.RiseLevel;
            if (isLit && !wasLit)
                lit++;
            wasLit = isLit;
        }
        Assert.InRange(lit, 1, 2 * FlashGuard.MaxFlashesPerSecond);
        Assert.True(guard.Dropped > 0);
    }

    [Fact]
    public void TheFlashGuardCapsThePeakAndLetsASlowFlashThrough()
    {
        var guard = new FlashGuard();
        Assert.Equal(FlashGuard.MaxLevel, guard.Limit(1f, 0));
        Assert.Equal(0f, guard.Limit(0f, 0.2));
        Assert.Equal(FlashGuard.MaxLevel, guard.Limit(1f, 1.0));
        Assert.Equal(2, guard.Allowed);
    }

    [Fact]
    public void SyncMarksFlashOnTheLeadersTwoSecondGrid()
    {
        Assert.True(SyncMarks.IsFlash(0.0));
        Assert.True(SyncMarks.IsFlash(4.049));
        Assert.False(SyncMarks.IsFlash(4.051));
        Assert.False(SyncMarks.IsFlash(5.0));
        Assert.False(SyncMarks.IsFlash(-0.01));
        Assert.Equal(6.0, SyncMarks.NextMark(4.2));
        Assert.Equal(4.0, SyncMarks.MarkBefore(4.2));
        Assert.True(SyncMarks.IsFlash(10.1, periodSeconds: 5, flashSeconds: 0.2));
    }

    [Fact]
    public void AStingRisesAndSettlesToNothingByItsEnd()
    {
        var sting = new EffectImpulse(PulsePreset.Explosion, 10, 2);
        Assert.True(EffectEnvelope.At(sting, 9.99).IsZero);
        Assert.True(EffectEnvelope.At(sting, 10.16).Burst > 0.9f);
        Assert.True(EffectEnvelope.At(sting, 11.9).Burst < 0.05f);
        Assert.True(EffectEnvelope.At(sting, 12.0).IsZero);
        foreach (var preset in Enum.GetValues<PulsePreset>())
            Assert.False(EffectEnvelope.Weights(preset).IsZero, preset.ToString());
    }

    [Fact]
    public void FractalMathKnowsInsideFromOutside()
    {
        Assert.Equal(1.0, FractalMath.Sample(FractalKind.Mandelbrot, 0, 0, 0, 0, 100, 0));
        Assert.True(FractalMath.Sample(FractalKind.Mandelbrot, 2, 2, 0, 0, 100, 0) < 0.1);
        var newton = FractalMath.Sample(FractalKind.Newton, 1.1, 0.01, 0, 0, 40, 0);
        Assert.InRange(newton, 0, 1 / 3.0);
        Assert.InRange(FractalMath.Sample(FractalKind.DomainWarp, 0.3, 0.7, 0, 0, 0, 1.5), 0, 1);
    }

    [Fact]
    public void FractalPresetsFillWhatTheOperatorDidNotSet()
    {
        Assert.Equal(24, FractalPresets.Scenes.Count);
        var seahorse = FractalSettings.Resolve("fractal.mandelbrot", ParamSet.Empty.With("preset", "Seahorse valley"));
        Assert.Equal(60, seahorse.Zoom);
        Assert.Equal(-0.745, seahorse.CenterX);
        var mine = FractalSettings.Resolve("fractal.julia", ParamSet.Empty.With("preset", "Seahorse valley").With("zoom", 3));
        Assert.Equal(FractalKind.Julia, mine.Kind);
        Assert.Equal(3, mine.Zoom);
        var plain = FractalSettings.Resolve("fractal.mandelbrot", ParamSet.Empty);
        Assert.Equal(160, plain.Iterations);
        Assert.Equal(5, plain.Palette().Length);
    }

    [Fact]
    public void ReactiveScenesSampleInsideTheUnitRange()
    {
        var settings = ReactiveSettings.Resolve(ParamSet.Empty);
        var view = ReactiveView.Of(settings, 12.3);
        foreach (var scene in Enum.GetValues<ReactiveScene>())
            for (var y = -0.5; y <= 0.5; y += 0.25)
                for (var x = -0.8; x <= 0.8; x += 0.2)
                    Assert.InRange(ReactiveField.Sample(scene, x, y, view), 0, 1);
        var c = ReactiveField.Map(0.5, settings.Palette(), 1f);
        Assert.InRange(c.R, 0, 1);
    }

    [Fact]
    public void AColourTurnedAWholeTurnIsTheSameColour()
    {
        var c = ColorRgb.Parse("#FF8000", ColorRgb.Black);
        Assert.Equal(c, c.TurnHue(1));
        var turned = c.TurnHue(0.5f);
        Assert.NotEqual(c, turned);
        Assert.Equal("#FF8000", c.ToString());
        Assert.Equal(2, ColorRgb.ParseList("#FFFFFF, nope ,#000000", ColorRgb.White).Length);
    }
}
