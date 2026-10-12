using System.Globalization;
using PlayMap.Core.Model;
using PlayMap.Core.Playback;
using PlayMap.Core.Wire;

namespace PlayMap.Engine;

/// <summary>The clip verbs (Patterns' meaning) and the playlist verbs.</summary>
public sealed partial class EngineCore
{
    private CommandReply Video(string verb, List<string> args)
    {
        var on = TakeOn(args);
        var (at, atError) = TakeAt(args);
        if (atError is not null)
            return CommandReply.Fail(atError);
        var list = ResolvePlaylist(on);
        if (list is null)
            return CommandReply.Fail(on is null ? "No video is on air — no playlist is focused" : $"no playlist {on}");
        var now = at!.Value;
        if (verb is "VIDEO PLAY" or "VIDEO PAUSE" or "VIDEO STOP" or "VIDEO RESTART" && args.Count > 0)
            return CommandReply.Fail($"{verb} takes no words after it (ON <playlist> and AT … aside)");
        Func<ClipTransport, TransportResult>? op = verb switch
        {
            "VIDEO PLAY" => t => t.Play(now),
            "VIDEO PAUSE" => t => t.Pause(now),
            "VIDEO STOP" => t => t.Stop(now),
            "VIDEO RESTART" => t => t.Restart(now),
            "VIDEO SEEK" => SeekOp(args, now),
            "VIDEO END" => EndOp(args, now),
            "VIDEO RATE" => RateOp(args, now),
            _ => null,
        };
        if (op is null)
            return CommandReply.Fail(verb switch
            {
                "VIDEO SEEK" => "SEEK takes 83, 83.5, 2.5s, 1:23, 1:02:03, top or END [s]",
                "VIDEO END" => "END takes 0 to 3600 seconds (default 10)",
                "VIDEO RATE" => "RATE takes 0.05 to 20",
                _ => $"{verb} is not a clip verb",
            });
        var (ok, words) = Playback.Transport(list, op, now);
        return ok ? CommandReply.Success(effect: $"{list.Name}: {words}") : CommandReply.Fail(words);
    }

    private static Func<ClipTransport, TransportResult>? SeekOp(List<string> args, long now)
    {
        if (args.Count == 0)
            return null;
        if (args[0].Equals("END", StringComparison.OrdinalIgnoreCase))
        {
            var s = 0.0;
            if (args.Count > 1 && !double.TryParse(args[1], NumberStyles.Float, CultureInfo.InvariantCulture, out s))
                return null;
            return t => t.EndIn(s, now);
        }
        if (!ClipTransport.TryParsePoint(args[0], out var ns))
            return null;
        return t => t.Seek(t.InNs + ns, now);
    }

    private static Func<ClipTransport, TransportResult>? EndOp(List<string> args, long now)
    {
        var s = 10.0;
        if (args.Count > 0 && !double.TryParse(args[0].TrimEnd('s', 'S'), NumberStyles.Float, CultureInfo.InvariantCulture, out s))
            return null;
        return t => t.EndIn(s, now);
    }

