using System.Runtime.CompilerServices;
using System.Text;

namespace PlayMap.Core.Wire;

/// <summary>The one reader of a row's syntax, used by the assistant's gate, the node's door and the desk's stand-in engine (a path
/// that reads as typed), the controllers' typed parameters, HELP and the catalogue's own tests. A row's syntax is one or more forms separated by a top-level " | " (outside &lt;…&gt; and […]);
/// each form starts with the verb's name.</summary>
public static class VerbSyntax
{
    /// <summary>Each row's forms, read once per row object and released with it.</summary>
    private static readonly ConditionalWeakTable<VerbSpec, IReadOnlyList<string>> FormsOf = new();

    /// <summary>Whether each row takes a path (<see cref="TakesPath"/>), read once per row object and released with it.</summary>
    private static readonly ConditionalWeakTable<VerbSpec, object> PathOf = new();

    private static readonly object PathTaken = new();

    private static readonly object NoPathTaken = new();

    /// <summary>The row's forms: its syntax split at every top-level " | " (outside &lt;…&gt; and […]), each form starting
    /// with the verb's name ("GENERATOR SET g p v | GENERATOR SET g p BY d" → two forms; "UNLOCK x | UNLOCK AREA a |
    /// UNLOCK ALL" → three; an alternative that omits the name is re-prefixed with it).</summary>
    public static IReadOnlyList<string> Alternatives(VerbSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        return FormsOf.GetValue(spec, static s => Split(s));
    }

    private static string[] Split(VerbSpec spec)
    {
        var forms = new List<string>();
        var syntax = spec.Syntax;
        var angle = 0;
        var square = 0;
        var start = 0;
        for (var i = 0; i < syntax.Length; i++)
        {
            var c = syntax[i];
            if (c == '<')
                angle++;
            else if (c == '>' && angle > 0)
                angle--;
            else if (angle == 0 && c == '[')
                square++;
            else if (angle == 0 && c == ']' && square > 0)
                square--;
            else if (c == '|' && angle == 0 && square == 0 && i > 0 && syntax[i - 1] == ' ' && i + 1 < syntax.Length && syntax[i + 1] == ' ')
            {
                forms.Add(Prefixed(spec.Name, syntax[start..i]));
                start = i + 1;
            }
        }
        forms.Add(Prefixed(spec.Name, syntax[start..]));
        return [.. forms];
    }

    private static string Prefixed(string name, string form)
    {
        var f = form.Trim();
        return f == name || f.StartsWith(name + " ", StringComparison.Ordinal) ? f : name + " " + f;
    }

