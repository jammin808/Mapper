using PlayMap.Core.Media;
using PlayMap.Core.Model;
using PlayMap.Engine.Playback;
using PlayMap.Media.Sources;
using PlayMap.Render.Engine;

namespace PlayMap.Engine.Rendering;

/// <summary>The renderer's pictures from the engine: library items, the playlists' running items, live
/// inputs, the Stagewatch timer and the scan's raw frames.
///
/// <para>A picture source is opened through the engine's media backend on a worker (opening never runs on the
/// render thread) and kept per consumer: a playlist item has its own decoder (so the next item prerolls while
/// the current one plays, and one clip twice in a row is two decoders), a library item shown directly has one,
/// a live input has one. A playlist's item and media time at a leader instant come from its lane
/// (<see cref="PlaylistPlayback.At"/>, the clip's <c>PositionAt</c>, wrapped for a loop), so the frame is a pure
/// function of the leader's clock; the next item is prepared at its in point
/// <see cref="PrepareAheadNs"/> before its switch, so the switch is gapless; a looping item sets the decoder's
/// loop. A library item shown directly loops on the leader's clock. A decoder no frame has used for
/// <see cref="IdleFrames"/> frames, or whose item left the project, is disposed on a worker.</para>
///
/// <para>A playlist on a generator, a colour or a canvas is routed to it (<see cref="Route"/>); an idle playlist
/// is black. Acquire never blocks and answers null while a source opens or has nothing new: the renderer holds
/// the picture it has. Called on the render thread only; nothing here allocates per frame once warm.</para></summary>
internal sealed class EngineSources : ISourceProvider, IDisposable
{
    public const int IdleFrames = 120;
    public const long PrepareAheadNs = 2_000_000_000;

    /// <summary>A source that could not open is opened again after this many frames (a file fetched from a
    /// peer, a camera plugged in).</summary>
    public const int RetryFrames = 300;

    private const int SweepEvery = 30;

    private static long s_epochs;

    private sealed class Slot
    {
        public required MediaItem Item { get; init; }
        public required long Epoch { get; init; }
        public required long OpenedFrame { get; init; }
        public IVideoSource? Source;
        public string? Problem;
        public int Retired;
        public long LastUsed;
        public long PreparedSeq = long.MinValue;
        public (bool On, long In, long Out) Loop;
        public bool LoopSet;
    }

    private readonly IMediaBackend? _media;
    private readonly ScanFrames? _scan;
    private readonly TimerView? _timer;
    private readonly RemappedLeasePool _leases = new();
    private readonly Dictionary<string, Slot> _itemSlots = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Slot> _mediaSlots = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Slot> _liveSlots = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (CameraRig Rig, MediaItem Item)> _rigItems = new(StringComparer.Ordinal);
    private readonly List<string> _stale = [];
    private readonly Func<string, Slot, bool> _itemGone;
    private readonly Func<string, Slot, bool> _mediaGone;
    private readonly Func<string, Slot, bool> _liveGone;
    private Project _project = Project.CreateDefault();
    private PlaybackSnapshot _playback = PlaybackSnapshot.Empty;
    private long _frame;
    private bool _disposed;

    public EngineSources(IMediaBackend? media, ScanFrames? scan = null, TimerView? timer = null)
    {
        _media = media;
        _scan = scan;
        _timer = timer;
        _itemGone = ItemGone;
        _mediaGone = MediaGone;
        _liveGone = LiveGone;
    }

    /// <summary>Decoders open now (all consumers).</summary>
    public int OpenSources => _itemSlots.Count + _mediaSlots.Count + _liveSlots.Count;

    /// <summary>Why a library item could not be opened, or null.</summary>
    public string? Problem(string mediaOrItemId) =>
        (_itemSlots.GetValueOrDefault(mediaOrItemId) ?? _mediaSlots.GetValueOrDefault(mediaOrItemId) ?? _liveSlots.GetValueOrDefault(mediaOrItemId))?.Problem;

