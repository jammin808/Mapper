using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace PlayMap.Core.Effects;

/// <summary>The GLSL dialects an ISF file is translated for: desktop GLSL 3.30 core and GLSL ES 3.00. The translated source
/// carries no #version and no precision line in either; the GPU device writes the dialect's header in front.</summary>
public enum IsfDialect
{
    Glsl330,
    Es300,
}

/// <summary>An ISF file as two program sources for a dialect, the issues the translation found, and the map from a compiler's
/// lines back to the file's.</summary>
public sealed record IsfProgramSource(string Vertex, string Fragment, ImmutableArray<IsfIssue> Issues, IsfLineMap LineMap);

/// <summary>Where each stage's body starts in its file. The translation puts the body after "#line 1 1", so the compiler
/// numbers it as source string 1 from line 1; anything it reports in source string 0 is PlayMap's own prelude.</summary>
public sealed partial record IsfLineMap(int FragmentBodyLine, int VertexBodyLine)
{
    /// <summary>A compiler's log as issues at the file's lines (errors only; Mesa, NVIDIA and ANGLE spellings).</summary>
    public ImmutableArray<IsfIssue> Read(string? log, bool vertex)
    {
        if (string.IsNullOrWhiteSpace(log))
            return [];
        var list = ImmutableArray.CreateBuilder<IsfIssue>();
        var first = vertex ? VertexBodyLine : FragmentBodyLine;
        var stage = vertex ? "vertex shader" : "";
        foreach (var raw in log.Split('\n'))
        {
            var line = raw.Trim();
            int source, n, col;
            string words;
            if (MesaLine().Match(line) is { Success: true } m)
                (source, n, col, words) = (Int(m.Groups[1].Value), Int(m.Groups[2].Value), Int(m.Groups[3].Value), m.Groups[5].Value);
            else if (NvidiaLine().Match(line) is { Success: true } v)
                (source, n, col, words) = (Int(v.Groups[1].Value), Int(v.Groups[2].Value), 0, v.Groups[4].Value);
            else if (AngleLine().Match(line) is { Success: true } a)
                (source, n, col, words) = (Int(a.Groups[1].Value), Int(a.Groups[2].Value), 0, a.Groups[3].Value);
            else
                continue;
            if (!line.Contains("error", StringComparison.OrdinalIgnoreCase) && !line.StartsWith("ERROR", StringComparison.Ordinal))
                continue;
            words = words.Trim();
            if (stage.Length > 0)
                words = $"{stage}: {words}";
            list.Add(source == 1 ? new IsfIssue(first + n - 1, col, words) : new IsfIssue(0, 0, "in PlayMap's prelude: " + words));
        }
        if (list.Count == 0)
            list.Add(new IsfIssue(0, 0, FirstLine(log)));
        return list.ToImmutable();
    }

    private static string FirstLine(string log)
    {
        var t = log.Trim();
        var nl = t.IndexOf('\n');
        return nl > 0 ? t[..nl].Trim() : t;
    }

    private static int Int(string s) => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;

