namespace PlayMap.Core.Bus;

/// <summary>wp2-fleet's machine-local settings; an older settings file reads the defaults.</summary>
public sealed partial record NodeSettings
{
    public FleetSettings Fleet { get; init; } = FleetSettings.Default;
}

public sealed record FleetSettings
{
    public static readonly FleetSettings Default = new();

    /// <summary>solo | backup | primary | fenced — re-entered at start.</summary>
    public string Role { get; init; } = "solo";

    /// <summary>The primary this node backs up (node id).</summary>
    public string Of { get; init; } = "";

    public int TimeoutMs { get; init; } = 2000;

    public long Epoch { get; init; }

    /// <summary>What a fenced node's outputs show: black | close.</summary>
    public string FencedOutputs { get; init; } = "black";

    public bool Monitor { get; init; } = true;

    public int MonitorMax { get; init; } = 32;
}
