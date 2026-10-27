using System.Buffers;
using System.Collections.Frozen;
using System.Globalization;
using System.Text;
using System.Text.Json;
using PlayMap.Core.Wire;

namespace PlayMap.Net.Links;

/// <summary>What Show Bus 1.3 §6's trimming left out of a context, by array: the words of the one grey <c>context:trimmed</c> item a
/// trimmed context carries.</summary>
public readonly record struct ContextLeftOut(int Recent, int Links, int Commands, int Items)
{
    /// <summary>Something was left out.</summary>
    public bool Any => Recent + Links + Commands + Items > 0;

    /// <summary>§6's words: "left out: 12 recent, 40 links, 30 commands, 5 items", each part only when it left one out.</summary>
    public string Words
    {
        get
        {
            var parts = new List<string>(4);
            if (Recent > 0)
                parts.Add(Count(Recent, "recent", "recent"));
            if (Links > 0)
                parts.Add(Count(Links, "link", "links"));
            if (Commands > 0)
                parts.Add(Count(Commands, "command", "commands"));
            if (Items > 0)
                parts.Add(Count(Items, "item", "items"));
            return "left out: " + string.Join(", ", parts);
        }
    }

    private static string Count(int n, string one, string many) => n.ToString(CultureInfo.InvariantCulture) + " " + (n == 1 ? one : many);
}

/// <summary>A context as it goes out in one post (<see cref="ContextFit.ForPost"/>): the body to post, or null when nothing within the
/// bytes can be made of it; its bytes (for a null body, the least the context could be made); the context's own bytes as it was given;
/// what §6's trimming left out; and how many recent lines and item states were cut short.</summary>
public sealed record ContextPost(string? Body, int Bytes, int ContextBytes, ContextLeftOut LeftOut, int RecentCut, int StatesCut)
{
    /// <summary>The body is not the context as it was given.</summary>
    public bool Trimmed => Body is not null && (LeftOut.Any || RecentCut > 0 || StatesCut > 0);

    /// <summary>The body leaves nothing out and cuts nothing but recent lines, each to §6's size for one (<see cref="ContextFit.RecentChars"/>),
    /// the most a reader keeps of one: §6's sizes, not its trimming, so it carries no <c>context:trimmed</c> item and a reader reads it as
    /// it would the context as given.</summary>
    public bool OnlyRecentCut => Body is not null && !LeftOut.Any && StatesCut == 0 && RecentCut > 0;

    /// <summary>The context is not one §6 reads (not a JSON object with its four arrays), so nothing of it is trimmed.</summary>
    public bool Unreadable { get; init; }

    /// <summary>What trimming did, as people read it: "left out: 2 recent, 40 links; cut short: 20 recent, 1 state" ("" when nothing).</summary>
    public string What
    {
        get
        {
            var parts = new List<string>(2);
            if (LeftOut.Any)
                parts.Add(LeftOut.Words);
            var cuts = new List<string>(2);
            if (RecentCut > 0)
                cuts.Add(RecentCut.ToString(CultureInfo.InvariantCulture) + " recent");
            if (StatesCut > 0)
                cuts.Add(StatesCut.ToString(CultureInfo.InvariantCulture) + (StatesCut == 1 ? " state" : " states"));
            if (cuts.Count > 0)
                parts.Add("cut short: " + string.Join(", ", cuts));
            return string.Join("; ", parts);
        }
    }
}

/// <summary>A context fitted to the bytes one post may carry (Show Bus 1.3 §6, §14 <c>context.bytes</c>), at the poster: the context a
/// node builds is never changed, only what one post carries of it. A context within the bytes, counted in UTF-8 as sent, is posted
/// byte for byte. One over them is first brought within §6's size for a recent line (<c>context.recent.chars</c>, the most a reader
/// keeps of one), then trimmed in §6's order, each step only until it fits: the oldest recent lines; grey links, then the other links;
/// commands other than panic, air and transport (by the catalogue's show class of the row the command is), from the end; grey items,
/// then green items, from the end. Only while it is still over: each amber item's state cut short, then each red item's; then amber
/// items, red items, and panic, air and transport commands left out, each from the end. When anything was left out, one grey
/// <c>context:trimmed</c> item, last among the items, then says what in §6's words, within the same bytes; a fit that only cut recent
/// lines to their size left nothing out and carries none. Every element kept is the given one byte for byte but
/// a cut one; every other field of the context (its node, light, headline and peers) is kept as given. When even that is over, nothing
/// is posted. Pure.</summary>
public static class ContextFit
{
    /// <summary>§14 <c>context.bytes</c>: the most one posted context carries, in UTF-8 bytes as sent; Stagewatch answers a body over it
    /// with 413.</summary>
    public const int MaxBytes = 65536;

