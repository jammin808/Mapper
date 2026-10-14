namespace PlayMap.Core.Bus;

/// <summary>wp2-align's machine-local settings; an older settings file reads the defaults.</summary>
public sealed partial record NodeSettings
{
    public AlignSettings Align { get; init; } = AlignSettings.Default;
}

public sealed record AlignSettings
{
    public static readonly AlignSettings Default = new();

    public int RunsKept { get; init; } = 20;
}
