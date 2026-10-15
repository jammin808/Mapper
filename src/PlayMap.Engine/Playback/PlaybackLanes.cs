using System.Collections.Immutable;
using PlayMap.Core.Model;
using PlayMap.Core.Playback;

namespace PlayMap.Engine.Playback;

/// <summary>What the render thread needs to show one playlist at any instant, without asking anyone: the
/// current item and its transport, and — when the current item will end into another — the next item with
/// the leader instant it takes over, already anchored. The switch therefore lands on the exact frame on every
/// node, whatever the control lane is doing at that moment. When the next item comes in with a fade or a dip,
/// <see cref="MixStartNs"/> opens its window before the switch (<see cref="Transitions"/>): the next item is
/// anchored to start inside it, so both pictures are known for every instant of the window.</summary>
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
    double Volume,
    /// <summary>How the next item comes in over the current one (a cut when none is scheduled).</summary>
    TransitionKind NextTransition = TransitionKind.Cut,
    /// <summary>The leader instant the next item's transition window opens (long.MaxValue = a cut at the switch).</summary>
    long MixStartNs = long.MaxValue)
{
    /// <summary>The item to show at a leader instant: the current one, or the next once its switch came.</summary>
    public (SourceRef Source, ClipTransport Clip, string? ItemId) At(long leaderNs) =>
        leaderNs >= SwitchAtLeaderNs && NextItemId is not null ? (NextSource, NextClip, NextItemId) : (Source, Clip, ItemId);

    /// <summary>The transition a leader instant falls in — the current item going out, the next coming in —
    /// with how far through its window it is (0..1), or null when one picture shows.</summary>
    public (TransitionKind Kind, double Progress)? MixAt(long leaderNs)
    {
        if (NextItemId is null || NextTransition == TransitionKind.Cut || leaderNs < MixStartNs || leaderNs >= SwitchAtLeaderNs
            || SwitchAtLeaderNs <= MixStartNs)
            return null;
        return (NextTransition, (double)(leaderNs - MixStartNs) / (SwitchAtLeaderNs - MixStartNs));
    }

    /// <summary>The leader instant the next item first shows: its window's opening, or the switch for a cut.</summary>
    public long NextShowsAtNs => Math.Min(MixStartNs, SwitchAtLeaderNs);

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
        if (ManualMix(list, lane, index, atLeaderNs) is { } mixed)
        {
            _lanes[list.Id] = mixed;
            Publish();
            return (true, $"{list.Name}: {(mixed.NextTransition == TransitionKind.Dip ? "dipping" : "fading")} into {Label(list, index)}");
        }
        var loaded = Load(list, index, lane.Clip.Seq);
        var played = loaded.Clip.Play(atLeaderNs);
        _lanes[list.Id] = Schedule(list, loaded with { Clip = played.Transport }, atLeaderNs);
        Publish();
        return (true, $"{list.Name}: playing {Label(list, index)}");
    }

    /// <summary>A GO (or NEXT, PREV) into an item that comes in with a fade or a dip keeps the item on the air through the
    /// transition's window: it stays the lane's current item, the incoming one is its next with the switch at the window's end and
    /// the window opening at the GO's instant, so the source provider mixes both sides exactly as between scheduled items. The item
    /// on the air at the GO's instant is the outgoing one (an incoming item already mixing in counts as on the air). Null when the GO
    /// cuts: nothing on the air, the same item again, a cut, or a playlist in a sync group (its followers adopt the leader's item
    /// and anchor, which carries no window, so the group cuts together at the GO).</summary>
    private PlaylistPlayback? ManualMix(Playlist list, PlaylistPlayback lane, int index, long atLeaderNs)
    {
        if (lane.IsIdle || list.SyncGroup is not null)
            return null;
        var outgoing = lane.NextItemId is not null && atLeaderNs >= Math.Min(lane.MixStartNs, lane.SwitchAtLeaderNs) ? Promote(list, lane) : lane;
        var incoming = list.Items[index];
        if (outgoing.ItemId is null || outgoing.Source.IsNone || string.Equals(outgoing.ItemId, incoming.Id, StringComparison.Ordinal))
            return null;
        var loaded = Load(list, index, outgoing.Clip.Seq);
        var rate = loaded.Clip.Anchor.Rate > 0 ? loaded.Clip.Anchor.Rate : 1;
        var window = Transitions.WindowNs(incoming.Transition, incoming.TransitionNs, atLeaderNs + incoming.TransitionNs, long.MaxValue,
            loaded.Clip.HasLength ? (long)(loaded.Clip.LengthNs / rate) : 0);
        if (window <= 0)
            return null;
        var switchAt = atLeaderNs + window;
        return outgoing with
        {
            SwitchAtLeaderNs = switchAt,
            NextIndex = index,
            NextItemId = loaded.ItemId,
            NextSource = loaded.Source,
            NextClip = loaded.Clip.Play(switchAt - Transitions.IncomingLeadNs(incoming.Transition, window)).Transport,
            NextTransition = incoming.Transition,
            MixStartNs = atLeaderNs,
        };
    }

    /// <summary>The index of the item on the air at an instant: the incoming one once its transition window opened.</summary>
    public static int OnAirIndex(PlaylistPlayback lane, long leaderNs)
    {
        ArgumentNullException.ThrowIfNull(lane);
        return lane.NextItemId is not null && leaderNs >= Math.Min(lane.MixStartNs, lane.SwitchAtLeaderNs) ? lane.NextIndex : lane.Index;
    }

    /// <summary>NEXT / PREV: the neighbouring item in list order (wrapping when the list loops), playing.</summary>
    public (bool Ok, string Words) Step(Playlist list, int step, long atLeaderNs)
    {
        ArgumentNullException.ThrowIfNull(list);
        var lane = Lane(list.Id) ?? Idle(list.Id);
        var target = lane.IsIdle ? (step > 0 ? 0 : -1) : PlaylistCursor.StepIndex(list, OnAirIndex(lane, atLeaderNs), step);
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
        // A verb that lands once the next item shows acts on the item on the air: the incoming one from its window's opening (a
        // fade or dip a GO started is kept and the verb lands on the GO's item), the next one after a switch.
        if (lane.NextItemId is not null && nowLeaderNs >= lane.NextShowsAtNs)
            lane = Promote(list, lane);
        var r = verb(lane.Clip);
        if (!r.Ok)
            return (false, r.Words);
        _lanes[list.Id] = Schedule(list, lane with { Clip = r.Transport }, nowLeaderNs);
        Publish();
        return (true, r.Words);
    }

    /// <summary>Takes a sync-group leader's transport for an item exactly as sent: the same anchor gives the
    /// same frame for the same leader instant on every node. The follower schedules the next item itself
    /// from the same anchor, so the switch lands on the same frame too.</summary>
    public (bool Ok, string Words) Adopt(Playlist list, int index, ClipTransport clip, long nowLeaderNs)
    {
        ArgumentNullException.ThrowIfNull(list);
        ArgumentNullException.ThrowIfNull(clip);
        if (index < 0 || index >= list.Items.Length)
            return (false, $"{list.Name} has no item {index + 1}");
        if (Lane(list.Id) is { } current && current.Index == index && current.Clip == clip)
            return (false, "already at that anchor");
        var loaded = Load(list, index, clip.Seq);
        _lanes[list.Id] = Schedule(list, loaded with { Clip = clip }, nowLeaderNs);
        Publish();
        return (true, $"{list.Name}: following {Label(list, index)}");
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
                lane = Schedule(list, Promote(list, lane), nowLeaderNs);
                _lanes[id] = lane;
                changed = true;
                continue;
            }
            var ticked = lane.Clip.Tick(nowLeaderNs);
            if (!ReferenceEquals(ticked, lane.Clip))
            {
                // An item that ends while another mixes in over it holds its last frame until the switch.
                if (ticked.State == TransportState.Ended && lane.Index >= 0 && lane.Index < list.Items.Length
                    && list.Items[lane.Index].End == EndAction.Stop && lane.NextItemId is null)
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
        var none = lane with
        {
            SwitchAtLeaderNs = long.MaxValue, NextIndex = -1, NextItemId = null, NextSource = SourceRef.None, NextClip = ClipTransport.Empty,
            NextTransition = TransitionKind.Cut, MixStartNs = long.MaxValue,
        };
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
        // The next item's window ends at the switch; one item following itself has one decoder, so it cuts.
        var incoming = list.Items[next];
        var rate = loaded.Clip.Anchor.Rate > 0 ? loaded.Clip.Anchor.Rate : 1;
        var window = string.Equals(loaded.ItemId, lane.ItemId, StringComparison.Ordinal) ? 0
            : Transitions.WindowNs(incoming.Transition, incoming.TransitionNs, switchAt, lane.Clip.Anchor.LeaderAt(lane.Clip.InNs),
                loaded.Clip.HasLength ? (long)(loaded.Clip.LengthNs / rate) : 0);
        var nextClip = loaded.Clip.Play(switchAt - Transitions.IncomingLeadNs(incoming.Transition, window)).Transport;
        return lane with
        {
            SwitchAtLeaderNs = switchAt,
            NextIndex = next,
            NextItemId = loaded.ItemId,
            NextSource = loaded.Source,
            NextClip = nextClip,
            NextTransition = window > 0 ? incoming.Transition : TransitionKind.Cut,
            MixStartNs = window > 0 ? switchAt - window : long.MaxValue,
        };
    }

    /// <summary>The next item becomes the current one, with its own transition and volume.</summary>
    private static PlaylistPlayback Promote(Playlist list, PlaylistPlayback lane)
    {
        var at = list.Items.IndexOfId(lane.NextItemId);
        var item = at >= 0 ? list.Items[at] : null;
        return new(lane.PlaylistId, lane.NextIndex, lane.NextItemId, lane.NextSource,
            lane.NextClip with { Seq = Math.Max(lane.NextClip.Seq, lane.Clip.Seq) + 1 }, long.MaxValue, -1, null, SourceRef.None,
            ClipTransport.Empty, item?.Transition ?? lane.Transition, item?.TransitionNs ?? lane.TransitionNs, item?.Volume ?? lane.Volume);
    }

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
