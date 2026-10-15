namespace PlayMap.Engine;

/// <summary>wp2-led's services: set by its extension's Configure.</summary>
public sealed partial record EngineServices
{
    /// <summary>The LED network edge (the sender); null on a node without LED output, where the rig is edited and planned but never
    /// sent.</summary>
    public PlayMap.Core.Led.ILedOutput? LedOutput { get; init; }

    /// <summary>Writes changed LED settings (the machine master) into the node's settings file; answers why it could not, or null.</summary>
    public Func<PlayMap.Core.Bus.LedSettings, string?>? LedPersist { get; init; }
}