    /// <summary>A form's tokens after the verb up to its first optional part ("[…]"); literal words stay literal. "SURFACE SOURCE
    /// &lt;surface&gt; &lt;source&gt;" requires two; "VIDEO PLAY [ON …]" none. Brackets inside a &lt;…&gt; belong to it.</summary>
    public static IReadOnlyList<string> RequiredTokens(VerbSpec spec, string form)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(form);
        var rest = form.StartsWith(spec.Name, StringComparison.Ordinal) ? form[spec.Name.Length..] : "";
        var tokens = new List<string>();
        var sb = new StringBuilder();
        var angle = 0;
        var square = 0;
        foreach (var c in rest)
        {
            if (c == ' ' && angle == 0 && square == 0)
            {
                if (sb.Length > 0)
                {
                    if (sb[0] == '[')
                        return tokens;
                    tokens.Add(sb.ToString());
                    sb.Clear();
                }
                continue;
            }
            angle += c == '<' ? 1 : c == '>' ? -1 : 0;
            if (angle == 0)
                square += c == '[' ? 1 : c == ']' ? -1 : 0;
            sb.Append(c);
        }
        if (sb.Length > 0 && sb[0] != '[')
            tokens.Add(sb.ToString());
        return tokens;
    }

    /// <summary>Whether the row takes a file's path as the first word after its name in one of its forms: "MEDIA IMPORT
    /// &lt;path|url&gt;", "PROJECT SAVE [&lt;path&gt;]", "FX IMPORT &lt;path&gt;|&lt;json&gt;".</summary>
    public static bool TakesPath(VerbSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        return ReferenceEquals(PathOf.GetValue(spec, static s => Alternatives(s).Any(f => IsPathToken(FirstToken(s, f))) ? PathTaken : NoPathTaken), PathTaken);
    }

    /// <summary>Null when the line's verb takes no path (<see cref="TakesPath"/>) or its path reads as typed; else why the line is
    /// refused. Inside quotes a backslash escapes the mark after it (<see cref="WireGrammar.DropsBackslash"/>), so a path quoted with
    /// single backslashes, as Explorer gives a Windows path and as a person or a model types one, would name another file: the words
    /// say what it reads as and the spelling that reads as typed (<see cref="WireGrammar.Quote"/> of the phrase as it stands), bare
    /// or quoted with each backslash doubled.</summary>
    public static string? DroppedBackslash(VerbSpec spec, WireLine line, int verbWords)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(line);
        if (!TakesPath(spec) || line.QuotedText(verbWords) is not { } typed || !WireGrammar.DropsBackslash(typed))
            return null;
        var spelled = WireGrammar.Quote(typed);
        var how = spelled.StartsWith('"') ? $"{spelled}, each \\ doubled inside the quotes," : $"bare, {spelled},";
        return $"{spec.Name}: inside quotes a backslash escapes the mark after it, so \"{typed}\" reads as {line.Word(verbWords)} — write the path {how} or with / in place of each \\";
    }

    /// <summary>A form's first token after the verb, optional or not ("[&lt;path&gt;]"), or "" when it has none.</summary>
    private static string FirstToken(VerbSpec spec, string form)
    {
        var rest = form.StartsWith(spec.Name, StringComparison.Ordinal) ? form[spec.Name.Length..].TrimStart() : "";
        var angle = 0;
        var square = 0;
        for (var i = 0; i < rest.Length; i++)
        {
            var c = rest[i];
            if (c == ' ' && angle == 0 && square == 0)
                return rest[..i];
            angle += c == '<' ? 1 : c == '>' ? -1 : 0;
            if (angle == 0)
                square += c == '[' ? 1 : c == ']' ? -1 : 0;
        }
        return rest;
    }

    /// <summary>A token whose first placeholder, optional or not, is a path alone or a path or something else.</summary>
    private static bool IsPathToken(string token)
    {
        var t = token.TrimStart('[');
        return t.StartsWith("<path>", StringComparison.Ordinal) || t.StartsWith("<path|", StringComparison.Ordinal);
    }

    /// <summary>True when a token is a placeholder (any word fits it: "&lt;surface&gt;", "&lt;source&gt;[,&lt;backup&gt;…]|OFF")
    /// rather than a choice of literal words ("ON|OFF").</summary>
    public static bool IsPlaceholder(string token)
    {
        ArgumentNullException.ThrowIfNull(token);
        return token.Contains('<', StringComparison.Ordinal) || token.Contains('…', StringComparison.Ordinal);
    }

    /// <summary>True when the line's words after the verb satisfy at least one form's required tokens: each is present, and a
    /// literal choice ("ON|OFF") holds one of its words (any case). <paramref name="why"/> says what the closest form lacks.</summary>
    public static bool Fits(VerbSpec spec, WireLine line, out string? why)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(line);
        var used = VerbCatalog.Match(line) is { } m && ReferenceEquals(m.Spec, spec) ? m.Words : spec.WordCount;
        return Fits(spec, line, used, out why);
    }

    /// <summary><see cref="Fits(VerbSpec, WireLine, out string?)"/> for a line whose verb took <paramref name="verbWords"/> words.</summary>
    public static bool Fits(VerbSpec spec, WireLine line, int verbWords, out string? why)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(line);
        why = null;
        var best = -1;
        foreach (var form in Alternatives(spec))
        {
            var required = RequiredTokens(spec, form);
            var (reached, reason) = Check(spec, required, line, verbWords);
            if (reason is null)
            {
                why = null;
                return true;
            }
            if (reached > best)
            {
                best = reached;
                why = reason;
            }
        }
        return false;
    }

    /// <summary>How many required tokens the line satisfies before the first that it does not, with the words why (null: all).</summary>
    private static (int Reached, string? Why) Check(VerbSpec spec, IReadOnlyList<string> required, WireLine line, int verbWords)
    {
        for (var i = 0; i < required.Count; i++)
        {
            var at = verbWords + i;
            var token = required[i];
            if (at >= line.Count)
                return (i, $"{spec.Name} needs {token} — {spec.Syntax}");
            if (IsPlaceholder(token))
                continue;
            var word = line.Word(at);
            if (!token.Split('|', StringSplitOptions.RemoveEmptyEntries).Any(c => c.Equals(word, StringComparison.OrdinalIgnoreCase)))
                return (i, $"{spec.Name} takes {token} there, not '{word}' — {spec.Syntax}");
        }
        return (required.Count, null);
    }
}
