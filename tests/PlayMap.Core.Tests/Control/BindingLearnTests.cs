using PlayMap.Core.Control;
using PlayMap.Core.Model;

namespace PlayMap.Core.Tests.Control;

/// <summary>Input words, target lines, a binding's run-time shape, and learn: what qualifies, what it makes, and what it never binds.</summary>
public sealed class BindingLearnTests
{
    private static TargetLine Target(string line) => TargetLine.Parse(line, out var why) ?? throw new InvalidOperationException(why);

    private static InputEvent Axis(InputAddress a, int raw, long ns = 1_000_000_000, byte bits = 7) =>
        new(a, InputKind.Axis, raw / (double)((1 << bits) - 1), raw, bits, ns);

    [Theory]
    [InlineData("MIDI APC40 CC 1 48", "MIDI APC40 CC 1 48")]
    [InlineData("midi cc 1 7", "MIDI CC 1 7")]
    [InlineData("CC 1 7 *", "MIDI CC 1 7")]
    [InlineData("NOTE 1 53", "MIDI NOTE 1 53")]
    [InlineData("MIDI BEND 2", "MIDI BEND 2")]
    [InlineData("MIDI X32 NRPN 3 1000", "MIDI X32 NRPN 3 1000")]
    [InlineData("MIDI MSC 127 GO", "MIDI MSC 127 GO")]
    [InlineData("OSC /fader/1", "OSC /fader/1")]
    [InlineData("OSC TouchOSC /fader/1 ARG 1", "OSC TouchOSC /fader/1 ARG 1")]
    [InlineData("ARTNET 0 101+102", "ARTNET 0 101+102")]
    [InlineData("ARTNET Desk 3 1", "ARTNET Desk 3 1")]
    public void InputWordsRoundTrip(string words, string formatted)
    {
        var a = InputAddress.Parse(words, out var why);
        Assert.True(a is not null, why);
        Assert.Equal(formatted, a!.Value.Format());
        Assert.Equal(a, InputAddress.Parse(a.Value.Format(), out _));
    }

    [Theory]
    [InlineData("MIDI CC 17 1", "MIDI CC <channel 1-16> <0-127>")]
    [InlineData("MIDI CC14 1 40", "MIDI CC14 <channel 1-16> <0-31>")]
    [InlineData("ARTNET 0 513", "ARTNET [<surface>] <universe 0-32767> <channel 1-512>[+<fine>]")]
    [InlineData("KEYBOARD a", "'KEYBOARD' is no input — MIDI, OSC, ARTNET or SACN")]
    [InlineData("OSC fader", "OSC [<surface>] </address> [ARG <n>]")]
    public void BadInputWordsAreRefusedWithTheShape(string words, string why)
    {
        Assert.Null(InputAddress.Parse(words, out var reason));
        Assert.Equal(why, reason);
    }

    [Theory]
    [InlineData("SET sf_a opacity *", Placeholder.Value)]
    [InlineData("MASTER *%", Placeholder.Percent)]
    [InlineData("PLAYLIST GO show #*", Placeholder.Index)]
    [InlineData("BLACKOUT TOGGLE", Placeholder.None)]
    public void ATargetCarriesOneWholeWordValue(string line, Placeholder placeholder) => Assert.Equal(placeholder, Target(line).Placeholder);

    [Theory]
    [InlineData("STATE", "STATE cannot be bound to a control (no session, secret, approval or binding verb is)")]
    [InlineData("CREDENTIAL SET x y", "CREDENTIAL SET cannot be bound to a control (no session, secret, approval or binding verb is)")]
    [InlineData("BIND FORGET CC 1 1", "BIND FORGET cannot be bound to a control (no session, secret, approval or binding verb is)")]
    [InlineData("APPROVE pr1", "APPROVE cannot be bound to a control (no session, secret, approval or binding verb is)")]
    [InlineData("OUTPUT LIST", "OUTPUT LIST changes nothing — a control fires lines that act")]
    [InlineData("SET sf_a opacity * *", "a target line carries one value word ('*', '*%' or '#*'), not two")]
    [InlineData("SET sf_a rect.x [*]", "a value can ride only as a whole word, never inside JSON")]
    [InlineData("FROB 1", "'FROB' is no verb — HELP lists the vocabulary")]
    public void ATargetThatCannotBeBoundIsRefusedWithWords(string line, string why)
    {
        Assert.Null(TargetLine.Parse(line, out var reason));
        Assert.Equal(why, reason);
    }

