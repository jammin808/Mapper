using System.Collections.Immutable;

namespace PlayMap.Core.Model;

/// <summary>A fleet member's role in the show.</summary>
public enum FleetRole { Primary, Backup }

/// <summary>Round 2 has hot backups only (a full render of the primary's show); warm and cold are later values.</summary>
public enum BackupMode { Hot }

/// <summary>A machine in the show's fleet as the primary recorded it when the backup joined.</summary>
public sealed record FleetMember(string Id, string NodeId, string Name, FleetRole Role, string? BackupOf, BackupMode Mode,
    int TimeoutMs) : IEntity;

/// <summary>What a takeover switches so the audience sees the machine that has the show: one output's projector, by its own input.
/// <paramref name="PrimaryNode"/> is the machine wired to <paramref name="PrimaryInput"/> (the node the route was made on); every
/// other machine reaches the projector on <paramref name="BackupInput"/>. A route whose device is "" switches nothing (the operator
/// switches by hand).</summary>
public sealed record FleetRoute(string OutputId, string DeviceId, string PrimaryInput, string BackupInput, string PrimaryNode)
{
    /// <summary>The input that shows <paramref name="nodeId"/>'s picture.</summary>
    public string InputFor(string nodeId) => string.Equals(nodeId, PrimaryNode, StringComparison.Ordinal) ? PrimaryInput : BackupInput;

    /// <summary>The machine a projector on <paramref name="input"/> shows: "primary", "backup", or "" for neither input.</summary>
    public string Side(string input) =>
        string.Equals(input, PrimaryInput, StringComparison.OrdinalIgnoreCase) ? "primary"
        : string.Equals(input, BackupInput, StringComparison.OrdinalIgnoreCase) ? "backup" : "";
}

/// <summary>Who backs up whom, who leads the sync group next, and what a takeover switches (wp2-fleet, wp2-backup).</summary>
public sealed record FleetPlan
{
    public static readonly FleetPlan Default = new();
    public ImmutableArray<FleetMember> Members { get; init; } = [];
    /// <summary>Node ids in the order they take the lead of the sync group when its leader goes quiet; [] = nobody promotes.</summary>
    public ImmutableArray<string> Succession { get; init; } = [];
    /// <summary>One route per output at most, in the order they were made.</summary>
    public ImmutableArray<FleetRoute> Routes { get; init; } = [];
}
