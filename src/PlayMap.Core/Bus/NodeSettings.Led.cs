namespace PlayMap.Core.Bus;

/// <summary>wp2-led's machine-local settings; an older settings file reads the defaults.</summary>
public sealed partial record NodeSettings
{
    public LedSettings Led { get; init; } = LedSettings.Default;
}

public sealed record LedSettings
{
    public static readonly LedSettings Default = new();

    /// <summary>The machine master: this machine transmits LED data (off on a fresh node: fail closed).</summary>
    public bool Output { get; init; }

    public int MulticastTtl { get; init; } = 1;

    /// <summary>The bandwidth guard.</summary>
    public int MaxMbps { get; init; } = 200;
}
