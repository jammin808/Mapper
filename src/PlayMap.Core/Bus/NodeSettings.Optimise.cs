namespace PlayMap.Core.Bus;

/// <summary>wp2-optimise's machine-local settings; an older settings file reads the defaults.</summary>
public sealed partial record NodeSettings
{
    public OptimiseNodeSettings Optimise { get; init; } = OptimiseNodeSettings.Default;
}

public sealed record OptimiseNodeSettings
{
    public static readonly OptimiseNodeSettings Default = new();

    /// <summary>auto (run jobs) | never (this node never encodes; it may still fetch).</summary>
    public string Role { get; init; } = "auto";

    public double TrickleGpuDuty { get; init; } = 0.15;

    public int TrickleDiskMbps { get; init; } = 50;

    public int PauseAfterDropS { get; init; } = 60;

    public int MinFreeGb { get; init; } = 10;

    public double MinPsnr { get; init; } = 30;

    public int IdleExitS { get; init; } = 120;
}
