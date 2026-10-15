using System.Globalization;
using System.Text.Json;
using PlayMap.Core.Content;
using PlayMap.Core.Model;
using PlayMap.Core.Wire;
using PlayMap.Engine.Content;

namespace PlayMap.Engine;

/// <summary>wp2-content's verbs: a generator's own time (RETIME), what it reads (INPUT), its parameters back to their defaults
/// (RESET), what it is (GET), and the stings every generator answers (STING, STING CLEAR, STING LIST). The model verbs answer when
/// the edit is applied; a sting answers when the channel the render thread reads holds it, and STATE.content.sting is where its
/// surge is observed.</summary>
public sealed partial class EngineCore
{
    /// <summary>The engine's side of content: the sting channel and the renderer's observations.</summary>
    internal ContentBoard Content { get; } = new();

    private void RouteContentVerbs(VerbRoutes routes)
    {
        routes.Lane("GENERATOR GET", c => GeneratorGet(c.Args));
        routes.Lane("GENERATOR RESET", c => GeneratorReset(c.Args));
        routes.Lane("GENERATOR INPUT", c => GeneratorInput(c.Args));
        routes.Lane("GENERATOR RETIME", c => GeneratorRetime(c.Args));
        routes.Lane("GENERATOR PRESETS", c => GeneratorPresets(c.Args));
        routes.Lane("GENERATOR RECALL", c => GeneratorRecall(c.Args));
        routes.Lane("GENERATOR SAVE", c => GeneratorSave(c.Args));
        routes.Lane("STING", c => Sting(c.Args));
        routes.Lane("STING CLEAR", _ => StingClear());
        routes.Lane("STING LIST", _ => CommandReply.Success(StingListJson()));
        routes.Index(new ContentIndex(this));
        routes.Index(new StingIndex(this));
    }

    /// <summary>A generator by id or name in the project being edited.</summary>
    private Generator? FindGenerator(string key) =>
        Edits.Project.FindGenerator(key) ?? Edits.Project.Generators.FirstOrDefault(x => x.Name.Equals(key, StringComparison.OrdinalIgnoreCase));

    /// <summary>The Unix ms a leader instant falls on, on this node's show clock.</summary>
    private long UnixMsAt(long leaderNs) => Services.Wall.UnixMs + (leaderNs - LeaderNowNs) / 1_000_000;

    // ---- GENERATOR NEW … PRESET, GENERATOR SET … BY ----

    /// <summary>"PRESET &lt;name&gt;" at the end of GENERATOR NEW's words: the preset's name (its words to the end), removed.</summary>
    private static string? TakePreset(List<string> args)
    {
        var i = args.FindIndex(a => a.Equals("PRESET", StringComparison.OrdinalIgnoreCase));
        if (i < 1 || i == args.Count - 1)
            return null;
        var name = string.Join(' ', args.Skip(i + 1));
        args.RemoveRange(i, args.Count - i);
        return name;
    }

    /// <summary>The curated look a kind takes by name, with its spelling; null with the reason when the kind has none by that name.</summary>
    internal static string? PresetFor(GeneratorKind kind, string name, out string why)
    {
        ArgumentNullException.ThrowIfNull(kind);
        why = "";
        if (kind.Param("preset") is null)
        {
            why = $"{kind.Name} has no presets";
            return null;
        }
        if (kind.Id.Equals("particles.field", StringComparison.OrdinalIgnoreCase))
        {
            if (ParticlePresets.Find(name) is { } p)
                return p.Name;
            why = $"no particle preset '{name}' — {string.Join(", ", ParticlePresets.Names)}";
            return null;
        }
        if (FractalPresets.Find(name) is { } f)
            return f.Name;
        why = $"no fractal preset '{name}' — {string.Join(", ", FractalPresets.Names)}";
        return null;
    }