    /// <summary>Once a frame on the render thread, before the renderer asks for anything.</summary>
    public void BeginFrame(EngineSnapshot snapshot, long frame)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        _project = snapshot.Project.Project;
        _playback = snapshot.Playback;
        _frame = frame;
        if (frame % SweepEvery == 0)
        {
            Sweep(_itemSlots, _itemGone);
            Sweep(_mediaSlots, _mediaGone);
            Sweep(_liveSlots, _liveGone);
        }
    }

    public VideoFrameLease? Acquire(SourceRef source, long leaderNs)
    {
        ArgumentNullException.ThrowIfNull(source);
        switch (source.Kind)
        {
            case SourceKind.Media:
                return _project.FindMedia(source.Id) is { } item ? MediaAt(item, leaderNs) : null;
            case SourceKind.Playlist:
                return PlaylistAt(source.Id, leaderNs);
            case SourceKind.Live:
                if (_scan is not null && _scan.TryAcquire(source, out var scanFrame))
                    return scanFrame;
                return LiveFor(source.Id) is { } live ? Live(source.Id, live) : null;
            case SourceKind.Timer:
                return _timer?.Acquire();
            default:
                return null;
        }
    }

    public SourceRef? Route(SourceRef source, long leaderNs)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Kind != SourceKind.Playlist)
            return null;
        if (!_playback.Playlists.TryGetValue(source.Id, out var lane) || lane.IsIdle)
            return SourceRef.None;
        PrepareNext(lane, leaderNs);
        var (now, _, _) = lane.At(leaderNs);
        return now.Kind is SourceKind.Generator or SourceKind.Color or SourceKind.Canvas or SourceKind.None ? now : null;
    }

    // ---- playlists ------------------------------------------------------------------------------------

    private VideoFrameLease? PlaylistAt(string playlistId, long leaderNs)
    {
        if (!_playback.Playlists.TryGetValue(playlistId, out var lane) || lane.IsIdle)
            return null;
        var (source, clip, itemId) = lane.At(leaderNs);
        if (itemId is null)
            return null;
        if (source.Kind == SourceKind.Live)
            return LiveFor(source.Id) is { } liveItem ? Live(source.Id, liveItem) : null;
        if (source.Kind != SourceKind.Media || _project.FindMedia(source.Id) is not { } item)
            return null;
        if (item.Kind == MediaKind.Stream)
            return Live(item.Id, item);
        var slot = SlotFor(_itemSlots, itemId, item);
        var src = slot is null ? null : Volatile.Read(ref slot.Source);
        if (slot is null || src is null)
            return null;
        if (slot.PreparedSeq != clip.Seq)
        {
            // A discontinuity (load, seek, pause, play, rate, loop): the decoder's play head and loop move once.
            SetLoop(slot, src, clip.Loop, clip.InNs, clip.OutNs);
            src.Prepare(clip.PositionAt(leaderNs), clip.State == Core.Playback.TransportState.Playing, clip.Anchor.Rate);
            slot.PreparedSeq = clip.Seq;
        }
        return _leases.Wrap(src.Acquire(clip.PositionAt(leaderNs)), slot.Epoch);
    }

    /// <summary>Opens and prerolls the item a playlist switches to next, once its switch is near.</summary>
    private void PrepareNext(PlaylistPlayback lane, long leaderNs)
    {
        if (lane.NextItemId is null || leaderNs >= lane.SwitchAtLeaderNs || lane.SwitchAtLeaderNs - leaderNs > PrepareAheadNs
            || lane.NextSource.Kind != SourceKind.Media || _project.FindMedia(lane.NextSource.Id) is not { } item || item.Kind == MediaKind.Stream)
            return;
        var slot = SlotFor(_itemSlots, lane.NextItemId, item);
        var src = slot is null ? null : Volatile.Read(ref slot.Source);
        if (slot is null || src is null || slot.PreparedSeq == lane.NextClip.Seq)
            return;
        var clip = lane.NextClip;
        SetLoop(slot, src, clip.Loop, clip.InNs, clip.OutNs);
        src.Prepare(clip.PositionAt(lane.SwitchAtLeaderNs), true, clip.Anchor.Rate);
        slot.PreparedSeq = clip.Seq;
    }

    /// <summary>Sets a decoder's loop when it changes (setting it refills the decoder's queue, so never per seek).</summary>
    private static void SetLoop(Slot slot, IVideoSource src, bool loop, long inNs, long outNs)
    {
        if (src is not FfmpegVideoSource video || slot.Loop == (loop, inNs, outNs) || (!loop && !slot.Loop.On))
            return;
        video.SetLoop(loop, inNs, outNs);
        slot.Loop = (loop, inNs, outNs);
    }

    // ---- library items and live inputs ----------------------------------------------------------------

    /// <summary>A library item shown directly (not through a playlist) loops on the leader's clock: every node
    /// shows the same frame at the same instant without a transport.</summary>
    private VideoFrameLease? MediaAt(MediaItem item, long leaderNs)
    {
        if (item.Kind == MediaKind.Stream)
            return Live(item.Id, item);
        var slot = SlotFor(_mediaSlots, item.Id, item);
        var src = slot is null ? null : Volatile.Read(ref slot.Source);
        if (slot is null || src is null)
            return null;
        var duration = src.Info.DurationNs > 0 ? src.Info.DurationNs : item.Info.DurationNs;
        if (duration <= 0)
            return _leases.Wrap(src.Acquire(0), slot.Epoch);
        if (!slot.LoopSet && src is FfmpegVideoSource video)
        {
            video.SetLoop(true, 0, 0);
            slot.LoopSet = true;
        }
        var t = ((leaderNs % duration) + duration) % duration;
        if (slot.PreparedSeq == long.MinValue)
        {
            src.Prepare(t, true, 1);
            slot.PreparedSeq = 0;
        }
        return _leases.Wrap(src.Acquire(t), slot.Epoch);
    }

    /// <summary>A live input's item: a stream in the library, or a camera rig whose source is a device or a
    /// stream (made into an item once per rig instance).</summary>
    private MediaItem? LiveFor(string id)
    {
        if (_project.FindMedia(id) is { Kind: MediaKind.Stream } item)
            return item;
        if (_project.Stage.Cameras.Find(id) is not { SourceKind: CameraSourceKind.Device or CameraSourceKind.Stream } rig || rig.Source.Length == 0)
            return null;
        if (_rigItems.TryGetValue(id, out var known) && ReferenceEquals(known.Rig, rig))
            return known.Item;
        var made = new MediaItem(rig.Id, rig.Name, MediaKind.Stream, rig.Source, "", 0, MediaInfo.Unknown, []);
        _rigItems[id] = (rig, made);
        return made;
    }

    private VideoFrameLease? Live(string id, MediaItem item)
    {
        var slot = SlotFor(_liveSlots, id, item);
        var src = slot is null ? null : Volatile.Read(ref slot.Source);
        // A live source has only now: its newest frame, whatever the time asked.
        return slot is null || src is null ? null : _leases.Wrap(src.Acquire(0), slot.Epoch);
    }

    // ---- decoders -------------------------------------------------------------------------------------

    private Slot? SlotFor(Dictionary<string, Slot> slots, string key, MediaItem item)
    {
        if (slots.TryGetValue(key, out var slot))
        {
            if (ReferenceEquals(slot.Item, item) || slot.Item == item)
            {
                slot.LastUsed = _frame;
                return slot;
            }
            // The item changed under the same key (a relinked file): a fresh decoder for it.
            Retire(slot);
            slots.Remove(key);
        }
        if (_media is null || _disposed)
            return null;
        slot = new Slot { Item = item, Epoch = Interlocked.Increment(ref s_epochs), LastUsed = _frame, OpenedFrame = _frame };
        slots[key] = slot;
        var media = _media;
        ThreadPool.UnsafeQueueUserWorkItem(static s => Open(s.Media, s.Slot), (Media: media, Slot: slot), preferLocal: false);
        return slot;
    }

    private static void Open(IMediaBackend media, Slot slot)
    {
        try
        {
            var source = media.Open(slot.Item);
            if (source is null)
            {
                slot.Problem = $"{slot.Item.Name} has no picture source on this node";
                return;
            }
            Volatile.Write(ref slot.Source, source);
            // Retired while it opened: nobody will ask for it, so it goes at once.
            if (Volatile.Read(ref slot.Retired) != 0)
                DisposeSource(slot);
        }
        catch (Exception ex) when (ex is NotSupportedException or IOException or InvalidOperationException or ArgumentException or UnauthorizedAccessException)
        {
            slot.Problem = $"{slot.Item.Name}: {ex.Message}";
        }
    }

    private static void Retire(Slot slot)
    {
        Volatile.Write(ref slot.Retired, 1);
        DisposeSource(slot);
    }

    /// <summary>A decoder is disposed on a worker: stopping its thread may wait on a read.</summary>
    private static void DisposeSource(Slot slot)
    {
        if (Interlocked.Exchange(ref slot.Source, null) is { } source)
            ThreadPool.UnsafeQueueUserWorkItem(static s => s.Dispose(), source, preferLocal: false);
    }

    /// <summary>True when the source could not open (the backend refused it, or it opened and gave up).</summary>
    private static bool Failed(Slot slot)
    {
        if (slot.Problem is not null)
            return true;
        var opened = Volatile.Read(ref slot.Source) switch
        {
            FfmpegVideoSource v => v.Opened,
            ImageSource i => i.Opened,
            ImageSequenceSource q => q.Opened,
            CaptureSource c => c.Opened,
            _ => null,
        };
        if (opened is not { IsCompleted: true })
            return false;
#pragma warning disable VSTHRD002 // The task has completed (checked above): Result reads it, it never waits.
        return !opened.IsCompletedSuccessfully || !opened.Result;
#pragma warning restore VSTHRD002
    }

    private bool ItemGone(string itemId, Slot slot) => _project.FindMedia(slot.Item.Id) is null || !ProjectHasItem(itemId);

    private bool MediaGone(string mediaId, Slot slot) => _project.FindMedia(mediaId) is null;

    private bool LiveGone(string id, Slot slot) => _project.FindMedia(id) is null && _project.Stage.Cameras.Find(id) is null;

    private bool ProjectHasItem(string itemId)
    {
        foreach (var list in _project.Playlists)
            foreach (var it in list.Items)
                if (string.Equals(it.Id, itemId, StringComparison.Ordinal))
                    return true;
        return false;
    }

    private void Sweep(Dictionary<string, Slot> slots, Func<string, Slot, bool> gone)
    {
        foreach (var (key, slot) in slots)
            if (_frame - slot.LastUsed > IdleFrames || gone(key, slot) || (Failed(slot) && _frame - slot.OpenedFrame > RetryFrames))
                _stale.Add(key);
        foreach (var key in _stale)
        {
            Retire(slots[key]);
            slots.Remove(key);
        }
        _stale.Clear();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        foreach (var slots in new[] { _itemSlots, _mediaSlots, _liveSlots })
        {
            foreach (var slot in slots.Values)
                Retire(slot);
            slots.Clear();
        }
    }
}
