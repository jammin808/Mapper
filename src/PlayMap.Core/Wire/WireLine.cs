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

    /// <summary>Word <paramref name="i"/> as the line spells it between its quotes, its escapes not undone; null when that word is not a
    /// quoted phrase (<see cref="WireGrammar.QuotedText"/>).</summary>
    public string? QuotedText(int i) => WireGrammar.QuotedText(Raw, i);

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
/// keeping a phrase whole, a backslash in it escaping the mark after it (\" a quote, \\ a backslash, and any
/// other mark read alone); a bare word keeps every backslash; replies are "OK …" / "ERR …" with the id after the
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
        ArgumentNullException.ThrowIfNull(text);
        words = [];
        why = "";
        var sb = new StringBuilder();
        var i = 0;
        while (SkipSpace(text, ref i))
        {
            sb.Clear();
            var start = i;
            if (!ReadWord(text, ref i, sb, out _))
            {
                why = EscapesAQuote(text, start + 1) ? UnclosedAfterEscapedQuote : "a quote that does not close";
                return false;
            }
            words.Add(sb.ToString());
        }
        return true;
    }

    /// <summary>Why a phrase that does not close holds a backslash before a quote: that quote is a mark of the phrase, so a phrase typed
    /// to end in a backslash, as Explorer gives a folder, runs on to the end of the line.</summary>
    private const string UnclosedAfterEscapedQuote =
        @"a quote that does not close — inside quotes \"" is a quote mark, never the phrase's end, so a phrase that ends in a backslash doubles it (""C:\\shows\\"")";

    /// <summary>Whether the open phrase from <paramref name="from"/> holds a backslash escaping a quote, read as <see cref="ReadWord"/>
    /// reads it.</summary>
    private static bool EscapesAQuote(string text, int from)
    {
        for (var i = from; i + 1 < text.Length; i++)
        {
            if (text[i] != '\\')
                continue;
            if (text[i + 1] == '"')
                return true;
            i++;
        }
        return false;
    }

    /// <summary>Word <paramref name="index"/> of a line as the line spells it between its quotes, its escapes not undone ("C:\\clips"
    /// for the word C:\clips); null when that word is not a quoted phrase (a bare word, JSON, past the last word) or the line does not
    /// split.</summary>
    public static string? QuotedText(string text, int index)
    {
        ArgumentNullException.ThrowIfNull(text);
        var sb = new StringBuilder();
        var i = 0;
        for (var w = 0; SkipSpace(text, ref i); w++)
        {
            sb.Clear();
            if (!ReadWord(text, ref i, sb, out var quoted))
                return null;
            if (w == index)
                return quoted is { } phrase ? text[phrase] : null;
        }
        return null;
    }

    /// <summary>Whether a quoted phrase, as the line spells it between its quotes, holds a backslash before a mark other than a quote or a
    /// backslash. A backslash in a quoted phrase escapes the mark after it, as Patterns reads it, so that mark is read alone and the
    /// backslash is lost: the phrase C:\clips\a.mp4 reads as C:clipsa.mp4. <see cref="Quote"/> writes no such phrase.</summary>
    public static bool DropsBackslash(string quoted)
    {
        ArgumentNullException.ThrowIfNull(quoted);
        for (var i = 0; i + 1 < quoted.Length; i++)
        {
            if (quoted[i] != '\\')
                continue;
            if (quoted[i + 1] is not ('"' or '\\'))
                return true;
            i++;
        }
        return false;
    }

    /// <summary>Moves past white space; false at the end of the text.</summary>
    private static bool SkipSpace(string text, ref int i)
    {
        while (i < text.Length && char.IsWhiteSpace(text[i]))
            i++;
        return i < text.Length;
    }

    /// <summary>Reads the word that starts at <paramref name="i"/> into <paramref name="word"/> and moves past it: a quoted phrase to its
    /// closing quote, a backslash in it taking the mark after it as it stands (<paramref name="quoted"/> is the phrase as the line spells
    /// it between its quotes); a word that opens with "{" or "[" to the end of the line (JSON); any other word to the next white space.
    /// False when a quote does not close.</summary>
    private static bool ReadWord(string text, ref int i, StringBuilder word, out Range? quoted)
    {
        quoted = null;
        if (text[i] == '"')
        {
            var from = ++i;
            while (i < text.Length)
            {
                var c = text[i];
                if (c == '\\' && i + 1 < text.Length)
                {
                    word.Append(text[i + 1]);
                    i += 2;
                    continue;
                }
                if (c == '"')
                {
                    quoted = from..i;
                    i++;
                    return true;
                }
                word.Append(c);
                i++;
            }
            return false;
        }
        if (text[i] is '{' or '[')
        {
            word.Append(text, i, text.Length - i);
            i = text.Length;
            return true;
        }
        while (i < text.Length && !char.IsWhiteSpace(text[i]))
            word.Append(text[i++]);
        return true;
    }

    /// <summary>Quotes a word for a line when it needs it: when it is empty, holds a space or a quote mark, or opens with "{" or "["
    /// (which <see cref="TrySplit"/> reads bare as JSON to the end of the line), so it reads back as one word wherever it stands.</summary>
    public static string Quote(string word)
    {
        ArgumentNullException.ThrowIfNull(word);
        if (word.Length > 0 && word[0] is not ('{' or '[') && !word.Any(c => char.IsWhiteSpace(c) || c == '"'))
            return word;
        return "\"" + word.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
    }

    /// <summary>Words as one line, each quoted as it needs (<see cref="Quote"/>) but the last when it opens with "{" or "[": that one
    /// rides bare, as JSON does, so it reads back as the same word and a verb that takes JSON reads it from the raw text.</summary>
    public static string Join(IReadOnlyList<string> words)
    {
        ArgumentNullException.ThrowIfNull(words);
        var sb = new StringBuilder();
        for (var i = 0; i < words.Count; i++)
        {
            var w = words[i];
            if (i > 0)
                sb.Append(' ');
            var tail = i == words.Count - 1 && w.Length > 0 && w[0] is ('{' or '[') && !char.IsWhiteSpace(w[^1]) && !w.Contains('\n', StringComparison.Ordinal);
            sb.Append(tail ? w : Quote(w));
        }
        return sb.ToString();
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
