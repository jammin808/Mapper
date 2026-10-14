namespace PlayMap.Engine;

/// <summary>wp2-control's services: set by its extension's Configure, read by the packages named beside each.</summary>
public sealed partial record EngineServices
{
    /// <summary>The one Art-Net socket (DMX in, ArtTimeCode, LED out).</summary>
    public PlayMap.Core.Dmx.IArtNetEndpoint? ArtNet { get; init; }
}
