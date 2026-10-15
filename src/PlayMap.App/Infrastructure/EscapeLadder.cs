namespace PlayMap.App.Infrastructure;

/// <summary>One rung of the Esc ladder: its order and the step that answers Esc (true when it used the key).</summary>
public sealed record EscapeRung(int Order, string Name, Func<bool> TryEscape);

/// <summary>Esc goes to the first rung that uses it, lowest order first: a pattern task on the wall, a learn, a draft,
/// the overlays (help, a drawer, a picker), PANIC (PLAY with the desk's preference), the selection. The guard: after a
/// rung below <see cref="Panic"/> used a key, Esc cannot reach PANIC for <see cref="GuardMs"/>, so two quick Escs that
/// close a drawer never panic (the rungs after PANIC still answer); and Esc never reaches PANIC while a text field types.
/// The clock is injected so the guard is deterministic under test.</summary>
public sealed class EscapeLadder(Func<long> nowMs)
{
    public const int PatternTask = 0;
    public const int Learn = 1;
    public const int Draft = 2;
    public const int Overlays = 3;
    public const int Panic = 4;
    public const int Selection = 5;

    /// <summary>How long after a lower rung used Esc the PANIC rung stays out of reach.</summary>
    public const long GuardMs = 600;

    private readonly List<EscapeRung> _rungs = [];
    private long _guardUntil = long.MinValue;

    public IReadOnlyList<EscapeRung> Rungs => _rungs;

    public void Add(EscapeRung rung)
    {
        ArgumentNullException.ThrowIfNull(rung);
        var at = _rungs.FindIndex(r => r.Order > rung.Order);
        if (at < 0)
            _rungs.Add(rung);
        else
            _rungs.Insert(at, rung);
    }

    /// <summary>The name of the rung that used Esc, or null when none did. While a text field types (<paramref name="typing"/>),
    /// Esc never reaches PANIC: it is the field's cancel.</summary>
    public string? Escape(bool typing = false)
    {
        var now = nowMs();
        foreach (var rung in _rungs)
        {
            if (rung.Order == Panic && (typing || now < _guardUntil))
                continue;
            if (!rung.TryEscape())
                continue;
            if (rung.Order < Panic)
                _guardUntil = now + GuardMs;
            return rung.Name;
        }
        return null;
    }
}