    /// <summary>A parameter stepped by a delta: a number by delta × its step ((max − min) / 100), a whole number likewise rounded, a
    /// choice to the next (or previous) one, wrapping. Null with the reason for a type that does not step.</summary>
    internal static string? Stepped(GeneratorKind kind, ParamSpec spec, ParamSet set, double delta, out string why)
    {
        ArgumentNullException.ThrowIfNull(kind);
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(set);
        why = "";
        switch (spec.Type)
        {
            case ParamType.Number:
            {
                var step = (spec.Max - spec.Min) / 100;
                var v = Math.Clamp(ParamValues.Number(set, kind, spec.Name) + delta * step, spec.Min, spec.Max);
                return v.ToString("R", CultureInfo.InvariantCulture);
            }
            case ParamType.Integer:
            {
                var step = Math.Max(1, Math.Round((spec.Max - spec.Min) / 100));
                var v = Math.Clamp(Math.Round(ParamValues.Number(set, kind, spec.Name) + delta * step), spec.Min, spec.Max);
                return ((long)v).ToString(CultureInfo.InvariantCulture);
            }
            case ParamType.Choice when spec.Choices.Count > 0:
            {
                var at = Math.Max(0, spec.Choices.ToList().FindIndex(c => c.Equals(ParamValues.Choice(set, kind, spec.Name), StringComparison.Ordinal)));
                var n = spec.Choices.Count;
                var by = Math.Sign(delta) * Math.Max(1, (int)Math.Round(Math.Abs(delta)));
                return spec.Choices[((at + by) % n + n) % n];
            }
            default:
                why = $"{spec.Name} is {spec.Type.ToString().ToLowerInvariant()}: only numbers and choices step BY";
                return null;
        }
    }

    // ---- GENERATOR GET ----

    private CommandReply GeneratorGet(List<string> args)
    {
        if (args.Count < 1)
            return CommandReply.Fail("GENERATOR GET <generator>");
        var g = FindGenerator(string.Join(' ', args));
        if (g is null)
            return CommandReply.Fail($"no generator {string.Join(' ', args)}");
        var kind = GeneratorCatalog.Find(g.Kind);
        var traits = GeneratorTraits.Of(g.Kind);
        var view = Content.View(g, LeaderNowNs);
        return CommandReply.Success(JsonOut(w =>
        {
            w.WriteStartObject();
            w.WriteString("id", g.Id);
            w.WriteString("name", g.Name);
            w.WriteString("kind", g.Kind);
            w.WritePropertyName("params");
            JsonSerializer.Serialize(w, g.Params, Core.Model.ProjectJson.Compact);
            w.WriteStartObject("effective");
            if (kind is not null)
                foreach (var p in kind.Params)
                    w.WriteString(p.Name, kind.Value(g.Params, p.Name));
            w.WriteEndObject();
            w.WritePropertyName("bindings");
            JsonSerializer.Serialize(w, g.Bindings, Core.Model.ProjectJson.Compact);
            w.WriteStartObject("inputs");
            foreach (var (name, source) in g.Inputs)
                w.WriteString(name, source.ToString());
            w.WriteEndObject();
            w.WriteNumber("originNs", g.OriginNs);
            w.WriteNull("morph");
            w.WriteStartObject("traits");
            w.WriteString("time", GeneratorTraits.Word(traits.Time));
            w.WriteNumber("periodSeconds", traits.PeriodSeconds);
            w.WriteNumber("maxLifeSeconds", ContentBoard.HorizonSeconds(g));
            w.WriteBoolean("readsAudio", traits.ReadsAudio);
            w.WriteBoolean("mayFlash", traits.MayFlash);
            w.WriteBoolean("drawsBindings", traits.DrawsBindings);
            w.WriteEndObject();
            w.WriteString("state", view.State);
            w.WriteNumber("progress", Math.Round(view.Progress, 3));
            w.WriteString("time", GeneratorTraits.Word(view.Time));
            w.WriteNumber("timeS", Math.Round(view.TimeSeconds, 3));
            w.WriteNumber("gpuMs", Math.Round(view.GpuMs, 3));
            w.WriteStartObject("live");
            foreach (var (name, value) in view.Live)
                w.WriteNumber(name, Math.Round(value, 4));
            w.WriteEndObject();
            w.WriteString("words", view.Words);
            w.WriteEndObject();
        }));
    }

    // ---- GENERATOR RESET ----

