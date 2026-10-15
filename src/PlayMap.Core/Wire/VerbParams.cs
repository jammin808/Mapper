using System.Collections.Frozen;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace PlayMap.Core.Wire;

/// <summary>What a parameter holds, for a controller's dropdowns and fields.</summary>
public enum ParamKind
{
    /// <summary>An id of the show (its prefix names the kind: "pl" a playlist, "it" an item…).</summary>
    Entity,
    /// <summary>One of the literal words the syntax lists.</summary>
    Enum,
    Number,
    Integer,
    Text,
    Json,
    /// <summary>A media time, a leader instant or a timecode.</summary>
    Time,
    /// <summary>A picture source reference ("media:md…", "color:#RRGGBB").</summary>
    Ref,
    /// <summary>Another line of the vocabulary.</summary>
    Line,
    /// <summary>An optional literal word that is there or not ("DRAFT").</summary>
    Flag,
}

/// <summary>One typed parameter of a verb's form: its name, kind, whether it may be left out, the keyword that introduces it ("AT"
/// &lt;time&gt;), an id prefix, the literal words it takes, a range, another form it accepts ("#&lt;n&gt;" for an item by
/// position, "ALL") and whether it repeats.</summary>
public sealed record VerbParam(string Name, ParamKind Kind, bool Optional = false)
{
    public string? Prefix { get; init; }

    public IReadOnlyList<string>? Values { get; init; }

    public double? Min { get; init; }

    public double? Max { get; init; }

    public double? Step { get; init; }

    public string? Keyword { get; init; }

    public string? Alt { get; init; }

    public bool Repeats { get; init; }
}

/// <summary>How a controller's key sends a verb: at once, held (pressed and held to confirm), or pressed twice.</summary>
public static class PressShapes
{
    public const string Instant = "instant", Hold = "hold", Double = "double";
}

/// <summary>Typed parameters for every row of the catalogue and every alternative of its syntax, read through <see cref="VerbSyntax"/>
/// (the one reader of a row's syntax) with overrides by row name where the syntax's words alone say too little; and each row's press
/// shape: Panic, Air and Transport rows fire at once (a show's emergency and its GO are never held), otherwise a high-risk row, and a
/// review row that puts a picture over the air, is held to confirm and a projector's power is pressed twice. VERBS and /api/verbs carry both, so a controller builds its actions from
/// data.</summary>
public static class VerbParams
{
    private static readonly ConditionalWeakTable<VerbSpec, IReadOnlyList<IReadOnlyList<VerbParam>>> Made = new();

