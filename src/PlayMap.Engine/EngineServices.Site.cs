namespace PlayMap.Engine;

/// <summary>wp2-facade's services: set by its extension's Configure, read by the packages named beside each.</summary>
public sealed partial record EngineServices
{
    /// <summary>A degrade gain per output that the air multiplies in (a stack that lost a member).</summary>
    public IOutputGains? OutputGains { get; init; }
}
