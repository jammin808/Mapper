using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using PlayMap.Core.Model;

namespace PlayMap.Core.Show;

/// <summary>What happened to one entity between two roots.</summary>
public enum ChangeOp { Added, Changed, Removed }

/// <summary>One field of a changed entity: its path the way SET takes it ("content.source", "items.2.label"), the value it had and
/// the value it has (compact JSON; "" when the field is new or gone).</summary>
public sealed record FieldChange(string Field, string From, string To)
{
    /// <summary>The change in words: "blend.gamma 2.2 → 2.4".</summary>
    public string Words => From.Length == 0 ? $"{Field} set to {To}" : To.Length == 0 ? $"{Field} ({From}) removed" : $"{Field} {From} → {To}";
}

/// <summary>One entity a draft adds, changes or removes: its id, its kind and name in words, and for a change its fields (at most
/// <see cref="ProjectDiff.FieldsShown"/>; <see cref="More"/> counts the rest).</summary>
public sealed record EntityChange(string Id, string Kind, string Label, ChangeOp Op, IReadOnlyList<FieldChange> Fields)
{
    /// <summary>Changed fields beyond the ones listed.</summary>
    public int More { get; init; }
}

/// <summary>A draft as the entities it changes against the root it started from. Applied to a live root, it puts each changed
/// entity's draft version in place by id (added, replaced or removed in its own list), so what changed live elsewhere stays.</summary>
public sealed record ProjectPatch(long BaseRevision, IReadOnlyList<EntityChange> Changes, Project Draft)
{
    /// <summary>The live root with this draft's changes put in, entity by entity. The live root's locks are kept (they are never a
    /// draft's to move).</summary>
    public Project ApplyTo(Project live)
    {
        ArgumentNullException.ThrowIfNull(live);
        var p = live;
        foreach (var c in Changes)
            p = ProjectDiff.Put(p, Draft, c.Id);
        return p;
    }

    /// <summary>The ids this draft changes that also changed live since the base (refused at commit unless forced).</summary>
    public IReadOnlyList<string> ConflictsWith(Project baseRoot, Project live)
    {
        ArgumentNullException.ThrowIfNull(baseRoot);
        ArgumentNullException.ThrowIfNull(live);
        List<string>? hits = null;
        foreach (var c in Changes)
        {
            if (!ProjectDiff.Same(ProjectDiff.Get(baseRoot, c.Id), ProjectDiff.Get(live, c.Id)))
                (hits ??= []).Add(c.Id);
        }
        return hits ?? (IReadOnlyList<string>)[];
    }

    /// <summary>True when an edit from <paramref name="before"/> to <paramref name="after"/> touches an entity this patch changes.</summary>
    public string? Touches(Project before, Project after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        foreach (var c in Changes)
        {
            if (!ProjectDiff.Same(ProjectDiff.Get(before, c.Id), ProjectDiff.Get(after, c.Id)))
                return c.Id;
        }
        return null;
    }
}

/// <summary>The difference between two project roots, entity by entity, in the words an operator reads. Pure: it walks every list
/// of the document (media, playlists with their items, generators, canvases with their layers, the stage's lists, outputs, sends,
/// controllers, timecode, effects assets, presets, LED rigs, the fleet plan) and the show-wide singletons (settings, the show's
/// rules and panic plans, audio, tempo, optimise, the site frame, the name), comparing by reference first, by value second and by
/// JSON last, so a draft that sets a field back to what it was changes nothing.</summary>
public static class ProjectDiff
{
    /// <summary>Field rows listed per changed entity; the rest are counted in one more row.</summary>
    public const int FieldsShown = 12;

    private sealed record Coll(string Kind, Func<Project, IEnumerable<IEntity>> Items, Func<Project, string, IEntity?, Project> Put);

    private sealed record Single(string Id, string Kind, Func<Project, object> Get, Func<Project, Project, Project> Put);

