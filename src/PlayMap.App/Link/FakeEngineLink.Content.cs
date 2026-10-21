using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using PlayMap.Core.Content;
using PlayMap.Core.Control;
using PlayMap.Core.Modulation;
using PlayMap.Core.Wire;

namespace PlayMap.App.Link;

/// <summary>Effects, modulation and learn in the demo, with the engine's meaning and STATE shapes (fx.json, audio.json,
/// control.json): FX KINDS answers the built-in kinds, FX ADD puts a slot in STATE.fx.slots; MOD SOURCES answers the beat
/// clock and LFO names, MOD BIND/UNBIND/BYPASS keep STATE.modulation; BIND LEARN puts a learn in STATE.control.learning
/// for a target the node's own rule takes (<see cref="TargetLine"/>, refused with its words otherwise) and BIND
/// LEARN OFF ends it. A line that would change nothing is refused with the reason.</summary>
public sealed partial class FakeEngineLink
{
    /// <summary>The engine's built-in effect kinds (PlayMap.Render's FxCatalog), as FX KINDS names them.</summary>
    private static readonly (string Id, string Name, string Family)[] FxKinds =
    [
        ("fx.invert", "Invert", "colour"), ("fx.levels", "Levels", "colour"), ("fx.hsl", "Hue, saturation, lightness", "colour"),
        ("fx.threshold", "Threshold", "colour"), ("fx.blur", "Blur", "blur"), ("fx.vignette", "Vignette", "look"),
        ("fx.pixelate", "Pixelate", "look"), ("fx.mirror", "Mirror", "geometry"), ("fx.tile", "Tile", "geometry"),
        ("fx.feedback", "Feedback", "time"),
    ];

    private int _fxSlots;
    private readonly Dictionary<string, HashSet<string>> _bound = new(StringComparer.Ordinal);

    partial void TryContentVerbs(WireLine line, ref WireReply? reply)
    {
        var two = (line.Verb + " " + line.WordUpper(1)).Trim();
        reply = two switch
        {
            "FX KINDS" => Ok(JsonSerializer.Serialize(FxKinds.Select(k => new Dictionary<string, string> { ["id"] = k.Id, ["name"] = k.Name, ["family"] = k.Family }))),
            "FX ADD" => FxAdd(line),
            "MOD SOURCES" => Ok(ModSourcesJson()),
            "MOD BIND" => ModBind(line),
            "MOD UNBIND" => ModUnbind(line),
            "BIND LEARN" => BindLearn(line),
            _ => null,
        };
    }

    private WireReply FxAdd(WireLine line)
    {
        if (line.Count < 4)
            return Err("FX ADD <target> <kind> [AT <position>] [<name>]");
        var target = line.Word(2);
        var kind = line.Word(3);
        if (!_projectMirror.Json.Contains("\"" + target + "\"", StringComparison.Ordinal))
            return Err($"no target '{target}' — an output, surface, layer or canvas");
        if (FxKinds.FirstOrDefault(k => k.Id.Equals(kind, StringComparison.OrdinalIgnoreCase) || k.Id[3..].Equals(kind, StringComparison.OrdinalIgnoreCase)) is not { Id: not null } k)
            return Err($"no effect kind '{kind}' — FX KINDS lists them");
        var fx = Section("fx");
        if (fx["slots"] is not JsonArray slots)
        {
            slots = [];
            fx["slots"] = slots;
        }
        var id = "fx" + (++_fxSlots).ToString("x8", CultureInfo.InvariantCulture);
        slots.Add(new JsonObject { ["id"] = id, ["name"] = k.Name, ["kind"] = k.Id, ["target"] = target, ["state"] = "ok", ["words"] = "", ["mix"] = 1 });
        Moved();
        return Ok(id);
    }

    private static string ModSourcesJson()
    {
        string[] names = ["beat", "bar", "phrase", "beat:pulse", "bar:pulse", "bpm", "lfo:sine/4", "lfo:triangle/1b"];
        return JsonSerializer.Serialize(names.Select(n => new Dictionary<string, string> { ["source"] = n, ["determinism"] = "pure", ["words"] = "" }));
    }

    private WireReply ModBind(WireLine line)
    {
        if (line.Count < 5)
            return Err("MOD BIND <entity> <param> <source> [AMOUNT …]");
        var entity = line.Word(2);
        var param = line.Word(3);
        if (_store.Project.FindGenerator(entity) is not { } gen || GeneratorCatalog.Find(gen.Kind)?.Param(param) is null)
            return Err($"'{entity}' has no number '{param}' to bind");
        if (!ModSource.TryParse(line.Word(4), out var source, out var why))
            return Err(why);
        if (!_bound.TryGetValue(entity, out var set))
        {
            set = new HashSet<string>(StringComparer.Ordinal);
            _bound[entity] = set;
        }
        if (!set.Add(param + "=" + source.Canonical))
            return Err($"{param} is bound to {source.Canonical} already");
        PublishModulation();
        return Ok("bound");
    }

    private WireReply ModUnbind(WireLine line)
    {
        if (line.Count < 4)
            return Err("MOD UNBIND <entity> <param>|ALL");
        var entity = line.Word(2);
        if (!_bound.TryGetValue(entity, out var set) || set.Count == 0)
            return Err($"nothing on '{entity}' is bound");
        var all = line.WordUpper(3) == "ALL";
        var removed = set.RemoveWhere(b => all || b.StartsWith(line.Word(3) + "=", StringComparison.Ordinal));
        if (removed == 0)
            return Err($"{line.Word(3)} is not bound");
        PublishModulation();
        return Ok("unbound");
    }

    private void PublishModulation()
    {
        Section("modulation")["bindings"] = _bound.Values.Sum(s => s.Count);
        Moved();
    }

    private WireReply BindLearn(WireLine line)
    {
        var control = Section("control");
        if (line.Count == 3 && line.WordUpper(2) == "OFF")
        {
            if (control["learning"] is not JsonObject)
                return Err("not learning — nothing to stop");
            control["learning"] = null;
            Moved();
            return Ok("learn stopped");
        }
        if (line.Count < 3)
            return Err("BIND LEARN [FROM <surface>] <target line> | BIND LEARN OFF");
        var target = line.RestAfter(2);
        if (TargetLine.Parse(target, out var why) is null)
            return Err(why);
        control["learning"] = new JsonObject { ["target"] = target, ["untilUnixMs"] = UnixMs + 30_000, ["words"] = "Move a control for " + target + " — Esc cancels" };
        Moved();
        return Ok("learning");
    }
}
