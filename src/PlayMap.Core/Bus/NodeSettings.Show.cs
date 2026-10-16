using System.Collections.Immutable;

namespace PlayMap.Core.Bus;

/// <summary>wp2-show's machine-local settings; an older settings file reads the defaults.</summary>
public sealed partial record NodeSettings
{
    public ShowSafetySettings Show { get; init; } = ShowSafetySettings.Default;
}

public sealed record ShowSafetySettings
{
    public static readonly ShowSafetySettings Default = new();

    /// <summary>Remote sessions that count as Operator once they prove the node's token on their connection: an address
    /// ("10.0.0.7"), a name at an address ("Companion: FOH deck@10.0.0.7"), or a bare name from this machine only; [] = only the
    /// desk.</summary>
    public ImmutableArray<string> Operators { get; init; } = [];

    /// <summary>Where the air's sound plays: "default" (this machine's default output), "off", or an OpenAL device's name.</summary>
    public string AirSound { get; init; } = "default";
}
