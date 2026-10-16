using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PlayMap.App.State;

/// <summary>One field of a card: its label, its STATE path, and the unit shown after the value.</summary>
public sealed record CardFieldSpec(string Label, string Path, string Unit);

/// <summary>One action of a card: its label and the verb line it sends (with <c>$(field)</c> or <c>{field}</c> taken from the
/// card's element).</summary>
public sealed record CardActionSpec(string Label, string Line);

/// <summary>A package's description of one STATE block as a card (tests/fixtures/state/&lt;pkg&gt;.cards.json and the README
/// there): its title, its path (<c>a.b</c>, <c>a.0</c>, <c>a[]</c> for one card per element), the light, the words, its fields
/// (the first six on the card's face, the rest under "more") and its actions as verb lines. Read tolerantly: every dialect the
/// packages wrote reads, an entry without a path is skipped.</summary>
public sealed record CardDescriptor(string Package, int Index, string Title, string Path, string? LabelField, string? KeyField,
    JsonElement Light, string Words, ImmutableArray<CardFieldSpec> Fields, ImmutableArray<CardActionSpec> Actions)
{
    /// <summary>The fields on a card's face; the rest are under its "more".</summary>
    public const int MaxFields = 6;

    /// <summary>A light given as a path with a value list per light (<c>{"path": "x", "red": ["bad"]}</c>): each light's values in
    /// their JSON text, read once from the descriptor.</summary>
    public ImmutableArray<(string Light, System.Collections.Frozen.FrozenSet<string> Values)> LightLists { get; private init; } = [];

    public static ImmutableArray<CardDescriptor> ParseFile(string package, string json)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(json);
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            return [];
        var list = ImmutableArray.CreateBuilder<CardDescriptor>();
        var i = 0;
        foreach (var e in doc.RootElement.EnumerateArray())
        {
            if (e.ValueKind == JsonValueKind.Object && Text(e, "path") is { Length: > 0 } path)
                list.Add(Parse(package, i, e, path));
            i++;
        }
        return list.ToImmutable();
    }

    private static CardDescriptor Parse(string package, int index, JsonElement e, string path)
    {
        var each = e.TryGetProperty("each", out var ev) && ev.ValueKind == JsonValueKind.True;
        if (each && !path.EndsWith("[]", StringComparison.Ordinal))
            path += "[]";
        var fields = ImmutableArray.CreateBuilder<CardFieldSpec>();
        if (e.TryGetProperty("fields", out var fs) && fs.ValueKind == JsonValueKind.Array)
        {
            foreach (var f in fs.EnumerateArray())
            {
                if (f.ValueKind == JsonValueKind.Object && Text(f, "path") is { Length: > 0 } fp)
                    fields.Add(new CardFieldSpec(Text(f, "label") ?? fp, fp, Text(f, "unit") ?? ""));
            }
        }
        var actions = ImmutableArray.CreateBuilder<CardActionSpec>();
        if (e.TryGetProperty("actions", out var a) && a.ValueKind == JsonValueKind.Array)
        {
            foreach (var x in a.EnumerateArray())
            {
                if (x.ValueKind == JsonValueKind.String && x.GetString() is { Length: > 0 } line)
                    actions.Add(new CardActionSpec(Label(line), line));
                else if (x.ValueKind == JsonValueKind.Object && Text(x, "line") is { Length: > 0 } l)
                    actions.Add(new CardActionSpec(Text(x, "label") ?? Label(l), l));
            }
        }
        var light = e.TryGetProperty("light", out var lv) ? lv.Clone() : default;
        var lists = light.ValueKind == JsonValueKind.Object
            ? new[] { "red", "amber", "green", "grey" }
                .Where(l => light.TryGetProperty(l, out var arr) && arr.ValueKind == JsonValueKind.Array)
                .Select(l => (l, System.Collections.Frozen.FrozenSet.ToFrozenSet(light.GetProperty(l).EnumerateArray().Select(x => x.GetRawText()), StringComparer.Ordinal)))
                .ToImmutableArray()
            : [];
        return new CardDescriptor(package, index, Text(e, "title") ?? package, path, Text(e, "label"), Text(e, "key"), light,
            Text(e, "words") ?? "", fields.ToImmutable(), actions.ToImmutable()) { LightLists = lists };
    }

    /// <summary>A line's own label when the descriptor gives none: its words up to the first placeholder, in sentence case.</summary>
    private static string Label(string line)
    {
        var cut = line.IndexOfAny(['$', '{', '#']);
        var words = (cut < 0 ? line : line[..cut]).Trim().ToLowerInvariant();
        return words.Length == 0 ? line : char.ToUpperInvariant(words[0]) + words[1..];
    }

    private static string? Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>Every descriptor built into the desk (the packages' cards.json files and the desk's own), in file order.</summary>
    public static ImmutableArray<CardDescriptor> BuiltIn => LazyBuiltIn.Value;

    private static readonly Lazy<ImmutableArray<CardDescriptor>> LazyBuiltIn = new(LoadBuiltIn);

    /// <summary>The packages whose descriptors are built in (the file names without .cards.json).</summary>
    public static ImmutableArray<string> BuiltInPackages => [.. BuiltInFiles().Select(f => f.Package)];

    private static IEnumerable<(string Package, string Json)> BuiltInFiles()
    {
        var asm = typeof(CardDescriptor).Assembly;
        foreach (var name in asm.GetManifestResourceNames().Where(n => n.StartsWith("cards/", StringComparison.Ordinal)).Order(StringComparer.Ordinal))
        {
            using var stream = asm.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream, Encoding.UTF8);
            yield return (name["cards/".Length..^".cards.json".Length], reader.ReadToEnd());
        }
    }

    private static ImmutableArray<CardDescriptor> LoadBuiltIn() => [.. BuiltInFiles().SelectMany(f => ParseFile(f.Package, f.Json))];
}

