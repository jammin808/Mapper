using System.Globalization;
using PlayMap.Core.Model;

namespace PlayMap.Core.Control;

/// <summary>A line a binding fires: the text, whether it is part of a gesture (an axis or an encoder: journalled once per gesture,
/// not per line) and the target value it carries (NaN for none).</summary>
public readonly record struct BindingFire(string Line, bool Gesture, double Value);

/// <summary>One binding at run time: its input, its parsed target and range, and the state its shape needs (takeover, toggle,
/// debounce, hold, the last value sent). Single-threaded: only the control thread offers it events.</summary>
public sealed class BindingRuntime
{
    private TakeoverState _takeover = TakeoverState.Fresh;
    private long _lastPressNs = long.MinValue;
    private bool _toggle;
    private bool _axisDown;
    private long _holdSince = -1;
    private string? _lastLine;
    private double? _lastWritten;

    public BindingRuntime(ControlBinding binding, InputAddress input, TargetLine target, TargetRange range, bool motorised = false)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(target);
        Binding = binding;
        Input = input;
        Target = target;
        Range = range;
        Behaviour = Resolve(binding.Shape.Behaviour, input, target);
        Takeover = binding.Shape.Takeover == TakeoverMode.Auto ? BindingMath.AutoTakeover(input.Source, motorised) : binding.Shape.Takeover;
    }

    public ControlBinding Binding { get; }

    public InputAddress Input { get; }

    public TargetLine Target { get; }

    public TargetRange Range { get; }

    /// <summary>The behaviour in force (Auto resolved from the input and the target).</summary>
    public ControlBehaviour Behaviour { get; }

    public TakeoverMode Takeover { get; }

    /// <summary>"up", "down" while a pick-up waits for the control to reach the target; null when caught or not taking over.</summary>
    public string? TakeoverDirection { get; private set; }

    /// <summary>The last target value this binding wrote (NaN for none).</summary>
    public double LastValue => _lastWritten ?? double.NaN;

    private double OutMin => Binding.Shape.OutMin ?? Range.Min;

    private double OutMax => Binding.Shape.OutMax ?? Range.Max;

    /// <summary>Auto: a continuous input on a value target is absolute; a relative event is relative; a continuous input on a
    /// target without a value is a button (it fires rising through 56 %); a button on a value target carries its velocity; a button
    /// on a plain line triggers it.</summary>
    public static ControlBehaviour Resolve(ControlBehaviour behaviour, InputAddress input, TargetLine target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (behaviour != ControlBehaviour.Auto)
            return behaviour;
        if (input.IsContinuous)
            return target.HasValue ? ControlBehaviour.Absolute : ControlBehaviour.Trigger;
        if (input.Source == InputSource.Midi && input.Family == MidiFamily.Program)
            return ControlBehaviour.Trigger;
        return target.HasValue ? ControlBehaviour.Velocity : ControlBehaviour.Trigger;
    }

    /// <summary>Re-arms a pick-up (a reconnect, BIND ENABLE).</summary>
    public void Rearm()
    {
        _takeover = _takeover.Rearm();
        _lastLine = null;
    }

    /// <summary>One event of this binding's input: the line to fire, or null when the shape holds it (a release on a trigger, a
    /// pick-up not caught, a debounced press, the same line as last time).</summary>
    /// <param name="read">The target's value now in its own units (null = unknown): takeover and encoders read it.</param>
    /// <param name="fact">Whether the target's switch is on now (null = unknown): a toggle chooses ON or OFF from it.</param>
    public BindingFire? Offer(in InputEvent e, Func<double?> read, Func<bool?> fact)
    {
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(fact);
        switch (Behaviour)
        {
            case ControlBehaviour.Absolute when e.Kind == InputKind.Axis:
                return Absolute(e, read);
            case ControlBehaviour.Relative:
                return Relative(e, read);
        }
        // Buttons: an axis crosses 56 % up (press) and 44 % down (release).
        var kind = e.Kind;
        if (kind == InputKind.Axis)
        {
            if (!_axisDown && e.Value >= 0.56)
            {
                _axisDown = true;
                kind = InputKind.Press;
            }
            else if (_axisDown && e.Value <= 0.44)
            {
                _axisDown = false;
                kind = InputKind.Release;
            }
            else
            {
                return null;
            }
        }
        if (kind is InputKind.Program or InputKind.Trigger)
            kind = InputKind.Press;
        if (kind == InputKind.Press)
        {
            var debounce = Math.Max(0, Binding.Shape.DebounceMs) * 1_000_000L;
            if (_lastPressNs != long.MinValue && e.MonoNs - _lastPressNs < debounce)
                return null;
            _lastPressNs = e.MonoNs;
            if (Binding.Shape.HoldMs > 0)
            {
                _holdSince = e.MonoNs;
                return null;
            }
            return Press(e, fact);
        }
        if (kind == InputKind.Release)
        {
            if (Binding.Shape.HoldMs > 0 && _holdSince >= 0)
            {
                _holdSince = -1;
                return null;
            }
            return Release();
        }
        return null;
    }

    /// <summary>A hold-to-fire press that has been held long enough fires now.</summary>
    public BindingFire? Poll(long nowNs, Func<bool?> fact)
    {
        ArgumentNullException.ThrowIfNull(fact);
        if (_holdSince < 0 || nowNs - _holdSince < Binding.Shape.HoldMs * 1_000_000L)
            return null;
        _holdSince = -1;
        return Press(new InputEvent(Input, InputKind.Press, 1, 127, 7, nowNs), fact);
    }

    private BindingFire? Absolute(in InputEvent e, Func<double?> read)
    {
        var x = BindingMath.Shape(Binding.Shape, e.Value);
        if (Takeover is TakeoverMode.PickUp or TakeoverMode.ValueScaling)
        {
            var now = read();
            if (now is { } current)
            {
                var eps = BindingMath.Epsilon(e.Bits);
                var t = BindingMath.InRange(current, OutMin, OutMax, Range.Log);
                // Someone else moved the target since we last wrote it: the control must catch it again.
                if (_lastWritten is { } last && Math.Abs(BindingMath.InRange(last, OutMin, OutMax, Range.Log) - t) > eps)
                    _takeover = _takeover.Rearm();
                var taken = BindingMath.Takeover(Takeover, ref _takeover, x, t, eps);
                if (taken is null)
                {
                    TakeoverDirection = x < t ? "up" : "down";
                    return null;
                }
                x = taken.Value;
            }
        }
        TakeoverDirection = null;
        var y = BindingMath.OutRange(x, OutMin, OutMax, Range.Log);
        if (Range.Integer || Target.Placeholder == Placeholder.Index)
            y = Math.Round(y, MidpointRounding.AwayFromZero);
        if (Target.Placeholder == Placeholder.Index && y < 0.5)
            return null;
        if (Target.Placeholder == Placeholder.Percent)
            y = x;
        return Send(Target.Fill(y), y, gesture: true);
    }

    private BindingFire? Relative(in InputEvent e, Func<double?> read)
    {
        var ticks = e.Kind == InputKind.Relative ? (int)Math.Round(e.Value)
            : e.Kind == InputKind.Axis ? BindingMath.Relative(Binding.Shape.Relative, e.Raw) : 0;
        if (ticks == 0)
            return null;
        if (read() is not { } current)
            return null;
        var y = BindingMath.Nudge(current, BindingMath.RelativeDelta(Binding.Shape, ticks, OutMin, OutMax), OutMin, OutMax, Binding.Shape.Wrap);
        if (Range.Integer)
            y = Math.Round(y, MidpointRounding.AwayFromZero);
        return Send(Target.Fill(y), y, gesture: true);
    }

    private BindingFire? Press(in InputEvent e, Func<bool?> fact)
    {
        switch (Behaviour)
        {
            case ControlBehaviour.Toggle:
                if (Target.Toggles)
                    return Edge(Target.Text, double.NaN);
                if (fact() is { } isOn)
                    _toggle = !isOn;
                else
                    _toggle = !_toggle;
                var on = _toggle;
                if (Target.SwitchForms is { } forms)
                    return Edge(on ? forms.On : forms.Off, on ? 1 : 0);
                return Edge(Target.Fill(on ? OutMax : OutMin), on ? OutMax : OutMin);
            case ControlBehaviour.Momentary:
                return Momentary(!Binding.Shape.Invert);
            case ControlBehaviour.Velocity:
            {
                var y = BindingMath.OutRange(BindingMath.Shape(Binding.Shape, e.Value), OutMin, OutMax, Range.Log);
                return Edge(Target.Fill(y), y);
            }
            case ControlBehaviour.Value:
                return Word(Binding.Shape.PressValue.Length > 0 ? Binding.Shape.PressValue : Number(OutMax));
            default:
                return Binding.Shape.PressValue.Length > 0 ? Word(Binding.Shape.PressValue)
                    : Target.HasValue ? Edge(Target.Fill(OutMax), OutMax) : Edge(Target.Text, double.NaN);
        }
    }

    private BindingFire? Release() => Behaviour switch
    {
        ControlBehaviour.Momentary => Momentary(Binding.Shape.Invert),
        ControlBehaviour.Value when Binding.Shape.ReleaseValue.Length > 0 => Word(Binding.Shape.ReleaseValue),
        _ => null,
    };

    private BindingFire? Momentary(bool on)
    {
        if (Target.SwitchForms is { } forms)
            return Edge(on ? forms.On : forms.Off, on ? 1 : 0);
        var word = on ? Binding.Shape.PressValue : Binding.Shape.ReleaseValue;
        if (word.Length > 0)
            return Word(word);
        var y = on ? OutMax : OutMin;
        return Edge(Target.Fill(y), y);
    }

    /// <summary>A press or release value given as a word: a number fills the placeholder as a value; any other word replaces it.</summary>
    private BindingFire? Word(string word)
    {
        if (double.TryParse(word, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
            return Edge(Target.Fill(v), v);
        return Target.PlaceholderAt >= 0 ? Edge(Target.With(Target.PlaceholderAt, word), double.NaN) : Edge(Target.Text, double.NaN);
    }

    private static string Number(double v) => v.ToString("R", CultureInfo.InvariantCulture);

    private BindingFire? Edge(string line, double value)
    {
        _lastLine = line;
        if (!double.IsNaN(value))
            _lastWritten = value;
        return new BindingFire(line, false, value);
    }

    private BindingFire? Send(string line, double value, bool gesture)
    {
        if (string.Equals(line, _lastLine, StringComparison.Ordinal))
            return null;
        _lastLine = line;
        _lastWritten = value;
        return new BindingFire(line, gesture, value);
    }
}
