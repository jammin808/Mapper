using System.Globalization;
using PlayMap.Core.Media;
using PlayMap.Core.Model;
using PlayMap.Core.Time;

namespace PlayMap.Media.Video;

/// <summary>What every network input shares: its frames in a <see cref="LiveFramePool"/> (the receive thread writes, the renderer and
/// any other reader acquire the newest), its counters, and its state read from the age of the newest frame on the node's monotonic
/// clock: connecting until the first frame; ok; stale once older than <see cref="LiveSettings.StaleMs"/> (0 = three periods of its own
/// measured rate); lost once older than <see cref="LiveSettings.LostMs"/>; failed while the protocol says it cannot receive (words
/// beside it). A live source has only now: <see cref="Acquire"/> answers the newest frame whatever the time asked. Tally reaches
/// the sender only when it changes. Thread-safe; nothing here allocates per frame once warm.</summary>
public abstract class LiveSource : ILiveSource
{
    private readonly IMonoClock _clock;
    private readonly LiveFramePool _pool;
    private readonly Lock _tallyGate = new();
    private LiveSettings _settings;
    private long _received;
    private long _dropped;
    private long _bytes;
    private long _firstArrivalNs = long.MinValue;
    private long _lastArrivalNs = long.MinValue;
    private long _periodNs;
    private int _width;
    private int _height;
    private int _layout = -1;
    private int _connections;
    private string? _failure;
    private string _waiting = "connecting";
    private (bool Program, bool Preview) _tally;
    private LivePacing _pacing = LivePacing.Latest;
    private int _disposed;