    private static Coll Of<T>(string kind, Func<Project, ImmutableArray<T>> get, Func<Project, ImmutableArray<T>, Project> set)
        where T : class, IEntity =>
        new(kind, p => get(p).Cast<IEntity>(), (p, id, e) =>
        {
            var list = get(p);
            var i = list.IndexOfId(id);
            if (e is null)
                return i < 0 ? p : set(p, list.RemoveAt(i));
            var item = (T)e;
            return set(p, i < 0 ? list.Add(item) : list.SetItem(i, item));
        });

    private static readonly Coll[] Lists =
    [
        Of("media", p => p.Media, (p, v) => p with { Media = v }),
        Of("playlist", p => p.Playlists, (p, v) => p with { Playlists = v }),
        Of("generator", p => p.Generators, (p, v) => p with { Generators = v }),
        Of("canvas", p => p.Canvases, (p, v) => p with { Canvases = v }),
        Of("surface", p => p.Stage.Surfaces, (p, v) => p with { Stage = p.Stage with { Surfaces = v } }),
        Of("mesh", p => p.Stage.Meshes, (p, v) => p with { Stage = p.Stage with { Meshes = v } }),
        Of("projector", p => p.Stage.Projectors, (p, v) => p with { Stage = p.Stage with { Projectors = v } }),
        Of("camera", p => p.Stage.Cameras, (p, v) => p with { Stage = p.Stage with { Cameras = v } }),
        Of("viewpoint", p => p.Stage.Viewpoints, (p, v) => p with { Stage = p.Stage with { Viewpoints = v } }),
        Of("scan", p => p.Stage.Scans, (p, v) => p with { Stage = p.Stage with { Scans = v } }),
        Of("device", p => p.Stage.Devices, (p, v) => p with { Stage = p.Stage with { Devices = v } }),
        Of("control point", p => p.Stage.Controls, (p, v) => p with { Stage = p.Stage with { Controls = v } }),
        Of("survey", p => p.Stage.Surveys, (p, v) => p with { Stage = p.Stage with { Surveys = v } }),
        Of("stack", p => p.Stage.Stacks, (p, v) => p with { Stage = p.Stage with { Stacks = v } }),
        Of("align group", p => p.Stage.Aligns, (p, v) => p with { Stage = p.Stage with { Aligns = v } }),
        Of("calibration", p => p.Stage.Calibrations, (p, v) => p with { Stage = p.Stage with { Calibrations = v } }),
        Of("output", p => p.Outputs, (p, v) => p with { Outputs = v }),
        Of("send", p => p.Sends, (p, v) => p with { Sends = v }),
        Of("effect", p => p.FxAssets, (p, v) => p with { FxAssets = v }),
        Of("preset", p => p.GeneratorPresets, (p, v) => p with { GeneratorPresets = v }),
        Of("control surface", p => p.Control.Surfaces, (p, v) => p with { Control = p.Control with { Surfaces = v } }),
        Of("binding", p => p.Control.Bindings, (p, v) => p with { Control = p.Control with { Bindings = v } }),
        Of("timecode source", p => p.Timecode.Sources, (p, v) => p with { Timecode = p.Timecode with { Sources = v } }),
        Of("LED node", p => p.Leds.Nodes, (p, v) => p with { Leds = p.Leds with { Nodes = v } }),
        Of("fixture type", p => p.Leds.Types, (p, v) => p with { Leds = p.Leds with { Types = v } }),
        Of("fixture", p => p.Leds.Fixtures, (p, v) => p with { Leds = p.Leds with { Fixtures = v } }),
        Of("fleet member", p => p.Fleet.Members, (p, v) => p with { Fleet = p.Fleet with { Members = v } }),
    ];

