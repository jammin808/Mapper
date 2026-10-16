using System.Collections.Frozen;
using System.Collections.Immutable;

namespace PlayMap.Core.Model;

/// <summary>One reason in a score: its code, its value with a unit, its share of the score, a light, the words, where on the
/// projector or output it is, a verb line that fixes it ("" when only hands can) and what the hands do.</summary>
public sealed record ScoreTerm(string Code, string Words, double Value, string Unit, double Penalty, string Light, string Where,
    string Fix, string Hint);

/// <summary>An explainable score. Unit "px": px-equivalents, lower is better (A ≤ 1.0, B ≤ 1.5, C ≤ 4, else F — OmniCal's scale);
/// unit "points": 0–100, higher is better (A ≥ 85, B ≥ 70, C ≥ 50, else F). A hard failure is F whatever the value.</summary>
public sealed record QualityScore(double Value, string Unit, string Grade, double RmsPx, double P95Px, double Confidence,
    ImmutableArray<ScoreTerm> Terms, string Words)
{
    public static readonly QualityScore None = new(0, "px", "", 0, 0, 0, [], "");

    /// <summary>What made the result: "pins" (a projector solve), "scan" (SCAN APPLY), "align" (a camera alignment run),
    /// "stack" (STACK ALIGN), "resolve" (a camera re-solve).</summary>
    public string Source { get; init; } = "pins";

    /// <summary>The grade of a value on its unit's scale; a hard failure is F.</summary>
    public static string GradeOf(double value, string unit, bool failed) =>
        failed ? "F"
        : unit == "points" ? (value >= 85 ? "A" : value >= 70 ? "B" : value >= 50 ? "C" : "F")
        : value <= 1.0 ? "A" : value <= 1.5 ? "B" : value <= 4 ? "C" : "F";
}

/// <summary>The term vocabulary every score draws from (owner wp2-wall): one code per reason, so the desk, Companion and the
/// assistant word a scan's or an alignment's score the same way as a pin solve's. Packages fill values; they add no codes.</summary>
public static class ScoreTermCodes
{
    // pins (wp2-wall)
    public const string Rms = "rms", P95 = "p95", Coverage = "coverage", Conditioning = "conditioning", Lens = "lens",
        Outliers = "outliers", Geometry = "geometry", Spread = "spread";
    // scans (wp2-multiscan)
    public const string DenseRms = "dense-rms", Decoded = "decoded", Views = "views";
    // alignment and stacks (wp2-resolve, wp2-stacks)
    public const string Seam = "seam", StackConvergence = "stack", Uniformity = "uniformity", ColourMatch = "colour";
    public static readonly FrozenSet<string> All = new[] { Rms, P95, Coverage, Conditioning, Lens, Outliers, Geometry, Spread,
        DenseRms, Decoded, Views, Seam, StackConvergence, Uniformity, ColourMatch }.ToFrozenSet(StringComparer.Ordinal);
}

public enum CalibrationTarget { Projector, Output }
public enum CalibrationSource { Pins, Scan, Resolve, Revert, Align, Stack, Manual }

/// <summary>A projector's optics, pose, locks and pins as solved.</summary>
public sealed record ProjectorCalibration(Intrinsics Intrinsics, LensDistortion Lens, Pose Pose, SolveLocks Locks,
    ImmutableArray<CalibrationPin> Pins)
{
    /// <summary>Who put the focal lock in <see cref="Locks"/>, put back with it (<see cref="Projector.FocalLock"/>).</summary>
    public FocalLockSource FocalLock { get; init; } = FocalLockSource.Operator;
}

/// <summary>A calibration result, never edited after it is made: a projector's pose and optics, or an output's measured
/// correction, with the score that explains it and where it came from. Files beside it live in DataPath.</summary>
public sealed record CalibrationSnapshot(string Id, string Name, CalibrationTarget Target, string TargetId, long CreatedUnixMs,
    CalibrationSource Source, string? ScanId, string? ParentId, QualityScore Score, string Who, bool Pinned) : IEntity
{
    public ProjectorCalibration? Projector { get; init; }
    public MeasuredCorrection? Output { get; init; }
    /// <summary>0 = not verified on the wall.</summary>
    public double VerifiedP50Px { get; init; }
    public double VerifiedP95Px { get; init; }
    public string MeshHash { get; init; } = "";
    public string RunId { get; init; } = "";
    public string DataPath { get; init; } = "";

    /// <summary>An output's blend γ as this result has it (0 = not recorded: a result made before it was, or a projector's), put back with
    /// it so a revert to a result from before a measured run restores the γ that run wrote.</summary>
    public double BlendGamma { get; init; }
}