    private static Func<ClipTransport, TransportResult>? RateOp(List<string> args, long now)
    {
        if (args.Count == 0 || !double.TryParse(args[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var r))
            return null;
        return t => t.SetRate(r, now);
    }

    private CommandReply PlaylistGet(List<string> args)
    {
        if (args.Count < 1)
            return CommandReply.Fail("PLAYLIST GET <playlist>");
        var list = ResolvePlaylist(args[0]);
        return list is null ? CommandReply.Fail($"no playlist {args[0]}") : CommandReply.Success(PlaylistJson(list));
    }

    private CommandReply PlaylistNew(List<string> args)
    {
        var name = args.Count > 0 ? string.Join(' ', args) : "Playlist";
        string id = "";
        var r = Store.Apply($"new playlist {name}", p =>
        {
            id = p.NewId(Ids.Playlist);
            return p with { Playlists = p.Playlists.Add(new Playlist(id, name, [], false, null)) };
        });
        return r.Changed ? CommandReply.Success(id, $"made playlist {name}") : FromEdit(r);
    }

    private CommandReply PlaylistAdd(List<string> args)
    {
        if (args.Count < 2)
            return CommandReply.Fail("PLAYLIST ADD <playlist> <source> [<label>]");
        var list = ResolvePlaylist(args[0]);
        if (list is null)
            return CommandReply.Fail($"no playlist {args[0]}");
        if (!SourceRef.TryParse(args[1], out var source) || source.IsNone)
            return CommandReply.Fail("a source is media:<id>, generator:<id>, canvas:<id>, live:<id>, timer:<id> or color:#RRGGBB");
        if (ResolveSourceProblem(source) is { } problem)
            return CommandReply.Fail(problem);
        var label = args.Count > 2 ? string.Join(' ', args.Skip(2)) : SourceLabel(source);
        string id = "";
        var r = Store.Apply($"add {label} to {list.Name}", p =>
        {
            id = p.NewId(Ids.PlaylistItem);
            var item = PlaylistItem.For(id, source, label) with
            {
                Transition = p.Settings.DefaultTransition,
                TransitionNs = p.Settings.DefaultTransitionNs,
                DurationNs = source.Kind is SourceKind.Generator or SourceKind.Color or SourceKind.Canvas or SourceKind.Timer
                    || p.FindMedia(source.Id)?.Kind == MediaKind.Image ? 10_000_000_000 : 0,
            };
            return p with { Playlists = p.Playlists.Update(list.Id, l => l with { Items = l.Items.Add(item) }) };
        });
        return r.Changed ? CommandReply.Success(id, $"added {label} to {list.Name}") : FromEdit(r);
    }

    private CommandReply PlaylistRemove(List<string> args)
    {
        if (args.Count < 2)
            return CommandReply.Fail("PLAYLIST REMOVE <playlist> <item>");
        var list = ResolvePlaylist(args[0]);
        if (list is null)
            return CommandReply.Fail($"no playlist {args[0]}");
        var index = ItemIndex(list, args[1]);
        if (index < 0)
            return CommandReply.Fail($"{list.Name} has no item {args[1]}");
        var item = list.Items[index];
        return FromEdit(Store.Apply($"remove {item.Label} from {list.Name}",
            p => p with { Playlists = p.Playlists.Update(list.Id, l => l with { Items = l.Items.RemoveAt(index) }) }));
    }

    private CommandReply PlaylistMove(List<string> args)
    {
        if (args.Count < 3 || !int.TryParse(args[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var to))
            return CommandReply.Fail("PLAYLIST MOVE <playlist> <item> <index>");
        var list = ResolvePlaylist(args[0]);
        if (list is null)
            return CommandReply.Fail($"no playlist {args[0]}");
        var index = ItemIndex(list, args[1]);
        if (index < 0)
            return CommandReply.Fail($"{list.Name} has no item {args[1]}");
        if (Math.Clamp(to, 0, list.Items.Length - 1) == index)
            return CommandReply.Fail("the item is already there");
        var id = list.Items[index].Id;
        return FromEdit(Store.Apply($"move {list.Items[index].Label}",
            p => p with { Playlists = p.Playlists.Update(list.Id, l => l with { Items = l.Items.Move(id, to) }) }));
    }

    private CommandReply PlaylistDelete(List<string> args)
    {
        if (args.Count < 1)
            return CommandReply.Fail("PLAYLIST DELETE <playlist>");
        var list = ResolvePlaylist(args[0]);
        if (list is null)
            return CommandReply.Fail($"no playlist {args[0]}");
        if (Playback.Lane(list.Id) is { IsIdle: false } lane && lane.Clip.State == TransportState.Playing)
            return CommandReply.Fail($"{list.Name} is playing — stop it before deleting it");
        return FromEdit(Store.Apply($"delete playlist {list.Name}", p => p with { Playlists = p.Playlists.RemoveId(list.Id) }));
    }

    private CommandReply PlaylistGo(List<string> args)
    {
        var (at, atError) = TakeAt(args);
        if (atError is not null)
            return CommandReply.Fail(atError);
        if (args.Count < 1)
            return CommandReply.Fail("PLAYLIST GO <playlist> [<item>|#<n>] [AT …]");
        var list = ResolvePlaylist(args[0]);
        if (list is null)
            return CommandReply.Fail($"no playlist {args[0]}");
        int index;
        if (args.Count > 1)
        {
            index = ItemIndex(list, args[1]);
            if (index < 0)
                return CommandReply.Fail($"{list.Name} has no item {args[1]}");
        }
        else
        {
            var lane = Playback.Lane(list.Id);
            index = lane is null || lane.IsIdle ? 0
                : lane.Clip.State is TransportState.Cued ? lane.Index
                : PlaylistCursor.StepIndex(list, lane.Index, 1);
            if (index < 0)
                return CommandReply.Fail($"{list.Name} is at its last item");
        }
        var (ok, words) = Playback.Go(list, index, at!.Value);
        return ok ? CommandReply.Success(effect: words) : CommandReply.Fail(words);
    }

    private CommandReply PlaylistCue(List<string> args)
    {
        if (args.Count < 2)
            return CommandReply.Fail("PLAYLIST CUE <playlist> <item>|#<n>");
        var list = ResolvePlaylist(args[0]);
        if (list is null)
            return CommandReply.Fail($"no playlist {args[0]}");
        var index = ItemIndex(list, args[1]);
        if (index < 0)
            return CommandReply.Fail($"{list.Name} has no item {args[1]}");
        var (ok, words) = Playback.Cue(list, index, LeaderNowNs);
        return ok ? CommandReply.Success(effect: words) : CommandReply.Fail(words);
    }

    private CommandReply PlaylistStep(List<string> args, int step)
    {
        var (at, atError) = TakeAt(args);
        if (atError is not null)
            return CommandReply.Fail(atError);
        var list = ResolvePlaylist(args.Count > 0 ? args[0] : null);
        if (list is null)
            return CommandReply.Fail(args.Count > 0 ? $"no playlist {args[0]}" : "no playlist is focused");
        var (ok, words) = Playback.Step(list, step, at!.Value);
        return ok ? CommandReply.Success(effect: words) : CommandReply.Fail(words);
    }

    private CommandReply PlaylistLoop(List<string> args)
    {
        if (args.Count < 2 || !TryOnOff(args[1], out var on))
            return CommandReply.Fail("PLAYLIST LOOP <playlist> ON|OFF");
        var list = ResolvePlaylist(args[0]);
        if (list is null)
            return CommandReply.Fail($"no playlist {args[0]}");
        if (list.Loop == on)
            return CommandReply.Fail(on ? "already looping" : "already not looping");
        return FromEdit(Store.Apply($"{list.Name} loop {(on ? "on" : "off")}", p => p with { Playlists = p.Playlists.Update(list.Id, l => l with { Loop = on }) }));
    }

    private CommandReply PlaylistSync(List<string> args)
    {
        if (args.Count < 2)
            return CommandReply.Fail("PLAYLIST SYNC <playlist> <group>|OFF");
        var list = ResolvePlaylist(args[0]);
        if (list is null)
            return CommandReply.Fail($"no playlist {args[0]}");
        var group = IsOff(args[1]) ? null : args[1];
        if (group is not null && !Ids.IsValid(group))
            return CommandReply.Fail("a sync group is 1–40 letters, digits, '-' or '_'");
        if (string.Equals(list.SyncGroup, group, StringComparison.Ordinal))
            return CommandReply.Fail(group is null ? "not in a sync group" : $"already in {group}");
        return FromEdit(Store.Apply($"{list.Name} sync {(group ?? "off")}", p => p with { Playlists = p.Playlists.Update(list.Id, l => l with { SyncGroup = group }) }));
    }

    private CommandReply PlaylistFocus(List<string> args)
    {
        if (args.Count < 1)
            return CommandReply.Fail("PLAYLIST FOCUS <playlist>");
        var list = ResolvePlaylist(args[0]);
        if (list is null)
            return CommandReply.Fail($"no playlist {args[0]}");
        var (ok, words) = Playback.Focus(list.Id);
        return ok ? CommandReply.Success(effect: $"{list.Name} {words}") : CommandReply.Fail(words);
    }

    /// <summary>An item by id, by "#n" (1-based) or by label.</summary>
    private static int ItemIndex(Playlist list, string key)
    {
        if (key.StartsWith('#') && int.TryParse(key.AsSpan(1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
            return n >= 1 && n <= list.Items.Length ? n - 1 : -1;
        var byId = list.Items.IndexOfId(key);
        if (byId >= 0)
            return byId;
        for (var i = 0; i < list.Items.Length; i++)
            if (list.Items[i].Label.Equals(key, StringComparison.OrdinalIgnoreCase))
                return i;
        return -1;
    }

    private static bool TryOnOff(string word, out bool on)
    {
        on = word.Equals("ON", StringComparison.OrdinalIgnoreCase) || word.Equals("TRUE", StringComparison.OrdinalIgnoreCase) || word == "1";
        return on || word.Equals("OFF", StringComparison.OrdinalIgnoreCase) || word.Equals("FALSE", StringComparison.OrdinalIgnoreCase) || word == "0";
    }

    /// <summary>Why a source cannot be used, or null when it can.</summary>
    private string? ResolveSourceProblem(SourceRef source)
    {
        var p = Store.Project;
        return source.Kind switch
        {
            SourceKind.Media when p.FindMedia(source.Id) is null => $"no media {source.Id}",
            SourceKind.Generator when p.FindGenerator(source.Id) is null => $"no generator {source.Id}",
            SourceKind.Canvas when p.FindCanvas(source.Id) is null => $"no canvas {source.Id}",
            SourceKind.Playlist when p.FindPlaylist(source.Id) is null => $"no playlist {source.Id}",
            SourceKind.Color when !Core.Content.ColorText.TryParse(source.Id, out _) => "a colour is #RRGGBB",
            _ => null,
        };
    }

    private string SourceLabel(SourceRef source)
    {
        var p = Store.Project;
        return source.Kind switch
        {
            SourceKind.Media => p.FindMedia(source.Id)?.Name ?? source.Id,
            SourceKind.Generator => p.FindGenerator(source.Id)?.Name ?? source.Id,
            SourceKind.Canvas => p.FindCanvas(source.Id)?.Name ?? source.Id,
            SourceKind.Playlist => p.FindPlaylist(source.Id)?.Name ?? source.Id,
            SourceKind.Color => "Colour " + source.Id,
            _ => source.ToString(),
        };
    }
}
