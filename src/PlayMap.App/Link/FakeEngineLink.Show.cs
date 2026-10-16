using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using PlayMap.Core.Fleet;
using PlayMap.Core.Show;
using PlayMap.Core.Wire;

namespace PlayMap.App.Link;

/// <summary>Show Mode in the demo, with the engine's meaning and STATE shapes (tests/fixtures/state/show.json): SHOW
/// ARM/DISARM (no password), PANIC and PANIC CLEAR (a second PANIC within the fade is HARD), LOCK/UNLOCK of an entity or
/// an area, LOCKS, MASTER, SHOW SET, and the desk's own blind draft (BLIND ON, DISCARD, COMMIT). Its doors are the engine's, in
/// the engine's order and with its words: an unpaired session's line that would change something; on a hot backup or a fenced
/// node (STATE.fleet.door, from the backup fixture) a line that would change the show; another show opened under a draft; a line
/// the show's policy refuses the desk (its draft's edits go to the draft); the network area's lock; then LockGuard on the
/// document. A verb that would change nothing is refused with the reason.</summary>
public sealed partial class FakeEngineLink
{
    private long _panicFadeUntilMs;

    /// <summary>Show time in unix ms, as STATE stamps it.</summary>
    private long UnixMs => 1_760_000_000_000 + _clock.NowNs / 1_000_000;

    private bool Armed => _sections.TryGetValue("show", out var s) && s is JsonObject o && o["armed"]?.GetValue<bool>() == true;

    /// <summary>The fleet's door for the desk, as the engine's: on a hot backup or a fenced node, the door's words for a line that would
    /// change the show it mirrors, or null.</summary>
    private string? DoorRefusal(WireLine line)
    {
        if (!_sections.TryGetValue("fleet", out var f) || f is not JsonObject fleet || fleet["door"] is not JsonValue door
            || door.GetValueKind() != JsonValueKind.String || VerbCatalog.Match(line) is not { } m)
            return null;
        var role = fleet["role"] is JsonValue r && r.GetValueKind() == JsonValueKind.String ? r.GetValue<string>() : FleetRoles.Solo;
        return BackupGate.Refused(m.Spec, line, "desk", role, door.GetValue<string>());
    }

    /// <summary>The router's admission, as the engine's: an unpaired session's line that changes something is refused.</summary>
    private string? PairingRefusal(WireLine line) =>
        !Paired && VerbCatalog.Match(line) is { } m && (m.Spec.Mutates || m.Spec.PairedOnly) ? PlayMap.Net.Commands.WireWords.NotPaired : null;

    /// <summary>The show's gate for the desk (its role), as the engine's: another show is not opened while a draft is open; the
    /// policy's words for a refused line; a document edit of the draft's owner goes to its draft (refused while its commit
    /// waits); a network line meets the network area's lock.</summary>
    private string? ShowRefusal(WireLine line)
    {
        if (VerbCatalog.Match(line) is not { } m || !_sections.ContainsKey("show"))
            return null;
        var blind = Section("show")["blind"] as JsonObject;
        if (blind is not null && BlindWords.OpensAnotherShow(m.Spec))
            return BlindWords.DraftOpen(blind["owner"]?.GetValue<string>() ?? "", blind["changes"]?.GetValue<int>() ?? 0);
        var owner = blind is not null && blind["session"]?.GetValue<string>() == SessionId && blind["transport"]?.GetValue<string>() == Transport;
        var rules = Section("show")["rules"] as JsonObject;
        var facts = new ShowFacts(Armed, owner, _blackout, rules?["remoteViewOnly"]?.GetValue<bool>() ?? false, rules?["disarmFrom"]?.GetValue<string>() ?? "paired");
        var role = Enum.TryParse<ShowRole>(Role, ignoreCase: true, out var r) ? r : ShowRole.Remote;
        var decision = ShowPolicy.Decide(m.Spec, line, role, facts);
        switch (decision.Verdict)
        {
            case ShowVerdict.Refuse:
                return decision.Words;
            case ShowVerdict.BlindOnly when (blind?["commit"] is JsonObject commit):
                return BlindWords.Landing(commit["when"]?.GetValue<string>() ?? "at its boundary");
            case ShowVerdict.BlindOnly:
                _lineStore = _draft?.Store;
                return _lineStore is null ? "this session holds no blind draft — BLIND ON first" : null;
            case ShowVerdict.Allow when ShowPolicy.ClassOf(m.Spec, line) == ShowClass.Network
                && ExplicitLocks().FirstOrDefault(l => l.Kind == PlayMap.Core.Model.LockKind.Area && l.Target == PlayMap.Core.Model.LockAreas.Network) is { } held:
                return LockGuard.AreaWords(held);
            default:
                return null;
        }
    }