    /// <summary>The show-wide values that are not in a list, each named by a word that no entity id can be. The show's locks are
    /// not a draft's: the "show" singleton is its rules and panic plans only.</summary>
    private static readonly Single[] Singles =
    [
        new("name", "project name", p => p.Name, (p, d) => p with { Name = d.Name }),
        new("settings", "show settings", p => p.Settings, (p, d) => p with { Settings = d.Settings }),
        new("show", "show rules", p => p.Show with { Locks = [] }, (p, d) => p with { Show = d.Show with { Locks = p.Show.Locks } }),
        new("audio", "audio settings", p => p.Audio, (p, d) => p with { Audio = d.Audio }),
        new("tempo", "tempo", p => p.Tempo, (p, d) => p with { Tempo = d.Tempo }),
        new("optimise", "optimise settings", p => p.Optimise, (p, d) => p with { Optimise = d.Optimise }),
        new("site", "site frame", p => p.Stage.Site, (p, d) => p with { Stage = p.Stage with { Site = d.Stage.Site } }),
        new("leds", "LED rig settings", p => p.Leds with { Nodes = [], Types = [], Fixtures = [] },
            (p, d) => p with { Leds = d.Leds with { Nodes = p.Leds.Nodes, Types = p.Leds.Types, Fixtures = p.Leds.Fixtures } }),
        new("control", "control settings", p => p.Control with { Surfaces = [], Bindings = [] },
            (p, d) => p with { Control = d.Control with { Surfaces = p.Control.Surfaces, Bindings = p.Control.Bindings } }),
        new("timecode", "timecode settings", p => p.Timecode with { Sources = [] },
            (p, d) => p with { Timecode = d.Timecode with { Sources = p.Timecode.Sources } }),
        new("fleet", "fleet plan", p => p.Fleet with { Members = [] }, (p, d) => p with { Fleet = d.Fleet with { Members = p.Fleet.Members } }),
    ];

