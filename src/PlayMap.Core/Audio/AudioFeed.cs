namespace PlayMap.Core.Audio;

/// <summary>What the modulation bus reads of a node's sound: the frame ring, and the listener while an input is open (null when the
/// node listens to nothing — then audio sources are missing, not silent). Written by whoever opens and closes the input; read
/// lock-free.</summary>
public sealed class AudioFeed
{
    private volatile AudioListener? _listener;

    public AnalysisRing Ring { get; } = new();

    /// <summary>The open input's listener, or null.</summary>
    public AudioListener? Listener
    {
        get => _listener;
        set => _listener = value;
    }

    /// <summary>True while an input is open (its frames are what audio sources read; none yet reads as silence).</summary>
    public bool Listening => _listener is not null;
}
