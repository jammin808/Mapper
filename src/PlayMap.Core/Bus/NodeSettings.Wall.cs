namespace PlayMap.Core.Bus;

/// <summary>wp2-wall's machine-local settings; an older settings file reads the defaults.</summary>
public sealed partial record NodeSettings
{
    public WallSettings Wall { get; init; } = WallSettings.Default;
}

public sealed record WallSettings
{
    public static readonly WallSettings Default = new();

    public int CursorLeaseMs { get; init; } = 3000;

    public int OverlayLeaseMs { get; init; } = 15000;

    public int MaxCursors { get; init; } = 8;

    public int CalibrationTaskTimeoutS { get; init; } = 120;
}