    private CommandReply GeneratorReset(List<string> args)
    {
        if (args.Count < 1)
            return CommandReply.Fail("GENERATOR RESET <generator> [<param>|ALL]");
        var all = args.Count == 1 || args[^1].Equals("ALL", StringComparison.OrdinalIgnoreCase);
        var g = FindGenerator(args.Count == 1 ? args[0] : string.Join(' ', args.Take(args.Count - 1)));
        if (g is null)
            return CommandReply.Fail($"no generator {(args.Count == 1 ? args[0] : string.Join(' ', args.Take(args.Count - 1)))}");
        var kind = GeneratorCatalog.Find(g.Kind);
        if (all)
        {
            if (g.Params.Values.Count == 0)
                return CommandReply.Fail($"{g.Name} is already at its defaults");
            return FromEdit(Edits.Apply($"{g.Name} back to its defaults",
                p => p with { Generators = p.Generators.Update(g.Id, x => x with { Params = ParamSet.Empty }) }));
        }
        var spec = kind?.Param(args[^1]);
        if (kind is null || spec is null)
            return CommandReply.Fail($"{g.Name} has no parameter {args[^1]}");
        if (g.Params.Get(spec.Name) is null)
            return CommandReply.Fail($"{g.Name} {spec.Name} is already its default ({spec.Default})");
        return FromEdit(Edits.Apply($"{g.Name} {spec.Name} back to {spec.Default}",
            p => p with { Generators = p.Generators.Update(g.Id, x => x with { Params = new ParamSet(x.Params.Values.Remove(spec.Name)) }) }));
    }

    // ---- GENERATOR INPUT ----

    /// <summary>The pictures a kind reads, by input name.</summary>
    internal static IReadOnlyList<string> InputsOf(string kindId) =>
        ParticleFlowSettings.IsKind(kindId) ? ["source"] : [];

    private CommandReply GeneratorInput(List<string> args)
    {
        if (args.Count < 3)
            return CommandReply.Fail("GENERATOR INPUT <generator> source <source>|NONE");
        var g = FindGenerator(string.Join(' ', args.Take(args.Count - 2)));
        if (g is null)
            return CommandReply.Fail($"no generator {string.Join(' ', args.Take(args.Count - 2))}");
        var input = args[^2].ToLowerInvariant();
        var inputs = InputsOf(g.Kind);
        if (!inputs.Contains(input))
            return CommandReply.Fail(inputs.Count == 0
                ? $"{g.Name} reads no picture"
                : $"{g.Name} reads {string.Join(", ", inputs)}, not {args[^2]}");
        SourceRef source;
        if (args[^1].Equals("NONE", StringComparison.OrdinalIgnoreCase))
            source = SourceRef.None;
        else if (!SourceRef.TryParse(args[^1], out source))
            return CommandReply.Fail("a source is media:<id>, playlist:<id>, generator:<id>, canvas:<id>, live:<id>, timer:<id>, color:#RRGGBB or NONE");
        if (!source.IsNone && ResolveSourceProblem(source) is { } problem)
            return CommandReply.Fail(problem);
        var had = g.Inputs.TryGetValue(input, out var old) ? old : SourceRef.None;
        if (had == source)
            return CommandReply.Fail(source.IsNone ? $"{g.Name} reads no {input}" : $"{g.Name} already reads {SourceLabel(source)}");
        if (!source.IsNone && ReachesGenerator(Edits.Project, source, g.Id, 0) is { } loop)
            return CommandReply.Fail($"{g.Name} would read {loop}, which shows {g.Name}: a loop");
        var effect = source.IsNone ? $"{g.Name} reads no {input}" : $"{g.Name} reads {SourceLabel(source)} as its {input}";
        return FromEdit(Edits.Apply(effect, p => p with
        {
            Generators = p.Generators.Update(g.Id, x => x with { Inputs = source.IsNone ? x.Inputs.Remove(input) : x.Inputs.SetItem(input, source) }),
        }));
    }

