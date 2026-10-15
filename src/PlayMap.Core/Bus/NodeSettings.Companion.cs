using System.Collections.Immutable;

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

    /// <summary>Addresses of other machines whose desk is the desk here once it proves the node's token on the TCP wire; this
    /// machine's desk needs no entry. Empty: a desk elsewhere is a remote client.</summary>
    public ImmutableArray<string> DeskAddresses { get; init; } = [];

    /// <summary>Announce this node as <c>_playmap._tcp</c> on the local network (mDNS / DNS-SD).</summary>
    public bool Mdns { get; init; } = true;
}
