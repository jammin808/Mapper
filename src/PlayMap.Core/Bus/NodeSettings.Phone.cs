namespace PlayMap.Core.Bus;

/// <summary>wp2-phonescan's machine-local settings; an older settings file reads the defaults.</summary>
public sealed partial record NodeSettings
{
    public PhoneSettings Phone { get; init; } = PhoneSettings.Default;
}

public sealed record PhoneSettings
{
    public static readonly PhoneSettings Default = new();

    /// <summary>Serve /scan and accept invites.</summary>
    public bool Enabled { get; init; } = true;

    public int InviteMinutes { get; init; } = 10;

    public int MaxConnected { get; init; } = 4;
}
