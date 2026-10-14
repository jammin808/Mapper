using System.Collections.Immutable;

namespace PlayMap.Engine;

/// <summary>wp2-show's services: set by its extension's Configure, read by the packages named beside each.</summary>
public sealed partial record EngineServices
{
    /// <summary>The show's gate: Show Mode's table, locks and the blind redirection. Open until the engine built from these services
    /// binds its own table at construction, then that engine's (the first engine built from them keeps it).</summary>
    public IShowGate ShowGate { get; init; } = new Show.LateShowGate();

    /// <summary>Every package's preflight checks; each adds its own in its extension's Configure.</summary>
    public ImmutableArray<IPreflightCheck> PreflightChecks { get; init; } = [];
}
