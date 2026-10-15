using PlayMap.Core.Media;
using PlayMap.Core.Model;

namespace PlayMap.Engine.VideoIo;

/// <summary>One live input of the library as the hub keeps it: the receiver (one per input, however many consumers read it), why it
/// is not receiving when it is not, which feed it uses, and when a consumer last drew it, and last drew it for the air (for tally).</summary>
internal sealed class LiveInput
{
    private ILiveSource? _source;
    private ILiveSource? _primary;
    private ILiveSource? _backup;
    private SourceRef? _lossRoute;
    private long _lastDrawnNs = long.MinValue;
    private long _lastAirNs = long.MinValue;
    private int _handles;

    public LiveInput(MediaItem item, LiveProtocol protocol, string address, bool byUrl)
    {
        Item = item;
        Protocol = protocol;
        Address = address;
        ByUrl = byUrl;
    }

    public MediaItem Item { get; set; }

    public string Id => Item.Id;

    public LiveProtocol Protocol { get; set; }

    public string Address { get; set; }

    public bool ByUrl { get; set; }

    /// <summary>The receiver consumers read now: the primary's, or the backup's while it stands in; null while closed.</summary>
    public ILiveSource? Source => Volatile.Read(ref _source);

    /// <summary>The primary feed's receiver (the item's own location).</summary>
    public ILiveSource? Primary
    {
        get => Volatile.Read(ref _primary);
        set
        {
            Volatile.Write(ref _primary, value);
            if (Using == "primary")
                Volatile.Write(ref _source, value);
        }
    }

    /// <summary>The backup feed's receiver (<see cref="LiveSettings.Backup"/>), warm beside the primary; null when none.</summary>
    public ILiveSource? Backup
    {
        get => Volatile.Read(ref _backup);
        set
        {
            Volatile.Write(ref _backup, value);
            if (Using == "backup")
                Volatile.Write(ref _source, value);
        }
    }

    /// <summary>The backup location its receiver was opened for ("" when none).</summary>
    public string BackupUri { get; set; } = "";

    /// <summary>Makes consumers read the primary or the backup from their next acquire (a pointer swap).</summary>
    public void Use(bool backup)
    {
        Using = backup ? "backup" : "primary";
        Volatile.Write(ref _source, backup ? Backup : Primary);
    }

    /// <summary>What a consumer shows instead of the feed while the loss policy holds (<see cref="SourceRef.None"/> for black, or the
    /// fallback), or null for the feed itself (a held picture included). Written by the hub's tick, read by the render thread.</summary>
    public SourceRef? LossRoute
    {
        get => Volatile.Read(ref _lossRoute);
        set => Volatile.Write(ref _lossRoute, value);
    }

    /// <summary>What the loss policy does now: "" while the feed delivers, else "hold", "black" or "fallback" (journalled and raised
    /// once per loss, cleared when the feed is back).</summary>
    public string Loss { get; set; } = "";

    /// <summary>When the feed has been steady since, while a loss holds (long.MinValue when not counting).</summary>
    public long SteadySinceNs { get; set; } = long.MinValue;

    /// <summary>When the primary's receiver opened (mono ns; long.MinValue while closed): a feed with no frame LostMs after it
    /// opened is lost.</summary>
    public long OpenedNs { get; set; } = long.MinValue;

    /// <summary>When the backup's receiver opened (mono ns; long.MinValue while closed).</summary>
    public long BackupOpenedNs { get; set; } = long.MinValue;

    /// <summary>Why it receives nothing (no runtime for its protocol, a refused open), or "".</summary>
    public string Problem { get; set; } = "";

    /// <summary>Why the backup receives nothing, or "".</summary>
    public string BackupProblem { get; set; } = "";

    /// <summary>A library item of the project (false for a camera rig's feed, which lives while a consumer holds it).</summary>
    public bool InProject { get; set; }

    /// <summary>"primary" or "backup".</summary>
    public string Using { get; set; } = "primary";

    /// <summary>The tally last given to the sender.</summary>
    public string Tally { get; set; } = "none";

    public int Handles => Volatile.Read(ref _handles);

    public long LastDrawnNs => Volatile.Read(ref _lastDrawnNs);

    /// <summary>When a consumer last drew it into the air (an output, or a send): program, while the air is not blacked out.</summary>
    public long LastAirNs => Volatile.Read(ref _lastAirNs);

    /// <summary>Any thread: a consumer drew it now, for the air or not (a preview, a scan camera); allocation-free.</summary>
    public void MarkDrawn(long monoNs, bool air)
    {
        Volatile.Write(ref _lastDrawnNs, monoNs);
        if (air)
            Volatile.Write(ref _lastAirNs, monoNs);
    }

    public void AddHandle() => Interlocked.Increment(ref _handles);

    public void RemoveHandle() => Interlocked.Decrement(ref _handles);
}

/// <summary>A consumer's view of a live input: the renderer's, a scan camera's, a thumbnail's. It reads whatever receiver the input
/// has now (so a reconnect or a backup taking over reaches every consumer at its next acquire) and disposing it lets go of the view
/// only — the receiver stays with the hub while the input is in the project.</summary>
internal sealed class LiveHandle(LiveInput input, Func<long> monoNs) : ILiveSource
{
    private int _disposed;

    /// <summary>Whether the next acquires draw it into the air: set by the renderer's sources for the part of a frame that draws
    /// outputs and sends, false for its previews; false for any other consumer (a scan camera, a thumbnail).</summary>
    public bool ForAir { get; set; }

    public string Key => input.Id;

    public LiveProtocol Protocol => input.Protocol;

    /// <summary>The input's loss route now (render thread; see <see cref="LiveInput.LossRoute"/>).</summary>
    public SourceRef? LossRoute => input.LossRoute;

    public MediaInfo Info => input.Source?.Info ?? MediaInfo.Unknown;

    public SourceHealth Health => input.Source?.Health
        ?? new SourceHealth(false, input.Problem.Length > 0 ? "failed" : "connecting", 0, 0, 0, 0, input.Protocol.ToString().ToLowerInvariant(), false)
        {
            Why = input.Problem,
        };

    public LiveHealth Live => input.Source?.Live ?? LiveHealth.Opening;

    public IPtz? Ptz => input.Source?.Ptz;

    public VideoFrameLease? Acquire(long mediaNs)
    {
        if (Volatile.Read(ref _disposed) != 0 || input.Source is not { } source)
            return null;
        input.MarkDrawn(monoNs(), ForAir);
        return source.Acquire(mediaNs);
    }

    public void Prepare(long mediaNs, bool playing, double rate)
    {
        // A live input receives whatever the timeline does.
    }

    public bool IsReadyAt(long mediaNs) => input.Source?.IsReadyAt(mediaNs) ?? false;

    public void SetTally(bool program, bool preview)
    {
        // Tally is the hub's: it sees every consumer of the input, a handle sees one.
    }

    public void SetPacing(LivePacing pacing, LiveAnchor? anchor) => input.Source?.SetPacing(pacing, anchor);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
            input.RemoveHandle();
    }
}
