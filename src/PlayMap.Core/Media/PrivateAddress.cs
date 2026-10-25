using System.Buffers;
using System.Globalization;

namespace PlayMap.Core.Media;

/// <summary>An address that carries a pass, read off the address alone — the rule Patterns reads (its <c>WindowAddresses</c>'
/// <c>LooksPrivate</c> and <c>LooksPrivateBare</c>), word for word and held to the same table: a Stagewatch window's path (<c>/w/</c>
/// and 26 of its window alphabet — the token is the window's pass), a query or fragment word that names a pass (<see cref="PassWords"/>:
/// a viewing code, a PIN, a key, a token), or a user in the authority. PlayMap keeps no such address: <c>MEDIA IMPORT</c> refuses one
/// without repeating it, a line that carries one is written down with it shown as <see cref="Marker"/>
/// (<see cref="Wire.VerbCatalog.Redacted(Wire.WireLine)"/>), and an item a project brings with one is scrubbed when the project is
/// read. Pure; any thread.</summary>
public static class PrivateAddress
{
    /// <summary>What a shown line, a name or a reply carries in place of a private address.</summary>
    public const string Marker = "‹private address›";

    /// <summary>Stagewatch's window alphabet: lower-case letters and digits without l, o, 0 and 1.</summary>
    public const string WindowAlphabet = "abcdefghijkmnpqrstuvwxyz23456789";

    /// <summary>A Stagewatch window token's length: the path is <c>/w/</c> and this many of <see cref="WindowAlphabet"/>.</summary>
    public const int WindowTokenLength = 26;

    private static readonly string[] PassWordList =
    [
        "win", "pin", "token", "key", "view", "code", "secret", "password", "pass", "apikey", "api_key", "access_token", "auth", "sig", "signature",
    ];

    /// <summary>The query and fragment words that name a pass. Compared whole, after percent-decoding, case-blind.</summary>
    public static readonly IReadOnlySet<string> PassWords = new HashSet<string>(PassWordList, StringComparer.Ordinal);

    private static readonly SearchValues<char> AuthorityEnd = SearchValues.Create("/?#\\");
    private static readonly SearchValues<char> QueryStart = SearchValues.Create("?#");
    private static readonly char[] PathSeparators = ['/', '\\'];
    private static readonly char[] QuerySeparators = ['?', '&', ';', '#', '/'];

    /// <summary>An address cut at its parts, by hand rather than by <see cref="Uri"/>, so a word that is not quite a URL is read the
    /// way a browser's address is.</summary>
    private readonly record struct Parts(string Scheme, string UserInfo, string HostPort, string Path, string Rest);

    /// <summary>True when a word holds the address of a Stagewatch window — a path segment <c>w</c> followed by a 26-symbol window
    /// token — wherever in the word <see cref="IsWord"/> finds it.</summary>
    public static bool IsStagewatchWindow(string? url)
    {
        if (string.IsNullOrEmpty(url) || !HasWindowSeparator(url))
            return false;
        var spans = new List<(int Start, int Stop)>();
        Find(url, spans);
        foreach (var (start, stop) in spans)
        {
            if (TrySplit(Absolute(url[start..stop]), out var p) && !IsHeldScheme(p.Scheme) && HasWindowPath(p.Path))
                return true;
        }
        return false;
    }

    /// <summary>True when a word holds a private address whose pass is more than a user in its authority: a window's path, a pass word
    /// in its query or fragment, or a token read private whole (<see cref="MaxSchemesInToken"/>). A stream that plays from its
    /// address keeps one whose only pass is a user (<c>rtsp://user:pass@…</c>); no player reads a window's token or a viewing code.</summary>
    public static bool PassBeyondUser(string? word)
    {
        if (string.IsNullOrEmpty(word) || !MayBePrivate(word))
            return false;
        var spans = new List<(int Start, int Stop)>();
        Find(word, spans);
        foreach (var (start, stop) in spans)
        {
            // A token read private whole is no one address: what its pass is was never read.
            var address = word[start..stop];
            if (!Looks(address) || !TrySplit(Absolute(address), out var p) || HasWindowPath(p.Path) || HasPassWord(p.Rest))
                return true;
        }
        return false;
    }

