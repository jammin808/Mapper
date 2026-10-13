using System.Text;
using PlayMap.Core.Model;
using PlayMap.Core.Wire;

namespace PlayMap.Assistant;

/// <summary>What the gate made of one proposed line. <see cref="Ok"/> false means it must not run and
/// <see cref="Reason"/> says why in words the model (or the operator) can act on. A refused line's risk is
/// high: unknown stays unknown. <see cref="HoldsSecret"/>: the line carries its verb's secret words, so it never runs
/// on an assistant's word — it waits for a person, who sees it without them.</summary>
public sealed record LineCheck(bool Ok, string Line, WireLine? Parsed, VerbSpec? Spec, VerbRisk Risk, string Reason, bool HoldsSecret = false)
{
    public static LineCheck Refused(string line, string reason) => new(false, line, null, null, VerbRisk.High, reason);
}

/// <summary>The fence every line an assistant proposes passes before anything runs or queues: the wire's own
/// grammar, the vocabulary, the verb's required words and fixed choices, and — given the project — that an id
/// it names exists. The connection's own verbs (HELLO, AUTH…) are refused. A line carrying its verb's secret words (a
/// link's token) passes marked: it is only ever queued for a person, shown without them, and an approval runs it
/// whole. Risk comes from the verb's entry in the catalogue; anything not in it is high.</summary>
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
        if (!CheckRequiredWords(spec, parsed, verbWords, out var why))
            return LineCheck.Refused(normalised, why);
        if (project is not null && FirstUnknownId(project, parsed, verbWords) is { } unknown)
            return LineCheck.Refused(normalised, $"no entity '{unknown}' in the project — look ids up (get_project, get_entity) rather than writing them");
        return new LineCheck(true, normalised, parsed, spec, VerbCatalog.RiskOf(parsed), "", VerbCatalog.HoldsSecret(parsed));
    }

    /// <summary>The words one of the verb's forms requires (those before its first optional part) are present, and a fixed
    /// choice ("ON|OFF") is one of its choices (<see cref="VerbSyntax.Fits(VerbSpec, WireLine, int, out string?)"/>).</summary>
    private static bool CheckRequiredWords(VerbSpec spec, WireLine parsed, int verbWords, out string why)
    {
        var fits = VerbSyntax.Fits(spec, parsed, verbWords, out var reason);
        why = reason ?? "";
        return fits;
    }

    /// <summary>The first form's tokens after the verb up to its first optional part: "SURFACE SOURCE &lt;surface&gt;
    /// &lt;source&gt;" requires two; "VIDEO PLAY [ON …]" none. Brackets inside a &lt;…&gt; belong to it.</summary>
    internal static IReadOnlyList<string> RequiredTokens(VerbSpec spec) =>
        VerbSyntax.RequiredTokens(spec, VerbSyntax.Alternatives(spec)[0]);

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
