using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using PlayMap.Core.Effects;
using PlayMap.Render.Effects;

namespace PlayMap.Engine.Fx;

/// <summary>An ISF file as FX IMPORT and FX CHECK take it: a name, the fragment and vertex sources, the imported pictures (base64),
/// and where it came from ("" for JSON).</summary>
internal sealed record FxBundle(string Name, string Fragment, string? Vertex, ImmutableSortedDictionary<string, string> Images, string Path);

/// <summary>Reading an ISF file from a path or the JSON form, and the compile report FX CHECK and FX IMPORT answer.</summary>
internal static class FxSources
{
    public const int MaxSourceBytes = 256 * 1024;
    public const int MaxImageBytes = 4 * 1024 * 1024;

    /// <summary>The bundle from "{"name","fs","vs"?,"images"?:{name:base64}}" or a path to a .fs file (its .vs beside it, its
    /// imports relative to it); null with the reason when it cannot be read.</summary>
    public static FxBundle? Read(string text, out string why)
    {
        why = "";
        var t = text.Trim();
        if (t.StartsWith('{'))
            return FromJson(t, out why);
        var path = t.Trim('"');
        if (!File.Exists(path))
        {
            why = $"no file {path}";
            return null;
        }
        if (new FileInfo(path).Length > MaxSourceBytes)
        {
            why = $"{Path.GetFileName(path)} is larger than {MaxSourceBytes / 1024} KB";
            return null;
        }
        var fs = File.ReadAllText(path);
        var vsPath = Path.ChangeExtension(path, ".vs");
        var vs = File.Exists(vsPath) ? File.ReadAllText(vsPath) : null;
        var images = ImmutableSortedDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        var doc = IsfParser.Parse(fs);
        var total = 0L;
        foreach (var imp in doc.Imported)
        {
            var file = System.IO.Path.GetFullPath(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path)) ?? ".", imp.Path));
            if (!File.Exists(file))
            {
                why = $"{Path.GetFileName(path)} imports {imp.Path}, which is not beside it";
                return null;
            }
            var bytes = File.ReadAllBytes(file);
            total += bytes.Length;
            if (total > MaxImageBytes)
            {
                why = $"the imported pictures are larger than {MaxImageBytes / (1024 * 1024)} MB";
                return null;
            }
            images[imp.Name] = Convert.ToBase64String(bytes);
        }
        return new FxBundle(System.IO.Path.GetFileNameWithoutExtension(path), fs, vs, images.ToImmutable(), path);
    }

    private static FxBundle? FromJson(string json, out string why)
    {
        why = "";
        JsonObject obj;
        try
        {
            obj = JsonNode.Parse(json) as JsonObject ?? throw new JsonException("not an object");
        }
        catch (JsonException ex)
        {
            why = "the JSON does not read: " + ex.Message;
            return null;
        }
        var fs = obj["fs"]?.GetValueKind() == JsonValueKind.String ? obj["fs"]!.GetValue<string>() : null;
        if (string.IsNullOrWhiteSpace(fs))
        {
            why = "the JSON needs \"fs\": the fragment file's text";
            return null;
        }
        if (fs.Length > MaxSourceBytes)
        {
            why = $"\"fs\" is larger than {MaxSourceBytes / 1024} KB";
            return null;
        }
        var vs = obj["vs"]?.GetValueKind() == JsonValueKind.String ? obj["vs"]!.GetValue<string>() : null;
        var name = obj["name"]?.GetValueKind() == JsonValueKind.String ? obj["name"]!.GetValue<string>().Trim() : "";
        var images = ImmutableSortedDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        if (obj["images"] is JsonObject imgs)
        {
            var total = 0L;
            foreach (var (k, v) in imgs)
            {
                if (v?.GetValueKind() != JsonValueKind.String)
                {
                    why = $"image {k} is not a base64 string";
                    return null;
                }
                var s = v.GetValue<string>();
                total += s.Length;
                if (total > MaxImageBytes * 4 / 3)
                {
                    why = $"the imported pictures are larger than {MaxImageBytes / (1024 * 1024)} MB";
                    return null;
                }
                images[k] = s;
            }
        }
        return new FxBundle(name, fs, string.IsNullOrWhiteSpace(vs) ? null : vs, images.ToImmutable(), "");
    }

    /// <summary>The report: whether the file reads, its role, inputs, passes, persistent buffers and issues by line, and per
    /// dialect whether it compiled on this node (null where this node has no such context, with words).</summary>
    public static (bool Ok, string Json) Report(IsfDocument doc, IsfProgramSource? glsl, IsfProgramSource? es, FxCompileReport? compiled,
        bool compiledIsEs, string laneMode)
    {
        var issues = new List<IsfIssue>();
        issues.AddRange(glsl?.Issues ?? doc.Issues);
        if (compiled is { Ok: false, Error: { } err } && (compiledIsEs ? es : glsl) is { } src)
            issues.AddRange(src.LineMap.Read(err, err.Contains("(vertex)", StringComparison.Ordinal)));
        var ok = doc.Valid && (glsl?.Issues.IsEmpty ?? false) && compiled is not { Ok: false };
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteBoolean("ok", ok);
            w.WriteString("role", doc.Valid ? doc.Role.ToString().ToLowerInvariant() : "");
            w.WriteStartArray("inputs");
            foreach (var i in doc.Inputs)
                w.WriteStringValue(i.Name);
            w.WriteEndArray();
            w.WriteNumber("passes", doc.Valid ? doc.Passes.Length : 0);
            w.WriteStartArray("persistent");
            foreach (var p in doc.Passes.Where(p => p.Persistent))
                w.WriteStringValue(p.Target);
            w.WriteEndArray();
            w.WriteStartArray("issues");
            foreach (var i in issues)
            {
                w.WriteStartObject();
                w.WriteNumber("line", i.Line);
                w.WriteNumber("col", i.Column);
                w.WriteString("words", i.Words);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteStartObject("dialects");
            Dialect(w, "glsl330", compiledIsEs ? null : compiled, laneMode);
            Dialect(w, "es300", compiledIsEs ? compiled : null, laneMode);
            w.WriteEndObject();
            w.WriteEndObject();
        }
        return (ok, System.Text.Encoding.UTF8.GetString(ms.ToArray()));
    }

    private static void Dialect(Utf8JsonWriter w, string name, FxCompileReport? compiled, string laneMode)
    {
        w.WriteStartObject(name);
        if (compiled is null)
        {
            w.WriteNull("ok");
            w.WriteString("words", laneMode == "none" ? "no GL context on this node" : "this node's context compiles the other dialect");
        }
        else
        {
            w.WriteBoolean("ok", compiled.Ok);
            w.WriteString("words", compiled.Ok ? $"compiled and linked ({compiled.Lane})" : compiled.Error ?? "did not build");
        }
        w.WriteEndObject();
    }
}