    /// <summary>§14 <c>context.recent.chars</c>: the most a recent line carries, and the most a reader keeps of one.</summary>
    public const int RecentChars = 200;

    /// <summary>How short §6's last resort cuts an amber or a red item's state, its "…" included: enough to name what is wrong. §6 leaves
    /// the length to the sender and §14 has no row for it; this is Stagewatch's own choice as a sender, taken so the two senders cut alike.
    /// A state no longer than this is never cut.</summary>
    public const int LastResortChars = 40;

    /// <summary>The id of the one grey item a trimmed context carries (§6).</summary>
    public const string TrimmedId = "context:trimmed";

    private static readonly string[] Arrays = ["items", "links", "recent", "commands"];

    /// <summary>Each row's syntax, as the context lists it, and whether the row is a panic, air or transport command (§6 keeps those
    /// last).</summary>
    private static readonly FrozenDictionary<string, bool> AirRows = VerbCatalog.All
        .GroupBy(v => v.Syntax, StringComparer.Ordinal)
        .ToFrozenDictionary(g => g.Key, g => g.Any(v => v.Show is ShowClass.Panic or ShowClass.Air or ShowClass.Transport), StringComparer.Ordinal);

    /// <summary><paramref name="json"/> as one post carries it, within <paramref name="maxBytes"/> UTF-8 bytes.</summary>
    public static ContextPost ForPost(string json, int maxBytes = MaxBytes)
    {
        ArgumentNullException.ThrowIfNull(json);
        var whole = Encoding.UTF8.GetByteCount(json);
        if (whole <= maxBytes)
            return new ContextPost(json, whole, whole, default, 0, 0);
        try
        {
            using var doc = JsonDocument.Parse(json);
            return Shape.Of(doc.RootElement) is { } shape ? shape.Fit(maxBytes, whole) : Unreadable(whole);
        }
        catch (JsonException)
        {
            return Unreadable(whole);
        }
    }

    private static ContextPost Unreadable(int whole) => new(null, whole, whole, default, 0, 0) { Unreadable = true };

    /// <summary><paramref name="text"/> within <paramref name="max"/> UTF-16 units: whole when it fits, else cut and ended with "…",
    /// never half a surrogate pair.</summary>
    internal static string Cut(string text, int max)
    {
        if (text.Length <= max)
            return text;
        var keep = max - 1;
        if (keep > 0 && char.IsHighSurrogate(text[keep - 1]))
            keep--;
        return text[..keep] + "…";
    }

    private static string Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() ?? "" : "";

    /// <summary>One element of a context's array as it is sent: its JSON, its UTF-8 bytes, its light (items and links), whether it is a
    /// panic, air or transport command, and the element itself (an item's or a recent line's, cut by rewriting it).</summary>
    private sealed record Part(string Raw, string Light, bool Air, JsonElement Element)
    {
        public int Bytes { get; } = Encoding.UTF8.GetByteCount(Raw);

        /// <summary>This fit cut it short (a recent line's text, or an item's state).</summary>
        public bool Shortened { get; init; }

        public static Part Of(JsonElement e, string array) => array switch
        {
            "commands" => new Part(e.GetRawText(), "", AirRows.GetValueOrDefault(Str(e, "verb")), default),
            "items" or "recent" => new Part(e.GetRawText(), array == "items" ? LightOf(e) : "", false, e.Clone()),
            _ => new Part(e.GetRawText(), LightOf(e), false, default),
        };

        /// <summary>An element of <paramref name="array"/> this fit wrote itself.</summary>
        public static Part Read(string json, string array)
        {
            using var doc = JsonDocument.Parse(json);
            return Of(doc.RootElement, array);
        }

        /// <summary>An element's light, one of §6's four; anything else is grey (unknown stays unknown).</summary>
        private static string LightOf(JsonElement e) => Str(e, "light") switch
        {
            "red" => "red",
            "amber" => "amber",
            "green" => "green",
            _ => "grey",
        };
    }

