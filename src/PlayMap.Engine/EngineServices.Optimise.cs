namespace PlayMap.Engine;

/// <summary>wp2-optimise's services: set by its extension's Configure, read by the packages named beside each.</summary>
public sealed partial record EngineServices
{
    /// <summary>Posters by content hash (THUMB reads them first).</summary>
    public IPosterStore? Posters { get; init; }
}
