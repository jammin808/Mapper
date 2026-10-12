using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using PlayMap.Devices.Model;

namespace PlayMap.Devices.Drivers.PjLink;

public enum PjLinkGreetingKind
{
    /// <summary>"PJLINK 0": no authentication.</summary>
    Open,

    /// <summary>"PJLINK 1 &lt;seed&gt;": the first command must carry MD5(seed + password).</summary>
    Protected,

    /// <summary>"PJLINK ERRA": the digest was wrong.</summary>
    AuthFailed,

    /// <summary>Not a PJLink greeting.</summary>
    Invalid,
}

public sealed record PjLinkGreeting(PjLinkGreetingKind Kind, string Seed);

/// <summary>One reply line: "%1POWR=1", "%2IRES=1920x1080", "%1INPT=ERR2". <see cref="Error"/> is "" for a value.</summary>
public sealed record PjLinkReply(int Class, string Command, string Value, string Error)
{
    public bool IsError => Error.Length > 0;

    public bool IsOk => !IsError && Value.Equals("OK", StringComparison.OrdinalIgnoreCase);
}

public enum PjLinkNoticeKind
{
    /// <summary>"%2ACKN=mac": a projector answering a search.</summary>
    SearchAck,

    /// <summary>"%2LKUP=mac": a projector's network came up.</summary>
    LinkUp,

    /// <summary>"%2ERST=xxxxxx": its error status changed.</summary>
    Errors,

    /// <summary>"%2POWR=n": its power changed.</summary>
    Power,

    /// <summary>"%2INPT=xx": its input changed.</summary>
    Input,

    /// <summary>"%2SRCH": a search (ours, heard back on the broadcast) — not a projector's.</summary>
    Search,
}

/// <summary>A class 2 datagram on UDP 4352.</summary>
public sealed record PjLinkNotice(PjLinkNoticeKind Kind, string Value);

/// <summary>
/// PJLink (JBMIA), class 1 and class 2, as pure functions: framing, the authentication digest, every reply's
/// meaning in words and the class 2 datagrams. TCP 4352, one CR-terminated line per command; a projector drops a
/// connection idle for 30 s; when it greets "PJLINK 1 seed" the first command of the connection carries
/// MD5(seed + password) in lowercase hex in front of it.
/// </summary>
public static class PjLinkProtocol
{
    public const int Port = 4352;

    /// <summary>The search datagram (class 2), broadcast to UDP 4352.</summary>
    public const string SearchDatagram = "%2SRCH\r";

    public static PjLinkGreeting ParseGreeting(string line)
    {
        var t = (line ?? "").TrimEnd('\r', '\n');
        if (!t.StartsWith("PJLINK ", StringComparison.OrdinalIgnoreCase))
            return new PjLinkGreeting(PjLinkGreetingKind.Invalid, "");
        var rest = t[7..].Trim();
        if (rest.Equals("0", StringComparison.Ordinal))
            return new PjLinkGreeting(PjLinkGreetingKind.Open, "");
        if (rest.StartsWith("1 ", StringComparison.Ordinal) && rest.Length > 2)
            return new PjLinkGreeting(PjLinkGreetingKind.Protected, rest[2..].Trim());
        if (rest.Equals("ERRA", StringComparison.OrdinalIgnoreCase))
            return new PjLinkGreeting(PjLinkGreetingKind.AuthFailed, "");
        return new PjLinkGreeting(PjLinkGreetingKind.Invalid, "");
    }

    /// <summary>MD5 of the seed and the password, lowercase hex: what a protected projector expects in front of
    /// the first command. The spec's own example: seed "498e4a67", password "JBMIAProjectorLink" →
    /// "5d8409bc1c3fa39749434aa3a5c38682".</summary>
    public static string Digest(string seed, string password)
    {
#pragma warning disable CA5351, S4790 // PJLink (JBMIA) specifies MD5 for its authentication digest — the projector's protocol, not a secret store
        var hash = MD5.HashData(Encoding.ASCII.GetBytes(seed + password));
#pragma warning restore CA5351, S4790
        return Convert.ToHexStringLower(hash);
    }

    /// <summary>"%1POWR 1\r" from class 1 and "POWR 1".</summary>
    public static string Frame(int pjClass, string body) =>
        "%" + pjClass.ToString(CultureInfo.InvariantCulture) + body.Trim() + "\r";

    /// <summary>A reply line, or null when the line is not one ("%" + class + four letters + "=" + value).</summary>
    public static PjLinkReply? ParseReply(string line)
    {
        var t = (line ?? "").Trim('\r', '\n', ' ');
        if (t.Length < 7 || t[0] != '%' || !char.IsAsciiDigit(t[1]) || t[6] != '=')
            return null;
        var cls = t[1] - '0';
        var cmd = t[2..6].ToUpperInvariant();
        var value = t[7..].Trim();
        var upper = value.ToUpperInvariant();
        var error = upper is "ERR1" or "ERR2" or "ERR3" or "ERR4" or "ERRA" ? upper : "";
        return new PjLinkReply(cls, cmd, error.Length > 0 ? "" : value, error);
    }

    /// <summary>The spec's meaning of each error, in words for the desk.</summary>
    public static string ErrorWords(string code) => code.ToUpperInvariant() switch
    {
        "ERR1" => "undefined command — this projector does not have it (a class 1 projector answers class 2 commands so)",
        "ERR2" => "out of parameter — that input or setting is not on this projector",
        "ERR3" => "unavailable now — the projector is busy, warming up, cooling down or in standby",
        "ERR4" => "projector failure — the projector reports a fault",
        "ERRA" => "authentication failed — check the link's password",
        _ => code,
    };

