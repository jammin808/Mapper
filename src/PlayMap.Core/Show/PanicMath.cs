using PlayMap.Core.Model;

namespace PlayMap.Core.Show;

/// <summary>Panic's words and its arithmetic, as pure functions of the leader instant: an output's level under a panic (a SAFE
/// look is two half fades — down to black, the safe source, up again), the level of an output coming back from one, and the
/// order of outcomes when two outputs showing one playlist disagree (the more conservative wins).</summary>
public static class PanicMath
{
    /// <summary>The wire word for an outcome (BLACK, FX-OFF, …).</summary>
    public static string Word(PanicOutcome outcome) => outcome switch
    {
        PanicOutcome.Black => "BLACK",
        PanicOutcome.Run => "RUN",
        PanicOutcome.Rewind => "REWIND",
        PanicOutcome.Next => "NEXT",
        PanicOutcome.Freeze => "FREEZE",
        PanicOutcome.Safe => "SAFE",
        PanicOutcome.Shutter => "SHUTTER",
        PanicOutcome.FxOff => "FX-OFF",
        _ => "HARD",
    };

    /// <summary>The JSON word for an outcome (black, fxOff, …).</summary>
    public static string Json(PanicOutcome outcome) => outcome switch
    {
        PanicOutcome.Black => "black",
        PanicOutcome.Run => "run",
        PanicOutcome.Rewind => "rewind",
        PanicOutcome.Next => "next",
        PanicOutcome.Freeze => "freeze",
        PanicOutcome.Safe => "safe",
        PanicOutcome.Shutter => "shutter",
        PanicOutcome.FxOff => "fxOff",
        _ => "hard",
    };

    /// <summary>An outcome from its wire or JSON word, ignoring case.</summary>
    public static bool TryParse(string word, out PanicOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(word);
        foreach (var o in Enum.GetValues<PanicOutcome>())
        {
            if (word.Equals(Word(o), StringComparison.OrdinalIgnoreCase) || word.Equals(Json(o), StringComparison.OrdinalIgnoreCase))
            {
                outcome = o;
                return true;
            }
        }
        outcome = PanicOutcome.Black;
        return false;
    }

    /// <summary>How conservative an outcome is for a transport: HARD > BLACK > REWIND > NEXT > RUN (the picture-only outcomes
    /// leave transports running, as RUN does).</summary>
    public static int Severity(PanicOutcome outcome) => outcome switch
    {
        PanicOutcome.Hard => 4,
        PanicOutcome.Black => 3,
        PanicOutcome.Rewind => 2,
        PanicOutcome.Next => 1,
        _ => 0,
    };

    /// <summary>True for the outcomes that take an output's picture to black over the fade (SAFE dips through black).</summary>
    public static bool Darkens(PanicOutcome outcome) =>
        outcome is PanicOutcome.Black or PanicOutcome.Run or PanicOutcome.Rewind or PanicOutcome.Next or PanicOutcome.Hard or PanicOutcome.Safe;

    /// <summary>An output's picture level under a panic at a leader instant. SAFE: the level fade takes it down to black, then from
    /// <see cref="OutputPanicAir.SafeFromLeaderNs"/> the safe look comes up over the same duration.</summary>
    public static float Level(OutputPanicAir o, long leaderNs)
    {
        ArgumentNullException.ThrowIfNull(o);
        if (o.Safe is not null && leaderNs >= o.SafeFromLeaderNs)
            return Up(o.SafeFromLeaderNs, o.Level.DurationNs, leaderNs);
        return o.Level.At(leaderNs);
    }

    /// <summary>A fade up from black starting at <paramref name="fromNs"/>; with no duration it is up from that instant on.</summary>
    internal static float Up(long fromNs, long durationNs, long leaderNs) =>
        durationNs <= 0 ? (leaderNs >= fromNs ? 1f : 0f) : new AirFade(0, 1, fromNs, durationNs).At(leaderNs);

    /// <summary>The instant an output's panic picture stops moving (the fade's end; SAFE: the safe look fully up).</summary>
    public static long SettlesAt(OutputPanicAir o)
    {
        ArgumentNullException.ThrowIfNull(o);
        return o.Safe is not null ? o.SafeFromLeaderNs + o.Level.DurationNs : o.Level.StartLeaderNs + o.Level.DurationNs;
    }

    /// <summary>The safe look while it shows (from the dip's bottom until it is cleared), else null.</summary>
    public static SourceRef? Override(OutputPanicAir o, long leaderNs)
    {
        ArgumentNullException.ThrowIfNull(o);
        return o.Safe is { } safe && leaderNs >= o.SafeFromLeaderNs && leaderNs < o.SafeUntilLeaderNs ? safe : null;
    }
}

/// <summary>An output coming back from a panic: its level fading up from where it was; a SAFE look dips back to the show (the
/// safe source fades out until <see cref="SafeUntilLeaderNs"/>, then the show fades in over the same duration).</summary>
public sealed record AirReturn(string OutputId, AirFade Level, SourceRef? Safe, long SafeUntilLeaderNs)
{
    public float At(long leaderNs) =>
        Safe is not null && leaderNs >= SafeUntilLeaderNs ? PanicMath.Up(SafeUntilLeaderNs, Level.DurationNs, leaderNs) : Level.At(leaderNs);

    public SourceRef? Override(long leaderNs) => Safe is { } safe && leaderNs < SafeUntilLeaderNs ? safe : null;

    public bool Settled(long leaderNs) => leaderNs >= (Safe is not null ? SafeUntilLeaderNs + Level.DurationNs : Level.StartLeaderNs + Level.DurationNs);
}
