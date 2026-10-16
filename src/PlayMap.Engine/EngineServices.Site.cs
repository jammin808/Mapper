namespace PlayMap.Engine;

/// <summary>wp2-facade's services: set by its extension's Configure, read by the packages named beside each.</summary>
public sealed partial record EngineServices
{
    /// <summary>A degrade gain per output that the air multiplies in (a stack that lost a member). The site part composes its own with
    /// any set before it (<see cref="Site.ComposedGains"/>).</summary>
    public IOutputGains? OutputGains { get; init; }

    /// <summary>The stacks' own degrade gains (failover MATCH), which the engine sets; null on a node composed without the site part.</summary>
    public Site.StackGains? StackGains { get; init; }
}
