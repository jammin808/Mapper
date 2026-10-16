using PlayMap.Core.Wire;

namespace PlayMap.Net.Companion;

/// <summary>The controllers' line protocol: WHOAMI's and the announcement's <c>proto</c>, and the number the Companion module knows
/// (its <c>KNOWN_LINE_PROTOCOL</c>; a node speaking a higher one tells the module to update).</summary>
public static class Controllers
{
    public const int LineProtocol = 1;
}

/// <summary>The controllers on the line wire as the node keeps them (STATE.clients, the context's client items, WHOAMI): the wire
/// and WebSocket sessions say who said HELLO, over which channel, and when they leave. The engine implements it beside its
/// endpoint; an endpoint that does not keeps no table.</summary>
public interface IClientRegistry
{
    /// <summary>A session said HELLO, or its pairing or name changed since; <paramref name="channel"/> is "tcp" or "ws".</summary>
    void Hello(SessionInfo session, string channel);

    /// <summary>The session ended.</summary>
    void Gone(string sessionId);
}

/// <summary>What a picture is asked as.</summary>
public enum ThumbFormat
{
    Jpeg,
    Png,
}

/// <summary>One small picture asked for: a source or view reference, the box it fits in, the format, and the ETag the asker holds.</summary>
public sealed record ThumbRequest(string Ref, int Width, int Height, ThumbFormat Format, string? IfNoneMatch);

/// <summary>A picture's answer: its ETag and bytes, "same" (the asker's ETag is current), or why there is none.</summary>
public sealed record ThumbResult(bool Ok, string? Etag, string? Mime, int Width, int Height, ReadOnlyMemory<byte> Bytes, bool NotModified, string? Why)
{
    public static ThumbResult Refused(string why) => new(false, null, null, 0, 0, ReadOnlyMemory<byte>.Empty, false, why);
}

/// <summary>Small pictures for keys and lists (THUMB on the wire, <c>GET /api/thumb</c> on the web port). The engine implements it
/// beside its endpoint.</summary>
public interface IThumbSource
{
    ValueTask<ThumbResult> ThumbAsync(ThumbRequest request, SessionInfo session, CancellationToken ct);
}

/// <summary>THUMB's words after the verb: <c>&lt;ref&gt; [&lt;w&gt;x&lt;h&gt;] [JPEG|PNG] [IF &lt;etag&gt;]</c> (96×96 JPEG when left out).</summary>
public static class ThumbArgs
{
    public const int DefaultSize = 96;
    public const int MaxSize = 512;

    public static bool TryParse(IReadOnlyList<string> words, out ThumbRequest? request, out string? why)
    {
        ArgumentNullException.ThrowIfNull(words);
        request = null;
        why = null;
        if (words.Count == 0)
        {
            why = "THUMB <ref> [<w>x<h>] [JPEG|PNG] [IF <etag>] — which picture?";
            return false;
        }
        int w = DefaultSize, h = DefaultSize;
        var format = ThumbFormat.Jpeg;
        string? etag = null;
        for (var i = 1; i < words.Count; i++)
        {
            var word = words[i];
            if (word.Equals("JPEG", StringComparison.OrdinalIgnoreCase) || word.Equals("JPG", StringComparison.OrdinalIgnoreCase))
                format = ThumbFormat.Jpeg;
            else if (word.Equals("PNG", StringComparison.OrdinalIgnoreCase))
                format = ThumbFormat.Png;
            else if (word.Equals("IF", StringComparison.OrdinalIgnoreCase) && i + 1 < words.Count)
                etag = words[++i].Trim('"');
            else if (!TrySize(word, out w, out h))
            {
                why = $"THUMB does not take '{word}' — <w>x<h> (8–{MaxSize}), JPEG or PNG, IF <etag>";
                return false;
            }
        }
        request = new ThumbRequest(words[0], w, h, format, etag);
        return true;
    }

    /// <summary>"96x54": both sides 8–512.</summary>
    public static bool TrySize(string word, out int w, out int h)
    {
        w = h = DefaultSize;
        var x = (word ?? "").IndexOf('x', StringComparison.OrdinalIgnoreCase);
        return x > 0
            && int.TryParse(word.AsSpan(0, x), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out w)
            && int.TryParse(word.AsSpan(x + 1), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out h)
            && w is >= 8 and <= MaxSize && h is >= 8 and <= MaxSize;
    }
}
