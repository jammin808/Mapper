using System.Text;

namespace PlayMap.Core.Media;

/// <summary>Who opens a capture device: FFmpeg's avdevice (DirectShow on Windows, Video4Linux2 on Linux), Media Foundation, or a
/// card maker's own runtime (Blackmagic DeckLink, AJA, Magewell), each loaded at run time from where its installer put it.</summary>
public enum CaptureProvider
{
    Dshow,
    V4l2,
    Mf,
    DeckLink,
    Aja,
    Magewell,
}

/// <summary>How a capture URI read on this node: taken (its provider's code is here), refused by the grammar (with words that repeat
/// nothing of it), or well formed for a provider whose code this node does not have.</summary>
public enum CaptureUriVerdict
{
    Taken,
    Refused,
    NotOnThisNode,
}

/// <summary>A capture URI as this node read it: the verdict, the URI when it is well formed, and the words.</summary>
public readonly record struct CaptureUriReading(CaptureUriVerdict Verdict, CaptureUri? Uri, string Words);

/// <summary>A capture device and input, in the one grammar every capture source parses — the observer's sources, the video I/O hub's
/// capture inputs — so a stored source means the same to every reader:
/// <code>capture:&lt;provider&gt;/&lt;device&gt;[/&lt;input&gt;]</code>
/// <list type="bullet">
/// <item>The scheme and the provider are read ignoring case and written in lower case; the providers are <c>dshow</c>, <c>v4l2</c>,
/// <c>mf</c>, <c>decklink</c>, <c>aja</c> and <c>magewell</c>.</item>
/// <item>The device and the input are names, kept as written (spaces included). A name that holds <c>%</c>, <c>/</c>, <c>?</c>,
/// <c>#</c> or <c>@</c> writes it as <c>%25</c>, <c>%2F</c>, <c>%3F</c>, <c>%23</c> or <c>%40</c>; no other escape is read.</item>
/// <item>It fails closed: an unknown provider, an authority (<c>capture://…</c>), user information (an <c>@</c>), a query (a
/// <c>?</c>), a fragment (a <c>#</c>), a malformed escape, an empty or blank name, a control character, a third segment or a URI
/// longer than <see cref="MaxLength"/> is refused with words. The words repeat nothing of the refused text, and a line that carries
/// a refused capture URI is written down with the URI replaced (<see cref="Shown"/>), so nothing of it is journalled.</item>
/// </list>
/// Pure; any thread.</summary>
public sealed record CaptureUri
{
    public const string Scheme = "capture:";

    /// <summary>The longest capture URI read, in characters.</summary>
    public const int MaxLength = 512;

    /// <summary>What a shown line carries in place of a capture URI the grammar refused.</summary>
    public const string RefusedMarker = "capture:‹refused›";

    /// <summary>The grammar's form, for words.</summary>
    public const string Form = "capture:<provider>/<device>[/<input>]";

    /// <summary>The providers' words, in the grammar's order.</summary>
    public static readonly IReadOnlyList<string> ProviderWords = ["dshow", "v4l2", "mf", "decklink", "aja", "magewell"];

    private CaptureUri(CaptureProvider provider, string device, string? input)
    {
        Provider = provider;
        Device = device;
        Input = input;
    }

    public CaptureProvider Provider { get; }

    /// <summary>The device's name as the provider lists it, unescaped.</summary>
    public string Device { get; }

    /// <summary>The input on the device (a card's connector or channel), unescaped; null for the device's default.</summary>
    public string? Input { get; }

    public string ProviderWord => Word(Provider);

