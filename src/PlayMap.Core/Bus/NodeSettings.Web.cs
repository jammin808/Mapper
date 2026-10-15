using System.Collections.Immutable;

namespace PlayMap.Core.Bus;

/// <summary>The web port's machine-local settings; an older settings file reads the defaults.</summary>
public sealed partial record NodeSettings
{
    public WebSettings Web { get; init; } = WebSettings.Default;
}

public sealed record WebSettings
{
    public static readonly WebSettings Default = new();

    /// <summary>Names this node answers to on the web port without the token, beyond its addresses, <c>localhost</c> and this
    /// machine's own name (bare or in <c>.local</c>): a venue DNS name a browser or a controller uses. A request let in without
    /// the token that names any other host is refused, so a page that points its own name at this machine cannot drive it.</summary>
    public ImmutableArray<string> Hosts { get; init; } = [];
}
