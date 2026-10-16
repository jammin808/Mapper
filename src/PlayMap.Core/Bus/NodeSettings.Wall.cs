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

    /// <summary>The spacing of a camera re-solve's dots (projector px).</summary>
    public double ResolveDotSpacing { get; init; } = 64;

    /// <summary>How long one pattern of a camera re-solve holds on the wall (present, the camera's latency and one settled picture);
    /// a capture's pattern time is its frames times this.</summary>
    public int ResolveFrameMs { get; init; } = 100;
}
