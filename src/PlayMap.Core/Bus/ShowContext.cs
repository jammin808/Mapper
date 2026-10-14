using System.Buffers;
using System.Collections.Frozen;
using System.Text;
using System.Text.Json;
using PlayMap.Core.Wire;

namespace PlayMap.Core.Bus;

/// <summary>A light (Show Bus §6): red = wrong now, amber = needs attention, green = fine, grey = unknown.
/// Unknown stays unknown — never guessed.</summary>
public enum Light
{
    Grey,
    Green,
    Amber,
    Red,
}

public sealed record ContextItem(string Id, string Kind, string Label, Light Light, string State);

public sealed record ContextLink(string From, string To, string Kind, Light Light);

/// <summary>The only link kinds Patterns' Eye draws (it drops any other silently).</summary>
public static class ContextLinkKinds
{
    public const string Feeds = "feeds", Shows = "shows", Drives = "drives", Carries = "carries", Controls = "controls",
        Mirrors = "mirrors", Follows = "follows", Hears = "hears", Routes = "routes", Serves = "serves", Sends = "sends";
    public static readonly FrozenSet<string> All = new[] { Feeds, Shows, Drives, Carries, Controls, Mirrors, Follows, Hears, Routes,
        Serves, Sends }.ToFrozenSet(StringComparer.Ordinal);
    public static bool IsEyeVerb(string kind) => All.Contains(kind);
}

public sealed record ContextRecent(long AtUnixMs, string Text);

public sealed record ContextCommand(string Verb, VerbRisk Risk, string Words);

public sealed record ContextPeer(string Kind, string Id, string Name, Light Light, string Headline);

