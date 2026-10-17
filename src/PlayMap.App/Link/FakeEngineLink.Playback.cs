using System.Globalization;
using PlayMap.Core.Model;
using PlayMap.Core.Playback;
using PlayMap.Core.Wire;

namespace PlayMap.App.Link;

/// <summary>The demo engine's playlists: one <see cref="PlaylistCursor"/> per playlist, the VIDEO verbs on
/// the focused one (or ON &lt;playlist&gt;), end actions applied as the clock passes an out point.</summary>
public sealed partial class FakeEngineLink
{
    private long Now => _clock.NowNs;

    private PlaylistCursor CursorOf(string playlistId) =>
        _cursors.TryGetValue(playlistId, out var c) ? c : PlaylistCursor.Idle(playlistId);

    private long DurationOf(PlaylistItem item) =>
        item.Source.Kind == SourceKind.Media && _store.Project.FindMedia(item.Source.Id) is { } m ? m.Info.DurationNs : 0;

    private Playlist Need(string id) =>
        _store.Project.FindPlaylist(id) ?? throw new EditRefusedException($"no playlist '{id}' — PLAYLIST LIST names them");

    private PlaylistCursor Cue(Playlist list, int index)
    {
        var cursor = CursorOf(list.Id);
        var item = index >= 0 && index < list.Items.Length ? list.Items[index] : null;
        var next = cursor.CueItem(list, index, item is null ? 0 : DurationOf(item));
        if (item is not null && next.Clip.OutNs <= next.Clip.InNs)
            // A generator or a page with no duration plays until told: a nominal minute keeps the counters honest.
            next = next with { Clip = next.Clip with { OutNs = next.Clip.InNs + 60 * DemoProject.Second } };
        _cursors[list.Id] = next;
        return next;
    }

    private WireReply PlaylistNew(WireLine line)
    {
        var name = line.Count > 2 ? line.Word(2) : "Playlist";
        var id = _store.Project.NewId(Ids.Playlist);
        return Edit("new playlist", p => p with { Playlists = p.Playlists.Add(new Playlist(id, name, [], false, null)) }, okText: id);
    }

    private WireReply PlaylistAdd(WireLine line)
    {
        var list = Need(line.Word(2));
        if (!SourceRef.TryParse(line.Word(3), out var src) || src.IsNone)
            return Err("PLAYLIST ADD <playlist> <source> — media:…, generator:…, canvas:…, color:#RRGGBB");
        var label = line.Count > 4 ? line.Word(4) : LabelOf(src);
        var id = _store.Project.NewId(Ids.PlaylistItem);
        var item = PlaylistItem.For(id, src, label);
        if (src.Kind != SourceKind.Media || _store.Project.FindMedia(src.Id)?.Kind == MediaKind.Image)
            item = item with { DurationNs = 10 * DemoProject.Second };
        return Edit("add " + label, p => p with { Playlists = p.Playlists.Update(list.Id, l => l with { Items = l.Items.Add(item) }) }, okText: id);
    }

    private string LabelOf(SourceRef src) => src.Kind switch
    {
        SourceKind.Media => Path.GetFileNameWithoutExtension(_store.Project.FindMedia(src.Id)?.Name ?? src.Id),
        SourceKind.Generator => _store.Project.FindGenerator(src.Id)?.Name ?? src.Id,
        SourceKind.Canvas => _store.Project.FindCanvas(src.Id)?.Name ?? src.Id,
        SourceKind.Color => "Colour " + src.Id,
        _ => src.ToString(),
    };

    private WireReply PlaylistRemove(WireLine line)
    {
        var list = Need(line.Word(2));
        var itemId = line.Word(3);
        if (list.Items.IndexOfId(itemId) < 0)
            return Err($"no item '{itemId}' in {list.Name}");
        var r = Edit("remove item", p => p with { Playlists = p.Playlists.Update(list.Id, l => l with { Items = l.Items.RemoveId(itemId) }) });
        if (r.Ok && CursorOf(list.Id).ItemId == itemId)
            _cursors[list.Id] = PlaylistCursor.Idle(list.Id) with { Seq = CursorOf(list.Id).Seq + 1 };
        return r;
    }

