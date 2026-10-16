namespace PlayMap.Engine;

/// <summary>wp2-sync's services: set by its extension's Configure, read by the packages named beside each.</summary>
public sealed partial record EngineServices
{
    /// <summary>The group's air across machines.</summary>
    public IFleetFanout? Fanout { get; init; }

    /// <summary>The group's sync, network side (roles kept, the anchor feed, the board); null on a node without the sync part.</summary>
    public Sync.SyncNode? GroupSync { get; init; }
}
