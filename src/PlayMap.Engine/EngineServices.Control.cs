namespace PlayMap.Engine;

/// <summary>wp2-control's services: set by its extension's Configure, read by the packages named beside each.</summary>
public sealed partial record EngineServices
{
    /// <summary>The one Art-Net socket (DMX in, ArtTimeCode, LED out).</summary>
    public PlayMap.Core.Dmx.IArtNetEndpoint? ArtNet { get; init; }

    /// <summary>The node's control inputs (OSC with its feedback, MIDI, Art-Net, sACN, timecode inputs); null on a bare engine, where
    /// the bindings still fire from CONTROL INJECT and the chases still follow internal sources.</summary>
    public Control.ControlService? Control { get; init; }
}