    /// <summary>Every entity <paramref name="draft"/> adds, changes or removes against <paramref name="baseRoot"/>, in the
    /// document's order.</summary>
    public static ProjectPatch Between(Project baseRoot, Project draft, long baseRevision)
    {
        ArgumentNullException.ThrowIfNull(baseRoot);
        ArgumentNullException.ThrowIfNull(draft);
        var changes = new List<EntityChange>();
        if (!ReferenceEquals(baseRoot, draft))
        {
            foreach (var s in Singles)
            {
                var a = s.Get(baseRoot);
                var b = s.Get(draft);
                if (!Same(a, b))
                    changes.Add(Changed(s.Id, s.Kind, s.Kind, a, b));
            }
            foreach (var c in Lists)
            {
                var before = c.Items(baseRoot).ToDictionary(e => e.Id, StringComparer.Ordinal);
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var e in c.Items(draft))
                {
                    seen.Add(e.Id);
                    if (!before.TryGetValue(e.Id, out var was))
                        changes.Add(new EntityChange(e.Id, c.Kind, LabelOf(e), ChangeOp.Added, []));
                    else if (!Same(was, e))
                        changes.Add(Changed(e.Id, c.Kind, LabelOf(e), was, e));
                }
                foreach (var (id, was) in before)
                {
                    if (!seen.Contains(id))
                        changes.Add(new EntityChange(id, c.Kind, LabelOf(was), ChangeOp.Removed, []));
                }
            }
        }
        return new ProjectPatch(baseRevision, changes, draft);
    }

    /// <summary>A change in words, one line per field: "Wall (surface) changed: content.source …", "Intro (item) added".</summary>
    public static IReadOnlyList<string> Words(EntityChange c)
    {
        ArgumentNullException.ThrowIfNull(c);
        var head = c.Label == c.Kind ? c.Kind : $"{c.Label} ({c.Kind})";
        return c.Op switch
        {
            ChangeOp.Added => [head + " added"],
            ChangeOp.Removed => [head + " removed"],
            _ when c.Fields.Count == 0 => [head + " changed"],
            _ when c.More == 0 => [.. c.Fields.Select(f => $"{head}: {f.Words}")],
            _ => [.. c.Fields.Select(f => $"{head}: {f.Words}"), string.Create(CultureInfo.InvariantCulture, $"{head}: and {c.More} more")],
        };
    }

    /// <summary>The entity or singleton value with this id in a root, or null.</summary>
    internal static object? Get(Project p, string id)
    {
        foreach (var s in Singles)
        {
            if (s.Id == id)
                return s.Get(p);
        }
        foreach (var c in Lists)
        {
            foreach (var e in c.Items(p))
            {
                if (string.Equals(e.Id, id, StringComparison.Ordinal))
                    return e;
            }
        }
        return null;
    }

    /// <summary>Puts the draft's version of one id into a root: replaced, added at the end of its list, or removed.</summary>
    internal static Project Put(Project into, Project draft, string id)
    {
        foreach (var s in Singles)
        {
            if (s.Id == id)
                return s.Put(into, draft);
        }
        foreach (var c in Lists)
        {
            IEntity? inDraft = null;
            var inLive = false;
            foreach (var e in c.Items(draft))
            {
                if (string.Equals(e.Id, id, StringComparison.Ordinal))
                    inDraft = e;
            }
            foreach (var e in c.Items(into))
            {
                if (string.Equals(e.Id, id, StringComparison.Ordinal))
                    inLive = true;
            }
            if (inDraft is not null || inLive)
                return c.Put(into, id, inDraft);
        }
        return into;
    }

    /// <summary>Equal by reference, by value, or by what the file would hold.</summary>
    internal static bool Same(object? a, object? b)
    {
        if (ReferenceEquals(a, b) || Equals(a, b))
            return true;
        if (a is null || b is null)
            return false;
        return ProjectJson.SerializeEntity(a) == ProjectJson.SerializeEntity(b);
    }

    private static string LabelOf(IEntity e) => ShowEntities.Describe(e).Label is { Length: > 0 } l and not "?" ? l : e.Id;

    /// <summary>A changed entity with the fields that differ, as SET paths, at most <see cref="FieldsShown"/> and a count of the rest.</summary>
    private static EntityChange Changed(string id, string kind, string label, object before, object after)
    {
        var a = JsonSerializer.SerializeToNode(before, before.GetType(), ProjectJson.Compact);
        var b = JsonSerializer.SerializeToNode(after, after.GetType(), ProjectJson.Compact);
        var rows = new List<FieldChange>();
        var more = 0;
        Walk(a, b, "", rows, ref more);
        return new EntityChange(id, kind, label, ChangeOp.Changed, rows) { More = more };
    }

    private static void Walk(JsonNode? a, JsonNode? b, string path, List<FieldChange> rows, ref int more)
    {
        if (JsonNode.DeepEquals(a, b))
            return;
        switch (a, b)
        {
            case (JsonObject oa, JsonObject ob):
                foreach (var (key, va) in oa)
                    Walk(va, ob.TryGetPropertyValue(key, out var vb) ? vb : null, Join(path, key), rows, ref more);
                foreach (var (key, vb) in ob)
                {
                    if (!oa.ContainsKey(key))
                        Walk(null, vb, Join(path, key), rows, ref more);
                }
                return;
            case (JsonArray xa, JsonArray xb) when xa.Count == xb.Count:
                for (var i = 0; i < xa.Count; i++)
                    Walk(xa[i], xb[i], Join(path, i.ToString(CultureInfo.InvariantCulture)), rows, ref more);
                return;
            case (JsonArray xa, JsonArray xb):
                Add(rows, new FieldChange(path, Count(xa.Count), Count(xb.Count)), ref more);
                return;
            default:
                Add(rows, new FieldChange(path.Length == 0 ? "value" : path, Text(a), Text(b)), ref more);
                return;
        }
    }

    private static void Add(List<FieldChange> rows, FieldChange row, ref int more)
    {
        if (rows.Count < FieldsShown)
            rows.Add(row);
        else
            more++;
    }

    private static string Count(int n) => n == 1 ? "1 entry" : string.Create(CultureInfo.InvariantCulture, $"{n} entries");

    private static string Join(string path, string key) => path.Length == 0 ? key : path + "." + key;

    private static string Text(JsonNode? n)
    {
        if (n is null)
            return "";
        var s = n.ToJsonString(ProjectJson.Compact);
        return s.Length <= 60 ? s : string.Concat(s.AsSpan(0, 57), "…");
    }
}
