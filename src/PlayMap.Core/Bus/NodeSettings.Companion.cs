namespace PlayMap.Core.Bus;

/// <summary>wp2-companion's machine-local settings; an older settings file reads the defaults.</summary>
public sealed partial record NodeSettings
{
    public CompanionSettings Companion { get; init; } = CompanionSettings.Default;
}

public sealed record CompanionSettings
{
    public static readonly CompanionSettings Default = new();

    /// <summary>Cached posters always; live output thumbnails at most 0.5 fps while armed.</summary>
    public bool ThumbsWhileArmed { get; init; } = true;

    public int MaxThumbsPerSession { get; init; } = 128;
}
