namespace PlayMap.Core.Bus;

/// <summary>wp2-output's machine-local settings; an older settings file reads the defaults.</summary>
public sealed partial record NodeSettings
{
    public PresentSettings Present { get; init; } = PresentSettings.Default;
}

public sealed record PresentSettings
{
    public static readonly PresentSettings Default = new();

    /// <summary>auto | dxgi | gl: the path the render host starts its outputs on. auto is gl on every platform this round; dxgi is
    /// experimental and explicit (a blown fuse forces gl, with words).</summary>
    public string Path { get; init; } = "auto";

    /// <summary>auto | core330 | es300: the GL dialect (ANGLE on Windows is es300).</summary>
    public string Dialect { get; init; } = "auto";

    /// <summary>auto: join the hardware lock when the group's leader applies it; off: free-run, measured.</summary>
    public string FrameLock { get; init; } = "auto";

    public int SwapGroup { get; init; } = 1;

    public int SwapBarrier { get; init; } = 1;
}
