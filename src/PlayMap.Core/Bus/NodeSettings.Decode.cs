namespace PlayMap.Core.Bus;

/// <summary>wp2-decode's machine-local settings; an older settings file reads the defaults.</summary>
public sealed partial record NodeSettings
{
    public DecodeSettings Decode { get; init; } = DecodeSettings.Default;
}

public sealed record DecodeSettings
{
    public static readonly DecodeSettings Default = new();

    /// <summary>gpu | ffmpeg (the CPU path for Hap1/5/Y/A/M only).</summary>
    public string Hap { get; init; } = "gpu";

    /// <summary>0 = cores − 1.</summary>
    public int HapWorkers { get; init; }

    public int HapQueueFrames { get; init; } = 4;

    public int HapReadAheadMs { get; init; } = 250;

    public int HapReadAheadMb { get; init; } = 192;

    /// <summary>0 = no arena (the copy path).</summary>
    public int UploadArenaMb { get; init; } = 256;

    /// <summary>auto | egl | glx (Linux).</summary>
    public string GlApi { get; init; } = "auto";

    /// <summary>auto | direct | gpu-copy | copy-back | software.</summary>
    public string Policy { get; init; } = "auto";

    /// <summary>"" = the platform's order; vaapi | cuda | d3d11va | videotoolbox.</summary>
    public string Device { get; init; } = "";

    public int QueueFramesHw { get; init; } = 8;
}
