using System.Text;
using PlayMap.Core.Model;
using PlayMap.Core.Wire;

namespace PlayMap.Assistant;

/// <summary>What the gate made of one proposed line. <see cref="Ok"/> false means it must not run and
/// <see cref="Reason"/> says why in words the model (or the operator) can act on. A refused line's risk is
/// high: unknown stays unknown.</summary>
public sealed record LineCheck(bool Ok, string Line, WireLine? Parsed, VerbSpec? Spec, VerbRisk Risk, string Reason)
{
    public static LineCheck Refused(string line, string reason) => new(false, line, null, null, VerbRisk.High, reason);
}

/// <summary>The fence every line an assistant proposes passes before anything runs or queues: the wire's own
/// grammar, the vocabulary, the verb's required words and fixed choices, and — given the project — that an id
/// it names exists. The connection's own verbs (HELLO, AUTH…) and a key or token in a line are refused: an
/// assistant never handles a secret. Risk comes from the verb's entry in the catalogue; anything not in it
/// is high.</summary>
public static class CommandGate
{
    /// <summary>Verbs that belong to a connection, not to the show.</summary>
    private static readonly HashSet<string> ConnectionVerbs = new(StringComparer.Ordinal) { "HELLO", "NONCE", "AUTH", "AUTH-PROOF", "SUBSCRIBE" };

    /// <summary>The id prefixes of the entities <see cref="Project.AllIds"/> lists (<see cref="Ids"/>); masks and
    /// pins live inside their owners and are left to the node to judge.</summary>
    private static readonly string[] IdPrefixes =
        [Ids.Media, Ids.Playlist, Ids.PlaylistItem, Ids.Canvas, Ids.Layer, Ids.Generator, Ids.Surface, Ids.Mesh, Ids.Projector, Ids.Output, Ids.Camera, Ids.Scan, Ids.Device];

    /// <summary>The risk of a verb by name: the catalogue's, or high when the catalogue does not know it.</summary>
    public static VerbRisk RiskOf(string verbName) => VerbCatalog.Find(verbName)?.Risk ?? VerbRisk.High;

    /// <summary>Checks one line. With <paramref name="project"/>, a word shaped like a generated id (a known
    /// prefix and eight hex digits, alone or after "kind:") must name an entity in it.</summary>
    public static LineCheck Check(string? line, Project? project = null)
    {
        if (string.IsNullOrWhiteSpace(line))
            return LineCheck.Refused("", "an empty line");
        var text = line.Trim();
        if (text.Contains('\n', StringComparison.Ordinal) || text.Contains('\r', StringComparison.Ordinal))
            return LineCheck.Refused(text, "one verb per line — a line break makes two lines; give each line its own entry");
        if (Encoding.UTF8.GetByteCount(text) > WireGrammar.MaxLineBytes)
            return LineCheck.Refused(text, "the line is longer than the wire takes (64 KiB)");
        if (!WireGrammar.TryParse(text, out var parsed, out var error))
            return LineCheck.Refused(text, error?.Reason ?? "the line could not be read");
        // The assistant numbers nothing itself: a request id the model wrote is dropped, the line kept.
        parsed = parsed with { RequestId = null };
        var normalised = parsed.Raw;
        var match = VerbCatalog.Match(parsed);
        if (match is null)
        {
            var named = parsed.Count >= 2 ? parsed.Word(0) + " " + parsed.Word(1) : parsed.Word(0);
            return LineCheck.Refused(normalised, $"'{named}' is not a PlayMap verb — list_verbs names every verb with its syntax");
        }
        var (spec, verbWords) = match.Value;
        if (ConnectionVerbs.Contains(spec.Name))
            return LineCheck.Refused(normalised, $"{spec.Name} is the connection's own verb, not a show verb");
        if (spec.Name.StartsWith("LINK ", StringComparison.Ordinal) && parsed.Count > verbWords + 1)
            return LineCheck.Refused(normalised, "a key or token never rides in an assistant's line — the operator links with its key by hand");
        if (!CheckRequiredWords(spec, parsed, verbWords, out var why))
            return LineCheck.Refused(normalised, why);
        if (project is not null && FirstUnknownId(project, parsed, verbWords) is { } unknown)
            return LineCheck.Refused(normalised, $"no entity '{unknown}' in the project — look ids up (get_project, get_entity) rather than writing them");
        return new LineCheck(true, normalised, parsed, spec, spec.Risk, "");
    }

    /// <summary>The words the verb's syntax requires (those before its first optional part) are present, and
    /// a fixed choice ("ON|OFF") is one of its choices.</summary>
    private static bool CheckRequiredWords(VerbSpec spec, WireLine parsed, int verbWords, out string why)
    {
        why = "";
        var required = RequiredTokens(spec);
        for (var i = 0; i < required.Count; i++)
        {
            var at = verbWords + i;
            if (at >= parsed.Count)
            {
                why = $"{spec.Name} needs {required[i]} — {spec.Syntax}";
                return false;
            }
            var token = required[i];
            if (token.Contains('<', StringComparison.Ordinal) || token.Contains('…', StringComparison.Ordinal))
                continue;
            var choices = token.Split('|', StringSplitOptions.RemoveEmptyEntries);
            var word = parsed.Word(at);
            if (!choices.Any(c => c.Equals(word, StringComparison.OrdinalIgnoreCase)))
            {
                why = $"{spec.Name} takes {token} there, not '{word}' — {spec.Syntax}";
                return false;
            }
        }
        return true;
    }

    /// <summary>The syntax's tokens after the verb up to its first optional part: "SURFACE SOURCE &lt;surface&gt;
    /// &lt;source&gt;" requires two; "VIDEO PLAY [ON …]" none. Brackets inside a &lt;…&gt; belong to it.</summary>
    internal static IReadOnlyList<string> RequiredTokens(VerbSpec spec)
    {
        // Every syntax in the catalogue starts with its verb's name.
        var rest = spec.Syntax.StartsWith(spec.Name, StringComparison.Ordinal) ? spec.Syntax[spec.Name.Length..] : "";
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

    /// <summary>The first word after the verb that looks like a generated id ("sf1a2b3c4d", or the id in
    /// "generator:gn1a2b3c4d") and is not in the project; null when every such word names something.</summary>
    private static string? FirstUnknownId(Project project, WireLine parsed, int verbWords)
    {
        HashSet<string>? ids = null;
        for (var i = verbWords; i < parsed.Count; i++)
        {
            var word = parsed.Word(i);
            if (word.Length > 0 && word[0] is '{' or '[')
                continue;
            var colon = word.IndexOf(':', StringComparison.Ordinal);
            var candidate = colon >= 0 ? word[(colon + 1)..] : word;
            if (!LooksLikeGeneratedId(candidate))
                continue;
            ids ??= project.AllIds().ToHashSet(StringComparer.Ordinal);
            if (!ids.Contains(candidate))
                return candidate;
        }
        return null;
    }

    /// <summary>A prefix from <see cref="Ids"/> and exactly eight lowercase hex digits — the shape
    /// <see cref="Ids.New"/> makes. Hand-made ids ("op_1", "cv_main") are left to the node to judge.</summary>
    internal static bool LooksLikeGeneratedId(string word)
    {
        if (word.Length != 10)
            return false;
        var prefix = word[..2];
        if (Array.IndexOf(IdPrefixes, prefix) < 0)
            return false;
        for (var i = 2; i < word.Length; i++)
            if (!char.IsAsciiHexDigitLower(word[i]))
                return false;
        return true;
    }
}