    private WireReply PlaylistMove(WireLine line)
    {
        var list = Need(line.Word(2));
        var itemId = line.Word(3);
        if (list.Items.IndexOfId(itemId) < 0)
            return Err($"no item '{itemId}' in {list.Name}");
        if (!int.TryParse(line.Word(4), NumberStyles.Integer, CultureInfo.InvariantCulture, out var index))
            return Err("PLAYLIST MOVE <playlist> <item> <index>");
        var r = Edit("move item", p => p with { Playlists = p.Playlists.Update(list.Id, l => l with { Items = l.Items.Move(itemId, index) }) });
        if (r.Ok && CursorOf(list.Id) is { ItemId: { } current } cursor)
            _cursors[list.Id] = cursor with { Index = Need(list.Id).Items.IndexOfId(current) };
        return r;
    }

    private WireReply PlaylistDelete(WireLine line)
    {
        var list = Need(line.Word(2));
        var r = Edit("delete " + list.Name, p => p with { Playlists = p.Playlists.RemoveId(list.Id) });
        if (r.Ok)
        {
            _cursors.Remove(list.Id);
            if (_focused == list.Id)
                _focused = _store.Project.Playlists.IsDefaultOrEmpty ? null : _store.Project.Playlists[0].Id;
        }
        return r;
    }

