using System.Collections.Immutable;
using PlayMap.Core.Model;
using PlayMap.Core.Playback;

namespace PlayMap.Engine.Playback;

/// <summary>What the render thread needs to show one playlist at any instant, without asking anyone: the
/// current item and its transport, and — when the current item will end into another — the next item with
/// the leader instant it takes over, already anchored. The switch therefore lands on the exact frame on every
/// node, whatever the control lane is doing at that moment.</summary>
public sealed record PlaylistPlayback(
    string PlaylistId,
    int Index,
    string? ItemId,
    SourceRef Source,
    ClipTransport Clip,
    /// <summary>The leader instant the next item takes over (long.MaxValue = none scheduled).</summary>
    long SwitchAtLeaderNs,
    int NextIndex,
    string? NextItemId,
    SourceRef NextSource,
    ClipTransport NextClip,
    TransitionKind Transition,
    long TransitionNs,
    double Volume)
{
    /// <summary>The item to show at a leader instant: the current one, or the next once its switch came.</summary>
    public (SourceRef Source, ClipTransport Clip, string? ItemId) At(long leaderNs) =>
        leaderNs >= SwitchAtLeaderNs && NextItemId is not null ? (NextSource, NextClip, NextItemId) : (Source, Clip, ItemId);

    public bool IsIdle => Index < 0;
}

/// <summary>Every playlist's playback, published as one immutable value for the render thread and STATE.</summary>
public sealed record PlaybackSnapshot(ImmutableDictionary<string, PlaylistPlayback> Playlists, string? FocusedId, long Revision)
{
    public static readonly PlaybackSnapshot Empty = new(ImmutableDictionary<string, PlaylistPlayback>.Empty.WithComparers(StringComparer.Ordinal), null, 0);

    public PlaylistPlayback? Focused => FocusedId is not null && Playlists.TryGetValue(FocusedId, out var p) ? p : null;
}

/// <summary>The playlists' transports. Single writer: the engine's control lane calls every method; readers
/// take <see cref="Snapshot"/>. Every verb answers what it did or why it did nothing, in Patterns' words.</summary>
public sealed class PlaybackLanes
{
    private readonly Func<string, long> _mediaDurationNs;
    private readonly Dictionary<string, PlaylistPlayback> _lanes = new(StringComparer.Ordinal);
    private string? _focused;
    private long _revision;
    private volatile PlaybackSnapshot _snapshot = PlaybackSnapshot.Empty;

    /// <param name="mediaDurationNs">The probed length of a source's media (0 = unknown or not media).</param>
    public PlaybackLanes(Func<string, long> mediaDurationNs)
    {
        _mediaDurationNs = mediaDurationNs;
    }

    public PlaybackSnapshot Snapshot => _snapshot;

    public string? FocusedId => _focused;

    public event Action<PlaybackSnapshot>? Changed;

    /// <summary>Keeps the lanes in step with the project: a removed playlist's lane goes; a new one is idle;
    /// the focus falls to the first playlist when its own goes.</summary>
    public void Reconcile(Project project)
    {
        ArgumentNullException.ThrowIfNull(project);
        var changed = false;
        foreach (var id in _lanes.Keys.ToList())
        {
            if (project.FindPlaylist(id) is null)
            {
                _lanes.Remove(id);
                changed = true;
            }
        }
        foreach (var list in project.Playlists)
        {
            if (!_lanes.ContainsKey(list.Id))
            {
                _lanes[list.Id] = Idle(list.Id);
                changed = true;
            }
        }
        if (_focused is null || !_lanes.ContainsKey(_focused))
        {
            var first = project.Playlists.Length > 0 ? project.Playlists[0].Id : null;
            if (!string.Equals(first, _focused, StringComparison.Ordinal))
            {
                _focused = first;
                changed = true;
            }
        }
        if (changed)
            Publish();
    }

    public (bool Ok, string Words) Focus(string playlistId)
    {
        if (!_lanes.ContainsKey(playlistId))
            return (false, $"no playlist {playlistId}");
        if (string.Equals(_focused, playlistId, StringComparison.Ordinal))
            return (false, "already focused");
        _focused = playlistId;
        Publish();
        return (true, "focused");
    }

