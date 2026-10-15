namespace PlayMap.Core.Bus;

/// <summary>wp2-content's machine-local settings; an older settings file reads the defaults.</summary>
public sealed partial record NodeSettings
{
    public ContentSettings Content { get; init; } = ContentSettings.Default;
}

public sealed record ContentSettings
{
    public static readonly ContentSettings Default = new();

    /// <summary>GPU ms per frame at 60 Hz before the governor advises (scaled by refresh).</summary>
    public double BudgetMs { get; init; } = 6.0;

    /// <summary>off | advise.</summary>
    public string Governor { get; init; } = "advise";

    public int MaxParticles { get; init; } = 1_048_576;
}
