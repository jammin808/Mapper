namespace PlayMap.Engine;

/// <summary>wp2-audio's services: set by its extension's Configure, read by the packages named beside each.</summary>
public sealed partial record EngineServices
{
    /// <summary>Modulation sources for bindings.</summary>
    public PlayMap.Core.Modulation.IModulationBus? Modulation { get; init; }

    /// <summary>The analysed audio frames.</summary>
    public PlayMap.Core.Modulation.IAudioFrames? AudioFrames { get; init; }

    /// <summary>The capture hub: audio's parts configure before control's, and control resolves it at start (its own input when absent).</summary>
    public PlayMap.Core.Audio.IAudioInputs? AudioInputs { get; init; }
}