    /// <summary>The show's own locks (STATE.show.locks without the armed show's implicit ones), as LockGuard holds them.</summary>
    private List<PlayMap.Core.Model.ShowLock> ExplicitLocks()
    {
        var list = new List<PlayMap.Core.Model.ShowLock>();
        if (!_sections.TryGetValue("show", out var s) || s is not JsonObject show || show["locks"] is not JsonArray locks)
            return list;
        foreach (var l in locks.OfType<JsonObject>())
        {
            if (l["implicit"]?.GetValue<bool>() == true)
                continue;
            list.Add(new PlayMap.Core.Model.ShowLock(l["target"]?.GetValue<string>() ?? "", l["kind"]?.GetValue<string>() == "area" ? PlayMap.Core.Model.LockKind.Area : PlayMap.Core.Model.LockKind.Entity,
                l["by"]?.GetValue<string>() ?? "", l["at"]?.GetValue<long>() ?? 0, l["words"]?.GetValue<string>() ?? ""));
        }
        return list;
    }

    /// <summary>A project with the show's own locks in it (the demo keeps them in STATE), for LockGuard.</summary>
    private PlayMap.Core.Model.Project WithLocks(PlayMap.Core.Model.Project p) => p with { Show = p.Show with { Locks = [.. ExplicitLocks()] } };

    partial void TryShowVerbs(WireLine line, ref WireReply? reply)
    {
        if ((PairingRefusal(line) ?? DoorRefusal(line) ?? ShowRefusal(line)) is { } why)
        {
            reply = Err(why);
            return;
        }
        if (line.Verb == "BLIND")
        {
            reply = Blind(line);
            return;
        }
        if (line.Verb is not ("SHOW" or "PANIC" or "LOCK" or "UNLOCK" or "LOCKS" or "MASTER"))
            return;
        var second = line.WordUpper(1);
        reply = (line.Verb, second) switch
        {
            ("SHOW", "ARM") => Arm(line),
            ("SHOW", "DISARM") => Disarm(),
            ("SHOW", "SET") => ShowRule(line),
            ("SHOW", "" or "STATUS") => Ok(Section("show").ToJsonString()),
            ("PANIC", "CLEAR") => PanicClear(),
            ("PANIC", "SET") => Err("the demo engine keeps no panic plans — a real engine does"),
            ("PANIC", _) => Panic(line),
            ("LOCK", _) => Lock(line),
            ("UNLOCK", _) => Unlock(line),
            ("LOCKS", _) => Ok(Locks().ToJsonString()),
            ("MASTER", _) => Master(line),
            _ => Err("SHOW ARM|DISARM|SET · PANIC [<outcome>|CLEAR] · LOCK · UNLOCK · LOCKS · MASTER"),
        };
    }

    /// <summary>The demo's blind draft: its owner's name, a store seeded with the live root, how many entities it changed.</summary>
    private sealed record FakeDraft(string Owner, PlayMap.Core.Model.ProjectStore Store, PlayMap.Core.Model.Project Base)
    {
        public int Changes => ProjectDiff.Between(Base, Store.Project, 0).Changes.Count;
    }

    private FakeDraft? _draft;
    private bool _promoting;

    /// <summary>The store a line's document edits go to (the draft for its owner's draftable line), or null for the live one.</summary>
    private PlayMap.Core.Model.ProjectStore? _lineStore;