    public PlaylistPlayback? Lane(string? playlistId) =>
        playlistId is not null && _lanes.TryGetValue(playlistId, out var l) ? l : null;

    /// <summary>Cues an item (held on its first frame) without playing it.</summary>
    public (bool Ok, string Words) Cue(Playlist list, int index, long nowLeaderNs)
    {
        ArgumentNullException.ThrowIfNull(list);
        if (index < 0 || index >= list.Items.Length)
            return (false, $"{list.Name} has no item {index + 1}");
        var lane = Lane(list.Id) ?? Idle(list.Id);
        _lanes[list.Id] = Schedule(list, Load(list, index, lane.Clip.Seq), nowLeaderNs);
        Publish();
        return (true, $"{list.Name}: cued {Label(list, index)}");
    }

    /// <summary>Cues an item and plays it at an instant (now or a scheduled start).</summary>
    public (bool Ok, string Words) Go(Playlist list, int index, long atLeaderNs)
    {
        ArgumentNullException.ThrowIfNull(list);
        if (index < 0 || index >= list.Items.Length)
            return (false, list.Items.Length == 0 ? $"{list.Name} is empty" : $"{list.Name} has no item {index + 1}");
        var lane = Lane(list.Id) ?? Idle(list.Id);
        var loaded = Load(list, index, lane.Clip.Seq);
        var played = loaded.Clip.Play(atLeaderNs);
        _lanes[list.Id] = Schedule(list, loaded with { Clip = played.Transport }, atLeaderNs);
        Publish();
        return (true, $"{list.Name}: playing {Label(list, index)}");
    }

    /// <summary>NEXT / PREV: the neighbouring item in list order (wrapping when the list loops), playing.</summary>
    public (bool Ok, string Words) Step(Playlist list, int step, long atLeaderNs)
    {
        ArgumentNullException.ThrowIfNull(list);
        var lane = Lane(list.Id) ?? Idle(list.Id);
        var target = lane.IsIdle ? (step > 0 ? 0 : -1) : PlaylistCursor.StepIndex(list, lane.Index, step);
        if (target < 0)
            return (false, step > 0 ? $"{list.Name} is at its last item" : $"{list.Name} is at its first item");
        return Go(list, target, atLeaderNs);
    }

    /// <summary>A clip verb on a playlist's current item (VIDEO PLAY/PAUSE/STOP/RESTART/SEEK/END/RATE).</summary>
    public (bool Ok, string Words) Transport(Playlist list, Func<ClipTransport, TransportResult> verb, long nowLeaderNs)
    {
        ArgumentNullException.ThrowIfNull(list);
        ArgumentNullException.ThrowIfNull(verb);
        var lane = Lane(list.Id);
        if (lane is null || lane.IsIdle)
            return (false, $"nothing is loaded in {list.Name} — PLAYLIST GO {list.Id} starts it");
        // A verb that lands after a scheduled switch acts on the item that is on now.
        if (nowLeaderNs >= lane.SwitchAtLeaderNs && lane.NextItemId is not null)
            lane = Promote(lane);
        var r = verb(lane.Clip);
        if (!r.Ok)
            return (false, r.Words);
        _lanes[list.Id] = Schedule(list, lane with { Clip = r.Transport }, nowLeaderNs);
        Publish();
        return (true, r.Words);
    }