    /// <summary>True when the address carries a pass: a window path, a pass word in the query or fragment, or a user in the authority.
    /// A held address (Patterns' <c>kept://</c> and <c>unkept://</c>), a file and an address with no host a browser reads (white space in
    /// it, or the punctuation of a source's name — "ndi:MEDIA-PC (OBS@Main)" is no user at a host) are never private. A bare host
    /// ("10.0.0.5:9924/w/…") reads as https, as a browser opens it.</summary>
    public static bool Looks(string? url)
    {
        // Every private form carries one of three marks; an address with none answers from one scan that allocates nothing.
        if (url is null || !MayBePrivate(url))
            return false;
        if (!TrySplit(Absolute(url), out var p))
            return false;
        if (p.Scheme.Equals("file", StringComparison.OrdinalIgnoreCase) || IsHeldScheme(p.Scheme) || !IsHostPort(p.HostPort))
            return false;
        return p.UserInfo.Length > 0 || HasWindowPath(p.Path) || HasPassWord(p.Rest);
    }

    /// <summary>True when a token written without a scheme is a private address beyond doubt: a host a browser reads, then a window's
    /// path or a pass word, the marks a name or a word of a line never carries. A user is not enough in words ("Doors 19:30@foyer");
    /// where the token can be nothing but a page, <paramref name="userWithPassword"/> counts a user with a password too.</summary>
    public static bool LooksBare(string? token, bool userWithPassword = false)
    {
        var s = (token ?? "").Trim();
        if (s.Length == 0 || s.Contains("://", StringComparison.Ordinal) || !(char.IsLetterOrDigit(s[0]) || s[0] == '['))
            return false;
        if (!MayBePrivate(s))
            return false;
        foreach (var c in s)
        {
            if (char.IsWhiteSpace(c) || char.IsControl(c))
                return false;
        }
        var absolute = Absolute(s);
        if (!absolute.StartsWith("https://", StringComparison.Ordinal) || absolute.Length != s.Length + 8)
            return false;   // a drive or rooted path is a file
        if (!TrySplit(absolute, out var p) || !IsHostPort(p.HostPort))
            return false;
        if (HasWindowPath(p.Path) || HasPassWord(p.Rest))
            return true;
        return userWithPassword && p.UserInfo.Contains(':', StringComparison.Ordinal);
    }

    /// <summary>True when a word (a location, a word of a line) holds a private address anywhere in it, read as
    /// <see cref="InText"/> reads a text: so "(https://h/w/…)", "&lt;https://h/w/…&gt;", "Stage:https://…" and "(10.0.0.5:9924/w/…)"
    /// are private as the bare address is, and a name such as "ops@venue" or "19:30@foyer" stays a name.</summary>
    public static bool IsWord(string? word) => InText(word);

    /// <summary>A word as it may be written down: <see cref="Marker"/> when it holds a private address (<see cref="IsWord"/>), else the
    /// word itself (the same instance).</summary>
    public static string Shown(string word) => IsWord(word) ? Marker : word;

    /// <summary>True when a text (a word, a name, a phrase) holds a private address, found as Patterns finds one in free text (its
    /// <c>WindowAddresses.RewriteText</c> and <c>RewriteBareText</c>): the text is cut into tokens at white space, a quote, an angle
    /// bracket or a backtick; from each <c>://</c> the scheme is read back over its characters and the address runs to its token's end,
    /// its trailing punctuation dropped, read by <see cref="Looks"/>; a token's text before its first scheme is read by
    /// <see cref="LooksBare"/> from past its leading punctuation and from the host before its first path or query mark. A rooted path
    /// stays a file. A token naming more than <see cref="MaxSchemesInToken"/> schemes, one of which may carry a pass, is private whole.
    /// The texts read here are words and strings the grammar or a JSON reader has already read, so no escape is undone. A text with
    /// none of a pass's marks is answered from one scan that allocates nothing.</summary>
    public static bool InText(string? text) => !string.IsNullOrEmpty(text) && MayBePrivate(text) && Find(text, null) > 0;