    /// <summary>BLIND ON, BLIND DISCARD and BLIND COMMIT [NOW] as the engine means them, for the desk's own session: STATE.show.blind
    /// names the owner's session as WHOAMI does; the draft's edits stay off the live project until BLIND COMMIT lands them.</summary>
    private WireReply Blind(WireLine line)
    {
        var word = line.WordUpper(1);
        switch (word)
        {
            case "ON":
                if (_draft is not null)
                    return Err("already blind — " + BlindWords.Changes(_draft.Changes) + " staged");
                if (Section("show")["blind"] is JsonObject held)
                    return Err($"{held["owner"]?.GetValue<string>()} holds the blind draft ({BlindWords.Changes(held["changes"]?.GetValue<int>() ?? 0)} staged) — one draft per node: it commits or discards it first");
                var store = new PlayMap.Core.Model.ProjectStore(_store.Project, () => _clock.NowNs / 1_000_000)
                {
                    Guard = (before, after) => LockGuard.Check(WithLocks(before), WithLocks(after), armed: false),
                };
                _draft = new FakeDraft(ClientName, store, _store.Project);
                DraftMoved();
                return Ok("blind editing by " + ClientName + ": changes stay off the air until BLIND COMMIT");
            case "DISCARD":
                if (_draft is not { } d)
                    return Err("not blind — nothing to discard");
                _draft = null;
                DraftMoved();
                return Ok("blind draft discarded (" + BlindWords.Changes(d.Changes) + ")");
            case "COMMIT":
                if (_draft is not { } c)
                    return Err("not blind — BLIND ON starts a draft");
                // A commit lands past the armed show's own locks (the show's explicit locks still hold it), as the engine's does.
                _promoting = true;
                WireReply landed;
                try
                {
                    landed = Edit("blind commit", _ => c.Store.Project);
                }
                finally
                {
                    _promoting = false;
                }
                if (landed.Ok)
                {
                    _draft = null;
                    DraftMoved();
                }
                return landed;
            default:
                return Err("the demo engine runs BLIND ON, BLIND DISCARD and BLIND COMMIT — a real engine runs the rest");
        }
    }

    /// <summary>STATE.show.blind follows the draft.</summary>
    private void DraftMoved()
    {
        Section("show")["blind"] = _draft is { } d
            ? new JsonObject
            {
                ["on"] = true, ["owner"] = d.Owner, ["session"] = SessionId, ["transport"] = Transport, ["since"] = UnixMs,
                ["base"] = 0, ["changes"] = d.Changes, ["commit"] = null,
            }
            : null;
        Moved();
    }

    private JsonArray Locks()
    {
        var show = Section("show");
        if (show["locks"] is JsonArray a)
            return a;
        var made = new JsonArray();
        show["locks"] = made;
        return made;
    }

    private WireReply Arm(WireLine line)
    {
        var show = Section("show");
        if (Armed)
            return Err("already armed by " + (show["by"]?.GetValue<string>() ?? "Desk"));
        show["armed"] = true;
        show["by"] = "Desk";
        show["since"] = UnixMs;
        var words = line.Count > 2 && !line.WordUpper(2).Equals("ALL", StringComparison.Ordinal) ? line.RestAfter(2) : "";
        show["words"] = words;
        var locks = Locks();
        foreach (var area in LockGuard.ArmedAreas)
        {
            if (!locks.Any(l => l?["target"]?.GetValue<string>() == area))
                locks.Add(new JsonObject { ["target"] = area, ["kind"] = "area", ["by"] = "Desk", ["at"] = UnixMs, ["words"] = "", ["implicit"] = true });
        }
        Moved();
        return Ok("armed");
    }

    private WireReply Disarm()
    {
        if (!Armed)
            return Err("not armed — nothing to disarm");
        var show = Section("show");
        show["armed"] = false;
        var locks = Locks();
        for (var i = locks.Count - 1; i >= 0; i--)
        {
            if (locks[i]?["implicit"]?.GetValue<bool>() == true)
                locks.RemoveAt(i);
        }
        Moved();
        return Ok("disarmed");
    }

