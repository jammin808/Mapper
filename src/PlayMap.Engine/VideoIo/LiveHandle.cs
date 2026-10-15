using PlayMap.Core.Media;
using PlayMap.Core.Model;

namespace PlayMap.Engine.VideoIo;

/// <summary>One live input of the library as the hub keeps it: the receiver (one per input, however many consumers read it), why it
/// is not receiving when it is not, which feed it uses, and when a consumer last drew it (for tally).</summary>
internal sealed class LiveInput
{
    private ILiveSource? _source;
    private long _lastDrawnNs = long.MinValue;
    private int _drawnInBlack;
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

    /// <summary>The receiver now (the primary's, or the backup's while it stands in); null while closed.</summary>
    public ILiveSource? Source
    {
        get => Volatile.Read(ref _source);
        set => Volatile.Write(ref _source, value);
    }

    /// <summary>Why it receives nothing (no runtime for its protocol, a refused open), or "".</summary>
    public string Problem { get; set; } = "";

    /// <summary>"primary" or "backup".</summary>
    public string Using { get; set; } = "primary";

    /// <summary>The tally last given to the sender.</summary>
    public string Tally { get; set; } = "none";

    public int Handles => Volatile.Read(ref _handles);

    public long LastDrawnNs => Volatile.Read(ref _lastDrawnNs);

    public bool DrawnInBlack => Volatile.Read(ref _drawnInBlack) != 0;

    /// <summary>Render thread: a consumer drew it now (allocation-free).</summary>
    public void MarkDrawn(long monoNs, bool black)
    {
        Volatile.Write(ref _lastDrawnNs, monoNs);
        Volatile.Write(ref _drawnInBlack, black ? 1 : 0);
    }

    public void AddHandle() => Interlocked.Increment(ref _handles);

    public void RemoveHandle() => Interlocked.Decrement(ref _handles);
}

/// <summary>A consumer's view of a live input: the renderer's, a scan camera's, a thumbnail's. It reads whatever receiver the input
/// has now (so a reconnect or a backup taking over reaches every consumer at its next acquire) and disposing it lets go of the view
/// only — the receiver stays with the hub while the input is in the project.</summary>
internal sealed class LiveHandle(LiveInput input, Func<long> monoNs, Func<bool> blackout) : ILiveSource
{
    private int _disposed;

    public string Key => input.Id;

    public LiveProtocol Protocol => input.Protocol;

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
        input.MarkDrawn(monoNs(), blackout());
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
