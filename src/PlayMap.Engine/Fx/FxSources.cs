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
    /// imports relative to it and inside its folder, each a PNG or JPEG file); null with the reason when it cannot be read. A
    /// file's IMPORTED paths never reach outside its own folder, so no import carries another file of this machine into the
    /// show.</summary>
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
        if (File.Exists(vsPath) && new FileInfo(vsPath).Length > MaxSourceBytes)
        {
            why = $"{Path.GetFileName(vsPath)} is larger than {MaxSourceBytes / 1024} KB";
            return null;
        }
        var vs = File.Exists(vsPath) ? File.ReadAllText(vsPath) : null;
        var images = ImmutableSortedDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        var doc = IsfParser.Parse(fs);
        var total = 0L;
        var folder = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path)) ?? ".";
        var inside = folder.EndsWith(System.IO.Path.DirectorySeparatorChar) ? folder : folder + System.IO.Path.DirectorySeparatorChar;
        var pathCase = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        foreach (var imp in doc.Imported)
        {
            var file = System.IO.Path.GetFullPath(System.IO.Path.Combine(folder, imp.Path));
            if (!file.StartsWith(inside, pathCase))
            {
                why = $"{Path.GetFileName(path)} imports {imp.Path}, which is outside its folder";
                return null;
            }
            if (!File.Exists(file))
            {
                why = $"{Path.GetFileName(path)} imports {imp.Path}, which is not beside it";
                return null;
            }
            total += new FileInfo(file).Length;
            if (total > MaxImageBytes)
            {
                why = $"the imported pictures are larger than {MaxImageBytes / (1024 * 1024)} MB";
                return null;
            }
            var bytes = File.ReadAllBytes(file);
            if (!IsPicture(bytes))
            {
                why = $"{Path.GetFileName(path)} imports {imp.Path}, which is not a PNG or JPEG file";
                return null;
            }
            images[imp.Name] = Convert.ToBase64String(bytes);
        }
        return new FxBundle(System.IO.Path.GetFileNameWithoutExtension(path), fs, vs, images.ToImmutable(), path);
    }

    /// <summary>The file starts as a PNG (89 50 4E 47) or a JPEG (FF D8 FF) does.</summary>
    private static bool IsPicture(ReadOnlySpan<byte> bytes) => bytes is [0x89, 0x50, 0x4E, 0x47, ..] or [0xFF, 0xD8, 0xFF, ..];

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
    /// dialect whether it compiled on this node (null with words where it was not compiled: no such context here, or issues
    /// to put right before a compile). <paramref name="compiledIsEs"/> says which dialect this node's context compiles.</summary>
    public static (bool Ok, string Json) Report(IsfDocument doc, IsfProgramSource? glsl, IsfProgramSource? es, FxCompileReport? compiled,
        bool compiledIsEs, string laneMode, System.Collections.Immutable.ImmutableArray<FxStage> stages = default)
    {
        var issues = new List<IsfIssue>();
        issues.AddRange(glsl?.Issues ?? doc.Issues);
        if (compiled is { Ok: false, Error: { } err } && (compiledIsEs ? es : glsl) is { } src)
            issues.AddRange(src.LineMap.Read(err, err.Contains("(vertex)", StringComparison.Ordinal)));
        var ok = doc.Valid && (glsl?.Issues.IsEmpty ?? false) && compiled is not { Ok: false } && !(!stages.IsDefault && stages.Any(st => st.Ok == false));
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
            Dialect(w, "glsl330", compiledIsEs ? null : compiled, laneMode, mine: !compiledIsEs);
            Dialect(w, "es300", compiledIsEs ? compiled : null, laneMode, mine: compiledIsEs);
            w.WriteEndObject();
            // A package's stages (EngineServices.FxStages), in the road's order; none when no package checks any.
            if (!stages.IsDefault)
            {
                w.WriteStartArray("stages");
                foreach (var st in stages)
                {
                    w.WriteStartObject();
                    w.WriteString("name", st.Name);
                    if (st.Ok is { } stageOk)
                        w.WriteBoolean("ok", stageOk);
                    else
                        w.WriteNull("ok");
                    w.WriteString("words", st.Words);
                    w.WriteNumber("line", st.Line);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
            }
            w.WriteEndObject();
        }
        return (ok, System.Text.Encoding.UTF8.GetString(ms.ToArray()));
    }

    private static void Dialect(Utf8JsonWriter w, string name, FxCompileReport? compiled, string laneMode, bool mine)
    {
        w.WriteStartObject(name);
        if (compiled is null)
        {
            w.WriteNull("ok");
            w.WriteString("words", laneMode == "none" ? "no GL context on this node"
                : mine ? "not compiled: the file's issues come first" : "this node's context compiles the other dialect");
        }
        else
        {
            w.WriteBoolean("ok", compiled.Ok);
            w.WriteString("words", compiled.Ok ? $"compiled and linked ({compiled.Lane})" : compiled.Error ?? "did not build");
        }
        w.WriteEndObject();
    }
}