    private static readonly FrozenDictionary<string, string> EntityPrefixes = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["playlist"] = "pl", ["item"] = "it", ["surface"] = "sf", ["projector"] = "pj", ["output"] = "op", ["device"] = "dv",
        ["generator"] = "gn", ["canvas"] = "cv", ["layer"] = "ly", ["media"] = "md", ["camera"] = "cm", ["mesh"] = "ms",
        ["scan"] = "sc", ["mask"] = "mk", ["viewpoint"] = "vp", ["pin"] = "pn", ["send"] = "sd", ["fixture"] = "lf", ["stack"] = "sk",
        ["survey"] = "sv", ["binding"] = "bd", ["slot"] = "fx", ["asset"] = "fa", ["snapshot"] = "cs", ["control"] = "ct",
    }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>Review rows that put a picture over the air (an output's or a projector's identify): held like a high-risk row.</summary>
    private static readonly FrozenSet<string> HeldReview = new[] { "OUTPUT IDENTIFY", "DEVICE IDENTIFY" }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly FrozenSet<string> AnyEntity = new[] { "id", "entity", "owner", "target" }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly FrozenSet<string> Numbers = new[]
    {
        "ms", "seconds", "minutes", "min", "max", "deg", "lux", "pct", "fps", "db", "ratio", "delta", "dx", "dy", "x", "y", "z", "value",
        "x1", "x2", "x3", "x4", "y1", "y2", "y3", "y4", "attack", "release", "level", "gain", "rate",
    }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly FrozenSet<string> Integers = new[]
    {
        "n", "index", "count", "seq", "universe", "channel", "port", "row", "column", "col", "rows", "epoch", "bars", "beats", "triangles",
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>Rows whose syntax names its parameters too plainly for a key's dropdown: their forms, by row name. A package that
    /// removes a row removes its line here too (VerbParamTests finds any left).</summary>
    private static readonly FrozenDictionary<string, IReadOnlyList<IReadOnlyList<VerbParam>>> Overrides =
        new Dictionary<string, IReadOnlyList<IReadOnlyList<VerbParam>>>(StringComparer.Ordinal)
        {
            ["VIDEO SEEK"] =
            [
                [
                    new VerbParam("time", ParamKind.Time) { Alt = "top|END" },
                    new VerbParam("on", ParamKind.Entity, Optional: true) { Keyword = "ON", Prefix = "pl" },
                ],
            ],
            ["BLACKOUT"] = [[new VerbParam("state", ParamKind.Enum) { Values = ["ON", "OFF", "TOGGLE"] }]],
            ["THUMB"] =
            [
                [
                    new VerbParam("ref", ParamKind.Ref) { Values = ["media:", "generator:", "playlist:", "color:#"] },
                    new VerbParam("size", ParamKind.Text, Optional: true) { Alt = "<w>x<h>" },
                    new VerbParam("format", ParamKind.Enum, Optional: true) { Values = ["JPEG", "PNG"] },
                    new VerbParam("if", ParamKind.Text, Optional: true) { Keyword = "IF" },
                ],
            ],
            ["EVENTS"] = [[new VerbParam("since", ParamKind.Integer, Optional: true) { Keyword = "SINCE", Min = 0 }]],
        }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>The rows that have an override.</summary>
    public static IReadOnlyCollection<string> OverriddenRows => Overrides.Keys;

    /// <summary>The first form's parameters of the row named <paramref name="verbName"/> (empty for an unknown name).</summary>
    public static IReadOnlyList<VerbParam> For(string verbName) =>
        VerbCatalog.Find(verbName) is { } spec && string.Equals(spec.Name, verbName, StringComparison.Ordinal) ? For(spec) : [];

    public static IReadOnlyList<VerbParam> For(VerbSpec spec) => Forms(spec)[0];

    /// <summary>The parameters of every alternative of the row, in the order <see cref="VerbSyntax.Alternatives"/> lists them.</summary>
    public static IReadOnlyList<IReadOnlyList<VerbParam>> Forms(VerbSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        return Made.GetValue(spec, static s => Overrides.TryGetValue(s.Name, out var o) ? o : [.. VerbSyntax.Alternatives(s).Select(f => Parse(s, f))]);
    }

    /// <summary>The press shape of a row's key.</summary>
    public static string Press(VerbSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        if (spec.Show is ShowClass.Panic or ShowClass.Air or ShowClass.Transport)
            return PressShapes.Instant;
        if (spec.Show == ShowClass.DevicePower)
            return PressShapes.Double;
        return spec.Risk == VerbRisk.High || HeldReview.Contains(spec.Name) ? PressShapes.Hold : PressShapes.Instant;
    }

    /// <summary>A form's parameters: its tokens after the verb, an optional group's as optional.</summary>
    public static IReadOnlyList<VerbParam> Parse(VerbSpec spec, string form)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(form);
        var rest = form.StartsWith(spec.Name, StringComparison.Ordinal) ? form[spec.Name.Length..] : form;
        var list = new List<VerbParam>();
        Read(Tokens(rest), optional: false, list);
        return Unique(list);
    }

    private static void Read(List<string> tokens, bool optional, List<VerbParam> into)
    {
        for (var i = 0; i < tokens.Count; i++)
        {
            var t = tokens[i];
            if (t.Length > 1 && t[0] == '[' && t[^1] == ']')
            {
                Read(Tokens(t[1..^1]), optional: true, into);
                continue;
            }
            var placeholder = VerbSyntax.IsPlaceholder(t);
            if (!placeholder && !t.Contains('|', StringComparison.Ordinal) && IsWord(t) && i + 1 < tokens.Count
                && VerbSyntax.IsPlaceholder(tokens[i + 1]) && tokens[i + 1][0] != '[')
            {
                into.Add(FromPlaceholder(tokens[++i], optional) with { Keyword = t, Name = t.ToLowerInvariant() });
                continue;
            }
            if (placeholder)
            {
                into.Add(FromPlaceholder(t, optional));
                continue;
            }
            var words = t.Split('|', StringSplitOptions.RemoveEmptyEntries);
            if (optional && words.Length == 1)
                into.Add(new VerbParam(words[0].ToLowerInvariant(), ParamKind.Flag, Optional: true) { Values = words });
            else
                into.Add(new VerbParam(ChoiceName(words), ParamKind.Enum, optional) { Values = words });
        }
    }

    private static bool IsWord(string t) => t.Length > 0 && t.All(c => char.IsAsciiLetterUpper(c) || c == '-' || char.IsAsciiDigit(c));

    private static string ChoiceName(string[] words) =>
        words.Length == 1 ? words[0].ToLowerInvariant()
        : words.Contains("ON") && words.Contains("OFF") ? "state"
        : "choice";

    /// <summary>A placeholder token ("&lt;item&gt;|#&lt;n&gt;", "&lt;output&gt;…|ALL", "&lt;ms 0-10000&gt;") as one parameter.</summary>
    private static VerbParam FromPlaceholder(string token, bool optional)
    {
        var alternatives = SplitTop(token, '|');
        var first = alternatives[0];
        var alt = alternatives.Count > 1 ? string.Join("|", alternatives.Skip(1)) : null;
        var repeats = token.Contains('…', StringComparison.Ordinal);
        var open = first.IndexOf('<', StringComparison.Ordinal);
        var inner = open >= 0 ? Inside(first, open) : first.Trim('…', '[', ']');
        var p = Typed(inner, optional);
        if (first.StartsWith('+'))
            p = p with { Kind = ParamKind.Time };
        return p with { Alt = alt ?? p.Alt, Repeats = repeats };
    }

    /// <summary>The text inside the angle brackets that open at <paramref name="open"/> (nested brackets kept).</summary>
    private static string Inside(string s, int open)
    {
        var depth = 0;
        for (var i = open; i < s.Length; i++)
        {
            if (s[i] == '<')
                depth++;
            else if (s[i] == '>' && --depth == 0)
                return s[(open + 1)..i];
        }
        return s[(open + 1)..];
    }

    private static VerbParam Typed(string inner, bool optional)
    {
        var text = inner.Trim();
        var union = SplitTop(text, '|');
        if (union.Count > 1)
        {
            // "<83|83.5|2.5s|1:23|…>" is a time; "<media|playlist|…>" a word from a list; "<media:<id>|color:#…>" a source.
            if (char.IsAsciiDigit(union[0].FirstOrDefault()))
                return new VerbParam("time", ParamKind.Time, optional);
            if (union.Any(u => u.Contains(':', StringComparison.Ordinal)))
                return new VerbParam("ref", ParamKind.Ref, optional) { Values = [.. union.Select(u => u[..(u.IndexOf(':', StringComparison.Ordinal) + 1)])] };
            return new VerbParam("kind", ParamKind.Text, optional) { Values = union };
        }
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var head = words.Length > 0 ? words[0] : text;
        var (min, max) = Range(words.Length > 1 ? words[^1] : head);
        var name = Name(head);
        if (min is not null && (words.Length == 1 || Numbers.Contains(name) || name == "ms"))
            return new VerbParam(words.Length == 1 ? "value" : name, ParamKind.Number, optional) { Min = min, Max = max };
        if (text.Contains("json", StringComparison.OrdinalIgnoreCase))
            return new VerbParam(name == "json" ? "json" : name, ParamKind.Json, optional);
        if (text.Contains("line", StringComparison.OrdinalIgnoreCase))
            return new VerbParam(name, ParamKind.Line, optional);
        if (text.Contains("unix", StringComparison.Ordinal) || (text.Contains(':', StringComparison.Ordinal) && char.IsAsciiDigit(text[0])) || name is "tc" or "hh")
            return new VerbParam(name, ParamKind.Time, optional);
        if (EntityPrefixes.TryGetValue(name, out var prefix))
            return new VerbParam(name, ParamKind.Entity, optional) { Prefix = prefix };
        if (AnyEntity.Contains(name))
            return new VerbParam(name, ParamKind.Entity, optional);
        if (name is "ref" or "source")
            return new VerbParam(name, ParamKind.Ref, optional);
        if (Integers.Contains(name))
            return new VerbParam(name, ParamKind.Integer, optional) { Min = min, Max = max };
        if (Numbers.Contains(name))
            return new VerbParam(name, ParamKind.Number, optional) { Min = min, Max = max };
        return new VerbParam(name, ParamKind.Text, optional);
    }

    /// <summary>"0-100", "0.5-2", "-24…24" as a range; anything else none.</summary>
    private static (double? Min, double? Max) Range(string s)
    {
        var t = s.Trim('±', '>', '<');
        if (t.Length < 3)
            return (null, null);
        var cut = t.IndexOf('…', StringComparison.Ordinal);
        if (cut < 0)
            cut = t.IndexOf('-', 1);
        if (cut <= 0 || cut >= t.Length - 1)
            return (null, null);
        return double.TryParse(t[..cut], NumberStyles.Float, CultureInfo.InvariantCulture, out var lo)
            && double.TryParse(t[(cut + 1)..], NumberStyles.Float, CultureInfo.InvariantCulture, out var hi)
            ? (lo, hi) : (null, null);
    }

    /// <summary>A parameter's name from a placeholder's first word: lower case, letters, digits and dots ("field.path", "host").</summary>
    private static string Name(string head)
    {
        var sb = new StringBuilder(head.Length);
        foreach (var c in head)
        {
            if (char.IsAsciiLetterOrDigit(c) || c == '.')
                sb.Append(char.ToLowerInvariant(c));
            else if (sb.Length > 0 && c is '[' or ':' or ',')
                break;
        }
        var name = sb.ToString().Trim('.');
        return name.Length == 0 || char.IsAsciiDigit(name[0]) ? "value" : name;
    }

    /// <summary>Names made unique within a form (a second "item" becomes "item2").</summary>
    private static List<VerbParam> Unique(List<VerbParam> list)
    {
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < list.Count; i++)
        {
            var n = list[i].Name;
            if (seen.TryGetValue(n, out var count))
            {
                seen[n] = count + 1;
                list[i] = list[i] with { Name = n + (count + 1).ToString(CultureInfo.InvariantCulture) };
            }
            else
            {
                seen[n] = 1;
            }
        }
        return list;
    }

    /// <summary>Space-separated tokens outside &lt;…&gt; and […].</summary>
    private static List<string> Tokens(string s)
    {
        var tokens = new List<string>();
        var sb = new StringBuilder();
        var angle = 0;
        var square = 0;
        foreach (var c in s)
        {
            if (c == ' ' && angle == 0 && square == 0)
            {
                if (sb.Length > 0)
                    tokens.Add(sb.ToString());
                sb.Clear();
                continue;
            }
            if (c == '<')
                angle++;
            else if (c == '>' && angle > 0)
                angle--;
            else if (angle == 0 && c == '[')
                square++;
            else if (angle == 0 && c == ']' && square > 0)
                square--;
            sb.Append(c);
        }
        if (sb.Length > 0)
            tokens.Add(sb.ToString());
        return tokens;
    }

    /// <summary>Splits at a separator outside &lt;…&gt; and […].</summary>
    private static List<string> SplitTop(string s, char separator)
    {
        var parts = new List<string>();
        var angle = 0;
        var square = 0;
        var start = 0;
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c == '<')
                angle++;
            else if (c == '>' && angle > 0)
                angle--;
            else if (angle == 0 && c == '[')
                square++;
            else if (angle == 0 && c == ']' && square > 0)
                square--;
            else if (c == separator && angle == 0 && square == 0)
            {
                parts.Add(s[start..i]);
                start = i + 1;
            }
        }
        parts.Add(s[start..]);
        return parts;
    }

    /// <summary>A form's parameters as JSON, keys present only when they say something.</summary>
    public static void Write(Utf8JsonWriter w, IReadOnlyList<VerbParam> parameters)
    {
        ArgumentNullException.ThrowIfNull(w);
        ArgumentNullException.ThrowIfNull(parameters);
        w.WriteStartArray();
        foreach (var p in parameters)
        {
            w.WriteStartObject();
            w.WriteString("name", p.Name);
            w.WriteString("kind", p.Kind.ToString().ToLowerInvariant());
            if (p.Optional)
                w.WriteBoolean("optional", true);
            if (p.Prefix is not null)
                w.WriteString("prefix", p.Prefix);
            if (p.Keyword is not null)
                w.WriteString("keyword", p.Keyword);
            if (p.Values is { Count: > 0 } values)
            {
                w.WriteStartArray("values");
                foreach (var v in values)
                    w.WriteStringValue(v);
                w.WriteEndArray();
            }
            if (p.Min is { } min)
                w.WriteNumber("min", min);
            if (p.Max is { } max)
                w.WriteNumber("max", max);
            if (p.Step is { } step)
                w.WriteNumber("step", step);
            if (p.Alt is not null)
                w.WriteString("alt", p.Alt);
            if (p.Repeats)
                w.WriteBoolean("repeats", true);
            w.WriteEndObject();
        }
        w.WriteEndArray();
    }
}