    /// <summary>A context taken apart: its top-level fields in their order (the four arrays by name, every other field's JSON as given)
    /// and the four arrays' elements.</summary>
    private sealed class Shape
    {
        private readonly List<(string Name, string? Raw)> _fields = [];
        private readonly Dictionary<string, List<Part>> _arrays = new(StringComparer.Ordinal);
        private int _recent, _links, _commands, _items, _recentCut, _statesCut;

        /// <summary>The four arrays' bytes as they stand: each element's, and the comma between two.</summary>
        private long _bytes;

        private List<Part> Items => _arrays["items"];

        private List<Part> Links => _arrays["links"];

        private List<Part> Recent => _arrays["recent"];

        private List<Part> Commands => _arrays["commands"];

        /// <summary>A context §6 reads: an object whose four arrays are each present and an array; null for anything else.</summary>
        public static Shape? Of(JsonElement root)
        {
            if (root.ValueKind != JsonValueKind.Object)
                return null;
            var shape = new Shape();
            foreach (var p in root.EnumerateObject())
            {
                if (Arrays.Contains(p.Name, StringComparer.Ordinal))
                {
                    if (p.Value.ValueKind != JsonValueKind.Array || shape._arrays.ContainsKey(p.Name))
                        return null;
                    shape._arrays[p.Name] = [.. p.Value.EnumerateArray().Select(e => Part.Of(e, p.Name))];
                    shape._fields.Add((p.Name, null));
                }
                else
                {
                    shape._fields.Add((p.Name, p.Value.GetRawText()));
                }
            }
            return Arrays.All(shape._arrays.ContainsKey) ? shape : null;
        }

        public ContextPost Fit(int maxBytes, int whole)
        {
            // The context with its four arrays empty: what trimming never touches.
            var fixedBytes = Encoding.UTF8.GetByteCount(Json(empty: true));
            _bytes = Arrays.Sum(a => _arrays[a].Count == 0 ? 0 : _arrays[a].Sum(p => (long)p.Bytes) + _arrays[a].Count - 1);

            // Within §6's size for a recent line first: a reader keeps no more of one, so nothing it reads is lost.
            for (var i = 0; i < Recent.Count; i++)
            {
                var text = Str(Recent[i].Element, "text");
                if (text.Length <= RecentChars)
                    continue;
                Replace(Recent, i, Part.Read(With(Recent[i].Element, "text", Cut(text, RecentChars)), "recent"));
            }
            if (fixedBytes + _bytes <= maxBytes)
                return Done(maxBytes, whole, trimmedItem: false);

            // Room for the context:trimmed item at its longest, every count at its most, and the comma before it.
            var most = new ContextLeftOut(Recent.Count, Links.Count, Commands.Count, Items.Count);
            var budget = maxBytes - fixedBytes - (Encoding.UTF8.GetByteCount(TrimmedItem(most)) + 1);
            bool Over() => _bytes > budget;

            while (Over() && Recent.Count > 0)
            {
                Remove(Recent, 0);
                _recent++;
            }
            _links += Drop(Links, l => l.Light == "grey", Over) + Drop(Links, _ => true, Over);
            _commands += Drop(Commands, c => !c.Air, Over);
            _items += Drop(Items, i => i.Light == "grey", Over) + Drop(Items, i => i.Light == "green", Over);
            // The last resort, only while it is still over: the states of ambers then reds cut short, then ambers, reds and the panic,
            // air and transport commands left out.
            foreach (var light in new[] { "amber", "red" })
            {
                for (var i = 0; i < Items.Count && Over(); i++)
                {
                    var state = Str(Items[i].Element, "state");
                    if (Items[i].Light != light || state.Length <= LastResortChars)
                        continue;
                    Replace(Items, i, Part.Read(With(Items[i].Element, "state", Cut(state, LastResortChars)), "items"));
                }
            }
            _items += Drop(Items, i => i.Light == "amber", Over) + Drop(Items, i => i.Light == "red", Over);
            _commands += Drop(Commands, _ => true, Over);
            if (Over())
            {
                // Even with every array empty but its one item, the context is over: nothing is posted.
                var least = fixedBytes + Encoding.UTF8.GetByteCount(TrimmedItem(new ContextLeftOut(_recent, _links, _commands, _items)));
                return new ContextPost(null, least, whole, LeftOut, _recentCut, _statesCut);
            }
            return Done(maxBytes, whole, trimmedItem: LeftOut.Any);
        }

