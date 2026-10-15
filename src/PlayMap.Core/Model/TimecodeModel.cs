using System.Collections.Immutable;

namespace PlayMap.Core.Model;

public enum TimecodeKind { Ltc, Mtc, ArtNet, Internal }
public enum ChaseMode { Locked }
public enum ChaseLoss { Hold, Stop, Continue }
public enum ChaseGap { Black, Hold }

/// <summary>A timecode input: where code arrives and how it is read.</summary>
public sealed record TimecodeSource(string Id, string Name, TimecodeKind Kind) : IEntity
{
    /// <summary>ltc: audio input key ("" = default; "file:/path.wav" loops a file); mtc: MIDI port key; artNet/internal: "".</summary>
    public string Device { get; init; } = "";
    /// <summary>ltc: 1-based input channel; artNet: StreamId 0–255.</summary>
    public int Channel { get; init; } = 1;
    /// <summary>"auto" | "23.976" | "24" | "25" | "29.97df" | "29.97" | "30" | "50" | "59.94df" | "59.94" | "60".</summary>
    public string Format { get; init; } = "auto";
    /// <summary>Which node opens it ("" = the node that writes the chase).</summary>
    public string NodeId { get; init; } = "";
    /// <summary>Input path latency: code arrives this late.</summary>
    public long LatencyNs { get; init; }
    public double FloorDbfs { get; init; } = -46;
    public int FitWindowMs { get; init; } = 1500;
}

/// <summary>How a playlist follows timecode. Engagement is runtime (kept in the recovery record), not the document.</summary>
public sealed record TimecodeChase(ImmutableArray<string> Sources)
{
    public ChaseMode Mode { get; init; } = ChaseMode.Locked;
    /// <summary>+ = the picture is shown later than its timecode.</summary>
    public long DelayNs { get; init; }
    public ChaseLoss OnLoss { get; init; } = ChaseLoss.Hold;
    /// <summary>How long a dropout is ridden through on the last line (0–10 s).</summary>
    public long FreewheelNs { get; init; } = 500_000_000;
    /// <summary>How far the code may land off its fitted line and still be drift; past it the code has jumped and the chase
    /// relocates at once. 0 = auto: max(2 frames, 5σ).</summary>
    public long JumpNs { get; init; }
    public ChaseGap Gap { get; init; } = ChaseGap.Black;
}

/// <summary>The show's timecode inputs (wp2-control).</summary>
public sealed record TimecodeModel
{
    public static readonly TimecodeModel Empty = new();
    public ImmutableArray<TimecodeSource> Sources { get; init; } = [];
}
