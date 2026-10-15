using System.Text.Json;
using System.Text.Json.Serialization;

namespace PlayMap.Engine.Tests.Fleet.PatternsEye;

// A copy, as data, of what Patterns' Eye does with another app's context: ShowContext.Parse (src/Patterns.Core/Services/ShowContext.cs)
// and the show-app section of EyeGraph.Build (src/Patterns.Core/Services/Eye.cs, "Round 87: the other show apps"), from patterns-svc
// at commit bcdbf28. Kept to the lines that read a context and draw it; the desk's menus, routes and the other bands are left out.
// It is Patterns' behaviour as it is, so PlayMap's context is checked against what Patterns draws, not against what PlayMap hopes.

public enum CheckLight
{
    Grey,
    Green,
    Amber,
    Red,
}

public enum ShowBusState
{
    Live,
    Stale,
    Gone,
}

public enum EyePlane
{
    Control,
    Video,
    Audio,
    Room,
}

public enum EyeEdgeKind
{
    Feeds,
    Shows,
    Drives,
    Carries,
    Controls,
    Mirrors,
    Follows,
    Hears,
    Routes,
    Serves,
    Sends,
}

public sealed record ShowContextNode
{
    [JsonPropertyName("t")] public string Kind { get; init; } = "";
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("name")] public string Name { get; init; } = "";
}

public sealed record ShowContextItem
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("kind")] public string Kind { get; init; } = "";
    [JsonPropertyName("label")] public string Label { get; init; } = "";
    [JsonPropertyName("light")] public string Light { get; init; } = "grey";
    [JsonPropertyName("state")] public string State { get; init; } = "";
}

public sealed record ShowContextLink
{
    [JsonPropertyName("from")] public string From { get; init; } = "";
    [JsonPropertyName("to")] public string To { get; init; } = "";
    [JsonPropertyName("kind")] public string Kind { get; init; } = "";
    [JsonPropertyName("light")] public string Light { get; init; } = "grey";
}

public sealed record ShowContextLine
{
    [JsonPropertyName("at")] public long At { get; init; }
    [JsonPropertyName("text")] public string Text { get; init; } = "";
}

public sealed record ShowContextCommand
{
    [JsonPropertyName("verb")] public string Verb { get; init; } = "";
    [JsonPropertyName("risk")] public string Risk { get; init; } = "review";
    [JsonPropertyName("words")] public string Words { get; init; } = "";
}

public sealed record ShowContextPeer
{
    [JsonPropertyName("t")] public string Kind { get; init; } = "";
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("light")] public string Light { get; init; } = "grey";
    [JsonPropertyName("headline")] public string Headline { get; init; } = "";
}

public sealed record ShowContext
{
    public const int MaxRecent = 20;
    public const int MaxItems = 200;