/// <summary>One action of a card as resolved now: the line to send, or the words why it cannot be (a field it needs is not in
/// STATE).</summary>
public sealed record CardAction(string Label, string? Line, string? Missing);

/// <summary>A card as STATE fills it now: one per descriptor, or one per element for a per-element descriptor.</summary>
public sealed record CardInstance(CardDescriptor Descriptor, string Key, string Title, string Light, string Words,
    ImmutableArray<(string Label, string Value)> Fields, ImmutableArray<CardAction> Actions, string? EntityId);

/// <summary>Fills cards from STATE. A path is tried relative to the card's block, then from the root; <c>/a.b</c> is from the
/// root; <c>a[]</c> walks every element (a light over it is the worst); <c>a.0</c> indexes; <c>a.length</c> counts. A block
/// that is absent or null gives no card (its package's feature is not on this node). Pure: the same STATE gives the same
/// cards.</summary>
public static partial class CardReader
{
    /// <summary>Where a card instance sits: the block, the elements above it (innermost last), every object the path passed
    /// through below the root (its owners, innermost last, each with the key it was reached by, "" for an element) and the root.</summary>
    private sealed record Scope(JsonElement Root, JsonElement Node, ImmutableArray<JsonElement> Elements, string NormalisedPath,
        ImmutableArray<(string Name, JsonElement Node)> Owners);

    public static IEnumerable<CardInstance> Read(CardDescriptor d, JsonElement root)
    {
        ArgumentNullException.ThrowIfNull(d);
        return root.ValueKind == JsonValueKind.Object ? ReadCore(d, root) : [];
    }

