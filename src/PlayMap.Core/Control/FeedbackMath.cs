using System.Globalization;
using System.Net;
using PlayMap.Core.Control.Osc;
using PlayMap.Core.Model;

namespace PlayMap.Core.Control;

/// <summary>What a binding sends back to a control: its target's value (a fader follows the desk), a lamp (lit while the target's
/// switch is on) or nothing.</summary>
public enum FeedbackKind { None, Value, Lamp }

/// <summary>Where and how a binding's feedback goes: an OSC address and argument index, an endpoint ("" = the clients its surface
/// reaches) and the lamp's on and off words ("" = 1 and 0). <see cref="Why"/> says why a binding sends nothing.</summary>
public readonly record struct FeedbackRoute(FeedbackKind Kind, string Address, int Arg, string To, string On, string Off, string Why)
{
    public static FeedbackRoute None(string why) => new(FeedbackKind.None, "", 0, "", "", "", why);

    /// <summary>The route in words for BIND LIST: "value → OSC /fader/1", "lamp → OSC 10.0.0.9:9000 /led/1", or why none.</summary>
    public string Words => Kind == FeedbackKind.None ? Why
        : $"{(Kind == FeedbackKind.Value ? "value" : "lamp")} → OSC {(To.Length > 0 ? To + " " : "")}{Address}{(Arg > 0 ? " ARG " + Arg.ToString(CultureInfo.InvariantCulture) : "")}";
}

/// <summary>The pure side of feedback: which kind a binding sends, to which OSC address, and the control value that shows a target's
/// value (the binding's shaping run backwards: out-range, curve, invert, in-range).</summary>
public static class FeedbackMath
{
    /// <summary>The kind a binding's feedback is. Off: none. Value: the target's field is read (SET or NUDGE). Lamp: the target's
    /// switch, or its field above half way. Auto: a value for a fader, knob or encoder on a field; a lamp for a button on a switch
    /// target (ON|OFF or TOGGLE); otherwise none.</summary>
    public static FeedbackKind KindOf(FeedbackMode mode, ControlBehaviour behaviour, TargetLine target)
    {
        ArgumentNullException.ThrowIfNull(target);
        var readable = target.Field is not null && target.Spec.Name != "GENERATOR SET";
        var switches = target.SwitchForms is not null || target.Toggles;
        return mode switch
        {
            FeedbackMode.Off => FeedbackKind.None,
            FeedbackMode.Value => readable ? FeedbackKind.Value : FeedbackKind.None,
            FeedbackMode.Lamp => readable || switches ? FeedbackKind.Lamp : FeedbackKind.None,
            _ when readable && behaviour is ControlBehaviour.Absolute or ControlBehaviour.Relative => FeedbackKind.Value,
            _ when switches => FeedbackKind.Lamp,
            _ => FeedbackKind.None,
        };
    }

