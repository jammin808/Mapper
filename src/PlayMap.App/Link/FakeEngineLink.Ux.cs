using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using PlayMap.Core.Wire;

namespace PlayMap.App.Link;

/// <summary>The consoles' safety patterns in the demo, with the engine's meaning: the GO fence (a GO, NEXT, PREV, VIDEO PLAY or VIDEO
/// RESTART within STATE.show.minGoMs of the last accepted one on the same playlist is refused with the engine's double-GO words, on the
/// demo's show clock, and an accepted one is published in STATE.show.lastGo as the engine writes it), and the Oops history (UNDO LIST
/// and UNDO TO of the store the line's edits go to: the blind owner's draft, else the live project).</summary>
public sealed partial class FakeEngineLink
{
    private readonly Dictionary<string, long> _lastGoMs = new(StringComparer.Ordinal);

    /// <summary>How many undo steps the live project holds (tests count a gesture's steps here).</summary>
    public int UndoSteps => _store.Steps.Length;

    partial void TryUxVerbs(WireLine line, ref WireReply? reply)
    {
        if (VerbCatalog.Match(line) is not { } m)
            return;
        switch (m.Spec.Name)
        {
            case "UNDO LIST":
                reply = UndoList(line, m.Words);
                return;
            case "UNDO TO":
                reply = line.Count == m.Words + 1 && long.TryParse(line.Word(m.Words), NumberStyles.Integer, CultureInfo.InvariantCulture, out var step)
                    ? Undo((_lineStore ?? _store).UndoTo(step))
                    : Err("UNDO TO <step> — UNDO LIST names the steps");
                return;
            case "PLAYLIST GO" or "PLAYLIST NEXT" or "PLAYLIST PREV" or "VIDEO PLAY" or "VIDEO RESTART":
                try
                {
                    reply = Fenced(line, m.Spec.Name, m.Words);
                }
                catch (PlayMap.Core.Model.EditRefusedException refused)
                {
                    reply = Err(refused.Message);
                }
                return;
        }
    }

    /// <summary>A GO line through the fence: refused inside the window of the last GO on its playlist, else answered as the demo answers
    /// it, and an accepted one opens the window.</summary>
    private WireReply Fenced(WireLine line, string verb, int words)
    {
        var playlist = verb.StartsWith("PLAYLIST", StringComparison.Ordinal) ? line.Word(2) : OnPlaylist(line, words) ?? _focused ?? "";
        var minGo = MinGoMs();
        var now = _clock.NowNs / 1_000_000;
        if (minGo > 0 && playlist.Length > 0 && _lastGoMs.TryGetValue(playlist, out var last) && now - last < minGo)
        {
            var name = _store.Project.Playlists.FirstOrDefault(p => p.Id == playlist)?.Name ?? playlist;
            return Err(string.Create(CultureInfo.InvariantCulture,
                $"double GO protection: {(now - last) / 1000.0:0.00} s since the last GO on {name} ({minGo / 1000.0:0.00} s {(Armed ? "armed" : "in rehearsal")})"));
        }
        var reply = verb switch
        {
            "PLAYLIST GO" => PlaylistGo(line, play: true),
            "PLAYLIST NEXT" => PlaylistStep(line, +1),
            "PLAYLIST PREV" => PlaylistStep(line, -1),
            _ => Video(verb, line, words),
        };
        if (reply.Ok && playlist.Length > 0)
        {
            _lastGoMs[playlist] = now;
            PublishGo(playlist);
        }
        return reply;
    }

    /// <summary>STATE.show.lastGo with this GO first (a node with Show Mode, as the demo has once a show verb made its section): the
    /// playlist's row replaced by the GO's leader time and wall time and this connection's session, in the engine's keys.</summary>
    private void PublishGo(string playlist)
    {
        if (!_sections.ContainsKey("show"))
            return;
        var rows = new JsonArray(new JsonObject
        {
            ["playlist"] = playlist, ["leaderNs"] = _clock.NowNs, ["at"] = UnixMs, ["session"] = SessionId, ["transport"] = Transport,
            ["by"] = ClientName,
        });
        if (Section("show")["lastGo"] is JsonArray had)
        {
            foreach (var row in had)
            {
                if (row is JsonObject o && o["playlist"]?.GetValue<string>() != playlist)
                    rows.Add(o.DeepClone());
            }
        }
        Section("show")["lastGo"] = rows;
        Moved();
    }

    private static string? OnPlaylist(WireLine line, int words)
    {
        for (var i = words; i < line.Count - 1; i++)
        {
            if (line.WordUpper(i) == "ON")
                return line.Word(i + 1);
        }
        return null;
    }

    /// <summary>STATE.show.minGoMs as this demo node holds it (0: no fence).</summary>
    private int MinGoMs() =>
        _sections.TryGetValue("show", out var s) && s is JsonObject show && show["minGoMs"] is JsonValue v && v.GetValueKind() == JsonValueKind.Number
            ? v.GetValue<int>()
            : 0;

    private WireReply UndoList(WireLine line, int words)
    {
        var n = 20;
        if (line.Count > words + 1 || (line.Count == words + 1
            && (!int.TryParse(line.Word(words), NumberStyles.Integer, CultureInfo.InvariantCulture, out n) || n is < 1 or > 100)))
            return Err("UNDO LIST [<n 1-100>]");
        var owner = _draft is not null && _commit is null && Section("show")["blind"] is JsonObject b && b["session"]?.GetValue<string>() == SessionId;
        var store = _lineStore ?? (owner ? _draft!.Store : _store);
        var steps = store.Steps;
        var list = new JsonArray();
        for (var i = 0; i < Math.Min(n, steps.Length); i++)
        {
            var s = steps[i];
            list.Add(new JsonObject
            {
                ["step"] = s.Number, ["label"] = s.Label, ["who"] = s.Who, ["at"] = s.AtUnixMs,
                ["words"] = s.Label + (s.Who.Length > 0 ? " — " + s.Who : ""), ["next"] = i == 0,
            });
        }
        return Ok(new JsonObject
        {
            ["draft"] = !ReferenceEquals(store, _store), ["count"] = steps.Length, ["canRedo"] = store.CanRedo, ["steps"] = list,
        }.ToJsonString());
    }
}