    protected LiveSource(LiveProtocol protocol, string key, LiveSettings settings, IMonoClock clock, int slots = 4)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(clock);
        Protocol = protocol;
        Key = key;
        _settings = settings;
        _clock = clock;
        _pool = new LiveFramePool(slots);
    }

    public LiveProtocol Protocol { get; }

    public string Key { get; }

    /// <summary>The settings the input was opened with.</summary>
    public LiveSettings Settings => Volatile.Read(ref _settings);

    /// <summary>The frames, for the receive thread of a derived source (and tests that read the pool).</summary>
    protected LiveFramePool Pool => _pool;

    protected IMonoClock Clock => _clock;

    protected bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    /// <summary>Frames published since it opened.</summary>
    public long Received => Volatile.Read(ref _received);

    public MediaInfo Info
    {
        get
        {
            var period = Volatile.Read(ref _periodNs);
            var fps = period > 0 ? (1e9 / period).ToString("0.###", CultureInfo.InvariantCulture) : "";
            var layout = Volatile.Read(ref _layout);
            return new MediaInfo(0, Volatile.Read(ref _width), Volatile.Read(ref _height), fps, Protocol.ToString().ToLowerInvariant(),
                layout < 0 ? "" : LivePlanes.Word((PixelLayout)layout), "", 0, 0, layout >= 0 && LivePlanes.HasAlpha((PixelLayout)layout), "");
        }
    }

    public SourceHealth Health
    {
        get
        {
            var live = Live;
            return new SourceHealth(live.State is LiveState.Ok or LiveState.Stale, StateWord(live.State), live.Received, live.Dropped, 0, 0,
                Protocol.ToString().ToLowerInvariant(), false)
            {
                Path = DecodePath.Software,
                Device = Protocol.ToString().ToLowerInvariant(),
                Why = live.Words,
                BusBytesPerFrame = live.Width * (long)live.Height * 2,
            };
        }
    }

    public LiveHealth Live
    {
        get
        {
            var now = _clock.NowNs;
            var last = Volatile.Read(ref _lastArrivalNs);
            var age = last == long.MinValue ? -1 : Math.Max(0, (now - last) / 1_000_000);
            var period = Volatile.Read(ref _periodNs);
            var fps = period > 0 ? Math.Round(1e9 / period, 3) : 0;
            var (state, words) = StateAt(age, period);
            var layout = Volatile.Read(ref _layout);
            var first = Volatile.Read(ref _firstArrivalNs);
            var spanS = first == long.MinValue || last == first ? 0 : (last - first) / 1e9;
            var mbps = spanS > 0 ? Math.Round(Volatile.Read(ref _bytes) * 8 / spanS / 1e6, 1) : 0;
            return new LiveHealth(state, words, Volatile.Read(ref _width), Volatile.Read(ref _height), fps, age, 0, Received,
                Volatile.Read(ref _dropped) + _pool.Dropped, 0, 0, layout < 0 ? "" : LivePlanes.Word((PixelLayout)layout),
                layout >= 0 && LivePlanes.HasAlpha((PixelLayout)layout), TransferFunction.Sdr, mbps, Volatile.Read(ref _connections))
            {
                Ptz = Ptz?.Supported ?? false,
            };
        }
    }

    public virtual IPtz? Ptz => null;

    /// <summary>The pacing asked; this build shows the newest frame (latest) whatever is asked, and says so in STATE.</summary>
    public LivePacing Pacing => _pacing;

    /// <summary>The tally last sent to the sender (neither until the first is sent: a sender starts with none).</summary>
    public (bool Program, bool Preview) Tally
    {
        get
        {
            lock (_tallyGate)
                return _tally;
        }
    }

    public VideoFrameLease? Acquire(long mediaNs) => IsDisposed ? null : _pool.Acquire();

    public void Prepare(long mediaNs, bool playing, double rate)
    {
        // A live source receives whatever the timeline does: there is nothing to seek or preroll.
    }

    public bool IsReadyAt(long mediaNs) => _pool.NewestGeneration > 0;

    public void SetTally(bool program, bool preview)
    {
        if (!Settings.Tally)
            program = preview = false;
        lock (_tallyGate)
        {
            if (_tally == (program, preview))
                return;
            _tally = (program, preview);
        }
        if (!IsDisposed)
            SendTally(program, preview);
    }

    public void SetPacing(LivePacing pacing, LiveAnchor? anchor) => _pacing = pacing;

    /// <summary>Applies changed settings in place (thresholds, tally, quality); what needs a new connection is the caller's.</summary>
    public void Update(LiveSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Volatile.Write(ref _settings, settings);
        OnSettings(settings);
    }

    /// <summary>The sender's tally changed (receive thread or caller's): pass it on.</summary>
    protected abstract void SendTally(bool program, bool preview);

    /// <summary>Settings changed in place.</summary>
    protected virtual void OnSettings(LiveSettings settings)
    {
        // A protocol with nothing to apply in place keeps the default.
    }

    /// <summary>Receive thread: a frame was published into <see cref="Pool"/> (its arrival on the node's clock and its size).</summary>
    protected void Delivered(PixelLayout layout, int width, int height, long bytes, long arrivalNs)
    {
        var last = Volatile.Read(ref _lastArrivalNs);
        if (last != long.MinValue && arrivalNs > last)
        {
            var interval = arrivalNs - last;
            var period = Volatile.Read(ref _periodNs);
            // A smoothed period: the newest interval weighs an eighth, so one late frame does not swing the rate.
            Volatile.Write(ref _periodNs, period == 0 ? interval : period + (interval - period) / 8);
        }
        if (Volatile.Read(ref _firstArrivalNs) == long.MinValue)
            Volatile.Write(ref _firstArrivalNs, arrivalNs);
        Volatile.Write(ref _lastArrivalNs, arrivalNs);
        Volatile.Write(ref _width, width);
        Volatile.Write(ref _height, height);
        Volatile.Write(ref _layout, (int)layout);
        Interlocked.Add(ref _bytes, bytes);
        Interlocked.Increment(ref _received);
        Volatile.Write(ref _failure, null);
    }

    /// <summary>Receive thread: a frame the protocol dropped or that could not be taken whole.</summary>
    protected void CountDropped() => Interlocked.Increment(ref _dropped);

    /// <summary>The receivers or connections the protocol reports.</summary>
    protected void SetConnections(int connections) => Volatile.Write(ref _connections, connections);

    /// <summary>The protocol cannot receive: words until the next frame.</summary>
    protected void Fail(string words) => Volatile.Write(ref _failure, words);

    /// <summary>What a source with no frame yet says (connecting, or why it waits).</summary>
    protected void Waiting(string words) => Volatile.Write(ref _waiting, words);

    private (LiveState State, string Words) StateAt(long ageMs, long periodNs)
    {
        if (Volatile.Read(ref _failure) is { } failure)
            return (LiveState.Failed, failure);
        if (ageMs < 0)
            return (LiveState.Connecting, Volatile.Read(ref _waiting));
        var settings = Settings;
        var lostMs = Math.Max(1, settings.LostMs);
        if (ageMs > lostMs)
            return (LiveState.Lost, string.Create(CultureInfo.InvariantCulture, $"no frame for {ageMs / 1000.0:0.0} s"));
        var staleMs = settings.StaleMs > 0 ? settings.StaleMs : periodNs > 0 ? Math.Max(1, 3 * periodNs / 1_000_000) : lostMs;
        if (ageMs > staleMs)
            return (LiveState.Stale, string.Create(CultureInfo.InvariantCulture, $"the newest frame is {ageMs} ms old"));
        return (LiveState.Ok, "");
    }

    /// <summary>The STATE word of a state.</summary>
    public static string StateWord(LiveState state) => state switch
    {
        LiveState.Connecting => "connecting",
        LiveState.Ok => "ok",
        LiveState.Stale => "stale",
        LiveState.Lost => "lost",
        LiveState.Absent => "absent",
        _ => "failed",
    };

    /// <summary>Stops the receive thread and closes the connection; runs once, before the frames go.</summary>
    protected abstract void Close();

    /// <summary>Closes the connection (<see cref="Close"/>), then frees the frames no reader holds. Once.</summary>
    protected virtual void Dispose(bool disposing)
    {
        if (!disposing || Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        Close();
        _pool.Dispose();
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }
}

/// <summary>What a protocol's runtime says beyond its <see cref="ProviderStatus"/>, for STATE.videoio: OMT's codec path and service
/// discovery, NDI's 6.3 advertisers. A provider answers what applies to it; the rest keep their defaults.</summary>
public interface IVideoRuntimeFacts
{
    /// <summary>The VMX codec's CPU path: "avx2", "sse42", "neon" or "missing".</summary>
    string Vmx => "";

    /// <summary>Service discovery: "avahi", "bonjour", "dnsapi" or "none".</summary>
    string Mdns => "";

    /// <summary>The 6.3 send and receive advertisers are made (a 6.3 or later runtime with a discovery server).</summary>
    bool Advertising => false;
}
