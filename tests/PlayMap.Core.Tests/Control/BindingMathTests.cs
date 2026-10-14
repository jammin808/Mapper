using PlayMap.Core.Control;
using PlayMap.Core.Model;

namespace PlayMap.Core.Tests.Control;

/// <summary>A binding's maths, one table row per rule: in-range clip, invert, each curve, out-range (linear and log), encoders'
/// codings, and each takeover mode reading after reading.</summary>
public sealed class BindingMathTests
{
    [Theory]
    // curve, amount, x → y
    [InlineData(ControlCurve.Linear, 2, 0.25, 0.25)]
    [InlineData(ControlCurve.Exponential, 2, 0.5, 0.25)]
    [InlineData(ControlCurve.Exponential, 3, 0.5, 0.125)]
    [InlineData(ControlCurve.Logarithmic, 2, 0.5, 0.75)]
    [InlineData(ControlCurve.SCurve, 2, 0.25, 0.15625)]
    [InlineData(ControlCurve.SCurve, 2, 0.5, 0.5)]
    [InlineData(ControlCurve.Steps, 5, 0.3, 0.25)]
    [InlineData(ControlCurve.Steps, 5, 0.9, 1)]
    [InlineData(ControlCurve.Steps, 2, 0.49, 0)]
    public void EachCurveIsItsFormula(ControlCurve curve, double amount, double x, double y) =>
        Assert.Equal(y, BindingMath.Curve(curve, amount, x), 9);

    [Theory]
    // inMin, inMax, invert, value → x
    [InlineData(0, 1, false, 0.4, 0.4)]
    [InlineData(0.1, 0.9, false, 0.05, 0)]
    [InlineData(0.1, 0.9, false, 0.5, 0.5)]
    [InlineData(0.1, 0.9, false, 0.95, 1)]
    [InlineData(0, 1, true, 0.25, 0.75)]
    [InlineData(0, 127, false, 63.5, 0.5)]
    public void InRangeClipsAndInvertFlips(double inMin, double inMax, bool invert, double value, double x)
    {
        var shape = ControlShape.Default with { InMin = inMin, InMax = inMax, Invert = invert };
        Assert.Equal(x, BindingMath.Shape(shape, value), 9);
    }

    [Theory]
    [InlineData(0.5, 0, 2, false, 1)]
    [InlineData(0.5, 0.05, 20, true, 1)]
    [InlineData(0, 0.05, 20, true, 0.05)]
    [InlineData(1, 0.05, 20, true, 20)]
    [InlineData(0.5, -0.5, 0.5, false, 0)]
    public void OutRangeIsLinearOrLog(double x, double min, double max, bool log, double y)
    {
        Assert.Equal(y, BindingMath.OutRange(x, min, max, log), 6);
        Assert.Equal(x, BindingMath.InRange(y, min, max, log), 6);
    }

    [Theory]
    [InlineData(RelativeCoding.TwosComplement, 1, 1)]
    [InlineData(RelativeCoding.TwosComplement, 127, -1)]
    [InlineData(RelativeCoding.TwosComplement, 126, -2)]
    [InlineData(RelativeCoding.BinaryOffset, 65, 1)]
    [InlineData(RelativeCoding.BinaryOffset, 63, -1)]
    [InlineData(RelativeCoding.SignedBit, 1, 1)]
    [InlineData(RelativeCoding.SignedBit, 65, -1)]
    [InlineData(RelativeCoding.SignedBit2, 65, 1)]
    [InlineData(RelativeCoding.SignedBit2, 1, -1)]
    [InlineData(RelativeCoding.Increment, -3, -3)]
    public void EncoderCodingsDecodeToTicks(RelativeCoding coding, int raw, int ticks) =>
        Assert.Equal(ticks, BindingMath.Relative(coding, raw));

    [Fact]
    public void AnEncoderStepsWithAccelerationAndClampsOrWraps()
    {
        var shape = ControlShape.Default with { Step = 0.01, Acceleration = 0.5 };
        Assert.Equal(0.01, BindingMath.RelativeDelta(shape, 1, 0, 1), 9);
        // 3 ticks at once: 3 × 0.01 × (1 + 0.5 × 2) = 0.06.
        Assert.Equal(0.06, BindingMath.RelativeDelta(shape, 3, 0, 1), 9);
        Assert.Equal(-0.06, BindingMath.RelativeDelta(shape, -3, 0, 1), 9);
        // A zero step is the range ÷ 128.
        Assert.Equal(2.0 / 128, BindingMath.RelativeDelta(shape with { Step = 0 }, 1, 0, 2), 9);
        Assert.Equal(1, BindingMath.Nudge(0.98, 0.05, 0, 1, wrap: false), 9);
        Assert.Equal(0.03, BindingMath.Nudge(0.98, 0.05, 0, 1, wrap: true), 9);
        Assert.Equal(0.97, BindingMath.Nudge(0.02, -0.05, 0, 1, wrap: true), 9);
    }