    [Fact]
    public void ATargetKnowsItsFieldItsSwitchFormsAndItsRange()
    {
        var set = Target("SET sf_a opacity *");
        Assert.Equal(("sf_a", "opacity"), set.Field);
        Assert.Equal("SET sf_a opacity 0.25", set.Fill(0.25));
        Assert.Equal(TargetRange.Unit, TargetRanges.Of(set, null));
        var show = Target("SURFACE SHOW sf_a ON");
        Assert.Equal(("SURFACE SHOW sf_a ON", "SURFACE SHOW sf_a OFF"), show.SwitchForms);
        var rate = TargetRanges.Of(Target("VIDEO RATE *"), null);
        Assert.Equal((0.05, 20.0, true), (rate.Min, rate.Max, rate.Log));
        Assert.Equal((0.0, 100.0, true), (TargetRanges.Of(Target("MASTER *"), null).Min, TargetRanges.Of(Target("MASTER *"), null).Max, TargetRanges.Of(Target("MASTER *"), null).Integer));
        Assert.False(TargetRanges.Of(Target("SET sf_a frob *"), null).Known);
    }

    [Fact]
    public void AnAxisBindingShapesAndSendsOnlyNewValues()
    {
        var input = InputAddress.Midi(MidiFamily.Cc, 1, 48);
        var binding = new ControlBinding("bd1", "", input.Format(), "SET sf_a opacity *", ControlShape.Default with { Takeover = TakeoverMode.None },
            ControlFeedback.Default, true);
        var r = new BindingRuntime(binding, input, Target(binding.Target), TargetRange.Unit);
        Assert.Equal(ControlBehaviour.Absolute, r.Behaviour);
        var fire = r.Offer(Axis(input, 127), () => null, () => null);
        Assert.Equal(new BindingFire("SET sf_a opacity 1", true, 1), fire);
        Assert.Null(r.Offer(Axis(input, 127), () => null, () => null));
        Assert.Equal("SET sf_a opacity 0", r.Offer(Axis(input, 0), () => null, () => null)!.Value.Line);
    }

    [Fact]
    public void APickUpBindingWaitsForTheFaderToReachTheTarget()
    {
        var input = InputAddress.Midi(MidiFamily.Cc, 1, 48);
        var binding = new ControlBinding("bd1", "", input.Format(), "SET sf_a opacity *", ControlShape.Default, ControlFeedback.Default, true);
        var r = new BindingRuntime(binding, input, Target(binding.Target), TargetRange.Unit);
        Assert.Equal(TakeoverMode.PickUp, r.Takeover);
        double? target = 0.5;
        Assert.Null(r.Offer(Axis(input, 20), () => target, () => null));
        Assert.Equal("up", r.TakeoverDirection);
        Assert.Null(r.Offer(Axis(input, 40), () => target, () => null));
        var caught = r.Offer(Axis(input, 70), () => target, () => null);
        Assert.Equal("SET sf_a opacity 0.551181", caught!.Value.Line);
        Assert.Null(r.TakeoverDirection);
        // Someone else moves the target: the fader must catch it again.
        target = 0.1;
        Assert.Null(r.Offer(Axis(input, 80), () => target, () => null));
        Assert.Equal("down", r.TakeoverDirection);
    }