        private ContextLeftOut LeftOut => new(_recent, _links, _commands, _items);

        private ContextPost Done(int maxBytes, int whole, bool trimmedItem)
        {
            if (trimmedItem)
                Items.Add(Part.Read(TrimmedItem(LeftOut), "items"));
            var body = Json(empty: false);
            var bytes = Encoding.UTF8.GetByteCount(body);
            // The parts' sum is the body's bytes; a body over the bytes is never posted, whatever the sum said.
            return bytes <= maxBytes
                ? new ContextPost(body, bytes, whole, LeftOut, _recentCut, _statesCut)
                : new ContextPost(null, bytes, whole, LeftOut, _recentCut, _statesCut);
        }

        /// <summary>Leaves out the elements <paramref name="which"/> takes, the last first, while <paramref name="more"/> holds; how many
        /// it left out.</summary>
        private int Drop(List<Part> parts, Func<Part, bool> which, Func<bool> more)
        {
            var n = 0;
            for (var i = parts.Count - 1; i >= 0 && more(); i--)
            {
                if (!which(parts[i]))
                    continue;
                Remove(parts, i);
                n++;
            }
            return n;
        }

        /// <summary>Leaves out one element, and its bytes and the comma beside it; one cut short before is no longer counted as cut,
        /// since it is not sent.</summary>
        private void Remove(List<Part> parts, int i)
        {
            _bytes -= parts[i].Bytes + (parts.Count > 1 ? 1 : 0);
            if (parts[i].Shortened)
                Shortened(parts, -1);
            parts.RemoveAt(i);
        }

        /// <summary>Puts a cut element in place of one, and its bytes in place of that one's; counted as cut while it is sent.</summary>
        private void Replace(List<Part> parts, int i, Part cut)
        {
            _bytes += cut.Bytes - parts[i].Bytes;
            parts[i] = cut with { Shortened = true };
            Shortened(parts, 1);
        }

        private void Shortened(List<Part> parts, int by)
        {
            if (ReferenceEquals(parts, Recent))
                _recentCut += by;
            else
                _statesCut += by;
        }

        /// <summary>The one grey item a trimmed context carries, saying what was left out in §6's words.</summary>
        private static string TrimmedItem(ContextLeftOut leftOut) => Write(w =>
        {
            w.WriteStartObject();
            w.WriteString("id", TrimmedId);
            w.WriteString("kind", "health");
            w.WriteString("label", "Context");
            w.WriteString("light", "grey");
            w.WriteString("state", leftOut.Words);
            w.WriteEndObject();
        });

        /// <summary><paramref name="element"/>, an object, with its <paramref name="name"/> string set to <paramref name="value"/> and every
        /// other field as given, in its order.</summary>
        private static string With(JsonElement element, string name, string value) => Write(w =>
        {
            w.WriteStartObject();
            foreach (var p in element.EnumerateObject())
            {
                w.WritePropertyName(p.Name);
                if (p.NameEquals(name))
                    w.WriteStringValue(value);
                else
                    w.WriteRawValue(p.Value.GetRawText(), skipInputValidation: true);
            }
            w.WriteEndObject();
        });

        /// <summary>What <paramref name="write"/> writes, as text.</summary>
        private static string Write(Action<Utf8JsonWriter> write)
        {
            var buffer = new ArrayBufferWriter<byte>(256);
            using (var w = new Utf8JsonWriter(buffer))
                write(w);
            return Encoding.UTF8.GetString(buffer.WrittenSpan);
        }

        /// <summary>The context in its fields' order: every field as given, the four arrays as they stand now (or empty).</summary>
        private string Json(bool empty) => Write(w =>
        {
            w.WriteStartObject();
            foreach (var (name, raw) in _fields)
            {
                w.WritePropertyName(name);
                if (raw is not null)
                {
                    w.WriteRawValue(raw, skipInputValidation: true);
                    continue;
                }
                w.WriteStartArray();
                if (!empty)
                {
                    foreach (var p in _arrays[name])
                        w.WriteRawValue(p.Raw, skipInputValidation: true);
                }
                w.WriteEndArray();
            }
            w.WriteEndObject();
        });
    }
}
