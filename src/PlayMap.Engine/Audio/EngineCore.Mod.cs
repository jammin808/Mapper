using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using PlayMap.Core.Model;
using PlayMap.Core.Wire;
using PlayMap.Core.Modulation;

namespace PlayMap.Engine;

/// <summary>The MOD verbs (FX BIND and GENERATOR BIND are MOD BIND; FX UNBIND and GENERATOR UNBIND are MOD UNBIND): a binding is a
/// project edit (undoable, one per parameter); its live value is computed per frame and never written; MOD BYPASS holds numbers at
/// their base on this node without touching the show.</summary>
public sealed partial class EngineCore
{
    private void RouteMod(VerbRoutes routes)
    {
        routes.Lane("MOD SOURCES", c => ModSources(c.Args));
        routes.Lane("MOD BIND", c => ModBind(c.Args));
        routes.Lane("MOD UNBIND", c => ModUnbind(c.Args));
        routes.Lane("MOD BYPASS", c => ModBypass(c.Args));
        routes.Lane("MOD PEEK", c => ModPeek(c.Args));
    }

    /// <summary>The sources MOD SOURCES lists by name (an LFO is any shape, period and phase; the list shows each shape once).</summary>
    private static readonly ImmutableArray<string> ListedSources =
    [
        "audio:level", "audio:bass", "audio:mid", "audio:high", "audio:level/n", "audio:bass/n", "audio:mid/n", "audio:high/n",
        "audio:peak", "audio:onset", "audio:flux", "audio:centroid",
        .. Enumerable.Range(0, ModSource.Bands).Select(i => "audio:band/" + i.ToString(CultureInfo.InvariantCulture)),
        "beat", "bar", "phrase", "beat:pulse", "bar:pulse", "bpm",
        "lfo:sine/1", "lfo:triangle/1", "lfo:saw/1", "lfo:ramp/1", "lfo:square/1", "lfo:pulse/1", "lfo:random/1", "lfo:sine/4b",
    ];