    [Fact]
    public void ButtonsTriggerToggleFromTheFactAndMomentaryOnAndOff()
    {
        var note = InputAddress.Midi(MidiFamily.Note, 1, 53);
        BindingRuntime Make(string target, ControlBehaviour behaviour) =>
            new(new ControlBinding("bd", "", note.Format(), target, ControlShape.Default with { Behaviour = behaviour, DebounceMs = 0 }, ControlFeedback.Default, true),
                note, Target(target), TargetRange.Unit);
        InputEvent Press(long ns) => new(note, InputKind.Press, 1, 127, 7, ns);
        InputEvent Release(long ns) => new(note, InputKind.Release, 0, 0, 7, ns);

        var trigger = Make("PLAYLIST NEXT show", ControlBehaviour.Auto);
        Assert.Equal(ControlBehaviour.Trigger, trigger.Behaviour);
        Assert.Equal("PLAYLIST NEXT show", trigger.Offer(Press(1), () => null, () => null)!.Value.Line);
        Assert.Null(trigger.Offer(Release(2), () => null, () => null));

        var toggle = Make("SURFACE SHOW sf_a ON", ControlBehaviour.Toggle);
        Assert.Equal("SURFACE SHOW sf_a OFF", toggle.Offer(Press(1), () => null, () => true)!.Value.Line);
        Assert.Equal("SURFACE SHOW sf_a ON", toggle.Offer(Press(2), () => null, () => false)!.Value.Line);

        var nodeToggle = Make("BLACKOUT TOGGLE", ControlBehaviour.Toggle);
        Assert.Equal("BLACKOUT TOGGLE", nodeToggle.Offer(Press(1), () => null, () => true)!.Value.Line);

        var momentary = Make("FREEZE ON", ControlBehaviour.Momentary);
        Assert.Equal("FREEZE ON", momentary.Offer(Press(1), () => null, () => null)!.Value.Line);
        Assert.Equal("FREEZE OFF", momentary.Offer(Release(2), () => null, () => null)!.Value.Line);
    }

    [Fact]
    public void PressesCloserThanTheDebounceAreDroppedAndAHoldFiresOnlyWhenHeld()
    {
        var note = InputAddress.Midi(MidiFamily.Note, 1, 1);
        var b = new ControlBinding("bd", "", note.Format(), "BLACKOUT ON", ControlShape.Default with { DebounceMs = 30 }, ControlFeedback.Default, true);
        var r = new BindingRuntime(b, note, Target(b.Target), TargetRange.Unit);
        Assert.NotNull(r.Offer(new InputEvent(note, InputKind.Press, 1, 127, 7, 1_000_000_000), () => null, () => null));
        Assert.Null(r.Offer(new InputEvent(note, InputKind.Press, 1, 127, 7, 1_020_000_000), () => null, () => null));
        Assert.NotNull(r.Offer(new InputEvent(note, InputKind.Press, 1, 127, 7, 1_040_000_000), () => null, () => null));

        var held = new BindingRuntime(b with { Shape = b.Shape with { HoldMs = 600 } }, note, Target(b.Target), TargetRange.Unit);
        Assert.Null(held.Offer(new InputEvent(note, InputKind.Press, 1, 127, 7, 2_000_000_000), () => null, () => null));
        Assert.Null(held.Poll(2_500_000_000, () => null));
        Assert.Equal("BLACKOUT ON", held.Poll(2_600_000_000, () => null)!.Value.Line);
        // Released before the hold: nothing fires.
        Assert.Null(held.Offer(new InputEvent(note, InputKind.Press, 1, 127, 7, 3_000_000_000), () => null, () => null));
        Assert.Null(held.Offer(new InputEvent(note, InputKind.Release, 0, 0, 7, 3_200_000_000), () => null, () => null));
        Assert.Null(held.Poll(3_700_000_000, () => null));
    }

