namespace PlayMap.Core.Bus;

/// <summary>wp2-videoio's machine-local settings; an older settings file reads the defaults.</summary>
public sealed partial record NodeSettings
{
    public VideoIoSettings VideoIo { get; init; } = VideoIoSettings.Default;
}

public sealed record VideoIoSettings
{
    public static readonly VideoIoSettings Default = new();

    public bool NdiEnabled { get; init; } = true;

    /// <summary>"" = the runtime's own search (its environment variable, the system paths); never shipped with PlayMap.</summary>
    public string NdiRuntimePath { get; init; } = "";

    public string NdiGroups { get; init; } = "";

    public string NdiDiscovery { get; init; } = "";

    public bool OmtEnabled { get; init; } = true;

    public string OmtDiscovery { get; init; } = "";

    public int OmtPortStart { get; init; } = 6400;

    public int OmtPortEnd { get; init; } = 6600;

    public bool SpoutEnabled { get; init; } = true;

    public int SpoutDiscoveryMs { get; init; } = 500;

    /// <summary>0 = 70 % of the measured link.</summary>
    public int MaxSendMbps { get; init; }

    /// <summary>"" = the default route's NIC.</summary>
    public string Interface { get; init; } = "";
}
