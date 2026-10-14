using System.Collections.Immutable;

namespace PlayMap.Core.Bus;

/// <summary>wp2-control's machine-local settings; an older settings file reads the defaults.</summary>
public sealed partial record NodeSettings
{
    public ControlSettings Control { get; init; } = ControlSettings.Default;
}

public sealed record ControlSettings
{
    public static readonly ControlSettings Default = new();

    public bool InputEnabled { get; init; } = true;

    public bool OscEnabled { get; init; } = true;

    public int OscPort { get; init; } = ShowBusConstants.PlayMapOscPort;

    /// <summary>"" = the node's Bind; "127.0.0.1" = this machine only.</summary>
    public string OscBind { get; init; } = "";

    public ImmutableArray<string> OscAllow { get; init; } = [];

    public ImmutableArray<string> OscFeedback { get; init; } = [];

    public bool OscTimetags { get; init; } = true;

    public bool OscQueryEnabled { get; init; } = true;

    public int OscQueryPort { get; init; } = ShowBusConstants.PlayMapOscQueryPort;

    public bool MidiEnabled { get; init; } = true;

    /// <summary>"" = the OS default; winmm | alsa-seq | coremidi.</summary>
    public string MidiBackend { get; init; } = "";

    public ImmutableArray<string> MidiIgnore { get; init; } = [];

    /// <summary>Surface name → this machine's port glob.</summary>
    public ImmutableSortedDictionary<string, string> SurfacePorts { get; init; } = ImmutableSortedDictionary.Create<string, string>(StringComparer.Ordinal);

    /// <summary>DMX input → binding lines only. The Art-Net socket itself opens when any consumer needs it (DMX input, an ARTNET
    /// timecode source, LED output), so LED output never turns on control input.</summary>
    public bool ArtNetEnabled { get; init; }

    /// <summary>"" = all interfaces; the one Art-Net socket (control in, ArtTimeCode, LED out) binds this address on UDP 6454
    /// (tests give it their slot's loopback address).</summary>
    public string ArtNetInterface { get; init; } = "";

    public bool SacnEnabled { get; init; }

    public string SacnInterface { get; init; } = "";

    public ImmutableArray<string> DmxAllow { get; init; } = [];

    public int AxisHz { get; init; } = 100;

    public int FeedbackHz { get; init; } = 50;
}
