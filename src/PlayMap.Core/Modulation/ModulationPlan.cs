using System.Collections.Immutable;
using System.Globalization;
using PlayMap.Core.Content;
using PlayMap.Core.Model;

namespace PlayMap.Core.Modulation;

/// <summary>What can be modulated: a generator's numbers, a layer's opacity and rectangle, a surface's opacity, an effect slot's mix
/// and its numbers.</summary>
public enum ModEntityKind { Generator, Layer, Surface, Fx }

/// <summary>An entity that holds bindings: its kind, id, name for words, bindings, and where it lives (a layer's canvas, an effect
/// slot's owner).</summary>
public sealed record ModEntity(ModEntityKind Kind, string Id, string Name, ImmutableArray<ParamBinding> Bindings, string ParentId)
{
    public string KindWord => Kind.ToString().ToLowerInvariant();
}

/// <summary>A number's range and the value the show keeps for it.</summary>
public readonly record struct ParamRange(double Min, double Max, double Base);

/// <summary>An effect kind's numeric parameters (its range and default), for binding slot parameters other than mix: the effects
/// package answers it from its catalogue. Null: a slot binds its mix only.</summary>
public interface IFxParamRanges
{
    bool TryRange(string kind, string param, out double min, out double max, out double defaultValue);
}

/// <summary>Finds bindable entities and their numbers in a project (pure).</summary>
public static class ModTargets
{
    public static readonly ImmutableArray<string> LayerParams = ["opacity", "rect.x", "rect.y", "rect.w", "rect.h"];