    private static IEnumerable<CardInstance> ReadCore(CardDescriptor d, JsonElement root)
    {
        foreach (var (node, elements, key, owners) in Expand(root, d.Path.Split('.'), 0, root, [], "", []))
        {
            if (node.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined or JsonValueKind.False)
                continue;
            var scope = new Scope(root, node, elements, d.Path, owners);
            var element = elements.IsEmpty ? (JsonElement?)null : elements[^1];
            // A per-element card is titled by its label field, or by the element's own name.
            var label = element is { } el ? Text(Get(el, d.LabelField ?? "name")) : null;
            var title = label is { Length: > 0 } && label != "—" ? d.Title + " · " + label : d.Title;
            var fields = d.Fields
                .Select(f => (f.Label, Value: Text(Resolve(f.Path, scope)) is var v && v != "—" && f.Unit.Length > 0 ? v + " " + f.Unit : Text(Resolve(f.Path, scope))))
                .ToImmutableArray();
            var actions = d.Actions.Select(a => ResolveLine(a, scope, d)).ToImmutableArray();
            yield return new CardInstance(d, key, title, LightOf(d.Light, d.LightLists, scope), WordsOf(d.Words, scope), fields, actions, EntityOf(scope, d));
        }
    }

    /// <summary>Every card of every descriptor that STATE fills now.</summary>
    public static ImmutableArray<CardInstance> ReadAll(IEnumerable<CardDescriptor> descriptors, JsonElement root) =>
        [.. descriptors.SelectMany(d => Read(d, root))];

    private static IEnumerable<(JsonElement Node, ImmutableArray<JsonElement> Elements, string Key, ImmutableArray<(string Name, JsonElement Node)> Owners)> Expand(
        JsonElement root, string[] parts, int at, JsonElement node, ImmutableArray<JsonElement> elements, string key,
        ImmutableArray<(string Name, JsonElement Node)> owners)
    {
        if (at == parts.Length)
        {
            yield return (node, elements, key, owners);
            yield break;
        }
        // The node this step leaves is an owner of what is below it (the root is not: its fields are the node's own), named by the
        // key it was reached by ("scan"), or "" when it is an element of a list.
        var reachedBy = at == 0 ? "" : parts[at - 1];
        var ownerName = reachedBy.EndsWith("[]", StringComparison.Ordinal) || int.TryParse(reachedBy, NumberStyles.None, CultureInfo.InvariantCulture, out _) ? "" : reachedBy;
        var below = at == 0 || node.ValueKind != JsonValueKind.Object ? owners : owners.Add((ownerName, node));
        var part = parts[at];
        var many = part.EndsWith("[]", StringComparison.Ordinal);
        var name = many ? part[..^2] : part;
        var next = name.Length == 0 ? node : Step(node, name);
        if (next is not { } n)
            yield break;
        if (!many)
        {
            foreach (var r in Expand(root, parts, at + 1, n, elements, key, below))
                yield return r;
            yield break;
        }
        if (n.ValueKind != JsonValueKind.Array)
            yield break;
        var i = 0;
        foreach (var e in n.EnumerateArray())
        {
            var id = Text(Get(e, "id")) is { } s && s != "—" ? s : i.ToString(CultureInfo.InvariantCulture);
            foreach (var r in Expand(root, parts, at + 1, e, elements.Add(e), key.Length == 0 ? id : key + "/" + id, below))
                yield return r;
            i++;
        }
    }

    /// <summary>A list's length as a value, made once per length.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, JsonElement> Lengths = new();

    /// <summary>One step down: a property, an array index, or an array's length.</summary>
    private static JsonElement? Step(JsonElement node, string name)
    {
        if (node.ValueKind == JsonValueKind.Object)
            return node.TryGetProperty(name, out var v) ? v : null;
        if (node.ValueKind == JsonValueKind.Array)
        {
            if (int.TryParse(name, NumberStyles.None, CultureInfo.InvariantCulture, out var index))
                return index < node.GetArrayLength() ? node[index] : null;
            if (name == "length")
                return Lengths.GetOrAdd(node.GetArrayLength(), n => JsonSerializer.SerializeToElement(n));
        }
        return null;
    }

    /// <summary>The values a path names from a node ([] expands to every element's).</summary>
    private static List<JsonElement> Values(JsonElement node, string path)
    {
        var current = new List<JsonElement> { node };
        foreach (var part in path.Split('.'))
        {
            var many = part.EndsWith("[]", StringComparison.Ordinal);
            var name = many ? part[..^2] : part;
            var next = new List<JsonElement>();
            foreach (var c in current)
            {
                if ((name.Length == 0 ? c : Step(c, name)) is not { } v)
                    continue;
                if (many && v.ValueKind == JsonValueKind.Array)
                    next.AddRange(v.EnumerateArray());
                else if (!many)
                    next.Add(v);
            }
            current = next;
            if (current.Count == 0)
                break;
        }
        return current;
    }

