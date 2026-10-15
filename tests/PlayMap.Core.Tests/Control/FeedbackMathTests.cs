using PlayMap.Core.Control;
using PlayMap.Core.Control.Osc;
using PlayMap.Core.Model;

namespace PlayMap.Core.Tests.Control;

/// <summary>Feedback's pure side: the control value that shows a target is the binding's shaping run backwards (every curve, invert,
/// in-range and linear or log out-range); the kind a binding sends by mode, behaviour and target; where it sends it; and the words a
/// feedback route reads or refuses.</summary>
public sealed class FeedbackMathTests
{
    private static TargetLine Target(string text) => TargetLine.Parse(text, out var why) ?? throw new InvalidOperationException(why);

    private static ControlBinding Binding(string input, string target, ControlFeedback? feedback = null) =>
        new("bd00000001", "", input, target, ControlShape.Default, feedback ?? ControlFeedback.Default, true);

    [Theory]
    // curve, amount, invert, inMin, inMax, outMin, outMax, log
    [InlineData(ControlCurve.Linear, 2, false, 0, 1, 0, 1, false)]
    [InlineData(ControlCurve.Exponential, 2, false, 0, 1, 0, 1, false)]
    [InlineData(ControlCurve.Exponential, 3, true, 0, 127, 0, 2, false)]
    [InlineData(ControlCurve.Logarithmic, 2, false, 0.1, 0.9, 0, 1, false)]
    [InlineData(ControlCurve.Logarithmic, 3, true, 0, 1, 0.05, 20, true)]
    [InlineData(ControlCurve.SCurve, 2, false, 0, 1, 0, 1, false)]
    [InlineData(ControlCurve.SCurve, 2, true, 0, 16383, 0.2, 5, true)]
    public void TheControlValueOfAShapedValueIsTheControlsOwn(ControlCurve curve, double amount, bool invert, double inMin, double inMax,
        double outMin, double outMax, bool log)
    {
        var shape = ControlShape.Default with { Curve = curve, CurveAmount = amount, Invert = invert, InMin = inMin, InMax = inMax };
        var range = new TargetRange(outMin, outMax, log, false, true);
        for (var i = 0; i <= 20; i++)
        {
            var control = inMin + (inMax - inMin) * i / 20.0;
            var target = BindingMath.OutRange(BindingMath.Shape(shape, control), outMin, outMax, log);
            Assert.Equal(control, FeedbackMath.ControlValue(shape, range, target), 6 - (int)Math.Log10(Math.Max(1, inMax)));
        }
    }

    [Fact]
    public void ABindingsOwnOutRangeOverridesTheTargetsAndStepsLandOnTheirStep()
    {
        var shape = ControlShape.Default with { OutMin = 0.5, OutMax = 1 };
        Assert.Equal(0.5, FeedbackMath.ControlValue(shape, TargetRange.Unit, 0.75), 9);
        // Below the binding's range the control rests at its bottom.
        Assert.Equal(0, FeedbackMath.ControlValue(shape, TargetRange.Unit, 0.2), 9);
        var steps = ControlShape.Default with { Curve = ControlCurve.Steps, CurveAmount = 5 };
        foreach (var y in new[] { 0, 0.25, 0.5, 0.75, 1 })
            Assert.Equal(y, BindingMath.Shape(steps, FeedbackMath.ControlValue(steps, TargetRange.Unit, y)), 9);
    }

    [Theory]
    [InlineData(ControlCurve.Exponential, 2)]
    [InlineData(ControlCurve.Logarithmic, 4)]
    [InlineData(ControlCurve.SCurve, 2)]
    public void EachCurvesInverseUndoesIt(ControlCurve curve, double amount)
    {
        for (var i = 0; i <= 100; i++)
            Assert.Equal(i / 100.0, FeedbackMath.InverseCurve(curve, amount, BindingMath.Curve(curve, amount, i / 100.0)), 9);
    }

