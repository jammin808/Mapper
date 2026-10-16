using System.Globalization;
using System.Text.RegularExpressions;
using PlayMap.Core.Model;
using PlayMap.Core.Wire;

namespace PlayMap.Net.Tests.Companion;

/// <summary>Whether a line fills one of its row's typed forms (<see cref="VerbParams.Forms"/>) word for word: some form takes every
/// word after the verb's own, none left over. A keyword parameter is its keyword (any case) and then its value; a choice or a flag is
/// one of its literal words (any case); an entity is an id with the parameter's prefix; a number or an integer reads culture-invariant
/// (an integer whole) within the parameter's range; a time is a show time as a line writes one ("83", "12.5", "2.5s", "1:23",
/// "10:00:00:00", "+500", a unix ms); a source is "&lt;kind&gt;:&lt;rest&gt;", its kind one the parameter names when it names any; text
/// is one word (a quoted phrase is one), and text whose only alternative is a shape ("&lt;w&gt;x&lt;h&gt;", "@&lt;fps&gt;") is a word of
/// that shape; JSON and an embedded line take the rest of the line. A value may instead be one of the parameter's alternatives (Alt):
/// a literal word, or a shape ("#&lt;n&gt;", "&lt;w&gt;x&lt;h&gt;", "@&lt;fps&gt;") whose marks the word carries. An optional parameter may be left out and
/// a repeating one takes one or more values; every reading of the optional ones is tried, so a line that fits is never refused.
/// A form a key cannot write right fits no line: one whose literal is no word ("#rrggbb" is the shape of a value, named bare, which a
/// key could only send as it stands), one with a flag straight before an optional choice ("QUALITY" then "LOW|MEDIUM|HIGH": a
/// keyword parted from its value, either of which a key could send alone), or one with any parameter after JSON or an embedded line
/// (which take the rest of the line, so nothing after them can be written).</summary>
internal static partial class FormMatcher
{
    /// <summary>The first words of placeholders that stand for a number ("&lt;n&gt;", "&lt;w&gt;x&lt;h&gt;", "&lt;unix ms&gt;").</summary>
    private static readonly HashSet<string> NumberWords = new(StringComparer.Ordinal)
    {
        "n", "ms", "unix", "fps", "w", "h", "c", "r", "col", "row", "x", "y", "m", "deg", "pct", "seconds", "count", "rows", "index",
        "universe", "address", "channel", "port",
    };