    /// <summary>The entity a key names: a generator (id or name), a layer, a surface (id or name) or an effect slot (id).</summary>
    public static ModEntity? Find(Project p, string key)
    {
        ArgumentNullException.ThrowIfNull(p);
        ArgumentNullException.ThrowIfNull(key);
        foreach (var e in All(p))
            if (string.Equals(e.Id, key, StringComparison.Ordinal))
                return e;
        return All(p).FirstOrDefault(e => e.Kind is ModEntityKind.Generator or ModEntityKind.Surface or ModEntityKind.Layer
            && e.Name.Equals(key, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Every entity that can hold bindings.</summary>
    public static IEnumerable<ModEntity> All(Project p)
    {
        ArgumentNullException.ThrowIfNull(p);
        return Walk(p);
    }

    private static IEnumerable<ModEntity> Walk(Project p)
    {
        foreach (var g in p.Generators)
            yield return new ModEntity(ModEntityKind.Generator, g.Id, g.Name, g.Bindings, "");
        foreach (var c in p.Canvases)
        {
            foreach (var l in c.Layers)
                yield return new ModEntity(ModEntityKind.Layer, l.Id, l.Name, l.Bindings, c.Id);
            foreach (var s in c.Fx.Slots)
                yield return Slot(s, c.Id);
            foreach (var l in c.Layers)
                foreach (var s in l.Fx.Slots)
                    yield return Slot(s, l.Id);
        }
        foreach (var sf in p.Stage.Surfaces)
        {
            yield return new ModEntity(ModEntityKind.Surface, sf.Id, sf.Name, sf.Bindings, "");
            foreach (var s in sf.Fx.Slots)
                yield return Slot(s, sf.Id);
        }
        foreach (var o in p.Outputs)
            foreach (var s in o.Fx.Slots)
                yield return Slot(s, o.Id);
        foreach (var l in p.Playlists)
            foreach (var it in l.Items)
                foreach (var s in it.Fx.Slots)
                    yield return Slot(s, it.Id);
    }

    private static ModEntity Slot(FxSlot s, string owner) =>
        new(ModEntityKind.Fx, s.Id, s.Name.Length > 0 ? s.Name : s.Kind, s.Bindings, owner);

    /// <summary>A bindable number of an entity: its range and the show's value; the reason when the parameter is not one (unknown,
    /// or not a number — a seed, a count, a choice or a colour does not take modulation).</summary>
    public static bool TryParam(Project p, ModEntity e, string param, IFxParamRanges? fx, out ParamRange range, out string canonical, out string why)
    {
        ArgumentNullException.ThrowIfNull(p);
        ArgumentNullException.ThrowIfNull(e);
        range = default;
        canonical = param.ToLowerInvariant();
        why = "";
        switch (e.Kind)
        {
            case ModEntityKind.Generator:
                return TryGenerator(p.FindGenerator(e.Id)!, param, out range, out canonical, out why);
            case ModEntityKind.Layer:
                {
                    var canvas = p.FindCanvas(e.ParentId)!;
                    var layer = canvas.Layers.Find(e.Id)!;
                    (double, double, double)? r = canonical switch
                    {
                        "opacity" => (0, 1, layer.Opacity),
                        "rect.x" => (-canvas.Width, canvas.Width, layer.Rect.X),
                        "rect.y" => (-canvas.Height, canvas.Height, layer.Rect.Y),
                        "rect.w" => (0, 2.0 * canvas.Width, layer.Rect.W),
                        "rect.h" => (0, 2.0 * canvas.Height, layer.Rect.H),
                        _ => null,
                    };
                    if (r is not { } v)
                    {
                        why = $"a layer modulates {string.Join(", ", LayerParams)}, not '{param}'";
                        return false;
                    }
                    range = new ParamRange(v.Item1, v.Item2, v.Item3);
                    return true;
                }
            case ModEntityKind.Surface:
                if (canonical != "opacity")
                {
                    why = $"a surface modulates its opacity, not '{param}'";
                    return false;
                }
                range = new ParamRange(0, 1, p.FindSurface(e.Id)!.Opacity);
                return true;
            default:
                return TryFx(p, e, param, fx, out range, out canonical, out why);
        }
    }

    private static bool TryGenerator(Generator g, string param, out ParamRange range, out string canonical, out string why)
    {
        range = default;
        canonical = param;
        why = "";
        if (GeneratorCatalog.Find(g.Kind) is not { } kind)
        {
            why = $"this node has no generator kind {g.Kind}, so its parameters are not known";
            return false;
        }
        if (kind.Param(param) is not { } spec)
        {
            why = $"{kind.Name} has no parameter '{param}'" + (kind.Params.Count == 0 ? "" : " — its numbers are "
                + string.Join(", ", kind.Params.Where(x => x.Type == ParamType.Number).Select(x => x.Name)));
            return false;
        }
        canonical = spec.Name;
        if (spec.Type != ParamType.Number)
        {
            why = $"{spec.Name} is {spec.Type.ToString().ToLowerInvariant()}, not a continuous number — it does not take modulation";
            return false;
        }
        range = new ParamRange(spec.Min, spec.Max, ParamValues.Number(g.Params, kind, spec.Name));
        return true;
    }

    private static bool TryFx(Project p, ModEntity e, string param, IFxParamRanges? fx, out ParamRange range, out string canonical, out string why)
    {
        range = default;
        canonical = param.ToLowerInvariant();
        why = "";
        var slot = p.FindFx(e.Id)!.Value.Slot;
        if (canonical == "mix")
        {
            range = new ParamRange(0, 1, slot.Mix);
            return true;
        }
        if (fx is null || !fx.TryRange(slot.Kind, param, out var min, out var max, out var def))
        {
            why = fx is null
                ? $"an effect's own parameters bind once this node's effects answer their ranges; its mix binds now"
                : $"{slot.Kind} has no number '{param}' — its mix and its numeric parameters bind";
            return false;
        }
        canonical = param;
        var value = slot.Params.Values.TryGetValue(param, out var text)
            && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : def;
        range = new ParamRange(min, max, value);
        return true;
    }

    /// <summary>The project with an entity's bindings replaced.</summary>
    public static Project WithBindings(Project p, ModEntity e, ImmutableArray<ParamBinding> bindings)
    {
        ArgumentNullException.ThrowIfNull(p);
        ArgumentNullException.ThrowIfNull(e);
        return e.Kind switch
        {
            ModEntityKind.Generator => p with { Generators = p.Generators.Update(e.Id, g => g with { Bindings = bindings }) },
            ModEntityKind.Layer => p with
            {
                Canvases = p.Canvases.Update(e.ParentId, c => c with { Layers = c.Layers.Update(e.Id, l => l with { Bindings = bindings }) }),
            },
            ModEntityKind.Surface => p.WithStage(s => s with { Surfaces = s.Surfaces.Update(e.Id, x => x with { Bindings = bindings }) }),
            _ => WithSlot(p, e.ParentId, e.Id, s => s with { Bindings = bindings }),
        };
    }

    /// <summary>The project with one effect slot changed, wherever its chain is.</summary>
    public static Project WithSlot(Project p, string ownerId, string slotId, Func<FxSlot, FxSlot> edit)
    {
        ArgumentNullException.ThrowIfNull(p);
        FxChain C(FxChain chain) => chain with { Slots = chain.Slots.Update(slotId, edit) };
        if (p.FindOutput(ownerId) is not null)
            return p with { Outputs = p.Outputs.Update(ownerId, o => o with { Fx = C(o.Fx) }) };
        if (p.FindSurface(ownerId) is not null)
            return p.WithStage(s => s with { Surfaces = s.Surfaces.Update(ownerId, x => x with { Fx = C(x.Fx) }) });
        if (p.FindCanvas(ownerId) is not null)
            return p with { Canvases = p.Canvases.Update(ownerId, c => c with { Fx = C(c.Fx) }) };
        foreach (var c in p.Canvases)
            if (c.Layers.Find(ownerId) is not null)
                return p with { Canvases = p.Canvases.Update(c.Id, cc => cc with { Layers = cc.Layers.Update(ownerId, l => l with { Fx = C(l.Fx) }) }) };
        foreach (var l in p.Playlists)
            if (l.Items.Find(ownerId) is not null)
                return p with { Playlists = p.Playlists.Update(l.Id, ll => ll with { Items = ll.Items.Update(ownerId, it => it with { Fx = C(it.Fx) }) }) };
        return p;
    }

    /// <summary>The source key a binding reads: its source, with the binding's smoothing when the source carries none.</summary>
    public static string SourceKey(ParamBinding b)
    {
        ArgumentNullException.ThrowIfNull(b);
        if (b.Source.Contains('~', StringComparison.Ordinal))
            return b.Source;
        var attack = b.AttackSeconds >= 0 ? b.AttackSeconds : b.SmoothSeconds;
        var release = b.ReleaseSeconds >= 0 ? b.ReleaseSeconds : b.SmoothSeconds;
        if (attack <= 0 && release <= 0)
            return b.Source;
        return string.Create(CultureInfo.InvariantCulture, $"{b.Source}~{Math.Max(0, attack) * 1000:0.###}/{Math.Max(0, release) * 1000:0.###}");
    }
}

/// <summary>One binding of a plan: the entity, the binding, the number's range and base, the source it reads, and why it does not
/// apply ("" when it does).</summary>
public sealed record PlanItem(ModEntity Entity, ParamBinding Binding, string Param, ParamRange Range, string SourceKey, ModSource? Source, string Problem);

/// <summary>Every binding of one project, resolved once per project (ranges, bases, sources) and evaluated per frame: the project
/// handed to the renderer has each bound number of a layer, a surface or an effect slot at its modulated value and those bindings
/// taken off (nothing downstream modulates them again); a generator keeps its numbers and its bindings for its own render path
/// (<see cref="DrawsLive"/>). The project itself is never written: the store keeps the base values, and the revision does not
/// move. A project without bindings is handed on as it is; bypassed, the plan hands on the base values with every binding taken
/// off (one copy per project, kept).</summary>
public sealed class ModulationPlan
{
    private readonly ImmutableArray<Group> _groups;
    private Project? _bypassed;

    private ModulationPlan(Project project, ImmutableArray<PlanItem> items)
    {
        Project = project;
        Items = items;
        _groups = [.. items.Select((x, i) => (x, i)).GroupBy(t => (t.x.Entity.Kind, t.x.Entity.Id))
            .Select(g => new Group(g.First().x.Entity, [.. g.Select(t => t.i)]))];
    }

    public Project Project { get; }

    public ImmutableArray<PlanItem> Items { get; }

    public static readonly ModulationPlan Empty = new(Model.Project.CreateDefault(""), []);

    /// <summary>Resolves every binding of the project.</summary>
    public static ModulationPlan For(Project p, IFxParamRanges? fx = null)
    {
        ArgumentNullException.ThrowIfNull(p);
        var items = ImmutableArray.CreateBuilder<PlanItem>();
        foreach (var e in ModTargets.All(p))
        {
            foreach (var b in e.Bindings)
            {
                var problem = "";
                ModSource? source = null;
                if (!ModTargets.TryParam(p, e, b.Param, fx, out var range, out var param, out var why))
                    problem = why;
                else if (!ModSource.TryParse(ModTargets.SourceKey(b), out var parsed, out var sourceWhy))
                    problem = sourceWhy;
                else
                    source = parsed;
                if (problem.Length == 0 && b.Mode == BindingMode.Rate)
                    problem = "RATE bindings are not evaluated on this node; the number holds its base value";
                items.Add(new PlanItem(e, b, problem.Length == 0 ? param : b.Param, range, ModTargets.SourceKey(b), source, problem));
            }
        }
        return new ModulationPlan(p, items.ToImmutable());
    }

    /// <summary>The live value of item <paramref name="i"/> at an instant (its base when it does not apply, is disabled, bypassed
    /// or its source is missing), and the source's value (NaN when missing).</summary>
    public (double Value, double Source) Evaluate(int i, IModulationBus bus, long leaderNs, bool bypassed = false)
    {
        ArgumentNullException.ThrowIfNull(bus);
        var item = Items[i];
        if (item.Problem.Length > 0)
            return (item.Range.Base, double.NaN);
        if (!bus.TryRead(item.SourceKey, leaderNs, out var s))
            return (item.Range.Base, double.NaN);
        if (bypassed || !item.Binding.Enabled)
            return (item.Range.Base, s);
        return (BindingEval.Apply(item.Binding, s, item.Range.Base, item.Range.Min, item.Range.Max), s);
    }

    /// <summary>False for a generator: a generator renderer re-reads its whole parameter set whenever a number changes (a particle
    /// field re-seeds and settles, a flash guard forgets its last flash), so a number rewritten every frame would restart it every
    /// frame and let it flash unguarded. Its bindings stay on the generator in the drawn project, for its renderer to evaluate on
    /// its own render path through the bus (the frame's <c>Modulation</c>); this plan evaluates them for MOD PEEK and STATE only.
    /// Layers, surfaces and effect slots take their live numbers from the drawn project.</summary>
    public static bool DrawsLive(ModEntityKind kind) => kind != ModEntityKind.Generator;

    /// <summary>The project the renderer draws at <paramref name="leaderNs"/>.</summary>
    /// <param name="bypassAll">MOD BYPASS ON: every number at its base.</param>
    /// <param name="bypassed">Entities held at their base (MOD BYPASS ON &lt;entity&gt;).</param>
    public Project Apply(IModulationBus bus, long leaderNs, bool bypassAll, IReadOnlySet<string>? bypassed = null)
    {
        ArgumentNullException.ThrowIfNull(bus);
        if (Items.IsEmpty)
            return Project;
        if (bypassAll)
            return _bypassed ??= Stripped();
        var p = Project;
        foreach (var g in _groups)
        {
            var hold = bypassed?.Contains(g.Entity.Id) == true;
            p = ApplyGroup(p, g, bus, leaderNs, hold);
        }
        return p;
    }

    private Project Stripped()
    {
        var p = Project;
        foreach (var g in _groups)
            p = ModTargets.WithBindings(p, g.Entity, []);
        return p;
    }

    private Project ApplyGroup(Project p, Group g, IModulationBus bus, long t, bool hold)
    {
        var e = g.Entity;
        switch (e.Kind)
        {
            case ModEntityKind.Generator:
                // Never rewritten per frame (see DrawsLive): the bindings stay on the generator for its own render path; held, they
                // are taken off and it draws its base.
                return hold ? p with { Generators = p.Generators.Update(e.Id, gen => gen with { Bindings = [] }) } : p;
            case ModEntityKind.Layer:
                return p with
                {
                    Canvases = p.Canvases.Update(e.ParentId, c => c with
                    {
                        Layers = c.Layers.Update(e.Id, l =>
                        {
                            var opacity = l.Opacity;
                            var r = l.Rect;
                            foreach (var i in g.Items)
                            {
                                if (Items[i].Problem.Length > 0)
                                    continue;
                                var v = Evaluate(i, bus, t, hold).Value;
                                switch (Items[i].Param)
                                {
                                    case "opacity": opacity = v; break;
                                    case "rect.x": r = r with { X = v }; break;
                                    case "rect.y": r = r with { Y = v }; break;
                                    case "rect.w": r = r with { W = v }; break;
                                    default: r = r with { H = v }; break;
                                }
                            }
                            return l with { Opacity = opacity, Rect = r, Bindings = [] };
                        }),
                    }),
                };
            case ModEntityKind.Surface:
                return p.WithStage(s => s with
                {
                    Surfaces = s.Surfaces.Update(e.Id, sf =>
                    {
                        var opacity = sf.Opacity;
                        foreach (var i in g.Items)
                            if (Items[i].Problem.Length == 0)
                                opacity = Evaluate(i, bus, t, hold).Value;
                        return sf with { Opacity = opacity, Bindings = [] };
                    }),
                });
            default:
                return ModTargets.WithSlot(p, e.ParentId, e.Id, slot =>
                {
                    var mix = slot.Mix;
                    var set = slot.Params;
                    foreach (var i in g.Items)
                    {
                        if (Items[i].Problem.Length > 0)
                            continue;
                        var v = Evaluate(i, bus, t, hold).Value;
                        if (Items[i].Param == "mix")
                            mix = v;
                        else
                            set = set.With(Items[i].Param, v);
                    }
                    return slot with { Mix = mix, Params = set, Bindings = [] };
                });
        }
    }

    private sealed record Group(ModEntity Entity, ImmutableArray<int> Items);
}