    /// <summary>POWR's value: 0 standby, 1 on, 2 cooling down, 3 warming up.</summary>
    public static PowerState Power(string value) => value.Trim() switch
    {
        "0" => PowerState.Standby,
        "1" => PowerState.On,
        "2" => PowerState.CoolingDown,
        "3" => PowerState.WarmingUp,
        _ => PowerState.Unknown,
    };

    private static readonly string[] ErstNames = ["fan", "lamp", "temperature", "cover open", "filter", "other"];

    /// <summary>ERST's six digits — fan, lamp, temperature, cover open, filter, other — each 0 none, 1 warning,
    /// 2 error. Null when the value is not six such digits.</summary>
    public static ImmutableArray<DeviceFault>? Faults(string value)
    {
        var v = value.Trim();
        if (v.Length != 6)
            return null;
        var faults = ImmutableArray.CreateBuilder<DeviceFault>();
        for (var i = 0; i < 6; i++)
        {
            switch (v[i])
            {
                case '0':
                    break;
                case '1':
                    faults.Add(new DeviceFault(ErstNames[i] + ": warning", FaultSeverity.Warning));
                    break;
                case '2':
                    faults.Add(new DeviceFault(ErstNames[i] + ": error", FaultSeverity.Error));
                    break;
                default:
                    return null;
            }
        }
        return faults.ToImmutable();
    }

    /// <summary>LAMP's pairs "hours on/off" ("1234 1 567 0"): the hours per lamp and whether each is lit; null
    /// when the value is not pairs.</summary>
    public static (ImmutableArray<int> Hours, ImmutableArray<bool> Lit)? Lamps(string value)
    {
        var p = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (p.Length == 0 || p.Length % 2 != 0)
            return null;
        var hours = ImmutableArray.CreateBuilder<int>(p.Length / 2);
        var lit = ImmutableArray.CreateBuilder<bool>(p.Length / 2);
        for (var i = 0; i < p.Length; i += 2)
        {
            if (!int.TryParse(p[i], NumberStyles.None, CultureInfo.InvariantCulture, out var h) || p[i + 1] is not ("0" or "1"))
                return null;
            hours.Add(h);
            lit.Add(p[i + 1] == "1");
        }
        return (hours.ToImmutable(), lit.ToImmutable());
    }

    /// <summary>AVMT's value: 11 video muted, 21 audio muted, 31 both; 10/20/30 the same off. Returns whether the
    /// picture is muted (the shutter closed); null when the value is not one of the six.</summary>
    public static bool? PictureMuted(string value) => value.Trim() switch
    {
        "11" or "31" => true,
        "10" or "30" or "20" or "21" => false,
        _ => null,
    };

    /// <summary>"RGB 1", "digital 3" — INPT's two characters in words (class 2 allows 1–9 and A–Z for the number).</summary>
    public static string InputWords(string code)
    {
        var c = code.Trim();
        if (c.Length != 2)
            return c;
        var family = c[0] switch
        {
            '1' => "RGB",
            '2' => "video",
            '3' => "digital",
            '4' => "storage",
            '5' => "network",
            '6' => "internal",
            _ => "input",
        };
        return family + " " + c[1];
    }

    /// <summary>An input code is two characters: a family 1–6 and a number 1–9 or A–Z.</summary>
    public static bool IsInputCode(string code) =>
        code.Length == 2 && code[0] is >= '1' and <= '6' && (code[1] is >= '1' and <= '9' || code[1] is >= 'A' and <= 'Z');

    /// <summary>IRES/RRES "1920x1080"; "-" = no signal, "*" = unknown signal (class 2).</summary>
    public static SignalInfo Resolution(string value)
    {
        var v = value.Trim();
        if (v == "-")
            return new SignalInfo(0, 0, 0, "no signal");
        if (v == "*")
            return new SignalInfo(0, 0, 0, "unknown signal");
        var x = v.IndexOfAny(['x', 'X']);
        if (x > 0
            && int.TryParse(v.AsSpan(0, x), NumberStyles.None, CultureInfo.InvariantCulture, out var w)
            && int.TryParse(v.AsSpan(x + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var h))
            return new SignalInfo(w, h, 0, v);
        return new SignalInfo(0, 0, 0, v);
    }

    /// <summary>A class 2 datagram, or null when it is not one.</summary>
    public static PjLinkNotice? ParseNotice(string datagram)
    {
        var t = (datagram ?? "").Trim('\r', '\n', ' ', '\0');
        if (t.Equals("%2SRCH", StringComparison.OrdinalIgnoreCase))
            return new PjLinkNotice(PjLinkNoticeKind.Search, "");
        if (t.Length < 7 || !t.StartsWith("%2", StringComparison.Ordinal) || t[6] != '=')
            return null;
        var value = t[7..].Trim();
        return t[2..6].ToUpperInvariant() switch
        {
            "ACKN" => new PjLinkNotice(PjLinkNoticeKind.SearchAck, value),
            "LKUP" => new PjLinkNotice(PjLinkNoticeKind.LinkUp, value),
            "ERST" => new PjLinkNotice(PjLinkNoticeKind.Errors, value),
            "POWR" => new PjLinkNotice(PjLinkNoticeKind.Power, value),
            "INPT" => new PjLinkNotice(PjLinkNoticeKind.Input, value),
            _ => null,
        };
    }
}