    /// <summary>Advances every lane to a leader instant: commits a switch that has come, ends or loops clips.
    /// Called by the control lane every few milliseconds; idempotent.</summary>
    public void Tick(Project project, long nowLeaderNs)
    {
        ArgumentNullException.ThrowIfNull(project);
        var changed = false;
        foreach (var id in _lanes.Keys.ToList())
        {
            var lane = _lanes[id];
            if (lane.IsIdle)
                continue;
            var list = project.FindPlaylist(id);
            if (list is null)
                continue;
            if (nowLeaderNs >= lane.SwitchAtLeaderNs && lane.NextItemId is not null)
            {
                lane = Schedule(list, Promote(lane), nowLeaderNs);
                _lanes[id] = lane;
                changed = true;
                continue;
            }
            var ticked = lane.Clip.Tick(nowLeaderNs);
            if (!ReferenceEquals(ticked, lane.Clip))
            {
                if (ticked.State == TransportState.Ended && lane.Index >= 0 && lane.Index < list.Items.Length
                    && list.Items[lane.Index].End == EndAction.Stop)
                    _lanes[id] = Idle(id) with { Clip = ClipTransport.Empty with { Seq = ticked.Seq + 1 } };
                else
                    _lanes[id] = lane with { Clip = ticked };
                changed = true;
            }
        }
        if (changed)
            Publish();
    }

    private PlaylistPlayback Load(Playlist list, int index, long seq)
    {
        var item = list.Items[index];
        var cursor = PlaylistCursor.Idle(list.Id) with { Clip = ClipTransport.Empty with { Seq = seq } };
        var cued = cursor.CueItem(list, index, item.Source.Kind == SourceKind.Media ? _mediaDurationNs(item.Source.Id) : 0);
        return new PlaylistPlayback(list.Id, index, item.Id, item.Source, cued.Clip, long.MaxValue, -1, null,
            SourceRef.None, ClipTransport.Empty, item.Transition, item.TransitionNs, item.Volume);
    }

    /// <summary>Works out what follows the current item and when, so the render switches on the exact frame.
    /// Only a playing item with a known out point that ends into another item gets a scheduled switch.</summary>
    private PlaylistPlayback Schedule(Playlist list, PlaylistPlayback lane, long nowLeaderNs)
    {
        var none = lane with { SwitchAtLeaderNs = long.MaxValue, NextIndex = -1, NextItemId = null, NextSource = SourceRef.None, NextClip = ClipTransport.Empty };
        if (lane.Index < 0 || lane.Index >= list.Items.Length || lane.Clip.State != TransportState.Playing || !lane.Clip.HasLength || lane.Clip.Loop)
            return none;
        var item = list.Items[lane.Index];
        if (item.End is EndAction.Hold or EndAction.Stop or EndAction.Loop)
            return none;
        var next = PlaylistCursor.NextIndex(list, lane.Index);
        if (next < 0)
            return none;
        var switchAt = lane.Clip.Anchor.LeaderAt(lane.Clip.OutNs);
        if (switchAt == long.MaxValue)
            return none;
        if (switchAt < nowLeaderNs)
            switchAt = nowLeaderNs;
        var loaded = Load(list, next, lane.Clip.Seq);
        var nextClip = loaded.Clip.Play(switchAt).Transport;
        return lane with
        {
            SwitchAtLeaderNs = switchAt,
            NextIndex = next,
            NextItemId = loaded.ItemId,
            NextSource = loaded.Source,
            NextClip = nextClip,
        };
    }

    private static PlaylistPlayback Promote(PlaylistPlayback lane) => new(lane.PlaylistId, lane.NextIndex, lane.NextItemId, lane.NextSource,
        lane.NextClip with { Seq = Math.Max(lane.NextClip.Seq, lane.Clip.Seq) + 1 }, long.MaxValue, -1, null, SourceRef.None,
        ClipTransport.Empty, lane.Transition, lane.TransitionNs, lane.Volume);

    private static PlaylistPlayback Idle(string id) =>
        new(id, -1, null, SourceRef.None, ClipTransport.Empty, long.MaxValue, -1, null, SourceRef.None, ClipTransport.Empty, TransitionKind.Cut, 0, 1);

    private static string Label(Playlist list, int index)
    {
        var item = list.Items[index];
        return string.IsNullOrWhiteSpace(item.Label) ? $"item {index + 1}" : item.Label;
    }

    private void Publish()
    {
        _revision++;
        var snap = new PlaybackSnapshot(_lanes.ToImmutableDictionary(StringComparer.Ordinal), _focused, _revision);
        _snapshot = snap;
        Changed?.Invoke(snap);
    }
}