    [GeneratedRegex(@"^(\d+):(\d+)\((\d+)\):\s*(error|warning)\s*:\s*(.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex MesaLine();

    [GeneratedRegex(@"^(\d+)\((\d+)\)\s*:\s*(error|warning)[^:]*:\s*(.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex NvidiaLine();

    [GeneratedRegex(@"^(?:ERROR|WARNING):\s*(\d+):(\d+):\s*(.*)$")]
    private static partial Regex AngleLine();
}

/// <summary>Translates an ISF file into program sources (our own translator; ISF v2 and v1 spellings): the declarations
/// every ISF file may rely on (PASSINDEX, RENDERSIZE, TIME, TIMEDELTA, DATE, FRAMEINDEX, the inputs, every image with its
/// size, rect and orientation flag), the IMG_* calls rewritten to one sampling helper that honours the flag, v1's vv_ names
/// renamed, #version and precision lines dropped in place (the line numbers hold), and for ES the built-in renames.</summary>
public static partial class IsfTranslator
{
    private const string FragmentPrelude =
        "out vec4 isf_FragColor;\n#define gl_FragColor isf_FragColor\n#define varying in\n#define texture2D texture\n#define texture2DRect texture\n";

    private const string VertexPrelude =
        "#define varying out\n#define attribute in\n#define texture2D texture\n#define texture2DRect texture\n";

    /// <summary>The full-screen triangle from gl_VertexID, bottom-left origin as ISF requires (isf_FragNormCoord 0..1 over the
    /// target); a custom vertex shader calls it, the default one is only it.</summary>
    private const string VertexInit =
        "void isf_vertShaderInit(void) {\n" +
        "    vec2 isf_p = vec2(float((gl_VertexID << 1) & 2), float(gl_VertexID & 2));\n" +
        "    gl_Position = vec4(isf_p * 2.0 - 1.0, 0.0, 1.0);\n" +
        "    isf_FragNormCoord = isf_p;\n" +
        "}\n";

    public static IsfProgramSource Translate(IsfDocument doc, string? vertexSource, IsfDialect dialect)
    {
        ArgumentNullException.ThrowIfNull(doc);
        var issues = ImmutableArray.CreateBuilder<IsfIssue>();
        issues.AddRange(doc.Issues);
        var images = doc.Images.ToHashSet(StringComparer.Ordinal);
        var body = Rewrite(doc.Body, doc.BodyLine, images, vertex: false, issues);
        var (vsBody, vsLine) = vertexSource is null ? ((string?)null, 1) : StripHeader(vertexSource);
        var vsRewritten = vsBody is null ? null : Rewrite(vsBody, vsLine, images, vertex: true, issues);

        var fs = new StringBuilder(FragmentPrelude.Length + body.Length + 1024);
        fs.Append(FragmentPrelude);
        Declarations(fs, doc, vertex: false);
        if (dialect == IsfDialect.Es300)
            fs.Append(IsfEsFixups.Renames(body));
        fs.Append("#line 1 1\n").Append(body);
        if (!body.EndsWith('\n'))
            fs.Append('\n');

        var vs = new StringBuilder(2048);
        vs.Append(VertexPrelude);
        Declarations(vs, doc, vertex: true);
        vs.Append(VertexInit);
        if (vsRewritten is null)
        {
            vs.Append("void main(void) { isf_vertShaderInit(); }\n");
        }
        else
        {
            if (dialect == IsfDialect.Es300)
                vs.Append(IsfEsFixups.Renames(vsRewritten));
            vs.Append("#line 1 1\n").Append(vsRewritten);
            if (!vsRewritten.EndsWith('\n'))
                vs.Append('\n');
        }
        return new IsfProgramSource(vs.ToString(), fs.ToString(), issues.ToImmutable(), new IsfLineMap(doc.BodyLine, vsLine));
    }

    /// <summary>The GLSL type of an input's uniform (null for images, which are samplers).</summary>
    public static string? GlslType(IsfInputType type) => type switch
    {
        IsfInputType.Bool or IsfInputType.Event => "bool",
        IsfInputType.Long => "int",
        IsfInputType.Float => "float",
        IsfInputType.Point2D => "vec2",
        IsfInputType.Color => "vec4",
        _ => null,
    };

    private static void Declarations(StringBuilder sb, IsfDocument doc, bool vertex)
    {
        sb.Append("uniform int PASSINDEX;\nuniform vec2 RENDERSIZE;\nuniform float TIME;\nuniform float TIMEDELTA;\nuniform vec4 DATE;\nuniform int FRAMEINDEX;\n");
        sb.Append(vertex ? "out vec2 isf_FragNormCoord;\n" : "in vec2 isf_FragNormCoord;\n");
        foreach (var input in doc.Inputs)
            if (GlslType(input.Type) is { } t)
                sb.Append("uniform ").Append(t).Append(' ').Append(input.Name).Append(";\n");
        foreach (var img in doc.Images)
            sb.Append("uniform sampler2D ").Append(img).Append(";\nuniform vec2 _").Append(img).Append("_imgSize;\nuniform vec4 _")
                .Append(img).Append("_imgRect;\nuniform bool _").Append(img).Append("_flip;\n");
        // Every picture is a normalised 2D texture; the flag says it is stored top row first (a PlayMap picture), so the
        // bottom-left origin ISF draws in reads it the right way up.
        sb.Append("vec4 isf_sample(sampler2D s, bool flip, vec2 uv) { return texture(s, flip ? vec2(uv.x, 1.0 - uv.y) : uv); }\n");
        if (!vertex)
            sb.Append("vec4 isf_sample(sampler2D s, bool flip, vec2 uv, float bias) { return texture(s, flip ? vec2(uv.x, 1.0 - uv.y) : uv, bias); }\n");
    }

    /// <summary>A .vs file's body: after its header comment when it has one (with the line it starts at), else all of it.</summary>
    private static (string Body, int Line) StripHeader(string source)
    {
        var t = source.TrimStart('﻿');
        var lead = 0;
        while (lead < t.Length && char.IsWhiteSpace(t[lead]))
            lead++;
        if (lead + 1 < t.Length && t[lead] == '/' && t[lead + 1] == '*')
        {
            var close = t.IndexOf("*/", lead + 2, StringComparison.Ordinal);
            if (close > 0 && t.AsSpan(lead, close - lead).Contains('{'))
                return (t[(close + 2)..], IsfParser.LineOf(t, close + 2));
        }
        return (t, 1);
    }

    /// <summary>The body with #version and precision lines blanked (line numbers hold), v1's names renamed, and every IMG_* call
    /// rewritten with its arguments read by balanced parentheses.</summary>
    private static string Rewrite(string body, int bodyLine, HashSet<string> images, bool vertex, ImmutableArray<IsfIssue>.Builder issues)
    {
        var lines = body.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var t = lines[i].Trim();
            if ((t.StartsWith('#') && t[1..].TrimStart().StartsWith("version", StringComparison.Ordinal)) || t.StartsWith("precision ", StringComparison.Ordinal))
                lines[i] = "";
        }
        var s = string.Join('\n', lines).Replace("vv_FragNormCoord", "isf_FragNormCoord", StringComparison.Ordinal)
            .Replace("vv_vertShaderInit", "isf_vertShaderInit", StringComparison.Ordinal);
        var sb = new StringBuilder(s.Length + 256);
        var pos = 0;
        while (true)
        {
            var m = Macro().Match(s, pos);
            if (!m.Success)
            {
                sb.Append(s, pos, s.Length - pos);
                break;
            }
            sb.Append(s, pos, m.Index - pos);
            var line = bodyLine + IsfParser.LineOf(s, m.Index) - 1;
            var macro = m.Groups[1].Value;
            if (!TryArgs(s, m.Index + m.Length, out var args, out var end))
            {
                issues.Add(new IsfIssue(line, 0, $"{macro}( is never closed"));
                sb.Append(s, m.Index, s.Length - m.Index);
                break;
            }
            for (var a = 0; a < args.Count; a++)
                args[a] = args[a].Trim(' ', '\t');
            var img = args.Count > 0 ? args[0].Trim() : "";
            var wants = macro is "IMG_PIXEL" or "IMG_NORM_PIXEL" ? "2 or 3" : "1";
            var countOk = macro is "IMG_PIXEL" or "IMG_NORM_PIXEL" ? args.Count is 2 or 3 : args.Count == 1;
            if (!countOk)
                issues.Add(new IsfIssue(line, 0, $"{macro} takes {wants} values, here {args.Count}"));
            else if (!images.Contains(img))
                issues.Add(new IsfIssue(line, 0, $"{macro} reads '{img}', which is not an image, pass target or import of this file"));
            if (args.Count == 3 && vertex)
                issues.Add(new IsfIssue(line, 0, $"{macro} with a bias works in the fragment shader only"));
            sb.Append(macro switch
            {
                "IMG_PIXEL" when args.Count >= 3 => $"isf_sample({img}, _{img}_flip, ({args[1]}) / _{img}_imgSize, {args[2]})",
                "IMG_PIXEL" => $"isf_sample({img}, _{img}_flip, ({(args.Count > 1 ? args[1] : "vec2(0.0)")}) / _{img}_imgSize)",
                "IMG_NORM_PIXEL" when args.Count >= 3 => $"isf_sample({img}, _{img}_flip, ({args[1]}), {args[2]})",
                "IMG_NORM_PIXEL" => $"isf_sample({img}, _{img}_flip, ({(args.Count > 1 ? args[1] : "vec2(0.0)")}))",
                "IMG_SIZE" => $"(_{img}_imgSize)",
                _ => $"isf_sample({img}, _{img}_flip, isf_FragNormCoord)",
            });
            pos = end;
        }
        return sb.ToString();
    }

    /// <summary>The arguments of a call whose '(' has been read, split at top-level commas, and the index after its ')'.</summary>
    private static bool TryArgs(string s, int start, out List<string> args, out int end)
    {
        args = [];
        end = start;
        var depth = 0;
        var from = start;
        for (var i = start; i < s.Length; i++)
        {
            var c = s[i];
            if (c is '(' or '[')
            {
                depth++;
            }
            else if (c is ')' or ']')
            {
                if (depth == 0)
                {
                    if (i > from || args.Count > 0)
                        args.Add(s[from..i]);
                    end = i + 1;
                    return c == ')';
                }
                depth--;
            }
            else if (c == ',' && depth == 0)
            {
                args.Add(s[from..i]);
                from = i + 1;
            }
        }
        return false;
    }

    [GeneratedRegex(@"\b(IMG_THIS_NORM_PIXEL|IMG_NORM_THIS_PIXEL|IMG_THIS_PIXEL|IMG_NORM_PIXEL|IMG_PIXEL|IMG_SIZE)\s*\(")]
    private static partial Regex Macro();
}
