using PlayMap.Core.Model;
using PlayMap.Core.Modulation;

namespace PlayMap.Core.Tests.Audio;

/// <summary>What a binding makes of a number, by its rule: ADD, SCALE and REPLACE, invert, offset, steps and the curves; RATE, a
/// disabled binding and a missing source hold the base. And the plan on a surface's opacity, an effect slot's mix and a layer's
/// rectangle: the drawn project carries the live numbers, the bindings taken off, and the show is never written.</summary>
public sealed class BindingEvalTests
{
    private static ParamBinding B(BindingMode mode, double amount = 1, double offset = 0) =>
        new("p", "beat", amount, offset, BindingCurve.Linear, 0) { Mode = mode };

    /// <summary>A bus whose sources read fixed values.</summary>
    private sealed class FixedBus(Dictionary<string, double> values) : IModulationBus
    {
        public bool TryRead(string source, long leaderNs, out double value01)
        {
            if (values.TryGetValue(source, out value01))
                return true;
            value01 = 0.5;
            return false;
        }

        public bool IsLive(string source) => false;
    }

    [Fact]
    public void AddMovesTheBaseByAmountOfTheRangeAndClamps()
    {
        Assert.Equal(0.5, BindingEval.Apply(B(BindingMode.Add, 0.5), 0.6, 0.2, 0, 1), 12);
        Assert.Equal(1, BindingEval.Apply(B(BindingMode.Add, 1), 1, 0.2, 0, 1), 12);
        Assert.Equal(0, BindingEval.Apply(B(BindingMode.Add, -1), 1, 0.2, 0, 1), 12);
        Assert.Equal(5, BindingEval.Apply(B(BindingMode.Add, 0.25), 1, 0, -10, 10), 12);
    }

    [Fact]
    public void ScaleLetsQuietPullTheNumberDown()
    {
        Assert.Equal(0.4, BindingEval.Apply(B(BindingMode.Scale, 0.5), 0, 0.8, 0, 1), 12);
        Assert.Equal(0.6, BindingEval.Apply(B(BindingMode.Scale, 0.5), 0.5, 0.8, 0, 1), 12);
        Assert.Equal(0.8, BindingEval.Apply(B(BindingMode.Scale, 0.5), 1, 0.8, 0, 1), 12);
        Assert.Equal(0.4, BindingEval.Apply(B(BindingMode.Scale, -0.5), 0, 0.8, 0, 1), 12);
    }

    [Fact]
    public void ReplaceSpansItsOwnShareOfTheRange()
    {
        var b = B(BindingMode.Replace) with { RangeMin = 0.2, RangeMax = 0.6 };
        Assert.Equal(2, BindingEval.Apply(b, 0, 7, 0, 10), 12);
        Assert.Equal(4, BindingEval.Apply(b, 0.5, 7, 0, 10), 12);
        Assert.Equal(6, BindingEval.Apply(b, 1, 7, 0, 10), 12);
    }

    [Fact]
    public void InvertOffsetStepsAndCurvesShapeTheSource()
    {
        Assert.Equal(0.75, BindingEval.Shape(B(BindingMode.Add) with { Invert = true }, 0.25), 12);
        Assert.Equal(0.3, BindingEval.Shape(B(BindingMode.Add, offset: 0.2), 0.5), 12);
        Assert.Equal(0, BindingEval.Shape(B(BindingMode.Add, offset: 0.7), 0.5), 12);
        Assert.Equal(0.25, BindingEval.Shape(B(BindingMode.Add) with { Steps = 5 }, 0.33), 12);
        Assert.Equal(1, BindingEval.Shape(B(BindingMode.Add) with { Steps = 1 }, 0.6), 12);
        Assert.Equal(0, BindingEval.Shape(B(BindingMode.Add) with { Steps = 1 }, 0.4), 12);
        Assert.Equal(0.15625, BindingEval.Curve(BindingCurve.Ease, 0.25), 12);
        Assert.Equal(0, BindingEval.Curve(BindingCurve.Step, 0.49), 12);
        Assert.Equal(1, BindingEval.Curve(BindingCurve.Step, 0.5), 12);
        Assert.Equal(0.5, BindingEval.Curve(BindingCurve.SCurve, 0.5), 12);
        foreach (var curve in Enum.GetValues<BindingCurve>())
        {
            Assert.Equal(0, BindingEval.Curve(curve, 0), 12);
            Assert.Equal(1, BindingEval.Curve(curve, 1), 12);
            for (var u = 0.0; u < 1; u += 0.01)
                Assert.True(BindingEval.Curve(curve, u + 0.01) >= BindingEval.Curve(curve, u), $"{curve} falls at {u}");
        }
        Assert.True(BindingEval.Curve(BindingCurve.Exponential, 0.5) < 0.5 && BindingEval.Curve(BindingCurve.Logarithmic, 0.5) > 0.5);
    }