    [Theory]
    // mode, target, readings → written (null = held), caught after the last reading
    [InlineData(TakeoverMode.None, 0.8, new[] { 0.1, 0.2 }, new[] { 0.1, 0.2 }, true)]
    // Pick-up from below: held until the control crosses 0.5, then 1:1.
    [InlineData(TakeoverMode.PickUp, 0.5, new[] { 0.1, 0.3, 0.6, 0.7 }, new[] { double.NaN, double.NaN, 0.6, 0.7 }, true)]
    // Pick-up from above: crossing downward catches it too.
    [InlineData(TakeoverMode.PickUp, 0.5, new[] { 0.9, 0.7, 0.4 }, new[] { double.NaN, double.NaN, 0.4 }, true)]
    // Within ε of the target catches at once.
    [InlineData(TakeoverMode.PickUp, 0.5, new[] { 0.505 }, new[] { 0.505 }, true)]
    // Never crossing: never written.
    [InlineData(TakeoverMode.PickUp, 0.5, new[] { 0.1, 0.2, 0.3 }, new[] { double.NaN, double.NaN, double.NaN }, false)]
    // Value scaling: from 0.2 toward the top, the target 0.6 moves by the step scaled (1 − 0.6)/(1 − 0.2) = 0.5.
    [InlineData(TakeoverMode.ValueScaling, 0.6, new[] { 0.2, 0.4 }, new[] { double.NaN, 0.7 }, false)]
    public void TakeoverHoldsUntilTheControlMeetsTheTarget(TakeoverMode mode, double target, double[] readings, double[] written, bool caught)
    {
        var state = TakeoverState.Fresh;
        var t = target;
        for (var i = 0; i < readings.Length; i++)
        {
            var w = BindingMath.Takeover(mode, ref state, readings[i], t, 0.01);
            if (double.IsNaN(written[i]))
            {
                Assert.Null(w);
            }
            else
            {
                Assert.NotNull(w);
                Assert.Equal(written[i], w!.Value, 9);
                t = w.Value;
            }
        }
        Assert.Equal(caught, state.Caught);
    }

    [Fact]
    public void ValueScalingMeetsTheControlAtTheEdge()
    {
        var state = TakeoverState.Fresh;
        var target = 0.6;
        double? last = null;
        foreach (var c in new[] { 0.2, 0.4, 0.6, 0.8, 1.0 })
        {
            var w = BindingMath.Takeover(TakeoverMode.ValueScaling, ref state, c, target, 0.01);
            if (w is { } v)
                target = v;
            last = w;
        }
        Assert.Equal(1.0, last!.Value, 6);
        Assert.True(state.Caught);
    }

    [Fact]
    public void ARearmedPickUpMustCatchAgain()
    {
        var state = TakeoverState.Fresh;
        Assert.NotNull(BindingMath.Takeover(TakeoverMode.PickUp, ref state, 0.5, 0.5, 0.01));
        state = state.Rearm();
        Assert.Null(BindingMath.Takeover(TakeoverMode.PickUp, ref state, 0.55, 0.9, 0.01));
    }

    [Theory]
    [InlineData(0.4173, Placeholder.Value, "0.4173")]
    [InlineData(1.0 / 3, Placeholder.Value, "0.333333")]
    [InlineData(0.425, Placeholder.Percent, "43")]
    [InlineData(2.6, Placeholder.Index, "3")]
    [InlineData(0, Placeholder.Value, "0")]
    [InlineData(1234567.0, Placeholder.Value, "1234570")]
    public void ValuesBecomeInvariantWords(double value, Placeholder placeholder, string word) =>
        Assert.Equal(word, BindingMath.Format(value, placeholder));

    [Fact]
    public void AutoTakeoverIsPickUpOnlyForMidiWithoutMotors()
    {
        Assert.Equal(TakeoverMode.PickUp, BindingMath.AutoTakeover(InputSource.Midi, motorised: false));
        Assert.Equal(TakeoverMode.None, BindingMath.AutoTakeover(InputSource.Midi, motorised: true));
        Assert.Equal(TakeoverMode.None, BindingMath.AutoTakeover(InputSource.Osc, motorised: false));
        Assert.Equal(TakeoverMode.None, BindingMath.AutoTakeover(InputSource.ArtNet, motorised: false));
        Assert.Equal(2.0 / 128, BindingMath.Epsilon(7), 12);
        Assert.Equal(2.0 / 16384, BindingMath.Epsilon(14), 12);
    }
}