    private CommandReply ModSources(List<string> args)
    {
        var prefix = string.Join(' ', args).Trim();
        var now = LeaderNowNs;
        var listening = AudioRt.Feed.Listening;
        var names = ListedSources.Where(n => prefix.Length == 0 || n.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList();
        if (names.Count == 0)
            return CommandReply.Fail($"no source starts with '{prefix}' — MOD SOURCES alone lists them");
        return CommandReply.Success(JsonOut(w =>
        {
            w.WriteStartArray();
            foreach (var name in names)
            {
                if (!ModSource.TryParse(name, out var source, out _))
                    continue;
                var (ok, value, raw) = AudioRt.Read(bus =>
                {
                    var got = bus.TryRead(name, now, out var v);
                    bus.TryReadRaw(name, now, out var r);
                    return (got, v, r);
                });
                w.WriteStartObject();
                w.WriteString("source", name);
                if (ok)
                    w.WriteNumber("value", Math.Round(value, 4));
                else
                    w.WriteNull("value");
                if (source.Kind is ModSourceKind.Bpm or ModSourceKind.Peak && ok)
                    w.WriteNumber("raw", Math.Round(raw, 3));
                w.WriteString("determinism", source.Determinism.ToString().ToLowerInvariant());
                w.WriteString("words", SourceWords(source, listening));
                w.WriteEndObject();
            }
            w.WriteEndArray();
        }));
    }

    private static string SourceWords(ModSource s, bool listening) => s.Kind switch
    {
        _ when s.IsAudio && !listening => "no audio on this node — AUDIO LISTEN chooses an input",
        _ when s.IsAudio => "this node's sound (live: a mirror backup does not hear it)",
        ModSourceKind.Lfo => "any shape (sine, triangle, saw, ramp, square, pulse, random), a period in seconds or beats (4b) and a phase: lfo:<shape>/<period>[b][/<phase>]",
        ModSourceKind.BeatPulse or ModSourceKind.BarPulse => "1 on the line, decaying (beat:pulse/<ms> sets the decay)",
        ModSourceKind.Bpm => "the tempo, 0–1 over 0–240 BPM",
        _ => "the beat clock: the same on every node at the same instant",
    };

    private CommandReply ModBind(List<string> args)
    {
        if (args.Count < 3)
            return CommandReply.Fail("MOD BIND <entity> <param> <source> [AMOUNT <-1…1>] [OFFSET <x>] [MODE ADD|SCALE|REPLACE] [CURVE …] [SMOOTH <attack ms>[/<release ms>]] [RANGE <min> <max>] [STEPS <n>] [INVERT]");
        var p = Edits.Project;
        if (ModTargets.Find(p, args[0]) is not { } entity)
            return CommandReply.Fail($"no generator, layer, surface or effect slot {args[0]}");
        if (!ModTargets.TryParam(p, entity, args[1], AudioRt.FxRanges, out _, out var param, out var paramWhy))
            return CommandReply.Fail(paramWhy);
        if (!ModSource.TryParse(args[2], out var source, out var sourceWhy))
            return CommandReply.Fail(sourceWhy);
        if (!TryBindOptions(args.Skip(3).ToList(), source, param, out var binding, out var optionsWhy))
            return CommandReply.Fail(optionsWhy);
        var existing = entity.Bindings.FirstOrDefault(b => b.Param.Equals(param, StringComparison.OrdinalIgnoreCase));
        if (existing is not null && existing == binding)
            return CommandReply.Fail(Inv($"{entity.Name} {param} is already bound to {binding.Source} with these settings"));
        var bindings = existing is null ? entity.Bindings.Add(binding) : entity.Bindings.Replace(existing, binding);
        var r = Edits.Apply($"modulate {entity.Name} {param} from {binding.Source}", pp => ModTargets.WithBindings(pp, entity, bindings));
        if (!r.Changed)
            return FromEdit(r);
        if (!source.IsAudio)
            TempoInUse();
        var note = source.IsAudio
            ? AudioRt.Feed.Listening ? " — live: this node's sound" : " — no audio on this node yet: it holds its base until AUDIO LISTEN"
            : " — pure: the same on every node";
        if (!ModulationPlan.DrawsLive(entity.Kind))
            note += "; " + GeneratorDrawsBase;
        return CommandReply.Success(BindingJson(entity, binding),
            $"{(existing is null ? "bound" : "rebound")} {entity.KindWord} {entity.Name} {param} to {binding.Source}{note}");
    }

    /// <summary>Reads MOD BIND's options into a binding (amount 0.5, ADD, linear, unsmoothed unless given).</summary>
    private static bool TryBindOptions(List<string> args, ModSource source, string param, out ParamBinding binding, out string why)
    {
        why = "";
        double amount = 0.5, offset = 0, attack = -1, release = -1, rangeMin = 0, rangeMax = 1;
        var mode = BindingMode.Add;
        var curve = BindingCurve.Linear;
        int steps = 0;
        var invert = false;
        binding = null!;
        static bool Num(List<string> a, int i, out double v) =>
            double.TryParse(i < a.Count ? a[i] : "", NumberStyles.Float, CultureInfo.InvariantCulture, out v) && double.IsFinite(v);
        for (var i = 0; i < args.Count; i++)
        {
            switch (args[i].ToUpperInvariant())
            {
                case "AMOUNT" when Num(args, i + 1, out amount) && amount is >= -1 and <= 1:
                    i++;
                    break;
                case "OFFSET" when Num(args, i + 1, out offset) && offset is >= -1 and <= 1:
                    i++;
                    break;
                case "MODE" when i + 1 < args.Count && Enum.TryParse(args[i + 1], true, out mode) && mode != BindingMode.Rate
                    && !int.TryParse(args[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out _):
                    i++;
                    break;
                case "CURVE" when i + 1 < args.Count && TryCurve(args[i + 1], out curve):
                    i++;
                    break;
                case "SMOOTH" when i + 1 < args.Count && TrySmooth(args[i + 1], out attack, out release):
                    i++;
                    break;
                case "RANGE" when Num(args, i + 1, out rangeMin) && Num(args, i + 2, out rangeMax)
                    && rangeMin is >= 0 and <= 1 && rangeMax is >= 0 and <= 1:
                    i += 2;
                    break;
                case "STEPS" when i + 1 < args.Count && int.TryParse(args[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out steps)
                    && steps is >= 1 and <= 64:
                    i++;
                    break;
                case "INVERT":
                    invert = true;
                    break;
                default:
                    why = args[i].ToUpperInvariant() switch
                    {
                        "AMOUNT" => "AMOUNT is -1 to 1",
                        "OFFSET" => "OFFSET is -1 to 1",
                        "MODE" => "MODE is ADD, SCALE or REPLACE",
                        "CURVE" => "CURVE is LINEAR, EASE, STEP, EXP, LOG or S",
                        "SMOOTH" => "SMOOTH is <attack ms>[/<release ms>], 0 to 10000",
                        "RANGE" => "RANGE is <min> <max>, each 0 to 1 of the parameter's range",
                        "STEPS" => "STEPS is 1 to 64",
                        _ => $"'{args[i]}' is not an option of MOD BIND (AMOUNT, OFFSET, MODE, CURVE, SMOOTH, RANGE, STEPS, INVERT)",
                    };
                    return false;
            }
        }
        if (source.IsSmoothed && attack >= 0)
        {
            why = "the source carries its own smoothing (~…); give SMOOTH or the suffix, not both";
            return false;
        }
        binding = new ParamBinding(param, source.Canonical, amount, offset, curve, attack >= 0 ? attack / 1000 : 0)
        {
            Mode = mode,
            AttackSeconds = attack >= 0 ? attack / 1000 : -1,
            ReleaseSeconds = release >= 0 ? release / 1000 : -1,
            RangeMin = rangeMin,
            RangeMax = rangeMax,
            Invert = invert,
            Steps = steps,
        };
        return true;
    }

    private static bool TryCurve(string word, out BindingCurve curve)
    {
        curve = word.ToUpperInvariant() switch
        {
            "LINEAR" => BindingCurve.Linear,
            "EASE" => BindingCurve.Ease,
            "STEP" => BindingCurve.Step,
            "EXP" => BindingCurve.Exponential,
            "LOG" => BindingCurve.Logarithmic,
            "S" => BindingCurve.SCurve,
            _ => (BindingCurve)(-1),
        };
        return Enum.IsDefined(curve);
    }

    private static bool TrySmooth(string text, out double attackMs, out double releaseMs)
    {
        releaseMs = -1;
        var parts = text.Split('/');
        static bool Ms(string s, out double v) =>
            double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v) && double.IsFinite(v) && v is >= 0 and <= 10000;
        if (!Ms(parts[0], out attackMs) || parts.Length > 2)
            return false;
        if (parts.Length == 1)
        {
            releaseMs = attackMs;
            return true;
        }
        return Ms(parts[1], out releaseMs);
    }

    private CommandReply ModUnbind(List<string> args)
    {
        if (args.Count != 2)
            return CommandReply.Fail("MOD UNBIND <entity> <param>|ALL");
        var p = Edits.Project;
        if (ModTargets.Find(p, args[0]) is not { } entity)
            return CommandReply.Fail($"no generator, layer, surface or effect slot {args[0]}");
        if (entity.Bindings.IsEmpty)
            return CommandReply.Fail($"{entity.Name} has no modulation");
        ImmutableArray<ParamBinding> left;
        string what;
        if (args[1].Equals("ALL", StringComparison.OrdinalIgnoreCase))
        {
            left = [];
            what = Inv($"all {entity.Bindings.Length} bindings");
        }
        else
        {
            var gone = entity.Bindings.FirstOrDefault(b => b.Param.Equals(args[1], StringComparison.OrdinalIgnoreCase));
            if (gone is null)
                return CommandReply.Fail($"{entity.Name} {args[1]} is not modulated — it modulates {string.Join(", ", entity.Bindings.Select(b => b.Param))}");
            left = entity.Bindings.Remove(gone);
            what = gone.Param;
        }
        return FromEdit(Edits.Apply($"unmodulate {entity.Name} {what}", pp => ModTargets.WithBindings(pp, entity, left)));
    }

    private CommandReply ModBypass(List<string> args)
    {
        if (args.Count is < 1 or > 2 || args[0].ToUpperInvariant() is not ("ON" or "OFF" or "TOGGLE"))
            return CommandReply.Fail("MOD BYPASS ON|OFF|TOGGLE [<entity>|ALL]");
        var word = args[0].ToUpperInvariant();
        if (args.Count == 1 || args[1].Equals("ALL", StringComparison.OrdinalIgnoreCase))
        {
            var on = word == "TOGGLE" ? !_modBypassAll : word == "ON";
            if (on == _modBypassAll)
                return CommandReply.Fail(on ? "modulation is already held at the base values" : "modulation is already live");
            _modBypassAll = on;
            Journal.Add("modulation", on ? "bypassed: every modulated number at its base" : "live again", ok: true);
            FactsChanged();
            return CommandReply.Success(effect: on ? "every modulated number holds its base value" : "modulation is live again");
        }
        if (ModTargets.Find(Store.Project, args[1]) is not { } entity)
            return CommandReply.Fail($"no generator, layer, surface or effect slot {args[1]}");
        if (entity.Bindings.IsEmpty)
            return CommandReply.Fail($"{entity.Name} has no modulation to hold");
        var held = _modBypassed.Contains(entity.Id);
        var hold = word == "TOGGLE" ? !held : word == "ON";
        if (hold == held)
            return CommandReply.Fail(hold ? $"{entity.Name} is already held at its base" : $"{entity.Name} is already live");
        _modBypassed = hold ? _modBypassed.Add(entity.Id) : _modBypassed.Remove(entity.Id);
        FactsChanged();
        return CommandReply.Success(effect: hold ? $"{entity.Name} holds its base values" : $"{entity.Name} is modulated again");
    }

    private CommandReply ModPeek(List<string> args)
    {
        if (args.Count != 1)
            return CommandReply.Fail("MOD PEEK <entity>");
        var plan = LanePlan();
        if (ModTargets.Find(plan.Project, args[0]) is not { } entity)
            return CommandReply.Fail($"no generator, layer, surface or effect slot {args[0]}");
        if (entity.Bindings.IsEmpty)
            return CommandReply.Fail($"{entity.Name} has no modulation");
        var now = LeaderNowNs;
        var held = _modBypassAll || _modBypassed.Contains(entity.Id);
        return CommandReply.Success(JsonOut(w =>
        {
            w.WriteStartObject();
            w.WriteString("entity", entity.Id);
            w.WriteString("kind", entity.KindWord);
            w.WriteString("name", entity.Name);
            w.WriteBoolean("held", held);
            w.WriteNumber("leaderNs", now);
            w.WriteStartArray("bindings");
            for (var i = 0; i < plan.Items.Length; i++)
            {
                var item = plan.Items[i];
                if (item.Entity.Id != entity.Id || item.Entity.Kind != entity.Kind)
                    continue;
                var (value, source) = AudioRt.Read(bus => plan.Evaluate(i, bus, now, held));
                WriteBinding(w, item.Binding);
                w.WriteNumber("base", Math.Round(item.Range.Base, 6));
                w.WriteNumber("value", Math.Round(value, 6));
                if (double.IsNaN(source))
                    w.WriteNull("sourceValue");
                else
                    w.WriteNumber("sourceValue", Math.Round(source, 6));
                w.WriteNumber("min", item.Range.Min);
                w.WriteNumber("max", item.Range.Max);
                w.WriteString("determinism", item.Source?.Determinism.ToString().ToLowerInvariant() ?? "");
                w.WriteString("words", item.Problem.Length > 0 ? item.Problem
                    : double.IsNaN(source) ? "its source is missing: the number holds its base" : held ? "held at its base (MOD BYPASS)"
                    : !ModulationPlan.DrawsLive(item.Entity.Kind) ? GeneratorDrawsBase : "");
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }));
    }

    private static string BindingJson(ModEntity entity, ParamBinding b) => JsonOut(w =>
    {
        WriteBinding(w, b);
        w.WriteString("entity", entity.Id);
        w.WriteString("kind", entity.KindWord);
        w.WriteEndObject();
    });

    /// <summary>Starts an object and writes a binding's fields (the caller ends it).</summary>
    private static void WriteBinding(Utf8JsonWriter w, ParamBinding b)
    {
        w.WriteStartObject();
        w.WriteString("param", b.Param);
        w.WriteString("source", b.Source);
        w.WriteString("mode", b.Mode.ToString().ToLowerInvariant());
        w.WriteNumber("amount", b.Amount);
        w.WriteNumber("offset", b.Offset);
        w.WriteString("curve", b.Curve.ToString().ToLowerInvariant());
        w.WriteNumber("attackMs", Math.Round(Math.Max(0, b.AttackSeconds >= 0 ? b.AttackSeconds : b.SmoothSeconds) * 1000, 3));
        w.WriteNumber("releaseMs", Math.Round(Math.Max(0, b.ReleaseSeconds >= 0 ? b.ReleaseSeconds : b.SmoothSeconds) * 1000, 3));
        w.WriteNumber("rangeMin", b.RangeMin);
        w.WriteNumber("rangeMax", b.RangeMax);
        w.WriteNumber("steps", b.Steps);
        w.WriteBoolean("invert", b.Invert);
        w.WriteBoolean("enabled", b.Enabled);
    }
}