    private WireReply ShowRule(WireLine line)
    {
        var show = Section("show");
        if (show["rules"] is not JsonObject rules)
        {
            rules = new JsonObject();
            show["rules"] = rules;
        }
        var what = line.WordUpper(2);
        var value = line.WordUpper(3);
        switch (what)
        {
            case "MINGO" when int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ms):
                rules["minGoMs"] = ms;
                show["minGoMs"] = ms;
                break;
            case "REMOTE-VIEW-ONLY" when value is "ON" or "OFF":
                rules["remoteViewOnly"] = value == "ON";
                break;
            case "EXPECT-ARMED" when value is "ON" or "OFF":
                rules["expectArmed"] = value == "ON";
                break;
            default:
                return Err("SHOW SET MINGO <ms> | REMOTE-VIEW-ONLY ON|OFF | EXPECT-ARMED ON|OFF");
        }
        Moved();
        return Ok("rule set");
    }

    private static readonly string[] Outcomes = ["BLACK", "RUN", "REWIND", "NEXT", "FREEZE", "SAFE", "SHUTTER", "FX-OFF", "HARD"];

    private WireReply Panic(WireLine line)
    {
        var word = line.Count > 1 ? line.WordUpper(1) : "";
        if (word.Length > 0 && word != "FADE" && word != "ON" && word != "AT" && word != "HERE" && Array.IndexOf(Outcomes, word) < 0)
            return Err("PANIC [BLACK|RUN|REWIND|NEXT|FREEZE|SAFE|SHUTTER|FX-OFF|HARD] [FADE <ms>]");
        var fade = 1000L;
        for (var i = 1; i + 1 < line.Count; i++)
        {
            if (line.WordUpper(i) == "FADE" && long.TryParse(line.Word(i + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var f))
                fade = f;
        }
        var now = UnixMs;
        // A second press within the fade is HARD (the engine's rule): the air goes at once.
        var hard = word == "HARD" || (_panicFadeUntilMs > now && Section("show")["panic"] is JsonObject);
        var outcome = word is "" or "FADE" or "ON" or "AT" or "HERE" or "HARD" ? "black" : word.ToLowerInvariant();
        _panicFadeUntilMs = hard ? 0 : now + fade;
        var outputs = new JsonArray();
        foreach (var o in _store.Project.Outputs)
            outputs.Add(new JsonObject { ["id"] = o.Id, ["outcome"] = outcome, ["level"] = hard ? 0 : 1, ["settled"] = hard });
        JsonObject Panic() => new()
        {
            ["outcome"] = outcome,
            ["hard"] = hard,
            ["by"] = "Desk",
            ["since"] = now,
            ["outputs"] = outputs.DeepClone(),
            ["devices"] = new JsonArray(),
        };
        Section("show")["panic"] = Panic();
        Section("air")["panic"] = Panic();
        Moved();
        return Ok(hard ? "panic: hard" : "panic: " + outcome);
    }

    private WireReply PanicClear()
    {
        if (Section("show")["panic"] is not JsonObject)
            return Err("not in panic — nothing to clear");
        Section("show")["panic"] = null;
        Section("air")["panic"] = null;
        _panicFadeUntilMs = 0;
        Moved();
        return Ok("panic cleared");
    }

    private WireReply Lock(WireLine line)
    {
        if (line.Count < 2)
            return Err("LOCK <id> [<words>] | LOCK AREA <area> [<words>]");
        var area = line.WordUpper(1) == "AREA";
        var target = area ? line.Word(2).ToLowerInvariant() : line.Word(1);
        if (area && !PlayMap.Core.Model.LockAreas.All.Contains(target))
            return Err($"no area '{target}' — mapping, layout, content, playlists, media, devices or network");
        if (!area && !_projectMirror.Json.Contains("\"" + target + "\"", StringComparison.Ordinal))
            return Err($"no entity '{target}'");
        var locks = Locks();
        if (locks.Any(l => l?["target"]?.GetValue<string>() == target))
            return Err(target + " is locked already");
        var words = line.Count > (area ? 3 : 2) ? line.RestAfter(area ? 3 : 2) : "";
        locks.Add(new JsonObject { ["target"] = target, ["kind"] = area ? "area" : "entity", ["by"] = "Desk", ["at"] = UnixMs, ["words"] = words, ["implicit"] = false });
        Moved();
        return Ok("locked " + target);
    }

    private WireReply Unlock(WireLine line)
    {
        if (line.Count < 2)
            return Err("UNLOCK <id> | UNLOCK AREA <area> | UNLOCK ALL");
        var locks = Locks();
        var removed = 0;
        var all = line.WordUpper(1) == "ALL";
        var target = line.WordUpper(1) == "AREA" ? line.Word(2).ToLowerInvariant() : line.Word(1);
        for (var i = locks.Count - 1; i >= 0; i--)
        {
            var l = locks[i];
            if (l?["implicit"]?.GetValue<bool>() == true)
                continue;
            if (all || l?["target"]?.GetValue<string>() == target)
            {
                locks.RemoveAt(i);
                removed++;
            }
        }
        if (removed == 0)
            return Err(all ? "nothing is locked" : target + " is not locked");
        Moved();
        return Ok("unlocked");
    }

    private WireReply Master(WireLine line)
    {
        if (!double.TryParse(line.Word(1), NumberStyles.Float, CultureInfo.InvariantCulture, out var pct) || pct is < 0 or > 100)
            return Err("MASTER <0-100> [FADE <ms>]");
        var level = pct / 100;
        var air = Section("air");
        if (air["master"] is JsonObject m && m["to"]?.GetValue<double>() is { } to && Math.Abs(to - level) < 1e-9)
            return Err("the master is at " + line.Word(1) + " % already");
        air["master"] = new JsonObject { ["level"] = level, ["to"] = level, ["untilLeaderNs"] = 0 };
        Moved();
        return Ok("master " + line.Word(1) + " %");
    }
}
