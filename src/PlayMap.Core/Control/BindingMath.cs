using System.Globalization;
using PlayMap.Core.Model;

namespace PlayMap.Core.Control;

/// <summary>Where a pick-up takeover stands for one binding: whether the control has caught the target, and where the control
/// was last (normalised), so a crossing is seen between two readings.</summary>
public readonly record struct TakeoverState(bool Caught, double LastControl, bool HasLast)
{
    public static readonly TakeoverState Fresh = new(false, 0, false);

    /// <summary>Re-arms the takeover (the target was moved by someone else, the controller reconnected, the binding was
    /// re-enabled): the control must catch the target again.</summary>
    public TakeoverState Rearm() => this with { Caught = false };
}

/// <summary>The pure maths of a binding: an input's value shaped into the target's, in the fixed order behaviour → in-range clip →
/// invert → curve → out-range → takeover → format. Every function is a function of its arguments; the runtime holds the state.</summary>
public static class BindingMath
{
    /// <summary>In-range clip, invert and curve: a normalised value in [0, 1].</summary>
    public static double Shape(ControlShape shape, double value)
    {
        ArgumentNullException.ThrowIfNull(shape);
        var span = shape.InMax - shape.InMin;
        var x = Math.Abs(span) < 1e-12 ? (value >= shape.InMax ? 1 : 0) : Math.Clamp((value - shape.InMin) / span, 0, 1);
        if (double.IsNaN(x))
            x = 0;
        if (shape.Invert)
            x = 1 - x;
        return Curve(shape.Curve, shape.CurveAmount, x);
    }

    /// <summary>A response curve on [0, 1]: linear x; exponential x^k; logarithmic 1 − (1 − x)^k; S-curve x²(3 − 2x); steps
    /// round(x(n − 1))/(n − 1) with n = <paramref name="amount"/> (at least 2).</summary>
    public static double Curve(ControlCurve curve, double amount, double x)
    {
        x = Math.Clamp(x, 0, 1);
        var k = amount > 0 ? amount : 2;
        return curve switch
        {
            ControlCurve.Exponential => Math.Pow(x, k),
            ControlCurve.Logarithmic => 1 - Math.Pow(1 - x, k),
            ControlCurve.SCurve => x * x * (3 - 2 * x),
            ControlCurve.Steps => Steps(x, (int)Math.Max(2, Math.Round(k))),
            _ => x,
        };
    }

    private static double Steps(double x, int n) => Math.Round(x * (n - 1), MidpointRounding.AwayFromZero) / (n - 1);

    /// <summary>A normalised value into a target range; a log-domain range (both ends positive) interpolates in log space.</summary>
    public static double OutRange(double x, double min, double max, bool log = false)
    {
        x = Math.Clamp(x, 0, 1);
        if (log && min > 0 && max > 0)
            return Math.Exp(Math.Log(min) + x * (Math.Log(max) - Math.Log(min)));
        return min + x * (max - min);
    }

    /// <summary>A target value back into [0, 1] of its range (the inverse of <see cref="OutRange"/>).</summary>
    public static double InRange(double y, double min, double max, bool log = false)
    {
        if (log && min > 0 && max > 0 && y > 0)
            return Math.Clamp((Math.Log(y) - Math.Log(min)) / (Math.Log(max) - Math.Log(min)), 0, 1);
        var span = max - min;
        return Math.Abs(span) < 1e-12 ? 0 : Math.Clamp((y - min) / span, 0, 1);
    }

    /// <summary>The ticks a relative encoder's 7-bit value means: two's complement (1 up, 127 down), binary offset (65 up,
    /// 63 down), signed bit (1 up, 65 down), signed bit 2 (65 up, 1 down); an increment (NRPN 96/97) is already signed.</summary>
    public static int Relative(RelativeCoding coding, int raw) => coding switch
    {
        RelativeCoding.BinaryOffset => raw - 64,
        RelativeCoding.SignedBit => (raw & 64) != 0 ? -(raw & 63) : raw & 63,
        RelativeCoding.SignedBit2 => (raw & 64) != 0 ? raw & 63 : -(raw & 63),
        RelativeCoding.Increment => raw,
        _ => raw <= 63 ? raw : raw - 128,
    };