    [Fact]
    public void AnAxisAsAButtonPressesRisingThrough56AndReleasesFallingThrough44()
    {
        var dmx = InputAddress.Dmx(InputSource.ArtNet, 0, 4);
        var b = new ControlBinding("bd", "", dmx.Format(), "SURFACE SHOW sf_a ON", ControlShape.Default with { Behaviour = ControlBehaviour.Momentary, DebounceMs = 0 },
            ControlFeedback.Default, true);
        var r = new BindingRuntime(b, dmx, Target(b.Target), TargetRange.Unit);
        InputEvent At(int raw) => new(dmx, InputKind.Axis, raw / 255.0, raw, 8, 1);
        Assert.Null(r.Offer(At(130), () => null, () => null));
        Assert.Equal("SURFACE SHOW sf_a ON", r.Offer(At(150), () => null, () => null)!.Value.Line);
        Assert.Null(r.Offer(At(120), () => null, () => null));
        Assert.Equal("SURFACE SHOW sf_a OFF", r.Offer(At(100), () => null, () => null)!.Value.Line);
    }

    [Fact]
    public void LearnWaitsForAPressNeverBindsAReleaseAndIgnoresJitter()
    {
        var learner = new Learner();
        learner.Arm(new LearnSession(Target("SET sf_a opacity *"), "", 30_000_000_000, 0, "Lobby opacity"));
        var cc = InputAddress.Midi(MidiFamily.Cc, 1, 48, "APC40");
        var note = InputAddress.Midi(MidiFamily.Note, 1, 53, "APC40");
        Assert.Null(learner.Offer(new InputEvent(note, InputKind.Release, 0, 0, 7, 1), _ => false));
        Assert.Null(learner.Offer(Axis(cc, 60), _ => false));
        Assert.Null(learner.Offer(Axis(cc, 62), _ => false));
        var outcome = learner.Offer(Axis(cc, 64), _ => false);
        Assert.NotNull(outcome);
        Assert.Equal(cc, outcome!.Input);
        Assert.Equal("SET sf_a opacity *", outcome.Target);
        Assert.Equal(ControlBehaviour.Absolute, outcome.Shape.Behaviour);
        Assert.Null(learner.Session);
    }

    [Fact]
    public void LearnKeepsANumberOnAPadAndMakesItAValueOnAFader()
    {
        var learner = new Learner();
        var pad = InputAddress.Midi(MidiFamily.Note, 1, 53);
        learner.Arm(new LearnSession(Target("SET sf_a opacity 0.5"), "", 30_000_000_000, 0, ""));
        var onPad = learner.Offer(new InputEvent(pad, InputKind.Press, 1, 127, 7, 1), _ => false)!;
        Assert.Equal(("SET sf_a opacity 0.5", ControlBehaviour.Value, "0.5"), (onPad.Target, onPad.Shape.Behaviour, onPad.Shape.PressValue));

        var fader = InputAddress.Midi(MidiFamily.Cc, 1, 7);
        learner.Arm(new LearnSession(Target("SET sf_a opacity 0.5"), "", 30_000_000_000, 0, ""));
        Assert.Null(learner.Offer(Axis(fader, 10), _ => false));
        var onFader = learner.Offer(Axis(fader, 20), _ => false)!;
        Assert.Equal("SET sf_a opacity *", onFader.Target);
    }

    [Fact]
    public void LearnPassesOverInputsAlreadyBoundToTheTargetAndOtherSourcesAndRunsOut()
    {
        var learner = new Learner();
        learner.Arm(new LearnSession(Target("BLACKOUT TOGGLE"), "OSC", 30_000_000_000, 0, ""));
        var note = InputAddress.Midi(MidiFamily.Note, 1, 1);
        Assert.Null(learner.Offer(new InputEvent(note, InputKind.Press, 1, 127, 7, 1), _ => false));
        var osc = InputAddress.Osc("/b/1");
        Assert.Null(learner.Offer(new InputEvent(osc, InputKind.Press, 1, 1, 0, 2), a => a == osc));
        var other = InputAddress.Osc("/b/2");
        Assert.Equal(other, learner.Offer(new InputEvent(other, InputKind.Press, 1, 1, 0, 3), _ => false)!.Input);

        learner.Arm(new LearnSession(Target("BLACKOUT TOGGLE"), "", 10, 0, ""));
        Assert.False(learner.Expire(9));
        Assert.True(learner.Expire(10));
        Assert.Null(learner.Session);
    }
}