    private WireReply PlaylistGo(WireLine line, bool play)
    {
        var list = Need(line.Word(2));
        if (list.Items.IsDefaultOrEmpty)
            return Err(list.Name + " is empty");
        var cursor = CursorOf(list.Id);
        int index;
        var which = line.Count > 3 && !line.WordUpper(3).Equals("AT", StringComparison.Ordinal) ? line.Word(3) : null;
        if (which is null)
            index = play ? PlaylistCursor.StepIndex(list, cursor.Index, 1) : Math.Max(cursor.Index, 0);
        else if (which.StartsWith('#'))
            index = int.TryParse(which[1..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n - 1 : -1;
        else
            index = list.Items.IndexOfId(which);
        if (cursor.IsIdle && which is null)
            index = 0;
        if (index < 0 || index >= list.Items.Length)
            return Err(which is null ? list.Name + " has nothing after this item" : $"no item '{which}' in {list.Name}");
        var cued = Cue(list, index);
        if (!play)
            return Ok("cued " + list.Items[index].Label);
        var r = cued.Clip.Play(Now);
        _cursors[list.Id] = cued with { Clip = r.Transport };
        PlaylistMovedForBlind(list.Id, go: true);
        return Ok("playing " + list.Items[index].Label);
    }

    private WireReply PlaylistStep(WireLine line, int step)
    {
        var list = Need(line.Word(2));
        var cursor = CursorOf(list.Id);
        var index = PlaylistCursor.StepIndex(list, cursor.Index, step);
        if (cursor.IsIdle)
            index = step > 0 ? 0 : -1;
        if (index < 0)
            return Err(step > 0 ? $"{list.Name} is at its last item" : $"{list.Name} is at its first item");
        var cued = Cue(list, index);
        _cursors[list.Id] = cued with { Clip = cued.Clip.Play(Now).Transport };
        PlaylistMovedForBlind(list.Id, go: step > 0);
        return Ok("playing " + list.Items[index].Label);
    }

    private WireReply PlaylistLoop(WireLine line)
    {
        var list = Need(line.Word(2));
        var on = line.WordUpper(3) switch { "ON" => true, "OFF" => false, _ => (bool?)null };
        if (on is null)
            return Err("PLAYLIST LOOP <playlist> ON|OFF");
        if (list.Loop == on)
            return Err(on.Value ? "already looping" : "already not looping");
        return Edit(on.Value ? "loop on" : "loop off", p => p with { Playlists = p.Playlists.Update(list.Id, l => l with { Loop = on.Value }) });
    }

    private WireReply PlaylistSync(WireLine line)
    {
        var list = Need(line.Word(2));
        var group = line.Word(3);
        if (group.Length == 0)
            return Err("PLAYLIST SYNC <playlist> <group>|OFF");
        string? next = group.Equals("OFF", StringComparison.OrdinalIgnoreCase) ? null : group;
        if (list.SyncGroup == next)
            return Err(next is null ? "not in a sync group" : "already in " + next);
        return Edit(next is null ? "leave sync" : "sync " + next, p => p with { Playlists = p.Playlists.Update(list.Id, l => l with { SyncGroup = next }) });
    }

    private WireReply PlaylistFocus(WireLine line)
    {
        var list = Need(line.Word(2));
        if (_focused == list.Id)
            return Err(list.Name + " has the focus already");
        _focused = list.Id;
        return Ok("focus " + list.Name);
    }

    private WireReply Video(string verb, WireLine line, int words)
    {
        var onAt = -1;
        for (var i = words; i < line.Count; i++)
            if (line.WordUpper(i) == "ON")
                onAt = i;
        var listId = onAt > 0 ? line.Word(onAt + 1) : _focused;
        if (listId is null)
            return Err("no playlist has the focus — PLAYLIST FOCUS <playlist>");
        var list = Need(listId);
        var cursor = CursorOf(list.Id);
        var arg = line.Count > words && words != onAt ? line.Word(words) : "";
        var now = Now;
        TransportResult r;
        switch (verb)
        {
            case "VIDEO PLAY":
                r = cursor.Clip.Play(now);
                break;
            case "VIDEO PAUSE":
                r = cursor.Clip.Pause(now);
                break;
            case "VIDEO STOP":
                r = cursor.Clip.Stop(now);
                break;
            case "VIDEO RESTART":
                r = cursor.Clip.Restart(now);
                break;
            case "VIDEO SEEK":
                if (arg.StartsWith("END", StringComparison.OrdinalIgnoreCase))
                {
                    var tail = line.Count > words + 1 && double.TryParse(line.Word(words + 1), NumberStyles.Float, CultureInfo.InvariantCulture, out var e) ? e : 0;
                    r = cursor.Clip.EndIn(tail, now);
                }
                else if (ClipTransport.TryParsePoint(arg, out var ns))
                    r = cursor.Clip.Seek(cursor.Clip.InNs + ns, now);
                else
                    return Err("VIDEO SEEK <83|83.5|2.5s|1:23|top|END [s]>");
                break;
            case "VIDEO END":
                var secs = arg.Length == 0 ? 10 : double.TryParse(arg, NumberStyles.Float, CultureInfo.InvariantCulture, out var s) ? s : double.NaN;
                r = cursor.Clip.EndIn(secs, now);
                break;
            case "VIDEO RATE":
                if (!double.TryParse(arg, NumberStyles.Float, CultureInfo.InvariantCulture, out var rate))
                    return Err("VIDEO RATE <0.05-20>");
                r = cursor.Clip.SetRate(rate, now);
                break;
            default:
                return Err(verb + " is not a transport verb");
        }
        if (!r.Ok)
            return Err(r.Words);
        _cursors[list.Id] = cursor with { Clip = r.Transport, Seq = cursor.Seq + 1 };
        return Ok(r.Words);
    }

    private bool TickTransports()
    {
        var moving = false;
        foreach (var (id, cursor) in _cursors.ToList())
        {
            if (cursor.Clip.State != TransportState.Playing)
                continue;
            moving = true;
            var ticked = cursor.Clip.Tick(Now);
            if (ticked.State != TransportState.Ended)
            {
                if (!ReferenceEquals(ticked, cursor.Clip))
                    _cursors[id] = cursor with { Clip = ticked };
                continue;
            }
            var list = _store.Project.FindPlaylist(id);
            if (list is null || cursor.Index < 0 || cursor.Index >= list.Items.Length)
            {
                _cursors[id] = cursor with { Clip = ticked };
                continue;
            }
            var item = list.Items[cursor.Index];
            var next = PlaylistCursor.NextIndex(list, cursor.Index);
            if (item.End == EndAction.Hold || next == cursor.Index)
                _cursors[id] = cursor with { Clip = ticked };
            else if (next < 0)
                _cursors[id] = item.End == EndAction.Stop ? PlaylistCursor.Idle(id) with { Seq = cursor.Seq + 1 } : cursor with { Clip = ticked };
            else
            {
                var cued = Cue(list, next);
                _cursors[id] = cued with { Clip = cued.Clip.Play(Now).Transport };
            }
        }
        return moving;
    }
}
