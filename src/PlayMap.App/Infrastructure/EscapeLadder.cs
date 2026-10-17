namespace PlayMap.App.Infrastructure;

/// <summary>One rung of the Esc ladder: its order and the step that answers Esc (true when it used the key).</summary>
public sealed record EscapeRung(int Order, string Name, Func<bool> TryEscape);

/// <summary>Esc goes to the first rung that uses it, lowest order first: a pattern task on the wall, a learn, a draft,
/// the overlays (help, a drawer, a picker), PANIC (PLAY with the desk's preference), the selection. The guard: after a
/// rung below <see cref="Panic"/> used a key, or anything the operator was looking at closed (a drawer, the help, a menu, a
/// flyout — by Esc, a click or its own button: <see cref="Closed"/>), Esc cannot reach PANIC for <see cref="GuardMs"/>, so two
/// quick Escs that close something never panic (the rungs after PANIC still answer). While a text field types, Esc is the
/// field's: only the overlays answer it. The clock is injected so the guard is deterministic under test.</summary>
public sealed class EscapeLadder(Func<long> nowMs)
{
    /// <summary>The desk's own clock for the guard: milliseconds since <paramref name="startTimestamp"/> on the stopwatch, without
    /// overflow however long the machine has run.</summary>
    public static Func<long> MonotonicMs(long startTimestamp) =>
        () => ElapsedMs(System.Diagnostics.Stopwatch.GetTimestamp() - startTimestamp, System.Diagnostics.Stopwatch.Frequency);

    /// <summary><paramref name="ticks"/> of a clock running at <paramref name="frequency"/> per second, in milliseconds, without
    /// overflow for any tick count.</summary>
    public static long ElapsedMs(long ticks, long frequency) =>
        ticks / frequency * 1000 + ticks % frequency * 1000 / frequency;

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

    /// <summary>Something the operator was looking at closed (a drawer, the help, a menu, a flyout): Esc cannot reach PANIC for
    /// <see cref="GuardMs"/>.</summary>
    public void Closed() => _guardUntil = nowMs() + GuardMs;

    /// <summary>The name of the rung that used Esc, or null when none did. While a text field types (<paramref name="typing"/>),
    /// only the overlays answer: Esc is the field's cancel, and never cancels a task, a learn or a selection, or reaches PANIC.</summary>
    public string? Escape(bool typing = false)
    {
        var now = nowMs();
        foreach (var rung in _rungs)
        {
            if (typing && rung.Order != Overlays)
                continue;
            if (rung.Order == Panic && now < _guardUntil)
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
