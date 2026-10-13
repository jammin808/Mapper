namespace PlayMap.Core.Model;

public enum LivePacing { Latest, Smooth, Locked }
public enum LiveLoss { Hold, Black, Fallback }
public enum LiveDepth { Auto, Eight, High }

/// <summary>How a live input (an NDI, OMT or Spout stream item) is received and what it shows when it fails.</summary>
public sealed record LiveSettings
{
    public static readonly LiveSettings Default = new();
    public LivePacing Pacing { get; init; } = LivePacing.Latest;
    /// <summary>NDI lowest bandwidth / OMT 1/8 preview.</summary>
    public bool Proxy { get; init; }
    public LiveDepth Depth { get; init; } = LiveDepth.Auto;
    /// <summary>Keep it connected while it is in the project.</summary>
    public bool Warm { get; init; } = true;
    /// <summary>A second live URI taken over on loss; "" = none.</summary>
    public string Backup { get; init; } = "";
    public LiveLoss OnLoss { get; init; } = LiveLoss.Hold;
    /// <summary>SourceRef text shown on loss with Fallback (never a live or stream source).</summary>
    public string Fallback { get; init; } = "";
    /// <summary>0 = three periods of the input's own rate.</summary>
    public int StaleMs { get; init; }
    public int LostMs { get; init; } = 1000;
    /// <summary>Steady frames before leaving a fallback.</summary>
    public int RecoverMs { get; init; } = 2000;
    /// <summary>Tell the sender when it is on air.</summary>
    public bool Tally { get; init; } = true;
    /// <summary>OMT suggested quality: default | low | medium | high.</summary>
    public string Quality { get; init; } = "default";
}

public enum SendProtocol { Ndi, Omt, Spout, Syphon }
public enum SendFormat { Auto, Uyvy, Uyva, P216, Pa16, Bgra8, Rgba8, Rgb10A2, Rgba16F }
public enum SendFit { Fit, Fill, Stretch }

/// <summary>A picture this node sends: a tap of one point of the pipeline, packed and announced under a name.</summary>
/// <param name="Tap">"output:op…" (final), "prewarp:op…", "canvas:cv…", "source:&lt;sourceref&gt;".</param>
/// <param name="Width">0 = the tap's own size.</param>
/// <param name="Fps">0 = the tap's rate (an output's RefreshHz, else the show rate).</param>
public sealed record VideoSend(string Id, string Name, SendProtocol Protocol, string Tap, int Width, int Height, double Fps,
    SendFormat Format, bool Enabled) : IEntity
{
    /// <summary>Publish every n-th frame (Spout).</summary>
    public int RateDivider { get; init; } = 1;
    /// <summary>The name other apps see; "" = "PlayMap &lt;node&gt; - &lt;name&gt;" (Spout) or the protocol's own "HOST (name)".</summary>
    public string PublishedName { get; init; } = "";
    /// <summary>Blackout and freeze apply to canvas and source taps too.</summary>
    public bool FollowAir { get; init; } = true;
    /// <summary>OMT sender quality; NDI refuses anything but "default".</summary>
    public string Quality { get; init; } = "default";
    /// <summary>NDI groups ("" = public).</summary>
    public string Groups { get; init; } = "";
    public SendFit Fit { get; init; } = SendFit.Fit;
    /// <summary>Pack and read back only while a receiver is connected.</summary>
    public bool OnlyWhenWatched { get; init; } = true;
}