    [Theory]
    // mode, behaviour, target → kind
    [InlineData(FeedbackMode.Auto, ControlBehaviour.Absolute, "SET sf_a opacity *", FeedbackKind.Value)]
    [InlineData(FeedbackMode.Auto, ControlBehaviour.Relative, "NUDGE sf_a opacity *", FeedbackKind.Value)]
    [InlineData(FeedbackMode.Auto, ControlBehaviour.Trigger, "BLACKOUT TOGGLE", FeedbackKind.Lamp)]
    [InlineData(FeedbackMode.Auto, ControlBehaviour.Momentary, "SURFACE SHOW sf_a ON", FeedbackKind.Lamp)]
    [InlineData(FeedbackMode.Auto, ControlBehaviour.Trigger, "PLAYLIST GO Show", FeedbackKind.None)]
    [InlineData(FeedbackMode.Auto, ControlBehaviour.Velocity, "SET sf_a opacity *", FeedbackKind.None)]
    [InlineData(FeedbackMode.Value, ControlBehaviour.Velocity, "SET sf_a opacity *", FeedbackKind.Value)]
    [InlineData(FeedbackMode.Value, ControlBehaviour.Trigger, "BLACKOUT TOGGLE", FeedbackKind.None)]
    [InlineData(FeedbackMode.Value, ControlBehaviour.Absolute, "GENERATOR SET gn_a speed *", FeedbackKind.None)]
    [InlineData(FeedbackMode.Lamp, ControlBehaviour.Trigger, "SET sf_a opacity 1", FeedbackKind.Lamp)]
    [InlineData(FeedbackMode.Off, ControlBehaviour.Absolute, "SET sf_a opacity *", FeedbackKind.None)]
    public void TheKindFollowsTheModeTheBehaviourAndTheTarget(FeedbackMode mode, ControlBehaviour behaviour, string target, FeedbackKind kind) =>
        Assert.Equal(kind, FeedbackMath.KindOf(mode, behaviour, Target(target)));

    [Fact]
    public void ARouteIsTheBindingsOwnAddressOrItsFeedbackWords()
    {
        var fader = Binding("OSC /fader/1", "SET sf_a opacity *");
        var route = FeedbackMath.RouteOf(fader, InputAddress.Osc("/fader/1"), Target(fader.Target), ControlBehaviour.Absolute);
        Assert.Equal((FeedbackKind.Value, "/fader/1", 0, ""), (route.Kind, route.Address, route.Arg, route.To));
        Assert.Equal("value → OSC /fader/1", route.Words);

        var xy = FeedbackMath.RouteOf(Binding("OSC /xy ARG 1", "SET sf_a opacity *"), InputAddress.Osc("/xy", 1), Target("SET sf_a opacity *"), ControlBehaviour.Absolute);
        Assert.Equal(("/xy", 1, "value → OSC /xy ARG 1"), (xy.Address, xy.Arg, xy.Words));

        var lamp = Binding("MIDI APC40 NOTE 1 53", "BLACKOUT TOGGLE", ControlFeedback.Default with { To = "OSC 10.0.0.9:9000 /led/1", On = "red", Off = "0" });
        var led = FeedbackMath.RouteOf(lamp, InputAddress.Midi(MidiFamily.Note, 1, 53, "APC40"), Target(lamp.Target), ControlBehaviour.Trigger);
        Assert.Equal((FeedbackKind.Lamp, "/led/1", "10.0.0.9:9000", "red", "0"), (led.Kind, led.Address, led.To, led.On, led.Off));
        Assert.Equal("lamp → OSC 10.0.0.9:9000 /led/1", led.Words);

        var endpointOnly = Binding("OSC /fader/2", "SET sf_a opacity *", ControlFeedback.Default with { To = "OSC 10.0.0.9:9000" });
        var there = FeedbackMath.RouteOf(endpointOnly, InputAddress.Osc("/fader/2"), Target(endpointOnly.Target), ControlBehaviour.Absolute);
        Assert.Equal(("/fader/2", "10.0.0.9:9000"), (there.Address, there.To));
    }

