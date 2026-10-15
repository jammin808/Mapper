namespace PlayMap.Core.Control.Osc;

/// <summary>OSC 1.0 address pattern matching, part by part between '/': '?' one character, '*' any run, "[a-z]" and "[!a-z]" a
/// character class, "{foo,bar}" one of the words. No regular expressions; a pattern is matched as written.</summary>
public static class OscPattern
{
    public static bool IsPattern(string address)
    {
        ArgumentNullException.ThrowIfNull(address);
        return address.AsSpan().IndexOfAny("?*[{") >= 0;
    }

    public static bool Matches(string pattern, string address)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        ArgumentNullException.ThrowIfNull(address);
        var p = pattern.Split('/');
        var a = address.Split('/');
        if (p.Length != a.Length)
            return false;
        for (var i = 0; i < p.Length; i++)
            if (!Part(p[i].AsSpan(), a[i].AsSpan()))
                return false;
        return true;
    }

    private static bool Part(ReadOnlySpan<char> p, ReadOnlySpan<char> s)
    {
        while (p.Length > 0)
        {
            switch (p[0])
            {
                case '*':
                    for (var i = 0; i <= s.Length; i++)
                        if (Part(p[1..], s[i..]))
                            return true;
                    return false;
                case '?':
                    if (s.Length == 0)
                        return false;
                    p = p[1..];
                    s = s[1..];
                    continue;
                case '[':
                {
                    var close = p.IndexOf(']');
                    if (close < 0 || s.Length == 0 || !InClass(p[1..close], s[0]))
                        return false;
                    p = p[(close + 1)..];
                    s = s[1..];
                    continue;
                }
                case '{':
                {
                    var close = p.IndexOf('}');
                    if (close < 0)
                        return false;
                    var rest = p[(close + 1)..];
                    foreach (var word in p[1..close].ToString().Split(','))
                        if (s.StartsWith(word, StringComparison.Ordinal) && Part(rest, s[word.Length..]))
                            return true;
                    return false;
                }
                default:
                    if (s.Length == 0 || s[0] != p[0])
                        return false;
                    p = p[1..];
                    s = s[1..];
                    continue;
            }
        }
        return s.Length == 0;
    }

    private static bool InClass(ReadOnlySpan<char> set, char c)
    {
        var negate = set.Length > 0 && set[0] == '!';
        if (negate)
            set = set[1..];
        var hit = false;
        for (var i = 0; i < set.Length; i++)
        {
            if (i + 2 < set.Length && set[i + 1] == '-')
            {
                if (c >= set[i] && c <= set[i + 2])
                    hit = true;
                i += 2;
            }
            else if (set[i] == c)
            {
                hit = true;
            }
        }
        return hit != negate;
    }
}