    [Fact]
    public void RateADisabledBindingAndAMissingSourceHoldTheBase()
    {
        Assert.Equal(0.3, BindingEval.Apply(B(BindingMode.Rate), 1, 0.3, 0, 1), 12);
        Assert.Equal(0.3, BindingEval.Apply(B(BindingMode.Add) with { Enabled = false }, 1, 0.3, 0, 1), 12);
        Assert.Equal(0.3, BindingEval.Apply(B(BindingMode.Add), double.NaN, 0.3, 0, 1), 12);
    }

    [Fact]
    public void ASurfacesOpacityAnEffectsMixAndALayersRectangleAreDrawnLive()
    {
        var slot = new FxSlot("fx_blur", "fx.blur", "Blur", ParamSet.Empty, false, 0.8, FxBlend.Normal, 1, 0)
        {
            Bindings = [new ParamBinding("mix", "bar", 0.5, 0, BindingCurve.Linear, 0) { Mode = BindingMode.Scale }],
        };
        var layer = new Layer("ly_a", "A", SourceRef.Solid("#ffffff"), new RectD(100, 0, 960, 540), 1, LayerBlend.Normal, true)
        {
            Fx = new FxChain([slot]),
            Bindings = [new ParamBinding("rect.x", "beat", 0.25, 0, BindingCurve.Linear, 0)],
        };
        var surface = new Surface("sf_wall", "Wall", "ms_wall", Transform3.Identity, [], SurfaceContent.Empty, 0.5, [], true, false, "#ffffff", [])
        {
            Bindings = [new ParamBinding("opacity", "audio:bass", 1, 0, BindingCurve.Linear, 0) { Mode = BindingMode.Replace, RangeMin = 0.1, RangeMax = 0.9 }],
        };
        var show = Project.CreateDefault("plan") with { Canvases = [new Canvas("cv_a", "A", 1920, 1080, "#000000", [layer])] };
        show = show.WithStage(s => s with { Surfaces = [surface] });
        var plan = ModulationPlan.For(show);
        Assert.Equal(3, plan.Items.Length);
        Assert.All(plan.Items, i => Assert.Equal("", i.Problem));

        var drawn = plan.Apply(new FixedBus(new() { ["bar"] = 0.5, ["beat"] = 1, ["audio:bass"] = 1 }), 0, bypassAll: false);
        var wall = drawn.FindSurface("sf_wall")!;
        Assert.Equal(0.9, wall.Opacity, 12); // REPLACE: 0.1 + 1·0.8 of 0–1.
        Assert.Empty(wall.Bindings);
        var drawnSlot = drawn.FindFx("fx_blur")!.Value.Slot;
        Assert.Equal(0.6, drawnSlot.Mix, 12); // SCALE by half: 0.8·lerp(0.5, 1, 0.5).
        Assert.Empty(drawnSlot.Bindings);
        var drawnLayer = drawn.Canvases[0].Layers[0];
        Assert.Equal(100 + 0.25 * 1 * 3840, drawnLayer.Rect.X, 9); // ADD over −1920…1920.
        Assert.Empty(drawnLayer.Bindings);

        // A source the bus lacks holds the base; the show itself is never written.
        var missing = plan.Apply(new FixedBus(new() { ["bar"] = 0.5, ["beat"] = 1 }), 0, bypassAll: false);
        Assert.Equal(0.5, missing.FindSurface("sf_wall")!.Opacity, 12);
        Assert.Equal(0.5, show.FindSurface("sf_wall")!.Opacity);
        Assert.Single(show.FindSurface("sf_wall")!.Bindings);
        Assert.Equal(0.8, show.FindFx("fx_blur")!.Value.Slot.Mix);
    }
}
