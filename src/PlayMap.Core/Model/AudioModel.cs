namespace PlayMap.Core.Model;

/// <summary>What the show listens to; a node may override the device (NodeSettings.Audio).</summary>
public sealed record AudioSettings
{
    public static readonly AudioSettings Default = new();
    /// <summary>"" off · "default" · "system" · "test:click|sweep|noise|drums" · a device name.</summary>
    public string Input { get; init; } = "";
    /// <summary>"1", "1+2" (summed), "3-4", "M", "S".</summary>
    public string Channels { get; init; } = "1+2";
    public double GainDb { get; init; }
    public bool AutoGain { get; init; } = true;
    public double GateDb { get; init; } = -60;
    /// <summary>0 = not shown; the acoustic delay is d ÷ 343 m/s.</summary>
    public double ListenerDistanceM { get; init; }
}

/// <summary>The show's beat clock settings (wp2-audio).</summary>
public sealed record TempoSettings
{
    public static readonly TempoSettings Default = new();
    /// <summary>"manual" | "detect".</summary>
    public string Source { get; init; } = "manual";
    public double Bpm { get; init; } = 120;
    public int BeatsPerBar { get; init; } = 4;
    public int BarsPerPhrase { get; init; } = 8;
    public double DetectMinBpm { get; init; } = 75;
    public double DetectMaxBpm { get; init; } = 180;
}
