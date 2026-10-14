using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;

namespace PlayMap.Core.Effects;

/// <summary>What an ISF file is for: a filter reads "inputImage", a transition reads "startImage" and "endImage" with
/// "progress", a generator reads no picture.</summary>
public enum IsfRole
{
    Filter,
    Transition,
    Generator,
}

/// <summary>The ISF input types (ISF v2 §INPUTS).</summary>
public enum IsfInputType
{
    Event,
    Bool,
    Long,
    Float,
    Point2D,
    Color,
    Image,
    Audio,
    AudioFft,
}

/// <summary>A problem in an ISF file at its line and column (1-based; 0 when the file has no line for it), in words.</summary>
public readonly record struct IsfIssue(int Line, int Column, string Words);

/// <summary>One declared input. Numbers travel as arrays: one value for float, long and bool, two for point2D, four for
/// color (r, g, b, a); empty when the file gives none.</summary>
public sealed record IsfInput(string Name, IsfInputType Type, string Label, ImmutableArray<double> Default, ImmutableArray<double> Min,
    ImmutableArray<double> Max, ImmutableArray<double> Identity, ImmutableArray<int> Values, ImmutableArray<string> Labels, int MaxSamples)
{
    /// <summary>The input is a picture in the shader (an image, or sound sent as an image).</summary>
    public bool IsImage => Type is IsfInputType.Image or IsfInputType.Audio or IsfInputType.AudioFft;
}

/// <summary>One rendering pass: the buffer it draws into ("" = the output), whether that buffer lives across frames, its
/// precision, and its size expressions ("" = the render size).</summary>
public sealed record IsfPass(string Target, bool Persistent, bool Float, string WidthExpr, string HeightExpr);

/// <summary>An image file the ISF file imports, by its buffer name and its path relative to the file.</summary>
public sealed record IsfImported(string Name, string Path);

/// <summary>A parsed ISF file (v2, and v1's PERSISTENT_BUFFERS folded into its passes): the JSON header's facts and the GLSL
/// body after it. <see cref="Body"/> starts at line <see cref="BodyLine"/> of the file, so a compiler's line in the body maps
/// back to the file. A file that cannot be read is <see cref="Valid"/> false with its issues; parsing never throws.</summary>
public sealed record IsfDocument(
    bool Valid,
    string Body,
    int BodyLine,
    string IsfVsn,
    string Vsn,
    string Description,
    string Credit,
    ImmutableArray<string> Categories,
    ImmutableArray<IsfInput> Inputs,
    ImmutableArray<IsfPass> Passes,
    ImmutableArray<IsfImported> Imported,
    ImmutableArray<string> Images,
    IsfRole Role,
    ImmutableArray<IsfIssue> Issues)
{
    /// <summary>A pass keeps a buffer from frame to frame: the effect integrates and is not a pure function of show time.</summary>
    public bool HistoryDependent => Passes.Any(p => p.Persistent);

    /// <summary>The file reads sound (an "audio" or "audioFFT" input).</summary>
    public bool ReadsAudio => Inputs.Any(i => i.Type is IsfInputType.Audio or IsfInputType.AudioFft);

    public IsfInput? Input(string name) => Inputs.FirstOrDefault(i => string.Equals(i.Name, name, StringComparison.Ordinal));
}

