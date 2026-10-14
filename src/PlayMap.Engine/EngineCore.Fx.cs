using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using PlayMap.Core.Bus;
using PlayMap.Core.Content;
using PlayMap.Core.Effects;
using PlayMap.Core.Model;
using PlayMap.Core.Wire;
using PlayMap.Engine.Fx;
using PlayMap.Render.Effects;

namespace PlayMap.Engine;

/// <summary>wp2-fx's hooks into the engine (EngineCore.Modules.cs): the FX verbs, STATE.fx and each output's fx, the context's
/// fx item, the check of a slot an edit adds, and the INDEX section. Every mutating verb refuses a change that changes nothing,
/// with the reason; a slot's state in STATE is what the renderer observed (compiling, then ok or faulted), never assumed.</summary>
public sealed partial class EngineCore
{
    private const int MaxSlotsPerChain = 16;

    /// <summary>The engine's side of effects: the render host's pass when it has one, and the slot views.</summary>
    internal FxBoard Fx { get; } = new();

    partial void RouteFx(VerbRoutes routes)
    {
        routes.Lane("FX KINDS", c => FxKindsVerb(c.Args));
        routes.Lane("FX LIST", c => FxList(c.Args));
        routes.Lane("FX ADD", c => FxAdd(c.Args));
        routes.Lane("FX REMOVE", c => FxRemove(c.Args));
        routes.Lane("FX MOVE", c => FxMove(c.Args));
        routes.Lane("FX SET", c => FxSet(c.Args));
        routes.Lane("FX BYPASS", c => FxBypass(c.Args));
        routes.Lane("FX RESET", c => FxReset(c.Args));
        routes.OffLane("FX IMPORT", FxImportAsync);
        routes.OffLane("FX CHECK", FxCheckAsync);
        routes.Index(new FxIndex(this));
    }

    // ---- targets and slots ----

