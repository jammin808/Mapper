namespace PlayMap.Core.Bus;

/// <summary>wp2-audio's machine-local settings; an older settings file reads the defaults.</summary>
public sealed partial record NodeSettings
{
    public AudioNodeSettings Audio { get; init; } = AudioNodeSettings.Default;
}

public sealed record AudioNodeSettings
{
    public static readonly AudioNodeSettings Default = new();

    /// <summary>This machine's input override ("" = the project's).</summary>
    public string Input { get; init; } = "";

    public string Channels { get; init; } = "";

    /// <summary>auto | ffmpeg | wasapi | none.</summary>
    public string Backend { get; init; } = "auto";

    /// <summary>Measured or entered input latency, ms; −1 = the backend's report.</summary>
    public double InputLatencyMs { get; init; } = -1;
}