    /// <summary>The label of the first source on the way from <paramref name="source"/> that shows the generator (it, a canvas whose
    /// layer shows it, a playlist whose item does, a generator that reads it), or null; nested sources are followed four deep.</summary>
    private string? ReachesGenerator(Project p, SourceRef source, string generatorId, int depth)
    {
        if (depth > 4)
            return null;
        switch (source.Kind)
        {
            case SourceKind.Generator:
                if (source.Id == generatorId)
                    return SourceLabel(source);
                if (p.FindGenerator(source.Id) is { } other)
                    foreach (var (_, input) in other.Inputs)
                        if (ReachesGenerator(p, input, generatorId, depth + 1) is not null)
                            return SourceLabel(source);
                return null;
            case SourceKind.Canvas when p.FindCanvas(source.Id) is { } canvas:
                foreach (var layer in canvas.Layers)
                    if (ReachesGenerator(p, layer.Source, generatorId, depth + 1) is not null)
                        return SourceLabel(source);
                return null;
            case SourceKind.Playlist when p.FindPlaylist(source.Id) is { } list:
                foreach (var item in list.Items)
                    if (ReachesGenerator(p, item.Source, generatorId, depth + 1) is not null)
                        return SourceLabel(source);
                return null;
            default:
                return null;
        }
    }

    // ---- GENERATOR RETIME ----

    private CommandReply GeneratorRetime(List<string> args)
    {
        var (at, atError) = TakeAt(args);
        if (atError is not null)
            return CommandReply.Fail(atError);
        if (args.Count < 1)
            return CommandReply.Fail("GENERATOR RETIME <generator>|ALL [AT +<ms>|<unix ms>]");
        var origin = at!.Value;
        var everyone = args.Count == 1 && args[0].Equals("ALL", StringComparison.OrdinalIgnoreCase);
        var targets = everyone ? [.. Edits.Project.Generators] : FindGenerator(string.Join(' ', args)) is { } one ? new List<Generator> { one } : [];
        if (targets.Count == 0)
            return CommandReply.Fail(everyone ? "no generator in the show" : $"no generator {string.Join(' ', args)}");
        var moving = targets.Where(g => g.OriginNs != origin).Select(g => g.Id).ToHashSet(StringComparer.Ordinal);
        if (moving.Count == 0)
            return CommandReply.Fail(targets.Count == 1 ? $"{targets[0].Name} already counts its time from that instant" : "every generator already counts its time from that instant");
        var when = UnixMsAt(origin).ToString(CultureInfo.InvariantCulture);
        var label = moving.Count == 1 ? targets.First(t => moving.Contains(t.Id)).Name : $"{moving.Count} generators";
        var r = Edits.Apply($"{label} retimed", p => p with
        {
            Generators = [.. p.Generators.Select(g => moving.Contains(g.Id) ? g with { OriginNs = origin } : g)],
        });
        if (!r.Changed)
            return FromEdit(r);
        return CommandReply.Success(JsonOut(w =>
        {
            w.WriteStartObject();
            w.WriteNumber("originNs", origin);
            w.WriteNumber("atUnixMs", UnixMsAt(origin));
            w.WriteStartArray("generators");
            foreach (var id in moving.Order(StringComparer.Ordinal))
                w.WriteStringValue(id);
            w.WriteEndArray();
            w.WriteEndObject();
        }), $"{label} count{(moving.Count == 1 ? "s" : "")} its time from {when} (Unix ms); a particle field settles anew from there");
    }

    // ---- GENERATOR PRESETS, RECALL, SAVE ----