    [JsonPropertyName("v")] public int V { get; init; } = 1;
    [JsonPropertyName("node")] public ShowContextNode Node { get; init; } = new();
    [JsonPropertyName("at")] public long At { get; init; }
    [JsonPropertyName("light")] public string Light { get; init; } = "grey";
    [JsonPropertyName("headline")] public string Headline { get; init; } = "";
    [JsonPropertyName("items")] public IReadOnlyList<ShowContextItem> Items { get; init; } = [];
    [JsonPropertyName("links")] public IReadOnlyList<ShowContextLink> Links { get; init; } = [];
    [JsonPropertyName("recent")] public IReadOnlyList<ShowContextLine> Recent { get; init; } = [];
    [JsonPropertyName("commands")] public IReadOnlyList<ShowContextCommand> Commands { get; init; } = [];
    [JsonPropertyName("peers")] public IReadOnlyList<ShowContextPeer> Peers { get; init; } = [];

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = false };

    public static ShowContext? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > 512 * 1024)
            return null;
        try
        {
            var c = JsonSerializer.Deserialize<ShowContext>(json, Options);
            if (c is null || c.Node is null || c.Node.Kind.Length == 0)
                return null;
            return c with
            {
                Light = NormalLight(c.Light),
                Items = (c.Items ?? []).Where(i => i is not null && i.Id.Length > 0).Take(MaxItems).Select(i => i with { Light = NormalLight(i.Light) }).ToList(),
                Links = (c.Links ?? []).Where(l => l is not null).Take(MaxItems * 2).Select(l => l with { Light = NormalLight(l.Light) }).ToList(),
                Recent = (c.Recent ?? []).Where(r => r is not null).TakeLast(MaxRecent).ToList(),
                Commands = (c.Commands ?? []).Where(x => x is not null).Take(64).ToList(),
                Peers = (c.Peers ?? []).Where(x => x is not null).Take(32).Select(x => x with { Light = NormalLight(x.Light) }).ToList(),
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static string NormalLight(string? light) => (light ?? "").Trim().ToLowerInvariant() switch
    {
        "red" => "red",
        "amber" or "yellow" or "orange" => "amber",
        "green" => "green",
        _ => "grey",
    };

    public static CheckLight LightOf(string? word) => NormalLight(word) switch
    {
        "red" => CheckLight.Red,
        "amber" => CheckLight.Amber,
        "green" => CheckLight.Green,
        _ => CheckLight.Grey,
    };
}

public sealed record EyeShowApp(string Id, string KindWords, string Name, ShowBusState State, bool Linked, string Words)
{
    public ShowContext? Context { get; init; }
}

public sealed record EyeNode(string Id, string Kind, EyePlane Plane, int Tier, string Label, string Sub, CheckLight Light)
{
    public IReadOnlyList<string> Words { get; init; } = [];
}

public sealed record EyeEdge(string From, string To, EyeEdgeKind Kind, CheckLight Light, string Words = "");

/// <summary>The show-app section of Patterns' EyeGraph.Build, with the desk as the hub it links to; the problems queue as Patterns
/// orders it (reds, then ambers; by band, tier and label).</summary>
public static class EyeShowApps
{
    public const string DeskId = "desk";

    public static string KindWords(string kind) => kind switch
    {
        "main" => "Stagewatch",
        "agent" => "Slides agent",
        "patterns" => "Patterns",
        "messenger" => "AGMessage",
        _ => kind.Length > 0 ? char.ToUpperInvariant(kind[0]) + kind[1..] : "App",
    };

    public static (List<EyeNode> Nodes, List<EyeEdge> Edges) Build(IReadOnlyList<EyeShowApp> apps)
    {
        var nodes = new List<EyeNode> { new(DeskId, "Desk", EyePlane.Control, 0, "Desk", "", CheckLight.Green) };
        var edges = new List<EyeEdge>();
        var ids = new HashSet<string>(StringComparer.Ordinal) { DeskId };
        void Add(EyeNode node)
        {
            if (ids.Add(node.Id))
                nodes.Add(node);
        }
        void Link(string from, string to, EyeEdgeKind kind, CheckLight light, string words = "") => edges.Add(new EyeEdge(from, to, kind, light, words));

        foreach (var app in apps.OrderBy(a => a.KindWords, StringComparer.OrdinalIgnoreCase).ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase))
        {
            var id = "app:" + app.Id;
            if (ids.Contains(id))
                continue;
            var ctx = app.Context;
            var light = app.State switch
            {
                ShowBusState.Gone => CheckLight.Red,
                ShowBusState.Stale => CheckLight.Amber,
                _ => ctx is not null ? ShowContext.LightOf(ctx.Light) : app.Linked ? CheckLight.Green : CheckLight.Grey,
            };
            var state = app.State switch { ShowBusState.Gone => "gone quiet", ShowBusState.Stale => "quiet", _ => app.Linked ? "linked" : "heard" };
            var sub = ctx is not null && app.State == ShowBusState.Live && ctx.Headline.Length > 0 ? ctx.Headline : state + (app.Words.Length > 0 ? " · " + app.Words : "");
            Add(new EyeNode(id, "ShowApp", EyePlane.Control, 2, $"{app.KindWords} {app.Name}".Trim(), sub, light));
            if (app.Linked)
                Link(id, DeskId, EyeEdgeKind.Follows, light, app.State == ShowBusState.Live ? "linked" : state);
            else
                Link(id, DeskId, EyeEdgeKind.Hears, light, app.State == ShowBusState.Live ? "heard on the show network" : state);
            if (ctx is null || app.State == ShowBusState.Gone)
                continue;
            var items = ctx.Items.OrderByDescending(i => ShowContext.LightOf(i.Light) == CheckLight.Red).ThenByDescending(i => ShowContext.LightOf(i.Light) == CheckLight.Amber).Take(12).ToList();
            var itemIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in items)
            {
                var itemId = $"{id}:{item.Id}";
                if (!itemIds.Add(item.Id) || ids.Contains(itemId))
                    continue;
                Add(new EyeNode(itemId, "ShowAppItem", EyePlane.Control, 3, item.Label.Length > 0 ? item.Label : item.Id, item.State, ShowContext.LightOf(item.Light))
                {
                    Words = new[] { $"{app.KindWords} {app.Name}: {item.Kind}", item.State }.Where(w => w.Length > 0).ToList(),
                });
            }
            var linked = new HashSet<string>(StringComparer.Ordinal);
            foreach (var l in ctx.Links)
            {
                if (!itemIds.Contains(l.From) || !itemIds.Contains(l.To) || !Enum.TryParse<EyeEdgeKind>(l.Kind, true, out var kind))
                    continue;
                Link($"{id}:{l.From}", $"{id}:{l.To}", kind, ShowContext.LightOf(l.Light));
                linked.Add(l.From);
                linked.Add(l.To);
            }
            foreach (var item in itemIds.Where(i => !linked.Contains(i)))
                Link(id, $"{id}:{item}", EyeEdgeKind.Serves, CheckLight.Grey);
        }
        return (nodes, edges);
    }

    /// <summary>EyeGraph's problems queue: reds, then ambers, each by band (pictures first), tier and label.</summary>
    public static IReadOnlyList<string> Problems(IReadOnlyList<EyeNode> nodes) =>
        nodes.Where(n => n.Light == CheckLight.Red).OrderBy(n => n.Plane switch { EyePlane.Video => 0, EyePlane.Control => 1, EyePlane.Audio => 2, _ => 3 })
            .ThenBy(n => n.Tier).ThenBy(n => n.Label, StringComparer.OrdinalIgnoreCase)
            .Concat(nodes.Where(n => n.Light == CheckLight.Amber).OrderBy(n => n.Plane switch { EyePlane.Video => 0, EyePlane.Control => 1, EyePlane.Audio => 2, _ => 3 })
                .ThenBy(n => n.Tier).ThenBy(n => n.Label, StringComparer.OrdinalIgnoreCase))
            .Select(n => n.Id).ToList();
}