/// <summary>What an AI (or Patterns' Eye) needs to understand this node, in the contract's one shape and key
/// order: v, node, at, light, headline, items, links, recent, commands, peers. The light is the worst of
/// the items'; the headline is computed from facts, never by a model. No secrets, no private content.</summary>
public sealed record ShowContext(
    string NodeKind,
    string NodeId,
    string NodeName,
    long AtUnixMs,
    string Headline,
    IReadOnlyList<ContextItem> Items,
    IReadOnlyList<ContextLink> Links,
    IReadOnlyList<ContextRecent> Recent,
    IReadOnlyList<ContextCommand> Commands,
    IReadOnlyList<ContextPeer> Peers)
{
    public Light Light => Items.Count == 0 ? Light.Grey : Items.Max(i => i.Light);

    public static string LightWord(Light l) => l switch
    {
        Light.Red => "red",
        Light.Amber => "amber",
        Light.Green => "green",
        _ => "grey",
    };

    public static Light ParseLight(string? s) => s switch
    {
        "red" => Light.Red,
        "amber" => Light.Amber,
        "green" => Light.Green,
        _ => Light.Grey,
    };

    public static string RiskWord(VerbRisk r) => r switch
    {
        VerbRisk.Low => "low",
        VerbRisk.Review => "review",
        _ => "high",
    };

    /// <summary>The commands an assistant may propose: every mutating verb with its risk, node-to-node verbs left out; the ones
    /// that move a panic, the air, a transport or Show Mode first (the Eye keeps the first 64), the rest in catalogue order.</summary>
    public static IReadOnlyList<ContextCommand> CommandsFromCatalog() => CommandsFromCatalog(AllRows.Instance);

    /// <summary>The verbs an assistant may propose on a node: the rows it answers now that change something, never a node-to-node
    /// one, the air's first.</summary>
    public static IReadOnlyList<ContextCommand> CommandsFromCatalog(IVerbAvailability availability) =>
        VerbCatalog.All.Where(v => v.Mutates && !v.Internal && availability.IsAvailable(v))
            .OrderBy(v => v.Show switch
            {
                ShowClass.Panic => 0,
                ShowClass.Air => 1,
                ShowClass.Transport => 2,
                ShowClass.Show => 3,
                _ => 4,
            })
            .Select(v => new ContextCommand(v.Syntax, v.Risk, v.Words)).ToList();

    public string ToJson()
    {
        var buffer = new ArrayBufferWriter<byte>(4096);
        using (var w = new Utf8JsonWriter(buffer))
            Write(w);
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    public void Write(Utf8JsonWriter w)
    {
        ArgumentNullException.ThrowIfNull(w);
        w.WriteStartObject();
        w.WriteNumber("v", 1);
        w.WriteStartObject("node");
        w.WriteString("t", NodeKind);
        w.WriteString("id", NodeId);
        w.WriteString("name", NodeName);
        w.WriteEndObject();
        w.WriteNumber("at", AtUnixMs);
        w.WriteString("light", LightWord(Light));
        w.WriteString("headline", Headline);
        w.WriteStartArray("items");
        foreach (var i in Items)
        {
            w.WriteStartObject();
            w.WriteString("id", i.Id);
            w.WriteString("kind", i.Kind);
            w.WriteString("label", i.Label);
            w.WriteString("light", LightWord(i.Light));
            w.WriteString("state", i.State);
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteStartArray("links");
        foreach (var l in Links)
        {
            w.WriteStartObject();
            w.WriteString("from", l.From);
            w.WriteString("to", l.To);
            w.WriteString("kind", l.Kind);
            w.WriteString("light", LightWord(l.Light));
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteStartArray("recent");
        foreach (var r in Recent.TakeLast(20))
        {
            w.WriteStartObject();
            w.WriteNumber("at", r.AtUnixMs);
            w.WriteString("text", r.Text);
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteStartArray("commands");
        foreach (var c in Commands)
        {
            w.WriteStartObject();
            w.WriteString("verb", c.Verb);
            w.WriteString("risk", RiskWord(c.Risk));
            w.WriteString("words", c.Words);
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteStartArray("peers");
        foreach (var p in Peers)
        {
            w.WriteStartObject();
            w.WriteString("t", p.Kind);
            w.WriteString("id", p.Id);
            w.WriteString("name", p.Name);
            w.WriteString("light", LightWord(p.Light));
            w.WriteString("headline", p.Headline);
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteEndObject();
    }

    /// <summary>Reads a peer's context with the contract's limits (≤ 512 KB, ≤ 200 items, ≤ 64 commands,
    /// ≤ 32 peers; an unknown light is grey). Null when it is not a context.</summary>
    public static ShowContext? Parse(string json)
    {
        if (string.IsNullOrEmpty(json) || json.Length > 512 * 1024)
            return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object || !r.TryGetProperty("node", out var node) || node.ValueKind != JsonValueKind.Object)
                return null;
            var items = Arr(r, "items", 200).Select(e => new ContextItem(S(e, "id"), S(e, "kind"), S(e, "label"), ParseLight(S(e, "light")), S(e, "state"))).ToList();
            var links = Arr(r, "links", 400).Select(e => new ContextLink(S(e, "from"), S(e, "to"), S(e, "kind"), ParseLight(S(e, "light")))).ToList();
            var recent = Arr(r, "recent", 20).Select(e => new ContextRecent(L(e, "at"), S(e, "text"))).ToList();
            var commands = Arr(r, "commands", 64).Select(e => new ContextCommand(S(e, "verb"),
                S(e, "risk") switch { "low" => VerbRisk.Low, "review" => VerbRisk.Review, _ => VerbRisk.High }, S(e, "words"))).ToList();
            var peers = Arr(r, "peers", 32).Select(e => new ContextPeer(S(e, "t"), S(e, "id"), S(e, "name"), ParseLight(S(e, "light")), S(e, "headline"))).ToList();
            return new ShowContext(S(node, "t"), S(node, "id"), S(node, "name"), L(r, "at"), S(r, "headline"), items, links, recent, commands, peers);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static IEnumerable<JsonElement> Arr(JsonElement e, string name, int max) =>
        e.TryGetProperty(name, out var a) && a.ValueKind == JsonValueKind.Array
            ? a.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Object).Take(max).Select(x => x.Clone()).ToList()
            : [];

    private static string S(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() ?? "" : "";

    private static long L(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt64(out var v) ? v : 0;
}

/// <summary>Every row of the catalogue, whether a node answers it or not (the vocabulary as written).</summary>
internal sealed class AllRows : IVerbAvailability
{
    public static readonly AllRows Instance = new();

    public bool IsAvailable(VerbSpec spec) => true;
}
