using System.Collections.Immutable;

namespace PlayMap.Core.Model;

/// <summary>A fleet member's role in the show.</summary>
public enum FleetRole { Primary, Backup }

/// <summary>Round 2 has hot backups only (a full render of the primary's show); warm and cold are later values.</summary>
public enum BackupMode { Hot }

/// <summary>A machine in the show's fleet as the primary recorded it when the backup joined.</summary>
public sealed record FleetMember(string Id, string NodeId, string Name, FleetRole Role, string? BackupOf, BackupMode Mode,
    int TimeoutMs) : IEntity;

/// <summary>Who backs up whom, and who leads the sync group next (wp2-fleet).</summary>
public sealed record FleetPlan
{
    public static readonly FleetPlan Default = new();
    public ImmutableArray<FleetMember> Members { get; init; } = [];
    /// <summary>Node ids in the order they take the lead of the sync group when its leader goes quiet; [] = nobody promotes.</summary>
    public ImmutableArray<string> Succession { get; init; } = [];
}
