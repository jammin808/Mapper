using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using PlayMap.Core.Fleet;
using PlayMap.Core.Show;
using PlayMap.Core.Wire;

namespace PlayMap.App.Link;

/// <summary>Show Mode in the demo, with the engine's meaning and STATE shapes (tests/fixtures/state/show.json): SHOW
/// ARM/DISARM (no password), PANIC and PANIC CLEAR (a second PANIC within the fade is HARD), LOCK/UNLOCK of an entity or
/// an area, LOCKS, MASTER, SHOW SET. On a hot backup or a fenced node (STATE.fleet.door, from the backup fixture) a line that
/// would change the show is refused with the door's words, and while armed a line the show's policy refuses the desk is refused
/// with the same words, as the engine's doors do, in their order. A verb that would change nothing is refused with the reason.</summary>
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

    /// <summary>The engine's gate for the desk (its role) while armed: the policy's words, or null.</summary>
    private string? ArmedRefusal(WireLine line)
    {
        if (!Armed || VerbCatalog.Match(line) is not { } m)
            return null;
        var rules = Section("show")["rules"] as JsonObject;
        var facts = new ShowFacts(true, false, _blackout, rules?["remoteViewOnly"]?.GetValue<bool>() ?? false, rules?["disarmFrom"]?.GetValue<string>() ?? "paired");
        var role = Enum.TryParse<ShowRole>(Role, ignoreCase: true, out var r) ? r : ShowRole.Remote;
        var decision = ShowPolicy.Decide(m.Spec, line, role, facts);
        return decision.Verdict == ShowVerdict.Refuse ? decision.Words : null;
    }

    partial void TryShowVerbs(WireLine line, ref WireReply? reply)
    {
        if ((DoorRefusal(line) ?? ArmedRefusal(line)) is { } why)
        {
            reply = Err(why);
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
