using System.Collections.Immutable;
using PlayMap.Core.Model;

namespace PlayMap.Core.Show;

/// <summary>A level fading on the show clock: a pure function of the leader instant, so every node fades on the same frames.</summary>
public readonly record struct AirFade(float From, float To, long StartLeaderNs, long DurationNs)
{
    public static readonly AirFade Full = new(1, 1, 0, 0);

    /// <summary>The level at a leader instant: From before the start, To once the fade is over, linear between.</summary>
    public float At(long leaderNs) =>
        leaderNs <= StartLeaderNs ? From
        : DurationNs <= 0 || leaderNs >= StartLeaderNs + DurationNs ? To
        : From + (To - From) * (float)((leaderNs - StartLeaderNs) / (double)DurationNs);

    public bool Settled(long leaderNs) => leaderNs >= StartLeaderNs + DurationNs;
}

/// <summary>A panic in force: the outcome, who, since when, whether the second press made it hard, and each output's air.</summary>
public sealed record PanicAir(PanicOutcome Outcome, string By, long SinceUnixMs, long SinceLeaderNs, bool Hard,
    ImmutableArray<OutputPanicAir> Outputs, long Seq);

/// <summary>One output's air under a panic: its outcome, its level's fade, and the safe look with when it shows.</summary>
public sealed record OutputPanicAir(string OutputId, PanicOutcome Outcome, AirFade Level, SourceRef? Safe, long SafeFromLeaderNs,
    long SafeUntilLeaderNs);
