using System.Collections.Immutable;

namespace PlayMap.Core.Bus;

/// <summary>wp2-fx's machine-local settings; an older settings file reads the defaults.</summary>
public sealed partial record NodeSettings
{
    public FxSettings Fx { get; init; } = FxSettings.Default;
}

public sealed record FxSettings
{
    public static readonly FxSettings Default = new();

    public ImmutableArray<string> IsfFolders { get; init; } = [];

    public ImmutableArray<string> FfglFolders { get; init; } = [];

    /// <summary>FFGL plugins load only when this is on and the plugin is trusted (FX TRUST, this machine only).</summary>
    public bool FfglEnabled { get; init; }

    /// <summary>Plugin kind → "in-process" | "off" (unlisted = off).</summary>
    public ImmutableSortedDictionary<string, string> FfglTrust { get; init; } = ImmutableSortedDictionary.Create<string, string>(StringComparer.Ordinal);

    public double BudgetMs { get; init; } = 4.0;
}