    /// <summary>True when some form of <paramref name="spec"/> takes the words of <paramref name="line"/> after its first
    /// <paramref name="verbWords"/>; otherwise <paramref name="why"/> says, form by form, where each stopped.</summary>
    public static bool Fits(VerbSpec spec, WireLine line, int verbWords, out string? why)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(line);
        var forms = VerbParams.Forms(spec);
        var syntax = VerbSyntax.Alternatives(spec);
        var reasons = new List<string>(forms.Count);
        for (var f = 0; f < forms.Count; f++)
        {
            var text = f < syntax.Count ? syntax[f] : spec.Syntax;
            if (Flaw(forms[f]) is { } flaw)
            {
                reasons.Add($"{text}: {flaw}");
                continue;
            }
            var far = verbWords;
            if (Match(forms[f], 0, line, verbWords, ref far))
            {
                why = null;
                return true;
            }
            reasons.Add(far >= line.Count
                ? $"{text}: the line ends before the form does"
                : $"{text}: no parameter takes '{line.Word(far)}' (word {far + 1})");
        }
        why = string.Join("; ", reasons);
        return false;
    }

    /// <summary>Why no line can fill the form right, or null when a key can write it.</summary>
    private static string? Flaw(IReadOnlyList<VerbParam> form)
    {
        for (var i = 0; i < form.Count; i++)
        {
            var p = form[i];
            if (p.Kind is ParamKind.Enum or ParamKind.Flag && p.Values?.FirstOrDefault(v => !IsLiteralWord(v)) is { } bare)
                return $"its {p.Name} names '{bare}', the shape of a value rather than a word the node reads, so a key could only send it as it stands";
            if (p.Kind is ParamKind.Flag && i + 1 < form.Count && form[i + 1] is { Kind: ParamKind.Enum, Optional: true, Keyword: null } next)
                return $"its flag {string.Join('|', p.Values ?? [])} stands before the choice {string.Join('|', next.Values ?? [])} — a keyword parted from its value, either of which a key could send alone";
            if (p.Kind is ParamKind.Json or ParamKind.Line && i + 1 < form.Count)
                return $"its {p.Name} takes the rest of the line, so no key can write the {string.Join(", ", form.Skip(i + 1).Select(q => q.Name))} after it";
        }
        return null;
    }

    /// <summary>Letters and digits, with "-", "+", ",", "." and "_" inside: "FX-OFF", "1+2", "SCAN,SOLVE", "hap-q".</summary>
    private static bool IsLiteralWord(string s) =>
        s.Length > 0 && char.IsAsciiLetterOrDigit(s[0]) && s.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '+' or ',' or '.' or '_');

    /// <summary>Whether the parameters from <paramref name="i"/> on take exactly the words from <paramref name="at"/> to the end;
    /// <paramref name="far"/> is the furthest word any reading reached.</summary>
    private static bool Match(IReadOnlyList<VerbParam> form, int i, WireLine line, int at, ref int far)
    {
        far = Math.Max(far, at);
        if (i == form.Count)
            return at == line.Count;
        var p = form[i];
        foreach (var end in Ends(p, line, at))
        {
            far = Math.Max(far, end);
            if (Match(form, i + 1, line, end, ref far))
                return true;
        }
        return p.Optional && Match(form, i + 1, line, at, ref far);
    }

    /// <summary>Every word index the parameter can end before when it starts at <paramref name="at"/> (none: it takes nothing there).</summary>
    private static List<int> Ends(VerbParam p, WireLine line, int at)
    {
        var ends = new List<int>();
        var w = at;
        if (p.Keyword is { } keyword)
        {
            if (w >= line.Count || !line.Word(w).Equals(keyword, StringComparison.OrdinalIgnoreCase))
                return ends;
            w++;
        }
        if (p.Kind is ParamKind.Json or ParamKind.Line)
        {
            if (w < line.Count)
                ends.Add(line.Count);
            return ends;
        }
        while (w < line.Count && Takes(p, line.Word(w)))
        {
            ends.Add(++w);
            if (!p.Repeats)
                break;
        }
        return ends;
    }

    private static bool Takes(VerbParam p, string word)
    {
        if (p.Alt is { } alt && Alternatives(alt).Any(a => Shaped(a, word)))
            return true;
        return p.Kind switch
        {
            ParamKind.Entity => Ids.IsValid(word) && (p.Prefix is not { } prefix || (word.Length > prefix.Length && word.StartsWith(prefix, StringComparison.Ordinal))),
            ParamKind.Enum or ParamKind.Flag => p.Values is { } values && values.Any(v => v.Equals(word, StringComparison.OrdinalIgnoreCase)),
            ParamKind.Number => double.TryParse(word, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && double.IsFinite(n) && InRange(p, n),
            ParamKind.Integer => long.TryParse(word, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var i) && InRange(p, i),
            ParamKind.Time => TimeWord().IsMatch(word),
            ParamKind.Ref => word.IndexOf(':', StringComparison.Ordinal) is > 0 and var colon && colon < word.Length - 1
                && (p.Values is not { Count: > 0 } kinds || kinds.Any(k => word.StartsWith(k, StringComparison.OrdinalIgnoreCase))),
            ParamKind.Text => p.Alt is not { } only || !IsShape(only),
            _ => false,
        };
    }

    /// <summary>An Alt that is one shape with marks of its own ("&lt;w&gt;x&lt;h&gt;", "@&lt;fps&gt;", "module=&lt;version&gt;"): the only
    /// way the value is written, not another word beside it.</summary>
    private static bool IsShape(string alt) =>
        Alternatives(alt) is [var one] && one.Contains('<', StringComparison.Ordinal) && Pieces(one).Any(p => p.Literal is not null);

    private static bool InRange(VerbParam p, double v) => (p.Min is not { } min || v >= min) && (p.Max is not { } max || v <= max);

    /// <summary>An Alt's alternatives: split at "|" outside angle brackets, a trailing "…" dropped.</summary>
    private static List<string> Alternatives(string alt)
    {
        var parts = new List<string>();
        var depth = 0;
        var start = 0;
        for (var i = 0; i < alt.Length; i++)
        {
            if (alt[i] == '<')
                depth++;
            else if (alt[i] == '>' && depth > 0)
                depth--;
            else if (alt[i] == '|' && depth == 0)
            {
                parts.Add(alt[start..i].TrimEnd('…'));
                start = i + 1;
            }
        }
        parts.Add(alt[start..].TrimEnd('…'));
        return parts;
    }

    /// <summary>Whether a word is one alternative: a literal word (any case; "\"\"" is the empty word), or a shape of literal marks
    /// and placeholders ("#&lt;n&gt;", "&lt;w&gt;x&lt;h&gt;", "@&lt;fps&gt;", "output:&lt;id&gt;"), each placeholder a number or any
    /// run of characters.</summary>
    private static bool Shaped(string alternative, string word)
    {
        if (alternative == "\"\"")
            return word.Length == 0;
        if (!alternative.Contains('<', StringComparison.Ordinal))
            return alternative.Equals(word, StringComparison.OrdinalIgnoreCase);
        return Shape(Pieces(alternative), 0, word, 0);
    }

    /// <summary>A piece of a shape: a literal run of marks, or a placeholder (a number, or any run of characters).</summary>
    private readonly record struct Piece(string? Literal, bool Number);

    private static List<Piece> Pieces(string alternative)
    {
        var pieces = new List<Piece>();
        var i = 0;
        while (i < alternative.Length)
        {
            if (alternative[i] == '<')
            {
                var close = alternative.IndexOf('>', i);
                var inner = close < 0 ? alternative[(i + 1)..] : alternative[(i + 1)..close];
                var head = inner.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
                pieces.Add(new Piece(null, head.Length > 0 && (char.IsAsciiDigit(head[0]) || NumberWords.Contains(head))));
                i = close < 0 ? alternative.Length : close + 1;
                continue;
            }
            var next = alternative.IndexOf('<', i);
            var literal = next < 0 ? alternative[i..] : alternative[i..next];
            pieces.Add(new Piece(literal, false));
            i += literal.Length;
        }
        return pieces;
    }

    private static bool Shape(List<Piece> pieces, int k, string word, int at)
    {
        if (k == pieces.Count)
            return at == word.Length;
        var piece = pieces[k];
        if (piece.Literal is { } literal)
            return word.AsSpan(at).StartsWith(literal, StringComparison.OrdinalIgnoreCase) && Shape(pieces, k + 1, word, at + literal.Length);
        for (var end = word.Length; end > at; end--)
        {
            var run = word.AsSpan(at, end - at);
            if (piece.Number && !(char.IsAsciiDigit(run[0]) && double.TryParse(run, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out _)))
                continue;
            if (Shape(pieces, k + 1, word, end))
                return true;
        }
        return false;
    }

    /// <summary>A show time as a line writes one: seconds ("83", "12.5", "2.5s"), a lead in ms ("+500"), a unix ms, or a clock or
    /// timecode ("1:23", "1:02:03", "10:00:00:00", "10:00:00;12").</summary>
    [GeneratedRegex(@"^(?:[+-]?\d+(?:\.\d+)?(?:s|ms)?|\d+(?:[:;]\d+){1,3}(?:\.\d+)?)$", RegexOptions.CultureInvariant)]
    private static partial Regex TimeWord();
}