    /// <summary>A text as a name may keep it: each private address in it (<see cref="InText"/>) replaced by its host and
    /// <see cref="Marker"/> ("10.0.0.5 ‹private address›"), every other character as it was ("Lobby (10.0.0.5 ‹private address›)");
    /// the text itself (the same instance) when it holds none.</summary>
    public static string ShownText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length == 0 || !MayBePrivate(text))
            return text;
        var spans = new List<(int Start, int Stop)>();
        if (Find(text, spans) == 0)
            return text;
        var shown = new System.Text.StringBuilder(text.Length);
        var copied = 0;
        foreach (var (start, stop) in spans)
        {
            shown.Append(text, copied, start - copied);
            if (HostOf(text[start..stop]) is { Length: > 0 } host)
                shown.Append(host).Append(' ');
            shown.Append(Marker);
            copied = stop;
        }
        return shown.Append(text, copied, text.Length - copied).ToString();
    }

    /// <summary>The most schemes one token is read for. An address names one, or two where its query carries another (a sign-in's
    /// return address); each is read to the token's end, so past this many a token that bears a pass's mark is private whole, unread,
    /// and the longest word the wire reads is read in time whatever it holds.</summary>
    public const int MaxSchemesInToken = 16;

    /// <summary>Each private address in a text, in order and never overlapping, added to <paramref name="spans"/> (where it starts and
    /// where it stops); how many were found. With no list, it stops at the first.</summary>
    private static int Find(string text, List<(int Start, int Stop)>? spans)
    {
        var found = 0;
        var i = 0;
        while (i < text.Length)
        {
            var end = TokenEnd(text, i);
            if (end == i)
            {
                i++;
                continue;
            }
            found += FindInToken(text, i, end, spans);
            if (found > 0 && spans is null)
                return found;
            i = end;
        }
        return found;
    }

    /// <summary>The private addresses of one token, <c>text[from..to]</c>: the bare part before its first scheme, then the first of its
    /// schemes whose address carries a pass (that address runs to the token's end, so nothing after it is read again).</summary>
    private static int FindInToken(string text, int from, int to, List<(int Start, int Stop)>? spans)
    {
        var found = 0;
        var first = text.IndexOf("://", from, to - from, StringComparison.Ordinal);
        var bareTo = first < 0 ? to : SchemeStart(text, from, first);
        if (Bare(text, from, bareTo) is var (bareStart, bareStop))
        {
            found++;
            if (spans is null)
                return found;
            spans.Add((bareStart, bareStop));
        }
        var schemes = 0;
        var firstScheme = -1;
        for (var at = first; at >= 0; at = at + 3 < to ? text.IndexOf("://", at + 3, to - at - 3, StringComparison.Ordinal) : -1)
        {
            var start = SchemeStart(text, from, at);
            while (start < at && !char.IsAsciiLetter(text[start]))
                start++;
            if (start == at)
                continue;   // no scheme before this "://": nothing to read from here
            if (firstScheme < 0)
                firstScheme = start;
            var stop = TrimTrailing(text, at + 3, to);
            if (++schemes > MaxSchemesInToken)
            {
                stop = TrimTrailing(text, firstScheme, to);
                if (!MayBePrivate(text.AsSpan(firstScheme, stop - firstScheme)))
                    break;
                spans?.Add((firstScheme, stop));
                return found + 1;
            }
            if (Looks(text[start..stop]))
            {
                spans?.Add((start, stop));
                return found + 1;
            }
        }
        return found;
    }

    /// <summary>Where the scheme before a <c>://</c> at <paramref name="at"/> begins, read back over scheme characters no further than
    /// <paramref name="from"/>; <paramref name="at"/> itself when none stands before it.</summary>
    private static int SchemeStart(string text, int from, int at)
    {
        var start = at;
        while (start > from && IsSchemeChar(text[start - 1]))
            start--;
        return start;
    }

    /// <summary>A private address written bare in <c>text[from..to]</c> (no scheme in it), with its trailing punctuation dropped: read
    /// from past the token's leading punctuation ("(10.0.0.5:9924/w/…)"), as Patterns reads one, and from the host that stands before
    /// the first path or query mark, after whatever is glued before it ("Stage:10.0.0.5:9924/w/…"). A rooted path ("/shows/w/…") is a
    /// file and is read as neither. Null when neither is private.</summary>
    private static (int Start, int Stop)? Bare(string text, int from, int to)
    {
        var stop = TrimTrailing(text, from, to);
        var start = from;
        while (start < stop && !(char.IsLetterOrDigit(text[start]) || text[start] == '[') && text[start] is not ('/' or '\\'))
            start++;
        if (start >= stop)
            return null;
        if (LooksBare(text[start..stop]))
            return (start, stop);
        var host = HostBeforePath(text, start, stop);
        return host > start && LooksBare(text[host..stop]) ? (host, stop) : null;
    }

    /// <summary>Where the host and port that stand before the first path or query mark of <c>text[from..to]</c> begin: read back from
    /// that mark over a port's digits and its colon, then over a host's characters (or a bracketed IPv6 literal); <paramref name="from"/>
    /// when the token has no such mark or nothing glued before its host.</summary>
    private static int HostBeforePath(string text, int from, int to)
    {
        var mark = text.AsSpan(from, to - from).IndexOfAny(AuthorityEnd);
        if (mark <= 0)
            return from;
        var at = from + mark;
        var port = at;
        while (port > from && char.IsAsciiDigit(text[port - 1]))
            port--;
        if (port < at && port > from && text[port - 1] == ':')
            at = port - 1;
        if (at > from && text[at - 1] == ']')
        {
            var open = text.LastIndexOf('[', at - 1, at - from);
            return open < 0 ? from : open;
        }
        var host = at;
        while (host > from && (char.IsLetterOrDigit(text[host - 1]) || text[host - 1] is '-' or '.' or '_' or '~' or '%'))
            host--;
        return host;
    }

    /// <summary>Where a token's address stops: its end with the trailing punctuation a sentence glues on dropped, never before
    /// <paramref name="from"/>.</summary>
    private static int TrimTrailing(string text, int from, int to)
    {
        var stop = to;
        while (stop > from && IsTrailingPunctuation(text[stop - 1]))
            stop--;
        return stop;
    }

    /// <summary>Where a token that starts at <paramref name="from"/> ends: at white space, a quote, an angle bracket or a backtick.</summary>
    private static int TokenEnd(string text, int from)
    {
        var j = from;
        while (j < text.Length && !IsTokenEnd(text[j]))
            j++;
        return j;
    }

    private static bool IsTrailingPunctuation(char c) => c is '.' or ',' or ';' or ':' or '!' or '?' or ')' or ']' or '}' or '…';

    private static bool IsTokenEnd(char c) => char.IsWhiteSpace(c) || c is '"' or '\'' or '<' or '>' or '`';

    /// <summary>The host an address names, in lower case and without its port or user ("" when it names none a browser reads): what
    /// may be said of a private address.</summary>
    public static string HostOf(string? address)
    {
        if (!TrySplit(Absolute(address), out var p))
            return "";
        return TrySplitHostPort(p.HostPort, out var host, out _) ? host : "";
    }

    /// <summary>An address as a browser opens it: a scheme kept as written; a drive, UNC or rooted path left alone; a bare host taken as
    /// https.</summary>
    private static string Absolute(string? url)
    {
        var s = (url ?? "").Trim();
        if (s.Length == 0 || s.Contains("://", StringComparison.Ordinal))
            return s;
        if (s.StartsWith('/') || s.StartsWith('\\') || (s.Length >= 2 && char.IsAsciiLetter(s[0]) && s[1] == ':' && (s.Length == 2 || s[2] is '/' or '\\')))
            return s;
        return "https://" + s;
    }

    private static bool IsHeldScheme(string scheme)
        => scheme.Equals("kept", StringComparison.OrdinalIgnoreCase) || scheme.Equals("unkept", StringComparison.OrdinalIgnoreCase);

    private static bool TrySplit(string s, out Parts parts)
    {
        parts = default;
        var sep = s.IndexOf("://", StringComparison.Ordinal);
        if (sep <= 0)
            return false;
        var scheme = s[..sep];
        if (!char.IsAsciiLetter(scheme[0]))
            return false;
        foreach (var c in scheme)
        {
            if (!IsSchemeChar(c))
                return false;
        }
        var after = s[(sep + 3)..];
        var end = after.AsSpan().IndexOfAny(AuthorityEnd);
        var authority = end < 0 ? after : after[..end];
        // An authority with white space in it is words, not an address ("https://" before "Timer @ FOH" names no user).
        foreach (var c in authority)
        {
            if (char.IsWhiteSpace(c) || char.IsControl(c))
                return false;
        }
        var tail = end < 0 ? "" : after[end..];
        var at = authority.LastIndexOf('@');
        var userInfo = at >= 0 ? authority[..at] : "";
        var hostPort = at >= 0 ? authority[(at + 1)..] : authority;
        var q = tail.AsSpan().IndexOfAny(QueryStart);
        parts = new Parts(scheme, at >= 0 && userInfo.Length == 0 ? "@" : userInfo, hostPort, q < 0 ? tail : tail[..q], q < 0 ? "" : tail[q..]);
        return true;
    }

    /// <summary>A host and port a browser reads, parsed: the host in lower case, the port 1–65535 (0 when none is named).</summary>
    private static bool TrySplitHostPort(string hostPort, out string host, out int port)
    {
        host = "";
        port = 0;
        if (!ReadHostPort(hostPort.AsSpan().Trim(), out var name, out var portText))
            return false;
        if (portText.Length > 0 && (!int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out port) || port is < 1 or > 65535))
            return false;
        host = name.ToString().ToLowerInvariant();
        return true;
    }

    /// <summary>True for an authority's host and port as a browser reads them: a name of letters, digits and <c>-._~%</c> (or a bracketed
    /// IPv6 literal), then a port of digits or none — no white space, and none of the punctuation a source's name carries ("Main)").</summary>
    private static bool IsHostPort(string hostPort) => ReadHostPort(hostPort, out _, out _);

    private static bool ReadHostPort(ReadOnlySpan<char> s, out ReadOnlySpan<char> host, out ReadOnlySpan<char> portText)
    {
        host = default;
        portText = default;
        if (s.Length == 0)
            return false;
        if (s[0] == '[')
        {
            var close = s.IndexOf(']');
            if (close < 2)
                return false;
            foreach (var c in s[1..close])
            {
                if (!(char.IsAsciiLetterOrDigit(c) || c is ':' or '.' or '%' or '-' or '_'))
                    return false;
            }
            host = s[..(close + 1)];
            var after = s[(close + 1)..];
            if (after.Length > 0)
            {
                if (after[0] != ':')
                    return false;
                portText = after[1..];
            }
        }
        else
        {
            var colon = s.IndexOf(':');
            host = colon >= 0 ? s[..colon] : s;
            portText = colon >= 0 ? s[(colon + 1)..] : default;
            if (host.Length == 0)
                return false;
            foreach (var c in host)
            {
                if (!(char.IsLetterOrDigit(c) || c is '-' or '.' or '_' or '~' or '%'))
                    return false;
            }
        }
        foreach (var c in portText)
        {
            if (!char.IsAsciiDigit(c))
                return false;
        }
        return true;
    }

    /// <summary>The first look, allocating nothing: every private form carries a user's <c>@</c>, a window's <c>/w/</c> segment or a
    /// pass word before an <c>=</c> (a percent-encoded name always earns the full read). False is the answer; true asks for the full
    /// read.</summary>
    private static bool MayBePrivate(ReadOnlySpan<char> s)
        => s.Contains('@') || HasWindowSeparator(s) || MayNamePassWord(s);

    /// <summary>A <c>w</c> segment between two path separators, either slash, any case.</summary>
    private static bool HasWindowSeparator(ReadOnlySpan<char> s)
    {
        for (var i = 1; i + 1 < s.Length; i++)
        {
            if ((s[i] is 'w' or 'W') && (s[i - 1] is '/' or '\\') && (s[i + 1] is '/' or '\\'))
                return true;
        }
        return false;
    }

    /// <summary>A name before an <c>=</c> that is a pass word, or that is percent-encoded — a superset of what <see cref="HasPassWord"/>
    /// finds: the same separators cut the same names, and only a segment's first <c>=</c> ends one.</summary>
    private static bool MayNamePassWord(ReadOnlySpan<char> s)
    {
        var segment = 0;
        var named = false;
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c is '?' or '&' or ';' or '#' or '/')
            {
                segment = i + 1;
                named = false;
            }
            else if (c == '=' && !named)
            {
                named = true;
                var name = s[segment..i].Trim();
                if (name.Contains('%') || IsPassWord(name))
                    return true;
            }
        }
        return false;
    }

    private static bool IsPassWord(ReadOnlySpan<char> name)
    {
        foreach (var word in PassWordList)
        {
            if (name.Equals(word, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static bool HasWindowPath(string path)
    {
        var segments = path.Split(PathSeparators);
        for (var i = 0; i + 1 < segments.Length; i++)
        {
            if (segments[i].Equals("w", StringComparison.OrdinalIgnoreCase) && StartsWithWindowToken(segments[i + 1]))
                return true;
        }
        return false;
    }

    /// <summary>A segment that begins with a window token and goes no further in letters or digits (a log may glue punctuation on).</summary>
    private static bool StartsWithWindowToken(string segment)
    {
        if (segment.Length < WindowTokenLength)
            return false;
        for (var i = 0; i < WindowTokenLength; i++)
        {
            if (!WindowAlphabet.Contains(char.ToLowerInvariant(segment[i]), StringComparison.Ordinal))
                return false;
        }
        return segment.Length == WindowTokenLength || !char.IsLetterOrDigit(segment[WindowTokenLength]);
    }

    private static bool HasPassWord(string rest)
    {
        if (rest.Length == 0)
            return false;
        foreach (var part in rest.Split(QuerySeparators, StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0)
                continue;
            var name = Uri.UnescapeDataString(part[..eq]).Trim().ToLowerInvariant();
            if (PassWords.Contains(name))
                return true;
        }
        return false;
    }

    private static bool IsSchemeChar(char c) => char.IsAsciiLetterOrDigit(c) || c is '+' or '-' or '.';
}