    /// <summary>A capture URI from its parts; null with <paramref name="words"/> when a part is not a name the grammar takes.</summary>
    public static CaptureUri? Of(CaptureProvider provider, string device, string? input, out string words)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (!Enum.IsDefined(provider))
        {
            words = UnknownProviderWords;
            return null;
        }
        if (NameProblem(device, "device") is { } d)
        {
            words = d;
            return null;
        }
        if (input is not null && NameProblem(input, "input") is { } i)
        {
            words = i;
            return null;
        }
        words = "";
        return new CaptureUri(provider, device, input);
    }

    /// <summary>Whether a text claims to be a capture URI (starts with the scheme, ignoring case): one the grammar then takes or
    /// refuses, never read as anything else.</summary>
    public static bool Claims(string? text) => text is not null && text.TrimStart().StartsWith(Scheme, StringComparison.OrdinalIgnoreCase);

    /// <summary>Reads a capture URI by the grammar alone; false with the refusal's words.</summary>
    public static bool TryParse(string? text, out CaptureUri? uri, out string words)
    {
        uri = null;
        if (string.IsNullOrWhiteSpace(text) || !Claims(text))
        {
            words = "a capture source is " + Form;
            return false;
        }
        var t = text.Trim();
        if (t.Length > MaxLength)
        {
            words = $"a capture URI is at most {MaxLength.ToString(System.Globalization.CultureInfo.InvariantCulture)} characters";
            return false;
        }
        var rest = t[Scheme.Length..];
        if (rest.StartsWith("//", StringComparison.Ordinal))
        {
            words = rest.Contains('@', StringComparison.Ordinal)
                ? "a capture URI carries no user information: " + Form
                : "a capture URI has no host part (no //): " + Form;
            return false;
        }
        if (rest.Contains('@', StringComparison.Ordinal))
        {
            words = "a capture URI carries no user information (a name that holds @ writes it as %40)";
            return false;
        }
        if (rest.Contains('?', StringComparison.Ordinal))
        {
            words = "a capture URI carries no query (a name that holds ? writes it as %3F)";
            return false;
        }
        if (rest.Contains('#', StringComparison.Ordinal))
        {
            words = "a capture URI carries no fragment (a name that holds # writes it as %23)";
            return false;
        }
        var parts = rest.Split('/');
        if (parts.Length is < 2 or > 3)
        {
            words = "a capture URI is " + Form + " (a name that holds / writes it as %2F)";
            return false;
        }
        if (ProviderOf(parts[0]) is not { } provider)
        {
            words = UnknownProviderWords;
            return false;
        }
        if (Unescape(parts[1], "device", out var problem) is not { } device)
        {
            words = problem;
            return false;
        }
        string? input = null;
        if (parts.Length == 3 && (input = Unescape(parts[2], "input", out problem)) is null)
        {
            words = problem;
            return false;
        }
        uri = Of(provider, device, input, out words);
        return uri is not null;
    }

    /// <summary>Reads a capture URI on a node that has the code of <paramref name="present"/> providers: refused by the grammar, not on
    /// this node yet (well formed, its provider's code absent), or taken.</summary>
    public static CaptureUriReading Read(string? text, IReadOnlyCollection<CaptureProvider> present)
    {
        ArgumentNullException.ThrowIfNull(present);
        if (!TryParse(text, out var uri, out var words))
            return new CaptureUriReading(CaptureUriVerdict.Refused, null, words);
        return present.Contains(uri!.Provider)
            ? new CaptureUriReading(CaptureUriVerdict.Taken, uri, "")
            : new CaptureUriReading(CaptureUriVerdict.NotOnThisNode, uri, NotOnThisNodeWords(uri.Provider));
    }

    /// <summary>"DeckLink capture is not on this node yet".</summary>
    public static string NotOnThisNodeWords(CaptureProvider provider) => Name(provider) + " capture is not on this node yet";

    /// <summary>A text as a line that carries it may be written down: a capture URI the grammar refuses becomes
    /// <see cref="RefusedMarker"/>; anything else is as it came.</summary>
    public static string Shown(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return Claims(text) && !TryParse(text, out _, out _) ? RefusedMarker : text;
    }

    /// <summary>The URI in the grammar's canonical form: the scheme and provider in lower case, each name escaped.</summary>
    public override string ToString()
    {
        var b = new StringBuilder(Scheme.Length + 12 + Device.Length + (Input?.Length ?? 0));
        b.Append(Scheme).Append(ProviderWord).Append('/');
        Escape(b, Device);
        if (Input is not null)
            Escape(b.Append('/'), Input);
        return b.ToString();
    }

    /// <summary>A provider's word in the grammar.</summary>
    public static string Word(CaptureProvider provider) => provider switch
    {
        CaptureProvider.Dshow => "dshow",
        CaptureProvider.V4l2 => "v4l2",
        CaptureProvider.Mf => "mf",
        CaptureProvider.DeckLink => "decklink",
        CaptureProvider.Aja => "aja",
        CaptureProvider.Magewell => "magewell",
        _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, "not a capture provider"),
    };

    /// <summary>A provider's name in words: "DirectShow", "Video4Linux2", "Media Foundation", "DeckLink", "AJA", "Magewell".</summary>
    public static string Name(CaptureProvider provider) => provider switch
    {
        CaptureProvider.Dshow => "DirectShow",
        CaptureProvider.V4l2 => "Video4Linux2",
        CaptureProvider.Mf => "Media Foundation",
        CaptureProvider.DeckLink => "DeckLink",
        CaptureProvider.Aja => "AJA",
        CaptureProvider.Magewell => "Magewell",
        _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, "not a capture provider"),
    };

    /// <summary>A provider from its word, ignoring case; null for any other word.</summary>
    public static CaptureProvider? ProviderOf(string? word) => word?.ToLowerInvariant() switch
    {
        "dshow" => CaptureProvider.Dshow,
        "v4l2" => CaptureProvider.V4l2,
        "mf" => CaptureProvider.Mf,
        "decklink" => CaptureProvider.DeckLink,
        "aja" => CaptureProvider.Aja,
        "magewell" => CaptureProvider.Magewell,
        _ => null,
    };

    private static readonly string UnknownProviderWords = "a capture URI names one of the providers " + string.Join(", ", ProviderWords);

    /// <summary>Why a name is not one the grammar takes (empty or blank, or holding a control character), or null.</summary>
    private static string? NameProblem(string name, string what)
    {
        if (string.IsNullOrWhiteSpace(name))
            return $"a capture URI names its {what}: " + Form;
        foreach (var c in name)
        {
            if (char.IsControl(c))
                return $"a capture URI's {what} holds no control character";
        }
        return null;
    }

    /// <summary>A name with its escapes read (%25 %2F %3F %23 %40, either case of hex); null with the words for any other escape or a
    /// name the grammar does not take.</summary>
    private static string? Unescape(string escaped, string what, out string problem)
    {
        if (!escaped.Contains('%', StringComparison.Ordinal))
        {
            problem = NameProblem(escaped, what) ?? "";
            return problem.Length == 0 ? escaped : null;
        }
        var b = new StringBuilder(escaped.Length);
        for (var i = 0; i < escaped.Length; i++)
        {
            var c = escaped[i];
            if (c != '%')
            {
                b.Append(c);
                continue;
            }
            var e = i + 2 < escaped.Length ? Escaped(escaped[i + 1], escaped[i + 2]) : null;
            if (e is null)
            {
                problem = $"a capture URI's {what} escapes only %, /, ?, # and @ (as %25, %2F, %3F, %23 and %40)";
                return null;
            }
            b.Append(e.Value);
            i += 2;
        }
        var name = b.ToString();
        problem = NameProblem(name, what) ?? "";
        return problem.Length == 0 ? name : null;
    }

    /// <summary>The character an escape's two hex digits name, of the five the grammar escapes; null for any other.</summary>
    private static char? Escaped(char high, char low) => (high, char.ToUpperInvariant(low)) switch
    {
        ('2', '5') => '%',
        ('2', 'F') => '/',
        ('3', 'F') => '?',
        ('2', '3') => '#',
        ('4', '0') => '@',
        _ => null,
    };

    private static void Escape(StringBuilder b, string name)
    {
        foreach (var c in name)
        {
            switch (c)
            {
                case '%':
                    b.Append("%25");
                    break;
                case '/':
                    b.Append("%2F");
                    break;
                case '?':
                    b.Append("%3F");
                    break;
                case '#':
                    b.Append("%23");
                    break;
                case '@':
                    b.Append("%40");
                    break;
                default:
                    b.Append(c);
                    break;
            }
        }
    }
}
