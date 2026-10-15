using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PlayMap.Core.Content;
using PlayMap.Core.Model;

namespace PlayMap.Engine.Companion;

/// <summary>INDEX as a controller reads it: the show's structure, its <see cref="Sig"/> (what a deck's layout depends on: the ids
/// and kinds of every listed entity, each item's source kind, each generator's parameter names, each section's row ids and kinds
/// and its preset hint), its <see cref="Labels"/> (the names and labels shown on keys) and its <see cref="Lights"/> (each section
/// row's light and state, which move with the show, not the project). A rename moves only the labels.</summary>
public sealed record IndexDocument(string Json, string Sig, string Labels, long Revision, string Lights);

/// <summary>Writes INDEX from a project, the published snapshot and every package's section (<see cref="IIndexContributor"/>):
/// playlists and their items, surfaces, projectors, outputs, devices, generators, canvases, the media count, then each section's
/// rows with its preset hint, as data. Keys are added at the end of objects only.</summary>
public static class IndexWriter
{
    public const int Version = 1;

    public static IndexDocument Write(Project project, long revision, EngineSnapshot snapshot, IEnumerable<IIndexContributor> sections)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(sections);
        var sig = new StringBuilder();
        var labels = new StringBuilder();
        var lights = new StringBuilder();
        var body = new ArrayBufferWriter<byte>(4096);
        using (var w = new Utf8JsonWriter(body))
        {
            w.WriteStartObject();
            WriteBody(w, project, snapshot, sections.OrderBy(s => s.Section, StringComparer.Ordinal).ToList(), sig, labels, lights);
            w.WriteEndObject();
        }
        var sigHex = Hash(sig);
        var labelsHex = Hash(labels);
        // The head goes in front of the body: "v", "revision", "sig", "labels", then the sections.
        var head = string.Create(CultureInfo.InvariantCulture, $"{{\"v\":{Version},\"revision\":{revision},\"sig\":\"{sigHex}\",\"labels\":\"{labelsHex}\"");
        var rest = Encoding.UTF8.GetString(body.WrittenSpan);
        var json = rest.Length > 2 ? head + "," + rest[1..] : head + "}";
        return new IndexDocument(json, sigHex, labelsHex, revision, Hash(lights));
    }

    private static string Hash(StringBuilder text) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())))[..16];

    private static void WriteBody(Utf8JsonWriter w, Project project, EngineSnapshot snapshot, List<IIndexContributor> sections,
        StringBuilder sig, StringBuilder labels, StringBuilder lights)
    {
        // A project read from a file is not trusted to hold each id once: the first wins, and INDEX (STATE's project.index) never throws.
        var lengths = new Dictionary<string, MediaItem>(project.Media.Length, StringComparer.Ordinal);
        foreach (var m in project.Media)
            lengths.TryAdd(m.Id, m);
        w.WriteStartArray("playlists");
        foreach (var list in project.Playlists)
        {
            Entity(sig, labels, "playlist", list.Id, list.Name);
            w.WriteStartObject();
            w.WriteString("id", list.Id);
            w.WriteString("name", list.Name);
            w.WriteBoolean("loop", list.Loop);
            NullableString(w, "syncGroup", list.SyncGroup);
            w.WriteStartArray("items");
            foreach (var item in list.Items)
            {
                Entity(sig, labels, "item:" + item.Source.Kind, item.Id, item.Label);
                w.WriteStartObject();
                w.WriteString("id", item.Id);
                w.WriteString("label", item.Label);
                w.WriteString("source", item.Source.ToString());
                w.WriteString("kind", KindOf(item.Source, lengths));
                var length = LengthNs(item, lengths);
                if (length > 0)
                    w.WriteNumber("lengthMs", length / 1_000_000);
                else
                    w.WriteNull("lengthMs");
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        w.WriteEndArray();

        var stage = project.Stage;
        w.WriteStartArray("surfaces");
        foreach (var s in stage.Surfaces)
        {
            Entity(sig, labels, "surface", s.Id, s.Name + "|" + string.Join(",", s.Tags));
            w.WriteStartObject();
            w.WriteString("id", s.Id);
            w.WriteString("name", s.Name);
            w.WriteStartArray("tags");
            foreach (var t in s.Tags)
                w.WriteStringValue(t);
            w.WriteEndArray();
            w.WriteString("source", s.Content.Source.ToString());
            w.WriteEndObject();
        }
        w.WriteEndArray();

        w.WriteStartArray("projectors");
        foreach (var pj in stage.Projectors)
        {
            Entity(sig, labels, "projector", pj.Id, pj.Name);
            w.WriteStartObject();
            w.WriteString("id", pj.Id);
            w.WriteString("name", pj.Name);
            NullableString(w, "output", project.Outputs.FirstOrDefault(o => o.ProjectorId == pj.Id)?.Id);
            NullableString(w, "device", stage.Devices.FirstOrDefault(d => d.ProjectorId == pj.Id)?.Id);
            w.WriteEndObject();
        }
        w.WriteEndArray();

        w.WriteStartArray("outputs");
        foreach (var o in project.Outputs)
        {
            Entity(sig, labels, "output", o.Id, o.Name);
            w.WriteStartObject();
            w.WriteString("id", o.Id);
            w.WriteString("name", o.Name);
            w.WriteString("kind", o.Kind.ToString().ToLowerInvariant());
            NullableString(w, "projector", o.ProjectorId);
            w.WriteBoolean("enabled", o.Enabled);
            w.WriteEndObject();
        }
        w.WriteEndArray();

        w.WriteStartArray("devices");
        foreach (var d in stage.Devices)
        {
            Entity(sig, labels, "device:" + d.Protocol, d.Id, d.Name);
            w.WriteStartObject();
            w.WriteString("id", d.Id);
            w.WriteString("name", d.Name);
            w.WriteString("protocol", d.Protocol);
            NullableString(w, "projector", d.ProjectorId.Length > 0 ? d.ProjectorId : null);
            w.WriteEndObject();
        }
        w.WriteEndArray();

        w.WriteStartArray("generators");
        foreach (var g in project.Generators)
        {
            var kind = GeneratorCatalog.Find(g.Kind);
            var names = string.Join(",", kind?.Params.Select(p => p.Name) ?? []);
            Entity(sig, labels, "generator:" + g.Kind + ":" + names, g.Id, g.Name);
            w.WriteStartObject();
            w.WriteString("id", g.Id);
            w.WriteString("name", g.Name);
            w.WriteString("kind", g.Kind);
            w.WriteStartArray("params");
            foreach (var p in kind?.Params ?? [])
            {
                w.WriteStartObject();
                w.WriteString("name", p.Name);
                w.WriteString("kind", p.Type.ToString().ToLowerInvariant());
                if (p.Type is ParamType.Number or ParamType.Integer)
                {
                    w.WriteNumber("min", p.Min);
                    w.WriteNumber("max", p.Max);
                    w.WriteNumber("step", p.Type == ParamType.Integer ? 1 : Step(p.Min, p.Max));
                }
                if (p.Choices.Count > 0)
                {
                    w.WriteStartArray("choices");
                    foreach (var c in p.Choices)
                        w.WriteStringValue(c);
                    w.WriteEndArray();
                }
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        w.WriteEndArray();

        w.WriteStartArray("canvases");
        foreach (var c in project.Canvases)
        {
            Entity(sig, labels, "canvas", c.Id, c.Name);
            w.WriteStartObject();
            w.WriteString("id", c.Id);
            w.WriteString("name", c.Name);
            w.WriteStartArray("layers");
            foreach (var l in c.Layers)
            {
                Entity(sig, labels, "layer", l.Id, l.Name);
                w.WriteStartObject();
                w.WriteString("id", l.Id);
                w.WriteString("name", l.Name);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        w.WriteEndArray();

        // A library of thousands is never pushed: keys reach media through playlists.
        w.WriteStartObject("media");
        w.WriteNumber("count", project.Media.Length);
        w.WriteEndObject();

        w.WriteStartArray("sections");
        foreach (var section in sections)
            WriteSection(w, section, project, snapshot, sig, labels, lights);
        w.WriteEndArray();
    }

    /// <summary>One package's section: its key, its preset hint (data the module builds presets from) and its rows. A section that
    /// faults writes no rows rather than taking INDEX down.</summary>
    private static void WriteSection(Utf8JsonWriter w, IIndexContributor section, Project project, EngineSnapshot snapshot,
        StringBuilder sig, StringBuilder labels, StringBuilder lights)
    {
        var hint = section.Hint;
        sig.Append("section|").Append(section.Section).Append('|').Append(hint.RowKind).Append('\n');
        labels.Append("section|").Append(hint.Title).Append('\n');
        w.WriteStartObject();
        w.WriteString("section", section.Section);
        w.WriteStartObject("hint");
        w.WriteString("title", hint.Title);
        w.WriteString("rowKind", hint.RowKind);
        w.WriteStartArray("keys");
        foreach (var k in hint.Keys)
        {
            sig.Append("key|").Append(k.DownLine).Append('|').Append(k.Feedback).Append('|').Append(k.Press).Append('\n');
            labels.Append("key|").Append(k.Label).Append('\n');
            w.WriteStartObject();
            w.WriteString("label", k.Label);
            w.WriteString("line", k.DownLine);
            NullableString(w, "feedback", k.Feedback);
            w.WriteString("press", k.Press);
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteEndObject();
        var rows = new ArrayBufferWriter<byte>(512);
        try
        {
            using (var rw = new Utf8JsonWriter(rows))
                section.WriteRows(project, snapshot, rw);
            using var doc = JsonDocument.Parse(rows.WrittenMemory);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("a section's rows are a JSON array");
            foreach (var row in doc.RootElement.EnumerateArray())
            {
                sig.Append("row|").Append(Text(row, "id")).Append('|').Append(Text(row, "kind")).Append('|').Append(Text(row, "parent")).Append('\n');
                labels.Append("row|").Append(Text(row, "label")).Append('\n');
                lights.Append(section.Section).Append('|').Append(Text(row, "id")).Append('|').Append(Text(row, "light")).Append('|')
                    .Append(Text(row, "state")).Append('\n');
            }
            w.WritePropertyName("rows");
            doc.RootElement.WriteTo(w);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // One package's section never takes INDEX, or STATE's project.index, down with it: the section says why instead.
            w.WriteStartArray("rows");
            w.WriteEndArray();
            w.WriteString("fault", $"{section.Section}'s rows could not be written: {ex.Message}");
        }
        w.WriteEndObject();
    }

    private static string Text(JsonElement row, string name) =>
        row.ValueKind == JsonValueKind.Object && row.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";

    private static void Entity(StringBuilder sig, StringBuilder labels, string kind, string id, string label)
    {
        sig.Append(kind).Append('|').Append(id).Append('\n');
        labels.Append(id).Append('|').Append(label).Append('\n');
    }

    private static void NullableString(Utf8JsonWriter w, string name, string? value)
    {
        if (value is null)
            w.WriteNull(name);
        else
            w.WriteString(name, value);
    }

    private static double Step(double min, double max)
    {
        var span = max - min;
        if (!(span > 0) || !double.IsFinite(span))
            return 1;
        var step = Math.Pow(10, Math.Floor(Math.Log10(span)) - 2);
        return step;
    }

    private static string KindOf(SourceRef source, Dictionary<string, MediaItem> media) => source.Kind switch
    {
        SourceKind.Media => media.TryGetValue(source.Id, out var m) ? m.Kind.ToString().ToLowerInvariant() : "missing",
        _ => source.Kind.ToString().ToLowerInvariant(),
    };

    private static long LengthNs(PlaylistItem item, Dictionary<string, MediaItem> media)
    {
        if (item.Source.Kind != SourceKind.Media || !media.TryGetValue(item.Source.Id, out var m) || m.Info.DurationNs <= 0)
            return 0;
        var end = item.OutNs > 0 ? Math.Min(item.OutNs, m.Info.DurationNs) : m.Info.DurationNs;
        return Math.Max(0, end - item.InNs);
    }
}