/// <summary>Reads ISF files: the JSON header comment at the top and the GLSL after it (ISF v2, with v1's spellings).</summary>
public static class IsfParser
{
    private static readonly JsonDocumentOptions JsonOptions = new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };

    public static IsfDocument Parse(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var issues = ImmutableArray.CreateBuilder<IsfIssue>();
        var open = FirstCommentStart(source);
        if (open < 0)
            return Invalid(source, issues, new IsfIssue(1, 1, "an ISF file starts with its JSON header in a /* … */ comment"));
        var close = source.IndexOf("*/", open + 2, StringComparison.Ordinal);
        if (close < 0)
            return Invalid(source, issues, new IsfIssue(LineOf(source, open), 1, "the header comment is never closed with */"));
        var headerLine = LineOf(source, open);
        var json = source[(open + 2)..close];
        var body = source[(close + 2)..];
        var bodyLine = LineOf(source, close + 2);
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            var line = headerLine + (int)(ex.LineNumber ?? 0);
            return Invalid(source, issues, new IsfIssue(line, (int)(ex.BytePositionInLine ?? 0) + 1, "the JSON header does not read: " + FirstSentence(ex.Message)));
        }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return Invalid(source, issues, new IsfIssue(headerLine, 1, "the JSON header is not an object"));
            var inputs = ReadInputs(root, headerLine, issues);
            var persistentV1 = ReadPersistentBuffers(root);
            var passes = ReadPasses(root, persistentV1, headerLine, issues);
            var imported = ReadImported(root, headerLine, issues);
            var images = ImmutableArray.CreateBuilder<string>();
            void AddImage(string name)
            {
                if (name.Length > 0 && !images.Contains(name))
                    images.Add(name);
            }
            foreach (var i in inputs.Where(i => i.IsImage))
                AddImage(i.Name);
            foreach (var p in passes)
                AddImage(p.Target);
            foreach (var b in persistentV1.Keys)
                AddImage(b);
            foreach (var m in imported)
                AddImage(m.Name);
            var names = inputs.Select(i => i.Name).ToHashSet(StringComparer.Ordinal);
            var role = names.Contains("inputImage") ? IsfRole.Filter
                : names.Contains("startImage") && names.Contains("endImage") ? IsfRole.Transition
                : IsfRole.Generator;
            return new IsfDocument(true, body, bodyLine, Str(root, "ISFVSN"), Str(root, "VSN"), Str(root, "DESCRIPTION"), Str(root, "CREDIT"),
                Strings(root, "CATEGORIES"), inputs, passes, imported, images.ToImmutable(), role, issues.ToImmutable());
        }
    }

    private static IsfDocument Invalid(string source, ImmutableArray<IsfIssue>.Builder issues, IsfIssue issue)
    {
        issues.Add(issue);
        return new IsfDocument(false, source, 1, "", "", "", "", [], [], [], [], [], IsfRole.Generator, issues.ToImmutable());
    }

    /// <summary>Where the first block comment opens, when nothing but white space comes before it.</summary>
    private static int FirstCommentStart(string source)
    {
        var i = 0;
        if (source.Length > 0 && source[0] == '﻿')
            i = 1;
        while (i < source.Length && char.IsWhiteSpace(source[i]))
            i++;
        return i + 1 < source.Length && source[i] == '/' && source[i + 1] == '*' ? i : -1;
    }

    /// <summary>The 1-based line of a character offset.</summary>
    public static int LineOf(string text, int offset)
    {
        ArgumentNullException.ThrowIfNull(text);
        var line = 1;
        for (var i = 0; i < offset && i < text.Length; i++)
            if (text[i] == '\n')
                line++;
        return line;
    }

    private static string FirstSentence(string message)
    {
        var dot = message.IndexOf(". ", StringComparison.Ordinal);
        return dot > 0 ? message[..dot] : message;
    }

    private static ImmutableArray<IsfInput> ReadInputs(JsonElement root, int line, ImmutableArray<IsfIssue>.Builder issues)
    {
        if (!root.TryGetProperty("INPUTS", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return [];
        var list = ImmutableArray.CreateBuilder<IsfInput>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var e in arr.EnumerateArray())
        {
            if (e.ValueKind != JsonValueKind.Object)
                continue;
            var name = Str(e, "NAME");
            var typeWord = Str(e, "TYPE");
            if (name.Length == 0)
            {
                issues.Add(new IsfIssue(line, 0, "an input has no NAME and is left out"));
                continue;
            }
            if (!seen.Add(name))
            {
                issues.Add(new IsfIssue(line, 0, $"input {name} is declared twice; the first one counts"));
                continue;
            }
            IsfInputType? type = typeWord switch
            {
                "event" => IsfInputType.Event,
                "bool" => IsfInputType.Bool,
                "long" => IsfInputType.Long,
                "float" => IsfInputType.Float,
                "point2D" => IsfInputType.Point2D,
                "color" => IsfInputType.Color,
                "image" => IsfInputType.Image,
                "audio" => IsfInputType.Audio,
                "audioFFT" => IsfInputType.AudioFft,
                _ => null,
            };
            if (type is null)
            {
                issues.Add(new IsfIssue(line, 0, $"input {name} has the unknown TYPE '{typeWord}' and is left out"));
                continue;
            }
            var maxSamples = type is IsfInputType.Audio or IsfInputType.AudioFft ? (int)Math.Max(0, Numbers(e, "MAX").FirstOrDefault()) : 0;
            var values = Numbers(e, "VALUES").Select(v => (int)Math.Round(v)).ToImmutableArray();
            list.Add(new IsfInput(name, type.Value, Str(e, "LABEL"), Numbers(e, "DEFAULT"), Numbers(e, "MIN"),
                type is IsfInputType.Audio or IsfInputType.AudioFft ? [] : Numbers(e, "MAX"), Numbers(e, "IDENTITY"), values,
                Strings(e, "LABELS"), maxSamples));
        }
        return list.ToImmutable();
    }

    /// <summary>v1's PERSISTENT_BUFFERS: a list of names, or names to {WIDTH, HEIGHT, FLOAT}.</summary>
    private static Dictionary<string, (string W, string H, bool Float)> ReadPersistentBuffers(JsonElement root)
    {
        var map = new Dictionary<string, (string, string, bool)>(StringComparer.Ordinal);
        if (!root.TryGetProperty("PERSISTENT_BUFFERS", out var pb))
            return map;
        if (pb.ValueKind == JsonValueKind.Array)
        {
            foreach (var b in pb.EnumerateArray())
                if (b.ValueKind == JsonValueKind.String && b.GetString() is { Length: > 0 } s)
                    map.TryAdd(s, ("", "", false));
        }
        else if (pb.ValueKind == JsonValueKind.Object)
        {
            foreach (var b in pb.EnumerateObject())
            {
                var v = b.Value;
                map.TryAdd(b.Name, v.ValueKind == JsonValueKind.Object ? (Expr(v, "WIDTH"), Expr(v, "HEIGHT"), Flag(v, "FLOAT")) : ("", "", false));
            }
        }
        return map;
    }

    private static ImmutableArray<IsfPass> ReadPasses(JsonElement root, Dictionary<string, (string W, string H, bool Float)> persistentV1, int line,
        ImmutableArray<IsfIssue>.Builder issues)
    {
        var list = ImmutableArray.CreateBuilder<IsfPass>();
        if (root.TryGetProperty("PASSES", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var e in arr.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.Object)
                {
                    list.Add(new IsfPass("", false, false, "", ""));
                    continue;
                }
                var target = Str(e, "TARGET");
                var (w, h, f) = (Expr(e, "WIDTH"), Expr(e, "HEIGHT"), Flag(e, "FLOAT"));
                var persistent = Flag(e, "PERSISTENT");
                if (persistentV1.TryGetValue(target, out var v1))
                {
                    persistent = true;
                    w = w.Length > 0 ? w : v1.W;
                    h = h.Length > 0 ? h : v1.H;
                    f |= v1.Float;
                }
                list.Add(new IsfPass(target, persistent, f, w, h));
            }
        }
        if (list.Count == 0)
            list.Add(new IsfPass("", false, false, "", ""));
        if (list.Count > 16)
            issues.Add(new IsfIssue(line, 0, $"{list.Count} passes; PlayMap draws at most 16 a frame and leaves the rest out"));
        return list.Count > 16 ? list.Take(16).ToImmutableArray() : list.ToImmutable();
    }

    private static ImmutableArray<IsfImported> ReadImported(JsonElement root, int line, ImmutableArray<IsfIssue>.Builder issues)
    {
        if (!root.TryGetProperty("IMPORTED", out var imp))
            return [];
        var list = ImmutableArray.CreateBuilder<IsfImported>();
        if (imp.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in imp.EnumerateObject())
                if (p.Value.ValueKind == JsonValueKind.Object && Str(p.Value, "PATH") is { Length: > 0 } path)
                    list.Add(new IsfImported(p.Name, path));
                else
                    issues.Add(new IsfIssue(line, 0, $"imported image {p.Name} has no PATH"));
        }
        else if (imp.ValueKind == JsonValueKind.Array)
        {
            foreach (var e in imp.EnumerateArray())
                if (e.ValueKind == JsonValueKind.Object && Str(e, "NAME") is { Length: > 0 } name)
                    list.Add(new IsfImported(name, Str(e, "PATH")));
        }
        return list.ToImmutable();
    }

    private static string Str(JsonElement e, string key) =>
        e.TryGetProperty(key, out var v) ? v.ValueKind switch
        {
            JsonValueKind.String => v.GetString() ?? "",
            JsonValueKind.Number => v.GetRawText(),
            _ => "",
        } : "";

    /// <summary>A WIDTH or HEIGHT: an expression string, or a plain number.</summary>
    private static string Expr(JsonElement e, string key) => Str(e, key).Trim();

    private static bool Flag(JsonElement e, string key)
    {
        if (!e.TryGetProperty(key, out var v))
            return false;
        return v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.Number => v.TryGetDouble(out var d) && d > 0,
            JsonValueKind.String => v.GetString() is { } s && (s.Equals("true", StringComparison.OrdinalIgnoreCase)
                || (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && n > 0)),
            _ => false,
        };
    }

    private static ImmutableArray<string> Strings(JsonElement e, string key)
    {
        if (!e.TryGetProperty(key, out var v))
            return [];
        if (v.ValueKind == JsonValueKind.String)
            return [v.GetString() ?? ""];
        if (v.ValueKind != JsonValueKind.Array)
            return [];
        var list = ImmutableArray.CreateBuilder<string>();
        foreach (var x in v.EnumerateArray())
            list.Add(x.ValueKind == JsonValueKind.String ? x.GetString() ?? "" : x.GetRawText());
        return list.ToImmutable();
    }

    /// <summary>A number, a bool or an array of them as numbers (true = 1); strings that hold numbers count.</summary>
    private static ImmutableArray<double> Numbers(JsonElement e, string key)
    {
        if (!e.TryGetProperty(key, out var v))
            return [];
        if (v.ValueKind != JsonValueKind.Array)
            return One(v) is { } d ? [d] : [];
        var list = ImmutableArray.CreateBuilder<double>();
        foreach (var x in v.EnumerateArray())
            if (One(x) is { } n)
                list.Add(n);
        return list.ToImmutable();
    }

    private static double? One(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.Number when v.TryGetDouble(out var d) && double.IsFinite(d) => d,
        JsonValueKind.True => 1,
        JsonValueKind.False => 0,
        JsonValueKind.String when double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var s) && double.IsFinite(s) => s,
        _ => null,
    };
}