    /// <summary>The change a relative turn makes: ticks × step × (1 + acceleration × (|ticks| − 1)); a zero step is the range ÷ 128.</summary>
    public static double RelativeDelta(ControlShape shape, int ticks, double rangeMin, double rangeMax)
    {
        ArgumentNullException.ThrowIfNull(shape);
        var step = shape.Step > 0 ? shape.Step : (rangeMax - rangeMin) / 128;
        var accel = Math.Max(0, shape.Acceleration);
        return ticks * step * (1 + accel * (Math.Abs(ticks) - 1));
    }

    /// <summary>A target moved by a delta inside its range: wrapped or clamped.</summary>
    public static double Nudge(double current, double delta, double min, double max, bool wrap)
    {
        var v = current + delta;
        if (!wrap || max <= min)
            return Math.Clamp(v, Math.Min(min, max), Math.Max(min, max));
        var span = max - min;
        v = (v - min) % span;
        return min + (v < 0 ? v + span : v);
    }

    /// <summary>The takeover tolerance for a control of a resolution: twice its smallest step (a float control: 1/128).</summary>
    public static double Epsilon(int bits) => bits is > 0 and < 31 ? 2.0 / (1 << bits) : 1.0 / 128;

    /// <summary>The automatic takeover for an input: pick-up for a MIDI absolute axis on a surface without motor faders; none for
    /// motorised faders, OSC and DMX (the lighting desk's fader is the truth).</summary>
    public static TakeoverMode AutoTakeover(InputSource source, bool motorised) =>
        source == InputSource.Midi && !motorised ? TakeoverMode.PickUp : TakeoverMode.None;

    /// <summary>One reading of a control through its takeover: the normalised target to write, or null to hold (the control has not
    /// caught the target yet). <paramref name="control"/> and <paramref name="target"/> are normalised. None writes the control.
    /// Pick-up holds until the control crosses the target (or comes within ε), then follows 1:1. Value scaling moves the target
    /// by the control's step scaled toward the edge it moves to, until they meet, then follows 1:1.</summary>
    public static double? Takeover(TakeoverMode mode, ref TakeoverState state, double control, double target, double epsilon)
    {
        var previous = state.HasLast ? state.LastControl : control;
        var first = !state.HasLast;
        state = state with { LastControl = control, HasLast = true };
        if (mode is TakeoverMode.None or TakeoverMode.Auto || state.Caught)
        {
            state = state with { Caught = true };
            return control;
        }
        if (Math.Abs(control - target) <= epsilon)
        {
            state = state with { Caught = true };
            return control;
        }
        if (mode == TakeoverMode.PickUp)
        {
            // A crossing between two readings catches the target.
            if (!first && Math.Sign(previous - target) != Math.Sign(control - target))
            {
                state = state with { Caught = true };
                return control;
            }
            return null;
        }
        // Value scaling: the step moves the target toward the edge the control heads for, proportionally, so both meet at it.
        var delta = control - previous;
        if (first || Math.Abs(delta) < 1e-12)
            return null;
        var edge = delta > 0 ? 1.0 : 0.0;
        var room = edge - previous;
        if (Math.Abs(room) < 1e-12)
            return null;
        var moved = target + delta * (edge - target) / room;
        if (Math.Abs(control - moved) <= epsilon)
            state = state with { Caught = true };
        return Math.Clamp(moved, 0, 1);
    }

    /// <summary>A shaped value as the word that replaces a placeholder: "*" → invariant, six significant digits, trimmed; "*%" → a
    /// 0–100 integer; "#*" → a 1-based integer index.</summary>
    public static string Format(double value, Placeholder placeholder)
    {
        return placeholder switch
        {
            Placeholder.Percent => ((int)Math.Round(Math.Clamp(value, 0, 1) * 100, MidpointRounding.AwayFromZero)).ToString(CultureInfo.InvariantCulture),
            Placeholder.Index => ((int)Math.Max(1, Math.Round(value, MidpointRounding.AwayFromZero))).ToString(CultureInfo.InvariantCulture),
            _ => Trim(value),
        };
    }

    private static string Trim(double value)
    {
        if (Math.Abs(value) < 1e-12)
            return "0";
        var s = value.ToString("G6", CultureInfo.InvariantCulture);
        if (!s.Contains('E', StringComparison.Ordinal))
            return s;
        // Six significant digits, written without an exponent (a line word is plain decimal).
        var digits = (int)Math.Floor(Math.Log10(Math.Abs(value))) + 1;
        var scale = Math.Pow(10, digits - 6);
        return (Math.Round(value / scale) * scale).ToString("0.############", CultureInfo.InvariantCulture);
    }
}