    /// <summary>The kinds a PRESETS filter names: a kind (its id or short name), a family, or every kind.</summary>
    private static IReadOnlyList<GeneratorKind>? KindsNamed(string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter))
            return GeneratorCatalog.All;
        if (GeneratorCatalog.FindLoose(filter) is { } kind)
            return [kind];
        var family = GeneratorCatalog.All.Where(k => k.Family.Equals(filter, StringComparison.OrdinalIgnoreCase)).ToList();
        return family.Count > 0 ? family : null;
    }

    private CommandReply GeneratorPresets(List<string> args)
    {
        var kinds = KindsNamed(args.Count > 0 ? string.Join(' ', args) : null);
        if (kinds is null)
            return CommandReply.Fail($"no generator kind or family '{string.Join(' ', args)}' — GENERATOR KINDS lists them");
        var saved = Edits.Project.GeneratorPresets;
        return CommandReply.Success(JsonOut(w =>
        {
            w.WriteStartArray();
            foreach (var kind in kinds)
            {
                foreach (var look in GeneratorLooks.For(kind.Id))
                {
                    w.WriteStartObject();
                    w.WriteNull("id");
                    w.WriteString("name", look.Name);
                    w.WriteString("kind", kind.Id);
                    w.WriteString("pack", look.Pack);
                    w.WriteBoolean("curated", true);
                    w.WriteEndObject();
                }
                foreach (var look in saved.Where(x => x.Kind.Equals(kind.Id, StringComparison.OrdinalIgnoreCase)))
                {
                    w.WriteStartObject();
                    w.WriteString("id", look.Id);
                    w.WriteString("name", look.Name);
                    w.WriteString("kind", look.Kind);
                    w.WriteString("pack", "saved");
                    w.WriteBoolean("curated", false);
                    w.WriteEndObject();
                }
            }
            w.WriteEndArray();
        }));
    }

    /// <summary>A look by name for a kind: a saved one (its id, or its name) first, then a curated one; null with the reason, which
    /// names the kind a look of another kind is for.</summary>
    private (string Name, ParamSet Params)? LookFor(GeneratorKind kind, string key, out string why)
    {
        why = "";
        var saved = Edits.Project.FindGeneratorPreset(key)
            ?? Edits.Project.GeneratorPresets.FirstOrDefault(x => x.Name.Equals(key, StringComparison.OrdinalIgnoreCase)
                && x.Kind.Equals(kind.Id, StringComparison.OrdinalIgnoreCase))
            ?? Edits.Project.GeneratorPresets.FirstOrDefault(x => x.Name.Equals(key, StringComparison.OrdinalIgnoreCase));
        if (saved is not null)
        {
            if (!saved.Kind.Equals(kind.Id, StringComparison.OrdinalIgnoreCase))
            {
                why = $"{saved.Name} is a look for {GeneratorCatalog.Find(saved.Kind)?.Name ?? saved.Kind}, not {kind.Name}";
                return null;
            }
            return (saved.Name, saved.Params);
        }
        if (GeneratorLooks.Find(kind.Id, key) is { } look)
            return (look.Name, look.Params);
        if (GeneratorLooks.FindAny(key) is { } other)
        {
            why = $"{other.Name} is a look for {GeneratorCatalog.Find(other.Kind)?.Name ?? other.Kind}, not {kind.Name}";
            return null;
        }
        var names = GeneratorLooks.For(kind.Id).Select(l => l.Name).ToList();
        why = names.Count == 0 ? $"no look '{key}': {kind.Name} has no curated looks (GENERATOR SAVE keeps one)"
            : $"no look '{key}' for {kind.Name} — {string.Join(", ", names)}";
        return null;
    }

    private CommandReply GeneratorRecall(List<string> args)
    {
        if (args.Count < 2)
            return CommandReply.Fail("GENERATOR RECALL <generator> <preset>");
        // The generator is the first word (an id or a one-word name); the look is the rest.
        var g = FindGenerator(args[0]);
        if (g is null)
            return CommandReply.Fail($"no generator {args[0]}");
        if (GeneratorCatalog.Find(g.Kind) is not { } kind)
            return CommandReply.Fail($"this node has no generator kind {g.Kind}");
        if (LookFor(kind, string.Join(' ', args.Skip(1)), out var why) is not { } look)
            return CommandReply.Fail(why);
        if (SameLook(g.Params, look.Params))
            return CommandReply.Fail($"{g.Name} already looks like {look.Name}");
        return FromEdit(Edits.Apply($"{g.Name} → {look.Name}",
            p => p with { Generators = p.Generators.Update(g.Id, x => x with { Params = look.Params }) }));
    }

    private CommandReply GeneratorSave(List<string> args)
    {
        if (args.Count < 2)
            return CommandReply.Fail("GENERATOR SAVE <generator> <name>");
        var g = FindGenerator(args[0]);
        if (g is null)
            return CommandReply.Fail($"no generator {args[0]}");
        if (GeneratorCatalog.Find(g.Kind) is not { } kind)
            return CommandReply.Fail($"this node has no generator kind {g.Kind}");
        var name = string.Join(' ', args.Skip(1));
        if (Edits.Project.GeneratorPresets.FirstOrDefault(x => x.Kind.Equals(kind.Id, StringComparison.OrdinalIgnoreCase)
            && x.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) is { } kept)
            return CommandReply.Fail($"the show already keeps a {kind.Name} look named {kept.Name} ({kept.Id})");
        // A kind whose looks ride its preset parameter keeps what the operator set (the preset fills the rest); every other kind's
        // look is written whole, so it recalls the same picture whatever the catalogue's defaults become.
        var parameters = kind.Param("preset") is not null ? g.Params : kind.Params.Aggregate(ParamSet.Empty, (set, p) => set.With(p.Name, kind.Value(g.Params, p.Name)));
        string id = "";
        var r = Edits.Apply($"save look {name}", p =>
        {
            id = p.NewId(Ids.GeneratorPreset);
            return p with { GeneratorPresets = p.GeneratorPresets.Add(new GeneratorPreset(id, name, kind.Id, parameters)) };
        });
        return r.Changed ? CommandReply.Success(id, $"kept {g.Name}'s look as {name}") : FromEdit(r);
    }

    private static bool SameLook(ParamSet a, ParamSet b) =>
        a.Values.Count == b.Values.Count && a.Values.All(kv => b.Values.TryGetValue(kv.Key, out var v) && string.Equals(v, kv.Value, StringComparison.Ordinal));

    // ---- STING ----

    private CommandReply Sting(List<string> args)
    {
        var (at, atError) = TakeAt(args);
        if (atError is not null)
            return CommandReply.Fail(atError);
        if (args.Count < 1 || args.Count > 2)
            return CommandReply.Fail("STING <preset> [<100-5000 ms>] [AT +<ms>|<unix ms>]");
        if (Stings.Find(args[0]) is not { } preset)
            return CommandReply.Fail($"no sting '{args[0]}' — {string.Join(", ", Stings.All.Select(s => s.Name))}");
        var ms = preset.DefaultMs;
        if (args.Count == 2)
        {
            if (!int.TryParse(args[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out ms) || ms is < Stings.MinMs or > Stings.MaxMs)
                return CommandReply.Fail($"a sting runs {Stings.MinMs}–{Stings.MaxMs} ms, not {args[1]}");
        }
        var start = at!.Value;
        var fired = new StingFired(preset, start, UnixMsAt(start), ms);
        Content.Fire(fired);
        FactsChanged();
        // Applied: the channel every generator reads holds it; its surge is observed in STATE.content.sting.
        return CommandReply.Success(JsonOut(w => WriteSting(w, fired, LeaderNowNs)),
            $"{preset.Name} {ms} ms at {fired.AtUnixMs.ToString(CultureInfo.InvariantCulture)} (Unix ms)");
    }

    private CommandReply StingClear()
    {
        if (Content.Sting is not { } s || LeaderNowNs >= s.EndLeaderNs)
            return CommandReply.Fail("no sting is running");
        Content.Clear();
        FactsChanged();
        return CommandReply.Success(effect: $"{s.Preset.Name} stopped");
    }

    private static string StingListJson() => JsonOut(w =>
    {
        w.WriteStartArray();
        foreach (var s in Stings.All)
        {
            w.WriteStartObject();
            w.WriteString("name", s.Name);
            w.WriteString("kind", s.Scored ? "score" : "pulse");
            w.WriteStartArray("channels");
            foreach (var c in s.Channels)
                w.WriteStringValue(c);
            w.WriteEndArray();
            w.WriteNumber("defaultMs", s.DefaultMs);
            w.WriteEndObject();
        }
        w.WriteEndArray();
    });

    private static void WriteSting(Utf8JsonWriter w, StingFired s, long nowNs)
    {
        w.WriteStartObject();
        w.WriteString("preset", s.Preset.Name);
        w.WriteNumber("atUnixMs", s.AtUnixMs);
        w.WriteNumber("lengthMs", s.LengthMs);
        w.WriteNumber("progress", Math.Round(s.Progress(nowNs), 3));
        w.WriteEndObject();
    }

    private int _stingTenth = -1;

    /// <summary>The tick: a running sting's progress reaches STATE in tenths (a push each), and its end once.</summary>
    private void TickStings(long leaderNs)
    {
        var tenth = Content.Sting is { } s && leaderNs < s.EndLeaderNs ? (int)Math.Floor(s.Progress(leaderNs) * 10) : -1;
        if (tenth == _stingTenth)
            return;
        _stingTenth = tenth;
        FactsChanged();
    }

    // ---- STATE and the context ----

    private void WriteContentState(Utf8JsonWriter w)
    {
        var settings = Services.Settings.Content;
        var now = LeaderNowNs;
        var views = Content.Views(Store.Project, now);
        var (light, cost) = ContentBoard.Cost(views, settings.BudgetMs);
        w.WriteStartObject("content");
        w.WriteNumber("budgetMs", settings.BudgetMs);
        w.WriteString("governor", settings.Governor);
        w.WriteString("light", light.ToString().ToLowerInvariant());
        w.WriteNumber("costMs", Math.Round(cost, 3));
        w.WriteString("renderer", Content.Frame is null ? "none" : "drawing");
        if (Content.Sting is { } s && now < s.EndLeaderNs)
        {
            w.WritePropertyName("sting");
            WriteSting(w, s, now);
        }
        else
        {
            w.WriteNull("sting");
        }
        w.WriteStartArray("generators");
        foreach (var v in views)
            ContentBoard.WriteGenerator(w, v, settings.BudgetMs);
        w.WriteEndArray();
        w.WriteEndObject();
    }

    private void AddContentContext(List<Core.Bus.ContextItem> items)
    {
        // The item is about the air: a show whose generators nothing draws (or a node with no renderer) adds none.
        var views = Content.Views(Store.Project, LeaderNowNs);
        var stinging = Content.Sting is { } running && LeaderNowNs < running.EndLeaderNs;
        if (!stinging && !views.Any(v => v.OnAir || v.State is "faulted" or "warming"))
            return;
        var budget = Services.Settings.Content.BudgetMs;
        var (light, cost) = ContentBoard.Cost(views, budget);
        var words = new List<string> { $"{views.Count} generator{(views.Count == 1 ? "" : "s")}" };
        if (cost > 0)
            words.Add($"{ContentBoard.Ms(cost)} ms of {budget.ToString("0.#", CultureInfo.InvariantCulture)}");
        foreach (var v in views.Where(v => v.State == "warming"))
            words.Add($"{v.Label} settling {(v.Progress * 100).ToString("0", CultureInfo.InvariantCulture)} %");
        foreach (var v in views.Where(v => v.State == "faulted"))
            words.Add($"{v.Label} faulted: {v.Words}");
        var live = views.Count(v => v.OnAir && v.Time == TimeClass.Live);
        if (live > 0)
            words.Add($"{live} listening to this node's sound (breaks mirror backup)");
        if (stinging)
            words.Add($"sting {Content.Sting!.Preset.Name} surging");
        items.Add(new Core.Bus.ContextItem("content", "content", "Content", light, string.Join(" · ", words)));
    }

    /// <summary>A generator's inputs are pictures its kind reads; nothing else is refused here (a kind this node does not have is
    /// kept: the show may come from a node that has it).</summary>
    private static void ValidateGenerator(object entity, ref string? why)
    {
        if (why is not null || entity is not Generator g || GeneratorCatalog.Find(g.Kind) is null)
            return;
        var inputs = InputsOf(g.Kind);
        if (g.Inputs.Keys.FirstOrDefault(k => !inputs.Contains(k)) is { } stray)
            why = inputs.Count == 0 ? $"{g.Name} reads no picture ({stray})" : $"{g.Name} reads {string.Join(", ", inputs)}, not {stray}";
    }

    /// <summary>True when a generator's binding reads a source this node's bus evaluates live: used by audio's words.</summary>
    internal static bool DrawsBindingsLive(Project p, string generatorId) =>
        p.FindGenerator(generatorId) is { } g && GeneratorTraits.Of(g.Kind).DrawsBindings;
}
