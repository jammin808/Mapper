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

    /// <summary>HELLO names or addresses whose remote sessions count as Operator; [] = only the desk.</summary>
    public ImmutableArray<string> Operators { get; init; } = [];
}