    /// <summary>A binding's feedback route. The feedback's own words ("OSC [&lt;host:port&gt;] [&lt;address&gt;]") override where it
    /// goes; "" sends to the binding's own OSC address and argument. A binding on an address pattern, or on a MIDI or DMX input
    /// without OSC words, sends nothing (MIDI feedback is next round).</summary>
    public static FeedbackRoute RouteOf(ControlBinding binding, InputAddress input, TargetLine target, ControlBehaviour behaviour)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(target);
        var fb = binding.Feedback ?? ControlFeedback.Default;
        var kind = KindOf(fb.Mode, behaviour, target);
        if (kind == FeedbackKind.None)
            return FeedbackRoute.None(fb.Mode == FeedbackMode.Off ? "feedback off"
                : fb.Mode == FeedbackMode.Auto ? "no feedback: the target has no value or switch to show" : $"no {fb.Mode.ToString().ToLowerInvariant()} to show for this target");
        if (fb.When.Length > 0)
            return FeedbackRoute.None("feedback from a STATE predicate (when) is next round");
        string to = "", address = "";
        var arg = 0;
        if (fb.To.Length > 0)
        {
            if (ParseTo(fb.To, out to, out address, out var why) is not true)
                return FeedbackRoute.None(why);
        }
        if (address.Length == 0)
        {
            if (input.Source != InputSource.Osc)
                return FeedbackRoute.None(input.Source == InputSource.Midi ? "MIDI feedback (lamps, motor faders) is next round" : "DMX input has no way back");
            if (OscPattern.IsPattern(input.Path))
                return FeedbackRoute.None("a binding on an address pattern has no one address to answer on");
            address = input.Path;
            arg = input.Number;
        }
        return new FeedbackRoute(kind, address, arg, to, fb.On, fb.Off, "");
    }

    /// <summary>Reads a feedback's words: "OSC", then an optional endpoint and an optional address (at least one of them). MIDI words
    /// read but send nothing in this build.</summary>
    public static bool? ParseTo(string words, out string to, out string address, out string why)
    {
        ArgumentNullException.ThrowIfNull(words);
        to = "";
        address = "";
        why = "";
        var parts = words.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length > 0 && parts[0].Equals("MIDI", StringComparison.OrdinalIgnoreCase))
        {
            why = "MIDI feedback (lamps, motor faders) is next round";
            return null;
        }
        if (parts.Length is < 2 or > 3 || !parts[0].Equals("OSC", StringComparison.OrdinalIgnoreCase))
        {
            why = "feedback goes to OSC [<host:port>] [<address>]";
            return false;
        }
        foreach (var p in parts.Skip(1))
        {
            if (p.StartsWith('/'))
            {
                if (address.Length > 0 || OscPattern.IsPattern(p) || p.Contains(' ', StringComparison.Ordinal))
                {
                    why = $"'{p}' is no single OSC address to send to";
                    return false;
                }
                address = p;
            }
            else if (TryEndpoint(p, out _) && to.Length == 0)
            {
                to = p;
            }
            else
            {
                why = $"'{p}' is no host:port (an IP address and a port 1–65535)";
                return false;
            }
        }
        return true;
    }

    /// <summary>An endpoint written "a.b.c.d:port" or "[v6]:port" with a port 1–65535 (no name lookups: the control thread never
    /// waits on DNS).</summary>
    public static bool TryEndpoint(string text, out IPEndPoint? endpoint)
    {
        endpoint = null;
        if (string.IsNullOrEmpty(text) || !IPEndPoint.TryParse(text, out var e) || e.Port is < 1 or > 65535)
            return false;
        var colon = text.LastIndexOf(':');
        if (colon < 0 || (e.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 && !text.StartsWith('[')))
            return false;
        endpoint = e;
        return true;
    }

    /// <summary>The control value that shows a target value: the target in its range (or the binding's out-range) normalised, the
    /// curve inverted, the invert undone, and the result in the binding's in-range.</summary>
    public static double ControlValue(ControlShape shape, TargetRange range, double target)
    {
        ArgumentNullException.ThrowIfNull(shape);
        var min = shape.OutMin ?? range.Min;
        var max = shape.OutMax ?? range.Max;
        var x = InverseCurve(shape.Curve, shape.CurveAmount, BindingMath.InRange(target, min, max, range.Log));
        if (shape.Invert)
            x = 1 - x;
        return shape.InMin + x * (shape.InMax - shape.InMin);
    }

    /// <summary>The inverse of <see cref="BindingMath.Curve"/> on [0, 1]: exponential y^(1/k); logarithmic 1 − (1 − y)^(1/k); S-curve
    /// ½ − sin(asin(1 − 2y)/3); steps and linear y.</summary>
    public static double InverseCurve(ControlCurve curve, double amount, double y)
    {
        y = Math.Clamp(y, 0, 1);
        var k = amount > 0 ? amount : 2;
        return curve switch
        {
            ControlCurve.Exponential => Math.Pow(y, 1 / k),
            ControlCurve.Logarithmic => 1 - Math.Pow(1 - y, 1 / k),
            ControlCurve.SCurve => Math.Clamp(0.5 - Math.Sin(Math.Asin(1 - 2 * y) / 3), 0, 1),
            _ => y,
        };
    }

    /// <summary>A lamp's word as an OSC argument: "" is 1 (on) or 0 (off) as a float; a number is a float; any other word a string.</summary>
    public static OscArg LampArg(bool on, string onWord, string offWord)
    {
        var word = on ? onWord : offWord;
        if (word.Length == 0)
            return OscArg.F(on ? 1f : 0f);
        return double.TryParse(word, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? OscArg.F((float)v) : OscArg.S(word);
    }
}
