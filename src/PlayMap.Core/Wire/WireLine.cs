using System.Text;

namespace PlayMap.Core.Wire;

/// <summary>One parsed line from the wire: the optional request id (Show Bus §3, "@a1 VIDEO END 10"),
/// the words (quoted strings kept whole), and the raw text after the verb for verbs that take JSON.</summary>
public sealed record WireLine(string? RequestId, IReadOnlyList<string> Words, string Raw)
{
    public string Verb => Words.Count > 0 ? Words[0].ToUpperInvariant() : "";

    public string Word(int i) => i < Words.Count ? Words[i] : "";

    public string WordUpper(int i) => i < Words.Count ? Words[i].ToUpperInvariant() : "";

    public int Count => Words.Count;

    /// <summary>The raw text after the first <paramref name="skip"/> words (JSON arguments ride here).</summary>
    public string RestAfter(int skip)
    {
        var s = Raw;
        var i = 0;
        for (var w = 0; w < skip; w++)
        {
            while (i < s.Length && char.IsWhiteSpace(s[i]))
                i++;
            if (i < s.Length && s[i] == '"')
            {
                i++;
                while (i < s.Length && s[i] != '"')
                    i += s[i] == '\\' ? 2 : 1;
                i++;
            }
            else
            {
                while (i < s.Length && !char.IsWhiteSpace(s[i]))
                    i++;
            }
        }
        return i >= s.Length ? "" : s[i..].Trim();
    }
}

/// <summary>Why a line could not be read at all (before any verb runs).</summary>
public sealed record WireParseError(string? RequestId, string Reason);

/// <summary>The line grammar Patterns' wire speaks, which PlayMap's wire speaks too: one UTF-8 line per
/// command; an optional "@id " prefix ([A-Za-z0-9_-]{1,32}); words split on spaces with "double quotes"
/// keeping a phrase whole (backslash escapes a quote); replies are "OK …" / "ERR …" with the id after the
/// first word.</summary>
public static class WireGrammar
{
    public const int MaxLineBytes = 64 * 1024;
    public const int MaxRequestId = 32;

    public static bool IsRequestId(ReadOnlySpan<char> id)
    {
        if (id.Length is 0 or > MaxRequestId)
            return false;
        foreach (var c in id)
            if (!(char.IsAsciiLetterOrDigit(c) || c == '-' || c == '_'))
                return false;
        return true;
    }

    /// <summary>Parses a line. An "@" prefix that is not a valid id is refused without running the line
    /// (Patterns' wording, so a sender sees the same correction from either app).</summary>
    public static bool TryParse(string? line, out WireLine parsed, out WireParseError? error)
    {
        parsed = new WireLine(null, [], "");
        error = null;
        if (line is null)
        {
            error = new WireParseError(null, "an empty line");
            return false;
        }
        var text = line.TrimEnd('\r', '\n').Trim();
        string? id = null;
        if (text.StartsWith('@'))
        {
            var space = text.IndexOf(' ', StringComparison.Ordinal);
            var candidate = space < 0 ? text[1..] : text[1..space];
            if (!IsRequestId(candidate) || space < 0)
            {
                error = new WireParseError(null,
                    "a request id is @ and 1-32 letters, digits, '-' or '_', then the line — \"@a1 VIDEO END 10\"");
                return false;
            }
            id = candidate;
            text = text[(space + 1)..].TrimStart();
        }
        if (text.Length == 0)
        {
            error = new WireParseError(id, "an empty line");
            return false;
        }
        if (!TrySplit(text, out var words, out var why))
        {
            error = new WireParseError(id, why);
            return false;
        }
        parsed = new WireLine(id, words, text);
        return true;
    }

    public static bool TrySplit(string text, out List<string> words, out string why)
    {
        words = [];
        why = "";
        var sb = new StringBuilder();
        var i = 0;
        while (i < text.Length)
        {
            while (i < text.Length && char.IsWhiteSpace(text[i]))
                i++;
            if (i >= text.Length)
                break;
            sb.Clear();
            if (text[i] == '"')
            {
                i++;
                var closed = false;
                while (i < text.Length)
                {
                    var c = text[i];
                    if (c == '\\' && i + 1 < text.Length)
                    {
                        sb.Append(text[i + 1]);
                        i += 2;
                        continue;
                    }
                    if (c == '"')
                    {
                        closed = true;
                        i++;
                        break;
                    }
                    sb.Append(c);
                    i++;
                }
                if (!closed)
                {
                    why = "a quote that does not close";
                    return false;
                }
            }
            else if (text[i] is '{' or '[')
            {
                // A JSON argument is one word to the end of the line.
                sb.Append(text, i, text.Length - i);
                i = text.Length;
            }
            else
            {
                while (i < text.Length && !char.IsWhiteSpace(text[i]))
                    sb.Append(text[i++]);
            }
            words.Add(sb.ToString());
        }
        return true;
    }

    /// <summary>Quotes a word for a line when it needs it.</summary>
    public static string Quote(string word)
    {
        ArgumentNullException.ThrowIfNull(word);
        if (word.Length > 0 && !word.Any(c => char.IsWhiteSpace(c) || c == '"'))
            return word;
        return "\"" + word.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
    }

    /// <summary>"OK", "OK @a1", "OK @a1 payload". A payload never contains a newline (JSON is compact).</summary>
    public static string Ok(string? requestId, string? payload = null)
    {
        var sb = new StringBuilder("OK");
        if (requestId is not null)
            sb.Append(" @").Append(requestId);
        if (!string.IsNullOrEmpty(payload))
            sb.Append(' ').Append(OneLine(payload));
        return sb.ToString();
    }

    public static string Err(string? requestId, string reason)
    {
        var sb = new StringBuilder("ERR");
        if (requestId is not null)
            sb.Append(" @").Append(requestId);
        sb.Append(' ').Append(OneLine(reason));
        return sb.ToString();
    }

    /// <summary>A reply is one line: any newline in words becomes a space.</summary>
    public static string OneLine(string s) =>
        s.Contains('\n', StringComparison.Ordinal) || s.Contains('\r', StringComparison.Ordinal)
            ? s.Replace("\r\n", " ", StringComparison.Ordinal).Replace('\n', ' ').Replace('\r', ' ')
            : s;
}
