namespace PlayMap.Core.Bus;

/// <summary>wp2-sync's machine-local settings; an older settings file reads the defaults.</summary>
public sealed partial record NodeSettings
{
    public SyncSettings Sync { get; init; } = SyncSettings.Default;
}

public sealed record SyncSettings
{
    public static readonly SyncSettings Default = new();

    /// <summary>solo | lead | follow — re-entered at start.</summary>
    public string Role { get; init; } = "solo";

    public string Group { get; init; } = "";

    public string Leader { get; init; } = "";

    public bool FollowPatternsBlackout { get; init; } = true;

    /// <summary>Clock corrections below 1 ms are slewed at most this fast; larger ones step (and rebase every anchor).</summary>
    public double SlewPpm { get; init; } = 200;

    /// <summary>A leader not heard for this long: followers free-wheel (Hold) and the context says so.</summary>
    public int LeaderTimeoutMs { get; init; } = 3000;
}
