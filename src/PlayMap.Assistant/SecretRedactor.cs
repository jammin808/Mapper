using System.Text.RegularExpressions;

namespace PlayMap.Assistant;

/// <summary>Masks secrets in words before they leave the node or reach a journal: the node's own known
/// secrets (its pairing token, link keys, the assistant's key) wherever they appear, and anything shaped like
/// an API key, a Stagewatch key, a bearer token or a token after AUTH / LINK. A secret rides in no line, no
/// journal row, no transcript.</summary>
public sealed class SecretRedactor
{
    public const string Mask = "[secret]";

    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(250);

    private static readonly Regex[] KeyShapes =
    [
        new(@"\bsk-[A-Za-z0-9_\-]{16,}", RegexOptions.CultureInvariant, Timeout),
        new(@"\bswk_[A-Za-z0-9_\-]{8,}", RegexOptions.CultureInvariant, Timeout),
        new(@"(?<=\bBearer\s+)[A-Za-z0-9._~+/=\-]{8,}", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, Timeout),
        new(@"(?<=\bAUTH(?:-PROOF)?\s+)[A-Za-z0-9_\-]{8,}", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, Timeout),
        new(@"(?<=\bLINK\s+(?:PATTERNS|STAGEWATCH)\s+\S+\s+)[A-Za-z0-9_\-]{8,}", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, Timeout),
    ];

    private readonly string[] _known;

    /// <param name="knownSecrets">Literal secrets of this node; shorter than six characters are ignored (a
    /// mask that hides every "on" hides nothing worth hiding).</param>
    public SecretRedactor(IEnumerable<string>? knownSecrets = null) =>
        _known = (knownSecrets ?? []).Where(s => !string.IsNullOrWhiteSpace(s) && s.Trim().Length >= 6).Select(s => s.Trim())
            .Distinct(StringComparer.Ordinal).OrderByDescending(s => s.Length).ToArray();

    /// <summary>A redactor that knows no literal secrets, only their shapes.</summary>
    public static SecretRedactor ShapesOnly { get; } = new();

    public string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return text ?? "";
        var s = text;
        foreach (var k in _known)
            s = s.Replace(k, Mask, StringComparison.Ordinal);
        foreach (var r in KeyShapes)
        {
            try
            {
                s = r.Replace(s, Mask);
            }
            catch (RegexMatchTimeoutException)
            {
                // A pathological input is masked whole rather than passed on unread.
                return Mask;
            }
        }
        return s;
    }
}