    private static JsonElement? Get(JsonElement node, string path) => Values(node, path) is { Count: > 0 } v ? v[0] : null;

    /// <summary>What a path names in a card: from the root with a leading /, after the card's own path when it repeats it,
    /// relative to the block, else from the root.</summary>
    private static List<JsonElement> Resolve(string path, Scope s)
    {
        if (path.StartsWith('/'))
            return Values(s.Root, path[1..]);
        var prefix = s.NormalisedPath + ".";
        if (path.StartsWith(prefix, StringComparison.Ordinal))
            return Values(s.Node, path[prefix.Length..]);
        if (path == "count" && s.Node.ValueKind == JsonValueKind.Array)
            return Values(s.Node, "length");
        var relative = Values(s.Node, path);
        if (relative.Count > 0)
            return relative;
        // A path that repeats the card's element path ("outputs[].present.light" in an "outputs[].lock" card) reads
        // the same element's fields.
        if (!s.Elements.IsEmpty && path.Contains("[].", StringComparison.Ordinal))
        {
            var tail = path[(path.LastIndexOf("[].", StringComparison.Ordinal) + 3)..];
            var own = Values(s.Elements[^1], tail);
            if (own.Count > 0)
                return own;
        }
        // A dotted path not in the block reads from the root (a sibling block's field); a bare name never does, so a block
        // without "light" or "name" never shows the node's own.
        return path.Contains('.', StringComparison.Ordinal) ? Values(s.Root, path) : [];
    }

    private static string Text(List<JsonElement> values) => values.Count == 0 ? "—" : Text(values[0]);

    private static string Text(JsonElement? value) => value is not { } v ? "—" : v.ValueKind switch
    {
        JsonValueKind.String => v.GetString() ?? "",
        JsonValueKind.Number => v.TryGetInt64(out var l) ? l.ToString(CultureInfo.InvariantCulture) : Math.Round(v.GetDouble(), 3).ToString("0.###", CultureInfo.InvariantCulture),
        JsonValueKind.True => "yes",
        JsonValueKind.False => "no",
        JsonValueKind.Array => v.GetArrayLength().ToString(CultureInfo.InvariantCulture),
        JsonValueKind.Object => Shorten(v.GetRawText()),
        _ => "—",
    };

    private static string Shorten(string s) => s.Length <= 40 ? s : s[..39] + "…";

