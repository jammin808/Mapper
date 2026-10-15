using PlayMap.Core.Model;

namespace PlayMap.Core.Control;

/// <summary>What learn is waiting for: the target, where a control may come from ("" any, a source word or a surface name), until
/// when (mono ns) and the target's words for the desk.</summary>
public sealed record LearnSession(TargetLine Target, string From, long UntilMonoNs, long UntilUnixMs, string Words);

/// <summary>What learn heard: the input that qualified and the binding shape and target it makes.</summary>
public sealed record LearnOutcome(InputAddress Input, string Target, ControlShape Shape);

/// <summary>Binds the next control that moves to a target. Releases never bind (the press is waited for); a continuous control
/// must move three steps from its first reading, so jitter and a DMX universe at rest never learn; an input already bound to this
/// target is passed over. The first qualifying event wins. Single-threaded: the control thread offers it events.</summary>
public sealed class Learner
{
    private readonly Dictionary<InputAddress, double> _first = [];

    public LearnSession? Session { get; private set; }

    public void Arm(LearnSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        Session = session;
        _first.Clear();
    }

    /// <summary>Stops learning; answers whether it was learning.</summary>
    public bool Cancel()
    {
        var was = Session is not null;
        Session = null;
        _first.Clear();
        return was;
    }

    /// <summary>True when the session has run out at this instant (it is cancelled).</summary>
    public bool Expire(long nowNs)
    {
        if (Session is null || nowNs < Session.UntilMonoNs)
            return false;
        Cancel();
        return true;
    }

    /// <summary>An event while learning: the outcome when it qualifies (learning then stops), else null.</summary>
    /// <param name="boundToTarget">Whether this input already fires this target.</param>
    public LearnOutcome? Offer(in InputEvent e, Func<InputAddress, bool> boundToTarget)
    {
        ArgumentNullException.ThrowIfNull(boundToTarget);
        if (Session is not { } s || e.MonoNs >= s.UntilMonoNs || !FromMatches(s.From, e.Address))
            return null;
        if (e.Kind is InputKind.Release || boundToTarget(e.Address))
            return null;
        if (e.Kind == InputKind.Axis)
        {
            var steps = e.Bits > 0 ? (1 << Math.Min((int)e.Bits, 16)) - 1 : 127;
            var at = e.Value * steps;
            if (!_first.TryGetValue(e.Address, out var first))
            {
                _first[e.Address] = at;
                return null;
            }
            if (Math.Abs(at - first) < 3)
                return null;
        }
        var outcome = Make(s.Target, e);
        Cancel();
        return outcome;
    }

    private static bool FromMatches(string from, InputAddress a)
    {
        if (from.Length == 0)
            return true;
        return from.ToUpperInvariant() switch
        {
            "MIDI" => a.Source == InputSource.Midi,
            "OSC" => a.Source == InputSource.Osc,
            "ARTNET" => a.Source == InputSource.ArtNet,
            "SACN" => a.Source == InputSource.Sacn,
            _ => string.Equals(a.Surface, from, StringComparison.OrdinalIgnoreCase),
        };
    }

    /// <summary>The binding learn makes: a continuous control on a value target is absolute; a fader on a target with a number becomes
    /// "*" there (Patterns' rule: a level learnt on a fader follows it, on a pad it stays the number); a press on a target with a
    /// number keeps the number (Value); a press on a plain line triggers it.</summary>
    private static LearnOutcome Make(TargetLine target, in InputEvent e)
    {
        var continuous = e.Kind is InputKind.Axis or InputKind.Relative;
        if (continuous)
        {
            if (target.HasValue)
                return new LearnOutcome(e.Address, target.Text, ControlShape.Default with { Behaviour = e.Kind == InputKind.Relative ? ControlBehaviour.Relative : ControlBehaviour.Absolute });
            if (LastNumber(target) is { } at)
                return new LearnOutcome(e.Address, target.With(at, "*"), ControlShape.Default with { Behaviour = ControlBehaviour.Absolute });
            return new LearnOutcome(e.Address, target.Text, ControlShape.Default with { Behaviour = ControlBehaviour.Trigger });
        }
        if (target.HasValue)
            return new LearnOutcome(e.Address, target.Text, ControlShape.Default with { Behaviour = ControlBehaviour.Velocity });
        if (LastNumber(target) is { } n)
            return new LearnOutcome(e.Address, target.Text, ControlShape.Default with { Behaviour = ControlBehaviour.Value, PressValue = target.Line.Words[n] });
        return new LearnOutcome(e.Address, target.Text, ControlShape.Default with { Behaviour = ControlBehaviour.Trigger });
    }

    private static int? LastNumber(TargetLine target)
    {
        for (var i = target.Line.Count - 1; i >= target.VerbWords; i--)
        {
            if (double.TryParse(target.Line.Words[i], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _))
                return i;
        }
        return null;
    }
}