    [Fact]
    public void ARouteThatCannotBeSaysWhy()
    {
        static string Why(ControlBinding b, InputAddress input, ControlBehaviour behaviour = ControlBehaviour.Absolute)
        {
            var r = FeedbackMath.RouteOf(b, input, Target(b.Target), behaviour);
            Assert.Equal(FeedbackKind.None, r.Kind);
            return r.Words;
        }

        Assert.Equal("a binding on an address pattern has no one address to answer on",
            Why(Binding("OSC /fader/*", "SET sf_a opacity *"), InputAddress.Osc("/fader/*")));
        Assert.Equal("MIDI feedback (lamps, motor faders) is next round",
            Why(Binding("MIDI CC 1 7", "SET sf_a opacity *"), InputAddress.Midi(MidiFamily.Cc, 1, 7)));
        Assert.Equal("DMX input has no way back", Why(Binding("ARTNET 0 1", "SET sf_a opacity *"), InputAddress.Dmx(InputSource.ArtNet, 0, 1)));
        Assert.Equal("feedback off", Why(Binding("OSC /f", "SET sf_a opacity *", ControlFeedback.Default with { Mode = FeedbackMode.Off }), InputAddress.Osc("/f")));
        Assert.Equal("no feedback: the target has no value or switch to show",
            Why(Binding("OSC /go", "PLAYLIST GO Show"), InputAddress.Osc("/go"), ControlBehaviour.Trigger));
        Assert.Equal("feedback from a STATE predicate (when) is next round",
            Why(Binding("OSC /f", "SET sf_a opacity *", ControlFeedback.Default with { When = "air.blackout" }), InputAddress.Osc("/f")));
        Assert.Equal("'nowhere' is no host:port (an IP address and a port 1–65535)",
            Why(Binding("OSC /f", "SET sf_a opacity *", ControlFeedback.Default with { To = "OSC nowhere" }), InputAddress.Osc("/f")));
    }

    [Theory]
    // words → read (null = reads, sends nothing this build), to, address, why
    [InlineData("OSC 10.0.0.9:9000 /led/1", true, "10.0.0.9:9000", "/led/1", "")]
    [InlineData("osc /led/1", true, "", "/led/1", "")]
    [InlineData("OSC [::1]:9000", true, "[::1]:9000", "", "")]
    [InlineData("MIDI APC40 NOTE 1 53", null, "", "", "MIDI feedback (lamps, motor faders) is next round")]
    [InlineData("OSC", false, "", "", "feedback goes to OSC [<host:port>] [<address>]")]
    [InlineData("HTTP 10.0.0.9:80", false, "", "", "feedback goes to OSC [<host:port>] [<address>]")]
    [InlineData("OSC /led/*", false, "", "", "'/led/*' is no single OSC address to send to")]
    [InlineData("OSC 10.0.0.9 /led", false, "", "", "'10.0.0.9' is no host:port (an IP address and a port 1–65535)")]
    [InlineData("OSC 10.0.0.9:0 /led", false, "", "", "'10.0.0.9:0' is no host:port (an IP address and a port 1–65535)")]
    [InlineData("OSC desk.local:9000", false, "", "", "'desk.local:9000' is no host:port (an IP address and a port 1–65535)")]
    public void FeedbackWordsReadOrSayWhy(string words, bool? read, string to, string address, string why)
    {
        Assert.Equal(read, FeedbackMath.ParseTo(words, out var t, out var a, out var w));
        Assert.Equal(why, w);
        if (read == true)
            Assert.Equal((to, address), (t, a));
    }

    [Fact]
    public void ALampsWordIsAFloatUnlessItIsAWord()
    {
        Assert.Equal(('f', 1.0), (FeedbackMath.LampArg(true, "", "").Tag, FeedbackMath.LampArg(true, "", "").Float));
        Assert.Equal(('f', 0.0), (FeedbackMath.LampArg(false, "", "").Tag, FeedbackMath.LampArg(false, "", "").Float));
        Assert.Equal(127.0, FeedbackMath.LampArg(true, "127", "0").Float);
        Assert.Equal(("s", "red"), (FeedbackMath.LampArg(true, "red", "off").Tag.ToString(), FeedbackMath.LampArg(true, "red", "off").Text));
        Assert.Equal(OscArg.S("off").Text, FeedbackMath.LampArg(false, "red", "off").Text);
    }
}