    private static bool Truthy(List<JsonElement> values) => values.Count > 0 && values.Any(v => v.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.False or JsonValueKind.Undefined => false,
        JsonValueKind.String => (v.GetString() ?? "").Length > 0,
        JsonValueKind.Number => v.GetDouble() != 0,
        JsonValueKind.Array => v.GetArrayLength() > 0,
        _ => true,
    });

    // ---- light ----

    private static int Rank(string light) => light switch
    {
        "red" => 3,
        "amber" => 2,
        "green" => 1,
        _ => 0,
    };

    /// <summary>A word as a light: the four light words, and the common state words of the packages' STATE — the frame lock's
    /// and a hot backup's mirror's as the engine lights them in its context.</summary>
    public static string LightWord(string word)
    {
        ArgumentNullException.ThrowIfNull(word);
        return word.ToLowerInvariant() switch
        {
            "green" or "ok" or "good" or "live" or "ready" or "locked" or "listening" or "rolling" or "done" or "playing"
                or "framelocked" or "house-locked" or "identical" => "green",
            "amber" or "yellow" or "warn" or "warning" or "stale" or "acquiring" or "opening" or "fair" or "running" or "busy"
                or "joining" or "soft-locked" or "grouped" or "free-measured" or "resyncing" or "different" => "amber",
            "red" or "fault" or "faulted" or "failed" or "error" or "lost" or "down" or "missing" or "poor" or "bad" or "fell-out" => "red",
            _ => "grey",
        };
    }

    private static string Worst(IEnumerable<string> lights) => lights.DefaultIfEmpty("grey").MaxBy(Rank)!;

    /// <summary>The light a "from" rule names for a value (<c>{"from": "state", "green": "identical", "red": ["lost"]}</c>: a word
    /// or a list of them per light), or null when it names none for it.</summary>
    private static string? Mapped(JsonElement rule, JsonElement value)
    {
        var text = Text(value);
        foreach (var light in (string[])["red", "amber", "green", "grey"])
        {
            if (!rule.TryGetProperty(light, out var listed))
                continue;
            if (listed.ValueKind == JsonValueKind.String && listed.GetString() == text)
                return light;
            if (listed.ValueKind == JsonValueKind.Array && listed.EnumerateArray().Any(x => x.GetRawText() == value.GetRawText()
                || (x.ValueKind == JsonValueKind.String && x.GetString() == text)))
                return light;
        }
        return null;
    }

    private static string LightOf(JsonElement spec, ImmutableArray<(string Light, System.Collections.Frozen.FrozenSet<string> Values)> lightLists, Scope s)
    {
        switch (spec.ValueKind)
        {
            case JsonValueKind.String:
                return Worst(Resolve(spec.GetString() ?? "", s).Select(v => LightWord(Text(v))));
            case JsonValueKind.Object when spec.TryGetProperty("path", out var p) && p.ValueKind == JsonValueKind.String:
                var values = Resolve(p.GetString() ?? "", s);
                var lists = lightLists;
                if (lists.IsEmpty)
                    return Worst(values.Select(v => LightWord(Text(v))));
                return Worst(values.Select(v => lists.FirstOrDefault(l => l.Values.Contains(v.GetRawText())).Light ?? "grey"));
            case JsonValueKind.Array:
                foreach (var rule in spec.EnumerateArray())
                {
                    if (rule.ValueKind != JsonValueKind.Object)
                        continue;
                    var light = rule.TryGetProperty("light", out var lv) && lv.ValueKind == JsonValueKind.String ? lv.GetString() : null;
                    if (rule.TryGetProperty("if", out var cond) && cond.ValueKind == JsonValueKind.String)
                    {
                        if (Truthy(Resolve(cond.GetString() ?? "", s)) && light is not null)
                            return light;
                        continue;
                    }
                    if (rule.TryGetProperty("fixed", out var fixedLight) && fixedLight.ValueKind == JsonValueKind.String)
                        return LightWord(fixedLight.GetString() ?? "");
                    if (rule.TryGetProperty("from", out var from) && from.ValueKind == JsonValueKind.String)
                    {
                        var got = Resolve(from.GetString() ?? "", s);
                        if (got.Count > 0)
                            return Worst(got.Select(v => Mapped(rule, v) ?? LightWord(Text(v))));
                        continue;
                    }
                    if (light is not null)
                        return light;
                }
                return "grey";
            default:
                return "grey";
        }
    }

    // ---- words ----

    private static string WordsOf(string words, Scope s)
    {
        if (words.Length == 0)
            return "";
        if (words.Contains('{', StringComparison.Ordinal))
            return Render(words, s);
        var values = Resolve(words, s);
        if (values.Count > 0)
            return Text(values);
        return words.Contains(' ', StringComparison.Ordinal) ? words : "";
    }

    /// <summary>A words template: {field}, {field?then:else} (either side may hold fields), {count}.</summary>
    private static string Render(string template, Scope s)
    {
        var sb = new StringBuilder();
        var i = 0;
        while (i < template.Length)
        {
            var c = template[i];
            if (c != '{')
            {
                sb.Append(c);
                i++;
                continue;
            }
            var end = Matching(template, i);
            if (end < 0)
            {
                sb.Append(template, i, template.Length - i);
                break;
            }
            var inner = template[(i + 1)..end];
            var q = TopLevel(inner, '?');
            if (q < 0)
                sb.Append(Text(Resolve(inner.Trim(), s)));
            else
            {
                var rest = inner[(q + 1)..];
                var colon = TopLevel(rest, ':');
                var then = colon < 0 ? rest : rest[..colon];
                var otherwise = colon < 0 ? "" : rest[(colon + 1)..];
                sb.Append(Render(Truthy(Resolve(inner[..q].Trim(), s)) ? then : otherwise, s));
            }
            i = end + 1;
        }
        return sb.ToString();
    }

    private static int Matching(string t, int open)
    {
        var depth = 0;
        for (var i = open; i < t.Length; i++)
        {
            if (t[i] == '{')
                depth++;
            else if (t[i] == '}' && --depth == 0)
                return i;
        }
        return -1;
    }

    private static int TopLevel(string t, char ch)
    {
        var depth = 0;
        for (var i = 0; i < t.Length; i++)
        {
            if (t[i] == '{')
                depth++;
            else if (t[i] == '}')
                depth--;
            else if (t[i] == ch && depth == 0)
                return i;
        }
        return -1;
    }

    // ---- actions and the entity ----

    [GeneratedRegex(@"\$\(([A-Za-z0-9_.]+)\)|\{([A-Za-z0-9_.]+)\}")]
    private static partial Regex Placeholder();

    private static CardAction ResolveLine(CardActionSpec a, Scope s, CardDescriptor d)
    {
        string? missing = null;
        var line = Placeholder().Replace(a.Line, m =>
        {
            var name = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
            if (Field(name, s, d) is { } v)
                return v;
            missing ??= name;
            return m.Value;
        });
        return missing is null ? new CardAction(a.Label, line, null) : new CardAction(a.Label, null, "STATE has no " + missing + " for this card");
    }

    /// <summary>A field an action names: the block's own, then the elements' above it (innermost first), then the first element of a
    /// block that is a list, then the objects that own the block (innermost first: "stage.projectors.0.uncertainty" reads the
    /// projector's id); "id" falls back to the descriptor's key field. A name no place holds as a field names the owner reached by
    /// that key: <c>$(scan)</c> in "scan.regions.0" is the scan's id.</summary>
    private static string? Field(string name, Scope s, CardDescriptor d)
    {
        IEnumerable<JsonElement> places = [s.Node, .. s.Elements.Reverse()];
        if (s.Node.ValueKind == JsonValueKind.Array && s.Node.GetArrayLength() > 0)
            places = places.Append(s.Node[0]);
        places = places.Concat(s.Owners.Reverse().Select(o => o.Node));
        foreach (var p in places)
        {
            if (p.ValueKind == JsonValueKind.Object && Get(p, name) is { ValueKind: JsonValueKind.String or JsonValueKind.Number } v)
                return Text(v);
            if (name == "id" && d.KeyField is { } k && p.ValueKind == JsonValueKind.Object && Get(p, k) is { ValueKind: JsonValueKind.String } kv)
                return kv.GetString();
        }
        foreach (var (key, node) in s.Owners.Reverse())
        {
            if (key == name && Get(node, "id") is { ValueKind: JsonValueKind.String } id)
                return id.GetString();
        }
        return null;
    }

    /// <summary>The entity a card is about, for the inspector: the element's id (or its key field), else the block's id, else the
    /// id of the list element that owns the block ("outputs.0.measured" is the output's, "stage.projectors.0.uncertainty" the
    /// projector's).</summary>
    private static string? EntityOf(Scope s, CardDescriptor d)
    {
        foreach (var p in (IEnumerable<JsonElement>)[.. s.Elements.Reverse(), s.Node, .. s.Owners.Reverse().Where(o => o.Name.Length == 0).Select(o => o.Node)])
        {
            if (p.ValueKind != JsonValueKind.Object)
                continue;
            if (d.KeyField is { } k && Get(p, k) is { ValueKind: JsonValueKind.String } kv)
                return kv.GetString();
            if (Get(p, "id") is { ValueKind: JsonValueKind.String } id)
                return id.GetString();
        }
        return null;
    }
}