    /// <summary>An effect chain's owner by id or name: an output (also by its number), a layer, a canvas, a surface or a playlist
    /// item.</summary>
    private static FxOwner? FxTarget(Project p, string key)
    {
        var owners = FxBoard.Owners(p).ToList();
        if (owners.FirstOrDefault(o => string.Equals(o.Id, key, StringComparison.Ordinal)) is { } byId)
            return byId;
        if (int.TryParse(key, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n >= 1 && n <= p.Outputs.Length)
            return owners.First(o => o.Id == p.Outputs[n - 1].Id);
        return owners.FirstOrDefault(o => o.Name.Equals(key, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>A slot by its id, or "&lt;target&gt;#&lt;n&gt;" (1-based), with its owner and index; null with the reason.</summary>
    private static (FxSlot Slot, FxOwner Owner, int Index)? FxSlotOf(Project p, string key, out string why)
    {
        why = "";
        var hash = key.LastIndexOf('#');
        if (hash > 0)
        {
            var owner = FxTarget(p, key[..hash]);
            if (owner is null)
            {
                why = $"no layer, canvas, surface, output or playlist item {key[..hash]}";
                return null;
            }
            if (!int.TryParse(key[(hash + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) || n < 1 || n > owner.Chain.Slots.Length)
            {
                why = owner.Chain.IsEmpty ? $"{owner.Name} has no effects" : $"{owner.Name} has effects 1 to {owner.Chain.Slots.Length}";
                return null;
            }
            return (owner.Chain.Slots[n - 1], owner, n - 1);
        }
        if (p.FindFx(key) is { } found)
            return (found.Slot, FxBoard.Owners(p).First(o => o.Id == found.OwnerId), found.Index);
        why = $"no effect {key} — FX LIST ALL lists them";
        return null;
    }

    private FxCatalog FxCatalogNow => FxCatalog.For(Edits.Project.FxAssets);

    // ---- read verbs ----

    private CommandReply FxKindsVerb(List<string> args)
    {
        var filter = string.Join(' ', args).Trim();
        var kinds = FxCatalogNow.Kinds.Where(k => filter.Length == 0 || k.Family.Equals(filter, StringComparison.OrdinalIgnoreCase)
            || k.Id.Contains(filter, StringComparison.OrdinalIgnoreCase) || k.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || k.Categories.Any(c => c.Contains(filter, StringComparison.OrdinalIgnoreCase))).ToList();
        if (kinds.Count == 0)
            return CommandReply.Fail($"no effect kind matches '{filter}' — FX KINDS alone lists them all");
        return CommandReply.Success(JsonOut(w =>
        {
            w.WriteStartArray();
            foreach (var k in kinds)
                WriteKind(w, k);
            w.WriteEndArray();
        }));
    }

    private static void WriteKind(Utf8JsonWriter w, FxKind k)
    {
        w.WriteStartObject();
        w.WriteString("id", k.Id);
        w.WriteString("format", k.Format.ToString().ToLowerInvariant());
        w.WriteString("name", k.Name);
        w.WriteString("family", k.Family);
        w.WriteStartArray("categories");
        foreach (var c in k.Categories)
            w.WriteStringValue(c);
        w.WriteEndArray();
        w.WriteString("role", k.Role.ToString().ToLowerInvariant());
        w.WriteString("words", k.Words);
        w.WriteStartArray("params");
        foreach (var p in k.Params)
        {
            w.WriteStartObject();
            w.WriteString("name", p.Name);
            w.WriteString("type", p.Type.ToString().ToLowerInvariant());
            w.WriteString("default", p.Default);
            if (p.Type is ParamType.Number or ParamType.Integer)
            {
                w.WriteNumber("min", p.Min);
                w.WriteNumber("max", p.Max);
            }
            if (p.Choices.Count > 0)
            {
                w.WriteStartArray("choices");
                foreach (var c in p.Choices)
                    w.WriteStringValue(c);
                w.WriteEndArray();
            }
            w.WriteString("words", p.Words);
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteStartArray("images");
        foreach (var i in k.ImageInputs)
            w.WriteStringValue(i);
        w.WriteEndArray();
        w.WriteStartObject("traits");
        w.WriteBoolean("history", k.Traits.HistoryDependent);
        w.WriteBoolean("audio", k.Traits.ReadsAudio);
        w.WriteBoolean("geometry", k.Traits.GeometryAware);
        w.WriteBoolean("native", k.Traits.NativeCode);
        w.WriteBoolean("mayFlash", k.Traits.MayFlash);
        w.WriteEndObject();
        w.WriteEndObject();
    }

    private CommandReply FxList(List<string> args)
    {
        if (args.Count < 1)
            return CommandReply.Fail("FX LIST <target>|ALL");
        var p = Edits.Project;
        var catalog = FxCatalog.For(p.FxAssets);
        List<FxOwner> owners;
        if (args[0].Equals("ALL", StringComparison.OrdinalIgnoreCase))
            owners = [.. FxBoard.Owners(p).Where(o => !o.Chain.IsEmpty)];
        else if (FxTarget(p, string.Join(' ', args)) is { } one)
            owners = [one];
        else
            return CommandReply.Fail($"no layer, canvas, surface, output or playlist item {string.Join(' ', args)}");
        return CommandReply.Success(JsonOut(w =>
        {
            w.WriteStartArray();
            foreach (var o in owners)
            {
                w.WriteStartObject();
                w.WriteString("owner", o.Id);
                w.WriteString("ownerKind", o.Kind);
                w.WriteString("name", o.Name);
                w.WriteStartArray("slots");
                for (var i = 0; i < o.Chain.Slots.Length; i++)
                    FxBoard.WriteSlot(w, Fx.View(o.Chain.Slots[i], o, i, catalog));
                w.WriteEndArray();
                w.WriteEndObject();
            }
            w.WriteEndArray();
        }));
    }

    // ---- edit verbs ----

    private CommandReply FxAdd(List<string> args)
    {
        if (args.Count < 2)
            return CommandReply.Fail("FX ADD <target> <kind> [AT <position>] [<name>]");
        var p = Edits.Project;
        var owner = FxTarget(p, args[0]);
        if (owner is null)
            return CommandReply.Fail($"no layer, canvas, surface, output or playlist item {args[0]}");
        var kind = FxCatalog.For(p.FxAssets).FindLoose(args[1]);
        if (kind is null)
            return CommandReply.Fail($"no effect kind '{args[1]}' — FX KINDS lists them");
        var rest = args.Skip(2).ToList();
        var at = owner.Chain.Slots.Length;
        if (rest.Count >= 2 && rest[0].Equals("AT", StringComparison.OrdinalIgnoreCase))
        {
            if (!int.TryParse(rest[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var pos) || pos < 1 || pos > owner.Chain.Slots.Length + 1)
                return CommandReply.Fail($"AT is a position from 1 to {owner.Chain.Slots.Length + 1}");
            at = pos - 1;
            rest.RemoveRange(0, 2);
        }
        if (owner.Chain.Slots.Length >= MaxSlotsPerChain)
            return CommandReply.Fail($"{owner.Name} has {MaxSlotsPerChain} effects, the most a chain holds");
        var name = string.Join(' ', rest).Trim();
        var id = "";
        var r = Edits.Apply($"add {kind.Name} to {owner.Name}", pp =>
        {
            id = pp.NewId(Ids.Fx);
            var slot = new FxSlot(id, kind.Id, name, ParamSet.Empty, false, 1, FxBlend.Normal, 1, LeaderNowNs);
            return WithChain(pp, owner.Id, c => c with { Slots = c.Slots.Insert(Math.Min(at, c.Slots.Length), slot) });
        });
        if (!r.Changed)
            return FromEdit(r);
        var drawn = owner.Kind == "output"
            ? owner.Drawn ? " — compiling" : " — its output is off"
            : $"; this build draws effects on outputs only, so the {owner.Kind}'s chain is kept, not drawn";
        return CommandReply.Success(id, $"added {kind.Name} to {owner.Kind} {owner.Name}{drawn}");
    }

    private CommandReply FxRemove(List<string> args)
    {
        if (args.Count < 1)
            return CommandReply.Fail("FX REMOVE <slot>");
        if (FxSlotOf(Edits.Project, args[0], out var why) is not { } found)
            return CommandReply.Fail(why);
        var (slot, owner, _) = found;
        return FromEdit(Edits.Apply($"remove {FxLabel(slot)} from {owner.Name}",
            pp => WithChain(pp, owner.Id, c => c with { Slots = c.Slots.RemoveId(slot.Id) })));
    }

    private CommandReply FxMove(List<string> args)
    {
        if (args.Count < 2)
            return CommandReply.Fail("FX MOVE <slot> <position>");
        if (FxSlotOf(Edits.Project, args[0], out var why) is not { } found)
            return CommandReply.Fail(why);
        var (slot, owner, index) = found;
        var count = owner.Chain.Slots.Length;
        if (!int.TryParse(args[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var pos) || pos < 1 || pos > count)
            return CommandReply.Fail($"a position in {owner.Name}'s chain is 1 to {count}");
        if (pos - 1 == index)
            return CommandReply.Fail($"{FxLabel(slot)} is already at position {pos}");
        return FromEdit(Edits.Apply($"move {FxLabel(slot)} to {pos}", pp => WithChain(pp, owner.Id, c => c with { Slots = c.Slots.Move(slot.Id, pos - 1) })));
    }

    private CommandReply FxSet(List<string> args)
    {
        if (args.Count < 3)
            return CommandReply.Fail("FX SET <slot> <param>|mix|blend|scale|name <value>, or FX SET <slot> <param> BY <delta>");
        var p = Edits.Project;
        if (FxSlotOf(p, args[0], out var why) is not { } found)
            return CommandReply.Fail(why);
        var (slot, owner, _) = found;
        var field = args[1];
        var by = args[2].Equals("BY", StringComparison.OrdinalIgnoreCase);
        var value = string.Join(' ', args.Skip(by ? 3 : 2)).Trim();
        if (by && !double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
            return CommandReply.Fail("BY takes a number: a fraction of the parameter's range, or steps of a choice");
        FxSlot changed;
        string label;
        switch (field.ToLowerInvariant())
        {
            case "mix":
            case "scale":
                {
                    var (min, max, current) = field.Equals("mix", StringComparison.OrdinalIgnoreCase) ? (0.0, 1.0, slot.Mix) : (0.25, 1.0, slot.Scale);
                    if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) || !double.IsFinite(v))
                        return CommandReply.Fail($"{field.ToLowerInvariant()} is a number from {min} to {max}");
                    var next = Math.Round(Math.Clamp(by ? current + v * (max - min) : v, min, max), 6);
                    if (Math.Abs(next - current) < 1e-9)
                        return CommandReply.Fail($"{FxLabel(slot)}'s {field.ToLowerInvariant()} is already {current.ToString("0.###", CultureInfo.InvariantCulture)}");
                    changed = field.Equals("mix", StringComparison.OrdinalIgnoreCase) ? slot with { Mix = next } : slot with { Scale = next };
                    label = $"{field.ToLowerInvariant()} {next.ToString("0.###", CultureInfo.InvariantCulture)}";
                    break;
                }
            case "blend":
                {
                    if (by || !Enum.TryParse<FxBlend>(value, ignoreCase: true, out var blend) || !Enum.IsDefined(blend) || int.TryParse(value, out _))
                        return CommandReply.Fail("blend is normal, add, multiply, screen or difference");
                    if (blend == slot.Blend)
                        return CommandReply.Fail($"{FxLabel(slot)}'s blend is already {blend.ToString().ToLowerInvariant()}");
                    changed = slot with { Blend = blend };
                    label = "blend " + blend.ToString().ToLowerInvariant();
                    break;
                }
            case "name":
                {
                    if (by)
                        return CommandReply.Fail("a name takes no BY");
                    if (string.Equals(value, slot.Name, StringComparison.Ordinal))
                        return CommandReply.Fail($"the effect is already named '{value}'");
                    changed = slot with { Name = value };
                    label = $"name '{value}'";
                    break;
                }
            default:
                {
                    var kind = FxCatalog.For(p.FxAssets).Find(slot.Kind);
                    if (kind is null)
                        return CommandReply.Fail($"this node has no effect {slot.Kind}, so its parameters are not known");
                    var spec = kind.Param(field);
                    if (spec is null)
                        return CommandReply.Fail($"{kind.Name} has no parameter {field}{(kind.Params.IsEmpty ? "" : " — it has " + string.Join(", ", kind.Params.Select(x => x.Name)))}, and every effect has mix, blend, scale and name");
                    var current = kind.Value(slot.Params, spec.Name);
                    string normalised;
                    if (by)
                    {
                        if (!TryStep(spec, current, double.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture), out normalised, out var stepWhy))
                            return CommandReply.Fail(stepWhy);
                    }
                    else if (!spec.TryNormalise(value, out normalised, out var invalid))
                    {
                        return CommandReply.Fail(invalid);
                    }
                    if (string.Equals(current, normalised, StringComparison.Ordinal))
                        return CommandReply.Fail($"{spec.Name} is already {normalised}");
                    changed = slot with { Params = slot.Params.With(spec.Name, normalised) };
                    label = $"{spec.Name} {normalised}";
                    break;
                }
        }
        return FromEdit(Edits.Apply($"{FxLabel(slot)} {label}", pp => WithChain(pp, owner.Id, c => c with { Slots = c.Slots.Update(slot.Id, _ => changed) }),
            coalesceKey: $"fx {slot.Id} {field.ToLowerInvariant()}"));
    }

    /// <summary>A parameter moved by a delta: numbers by that fraction of their range, choices by that many steps (clamped at the
    /// ends); flags, colours and text take no BY.</summary>
    private static bool TryStep(ParamSpec spec, string current, double delta, out string normalised, out string why)
    {
        why = "";
        normalised = current;
        switch (spec.Type)
        {
            case ParamType.Number or ParamType.Integer:
                {
                    var now = double.TryParse(current, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0;
                    var next = now + delta * (spec.Max - spec.Min);
                    if (spec.Type == ParamType.Integer && Math.Abs(next - now) < 1)
                        next = now + Math.Sign(delta);
                    return spec.TryNormalise(next.ToString("R", CultureInfo.InvariantCulture), out normalised, out why);
                }
            case ParamType.Choice:
                {
                    var i = spec.Choices.ToList().FindIndex(c => c.Equals(current, StringComparison.OrdinalIgnoreCase));
                    var steps = (int)Math.Round(delta);
                    normalised = spec.Choices[Math.Clamp(Math.Max(i, 0) + steps, 0, spec.Choices.Count - 1)];
                    return true;
                }
            default:
                why = $"{spec.Name} takes a value, not BY";
                return false;
        }
    }

    private CommandReply FxBypass(List<string> args)
    {
        if (args.Count < 2)
            return CommandReply.Fail("FX BYPASS <slot>|<target>|ALL ON|OFF|TOGGLE");
        var mode = args[^1].ToUpperInvariant();
        if (mode is not ("ON" or "OFF" or "TOGGLE"))
            return CommandReply.Fail("FX BYPASS … ends ON, OFF or TOGGLE");
        var key = string.Join(' ', args.Take(args.Count - 1));
        var p = Edits.Project;
        List<FxSlot> slots;
        string what;
        if (key.Equals("ALL", StringComparison.OrdinalIgnoreCase))
        {
            slots = [.. FxBoard.Owners(p).SelectMany(o => o.Chain.Slots)];
            what = "every effect";
        }
        else if (FxSlotOf(p, key, out _) is { } one)
        {
            slots = [one.Slot];
            what = FxLabel(one.Slot);
        }
        else if (FxTarget(p, key) is { } owner)
        {
            slots = [.. owner.Chain.Slots];
            what = $"{owner.Name}'s effects";
        }
        else
        {
            return CommandReply.Fail($"no effect, layer, canvas, surface, output or playlist item {key}");
        }
        if (slots.Count == 0)
            return CommandReply.Fail(key.Equals("ALL", StringComparison.OrdinalIgnoreCase) ? "the show has no effects" : $"{key} has no effects");
        var flips = slots.Where(s => mode == "TOGGLE" || s.Bypass != (mode == "ON")).Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        if (flips.Count == 0)
            return CommandReply.Fail(mode == "ON" ? $"{what} {(slots.Count == 1 ? "is" : "are")} already bypassed" : $"{what} {(slots.Count == 1 ? "is" : "are")} not bypassed");
        var r = Edits.Apply($"bypass {what} {mode.ToLowerInvariant()}", pp =>
        {
            foreach (var owner in FxBoard.Owners(pp).Where(o => o.Chain.Slots.Any(s => flips.Contains(s.Id))).ToList())
                pp = WithChain(pp, owner.Id, c => c with { Slots = [.. c.Slots.Select(s => flips.Contains(s.Id) ? s with { Bypass = !s.Bypass } : s)] });
            return pp;
        });
        if (!r.Changed)
            return FromEdit(r);
        var count = flips.Count.ToString(CultureInfo.InvariantCulture);
        return CommandReply.Success(count, $"{(mode == "TOGGLE" ? "toggled" : mode == "ON" ? "bypassed" : "brought back")} {count} effect{(flips.Count == 1 ? "" : "s")}");
    }

    private CommandReply FxReset(List<string> args)
    {
        if (args.Count < 1)
            return CommandReply.Fail("FX RESET <slot>");
        var p = Edits.Project;
        if (FxSlotOf(p, args[0], out var why) is not { } found)
            return CommandReply.Fail(why);
        var (slot, owner, _) = found;
        var kind = FxCatalog.For(p.FxAssets).Find(slot.Kind);
        if (slot.Params.Values.IsEmpty || (kind is not null && kind.AtDefaults(slot.Params)))
            return CommandReply.Fail($"{FxLabel(slot)}'s parameters are already at their defaults");
        return FromEdit(Edits.Apply($"reset {FxLabel(slot)}", pp => WithChain(pp, owner.Id, c => c with { Slots = c.Slots.Update(slot.Id, s => s with { Params = ParamSet.Empty }) })));
    }

    private string FxLabel(FxSlot slot) => slot.Name.Length > 0 ? slot.Name : FxCatalogNow.Find(slot.Kind)?.Name ?? slot.Kind;

    // ---- off-lane verbs: files, the compile lane ----

    private async Task<CommandReply> FxImportAsync(VerbCall call)
    {
        if (call.Args.Count < 1)
            return CommandReply.Fail("FX IMPORT <path>|<json>");
        var text = JoinRaw(call);
        if (FxSources.Read(text, out var why) is not { } bundle)
            return CommandReply.Fail(why);
        var doc = IsfParser.Parse(bundle.Fragment);
        if (!doc.Valid)
            return CommandReply.Fail($"the file does not read as ISF: line {doc.Issues[0].Line}: {doc.Issues[0].Words}");
        var missing = doc.Imported.Select(i => i.Name).Where(n => !bundle.Images.ContainsKey(n)).ToList();
        if (missing.Count > 0)
            return CommandReply.Fail($"the file imports {string.Join(", ", missing)}: give it as \"images\" in the JSON form, or import the file from its folder");
        var hash = FxCatalog.ContentHash(bundle.Fragment, bundle.Vertex);
        var name = bundle.Name.Length > 0 ? bundle.Name : doc.Description.Length is > 0 and <= 40 ? doc.Description : "ISF effect";
        var (ok, report) = await FxReportAsync(doc, bundle, call.Ct).ConfigureAwait(false);
        var id = "";
        var r = await EditAsync(call.Session, $"import effect {name}", p =>
        {
            if (p.FxAssets.FirstOrDefault(a => a.ContentHash == hash) is { } same)
                throw new EditRefusedException($"the show has this file already, as {same.Id} ({same.Name})");
            id = p.NewId(Ids.FxAsset);
            var asset = new FxAsset(id, name, FxFormat.Isf, bundle.Path, hash)
            {
                Source = bundle.Fragment,
                VertexSource = bundle.Vertex ?? "",
                Images = bundle.Images,
            };
            return p with { FxAssets = p.FxAssets.Add(asset) };
        }).ConfigureAwait(false);
        if (!r.Changed)
            return FromEdit(r);
        var payload = JsonOut(w =>
        {
            w.WriteStartObject();
            w.WriteString("id", id);
            w.WriteString("kind", FxCatalog.AssetKindId(id));
            w.WritePropertyName("check");
            using var check = JsonDocument.Parse(report);
            check.RootElement.WriteTo(w);
            w.WriteEndObject();
        });
        return CommandReply.Success(payload, $"imported {name} as {FxCatalog.AssetKindId(id)}{(ok ? "" : " — its check found problems")}");
    }

    private async Task<CommandReply> FxCheckAsync(VerbCall call)
    {
        if (call.Args.Count < 1)
            return CommandReply.Fail("FX CHECK <asset>|<json>");
        var text = JoinRaw(call);
        FxBundle bundle;
        var p = Store.Project;
        var catalog = FxCatalog.For(p.FxAssets);
        var key = text.Trim();
        if (catalog.FindLoose(key.StartsWith("fa", StringComparison.Ordinal) ? FxCatalog.AssetKindId(key) : key) is { } kind && catalog.Source(kind.Id) is { } source)
            bundle = new FxBundle(kind.Name, source.Fragment, source.Vertex, source.Images, "");
        else if (FxSources.Read(text, out var why) is { } read)
            bundle = read;
        else
            return CommandReply.Fail(key.StartsWith('{') ? why : $"no effect or asset {key}, and {why}");
        var doc = IsfParser.Parse(bundle.Fragment);
        var (_, report) = await FxReportAsync(doc, bundle, call.Ct).ConfigureAwait(false);
        return CommandReply.Success(report);
    }

    /// <summary>The file's report: parsed and translated for both dialects here, compiled on this node's context through the
    /// compile lane (off the control lane), with a 10 s bound.</summary>
    private async Task<(bool Ok, string Json)> FxReportAsync(IsfDocument doc, FxBundle bundle, CancellationToken ct)
    {
        var glsl = doc.Valid ? IsfTranslator.Translate(doc, bundle.Vertex, IsfDialect.Glsl330) : null;
        var es = doc.Valid ? IsfTranslator.Translate(doc, bundle.Vertex, IsfDialect.Es300) : null;
        FxCompileReport? compiled = null;
        var esContext = false;
        if (doc.Valid && Fx.Pass is { } pass && glsl is { Issues.IsEmpty: true })
        {
            esContext = pass.Lane.Dialect == IsfDialect.Es300;
            var src = esContext ? es! : glsl;
            var check = pass.Lane.CheckAsync("check " + bundle.Name, src.Vertex, src.Fragment);
            var done = await Task.WhenAny(check, Task.Delay(TimeSpan.FromSeconds(10), ct)).ConfigureAwait(false);
            compiled = done == check ? await check.ConfigureAwait(false) : new FxCompileReport(false, "the compile lane did not answer in 10 s", pass.Lane.Mode);
        }
        return FxSources.Report(doc, glsl, es, compiled, esContext, Fx.LaneMode);
    }

    /// <summary>The words after the verb: a JSON object is one word to the end of the line, a quoted path one word.</summary>
    private static string JoinRaw(VerbCall call) => string.Join(' ', call.Args);

    // ---- STATE, context, validation ----

    partial void StateFx(Utf8JsonWriter w)
    {
        var p = Store.Project;
        var catalog = FxCatalog.For(p.FxAssets);
        var views = Fx.Views(p);
        w.WriteStartObject("fx");
        w.WriteNumber("budgetMs", Services.Settings.Fx.BudgetMs);
        w.WriteStartObject("library");
        w.WriteNumber("builtin", catalog.BuiltinCount);
        w.WriteNumber("isf", catalog.IsfCount);
        w.WriteNumber("ffgl", 0);
        w.WriteNumber("quarantined", 0);
        w.WriteEndObject();
        w.WriteStartObject("compile");
        w.WriteString("lane", Fx.LaneMode);
        w.WriteNumber("pending", Fx.Pass?.Lane.Pending ?? 0);
        w.WriteString("words", Fx.Pass is null ? "no renderer on this node" : Fx.Pass.Lane.Words);
        w.WriteEndObject();
        w.WriteStartArray("slots");
        foreach (var v in views)
            FxBoard.WriteSlot(w, v);
        w.WriteEndArray();
        w.WriteStartObject("plugins");
        w.WriteBoolean("enabled", false);
        w.WriteNumber("inProcess", 0);
        w.WriteString("words", "FFGL plugins are not hosted by this build");
        w.WriteEndObject();
        w.WriteEndObject();
    }

    partial void OutputFx(Utf8JsonWriter w, Output o)
    {
        if (o.Fx.IsEmpty)
        {
            w.WriteNull("fx");
            return;
        }
        var catalog = FxCatalog.For(Store.Project.FxAssets);
        var owner = new FxOwner(o.Id, "output", o.Name, o.Fx, o.Enabled);
        var views = o.Fx.Slots.Select((s, i) => Fx.View(s, owner, i, catalog)).ToList();
        var (cost, light) = FxBoard.Cost(views, Services.Settings.Fx.BudgetMs);
        w.WriteStartObject("fx");
        w.WriteNumber("slots", views.Count);
        w.WriteNumber("costMs", Math.Round(cost, 3));
        w.WriteString("light", light.ToString().ToLowerInvariant());
        w.WriteEndObject();
    }

    partial void ContextFx(List<ContextItem> items, List<ContextLink> links)
    {
        var p = Store.Project;
        var views = Fx.Views(p);
        if (views.Count == 0)
            return;
        var budget = Services.Settings.Fx.BudgetMs;
        var light = Light.Green;
        var words = new List<string> { $"{views.Count} effect{(views.Count == 1 ? "" : "s")}" };
        foreach (var group in views.Where(v => v.Owner.Kind == "output").GroupBy(v => v.Owner.Id))
        {
            var (cost, l) = FxBoard.Cost(group, budget);
            if (l == Light.Red || (l == Light.Amber && light != Light.Red))
                light = l;
            if (cost > budget)
                words.Add($"{group.First().Owner.Name} {cost.ToString("0.0", CultureInfo.InvariantCulture)} ms of {budget.ToString("0.#", CultureInfo.InvariantCulture)}");
        }
        foreach (var bad in views.Where(v => v.State is "faulted" or "missing"))
        {
            if (light != Light.Red)
                light = Light.Amber;
            words.Add($"{bad.Label} {bad.State}: {bad.Words}");
        }
        var history = views.Count(v => !v.Deterministic && v.Kind is not null && v.State != "bypassed");
        if (history > 0)
            words.Add($"{history} not frame-exact across nodes (breaks mirror backup)");
        if (views.All(v => v.State is "idle" or "bypassed"))
            light = Light.Grey;
        items.Add(new ContextItem("fx", "fx", "Effects", light, string.Join(" · ", words)));
    }

    partial void ValidateFx(Project p, object entity, ref string? why)
    {
        // A kind this node does not have is kept (the show may come from a node that has it) and shows as missing in STATE;
        // a parameter its kind does not have is refused.
        if (why is not null || entity is not FxSlot slot || FxCatalog.For(p.FxAssets).Find(slot.Kind) is not { } kind)
            return;
        if (slot.Params.Values.Keys.FirstOrDefault(k => kind.Param(k) is null) is { } stray)
            why = $"{kind.Name} has no parameter {stray}";
    }
}
