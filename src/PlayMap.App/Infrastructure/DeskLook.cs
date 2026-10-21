using System.Collections.Frozen;
using System.Globalization;

namespace PlayMap.App.Infrastructure;

/// <summary>The desk's own themes (a desk preference, never the show's): dark, the dark room (unselected chrome dimmed, the selected
/// and the critical at full contrast) and daylight.</summary>
public enum DeskTheme
{
    Dark,
    Dim,
    Daylight,
}

/// <summary>One theme's colour tokens, by the key Theme/Tokens.axaml names them (the brush of each is its key + "Brush"), as 0xRRGGBB.
/// A theme recolours neutrals, never a meaning: the status lights (LightGreen, LightAmber, LightRed, LightGrey) keep their job in every
/// theme, and the blind grey (<see cref="Grey"/>) leaves them and the air's red alone.</summary>
public sealed record DeskPalette(string Name, FrozenDictionary<string, uint> Tokens)
{
    /// <summary>The status tokens: reserved for the air and the node's condition, in every theme.</summary>
    public static readonly IReadOnlyList<string> StatusTokens = ["LightGreen", "LightAmber", "LightRed", "LightGrey"];

    /// <summary>The tokens that mark what touches the air (blackout, PANIC, armed, a high-risk verb).</summary>
    public static readonly IReadOnlyList<string> AirTokens = ["DeskDanger", "DeskDangerSoft", "LightRed"];

    /// <summary>Today's tokens (Theme/Tokens.axaml), the dark theme.</summary>
    public static readonly DeskPalette Dark = Make("dark", new()
    {
        ["DeskBg"] = 0x121417, ["DeskPanel"] = 0x191C20, ["DeskRaised"] = 0x22262B, ["DeskRaised2"] = 0x2B3036,
        ["DeskBorder"] = 0x2D3339, ["DeskBorderStrong"] = 0x3C434B, ["DeskText"] = 0xE7E9EC, ["DeskTextDim"] = 0xA2A9B2,
        ["DeskTextFaint"] = 0x6F7882, ["DeskAccent"] = 0x3D9BFF, ["DeskAccentSoft"] = 0x1B3555, ["DeskDanger"] = 0xE5484D,
        ["DeskDangerSoft"] = 0x3D1618, ["LightGreen"] = 0x3FB950, ["LightAmber"] = 0xD29922, ["LightRed"] = 0xE5484D,
        ["LightGrey"] = 0x6F7882,
    });

    /// <summary>The dark room: the chrome a person is not looking at (dim and faint words, borders, raised panels) sinks toward the
    /// background; the text of the selected, the accent and every status light stay at full strength.</summary>
    public static readonly DeskPalette Dim = Make("dim", new()
    {
        ["DeskBg"] = 0x0A0B0D, ["DeskPanel"] = 0x0F1114, ["DeskRaised"] = 0x15181B, ["DeskRaised2"] = 0x1B1F23,
        ["DeskBorder"] = 0x1A1E22, ["DeskBorderStrong"] = 0x252A30, ["DeskText"] = 0xE7E9EC, ["DeskTextDim"] = 0x6E757D,
        ["DeskTextFaint"] = 0x4A5057, ["DeskAccent"] = 0x3D9BFF, ["DeskAccentSoft"] = 0x15293F, ["DeskDanger"] = 0xE5484D,
        ["DeskDangerSoft"] = 0x2A0F11, ["LightGreen"] = 0x3FB950, ["LightAmber"] = 0xD29922, ["LightRed"] = 0xE5484D,
        ["LightGrey"] = 0x6F7882,
    });

    /// <summary>Daylight: light panels and dark words; the status lights darkened to keep their contrast on a light panel.</summary>
    public static readonly DeskPalette Daylight = Make("daylight", new()
    {
        ["DeskBg"] = 0xE9EBEE, ["DeskPanel"] = 0xF3F4F6, ["DeskRaised"] = 0xFFFFFF, ["DeskRaised2"] = 0xE3E6EA,
        ["DeskBorder"] = 0xD0D5DB, ["DeskBorderStrong"] = 0xB4BBC3, ["DeskText"] = 0x15181C, ["DeskTextDim"] = 0x4A525B,
        ["DeskTextFaint"] = 0x5F6873, ["DeskAccent"] = 0x1F6FD1, ["DeskAccentSoft"] = 0xD6E6FA, ["DeskDanger"] = 0xCF222E,
        ["DeskDangerSoft"] = 0xFBE3E5, ["LightGreen"] = 0x1A7F37, ["LightAmber"] = 0x9A6700, ["LightRed"] = 0xCF222E,
        ["LightGrey"] = 0x5F6873,
    });

    public static DeskPalette Of(DeskTheme theme) => theme switch
    {
        DeskTheme.Dim => Dim,
        DeskTheme.Daylight => Daylight,
        _ => Dark,
    };

    /// <summary>Whether the palette's panels are light (Fluent's light variant draws its own controls).</summary>
    public bool IsLight => Luminance(Tokens["DeskPanel"]) > 0.5;

    /// <summary>The blind variant of a theme: every neutral and the accent drawn in grey (the whole desk turns grey while this desk
    /// edits a draft), the status lights and the air's red kept.</summary>
    public DeskPalette Grey()
    {
        var keep = new HashSet<string>(StatusTokens.Concat(AirTokens), StringComparer.Ordinal);
        var tokens = new Dictionary<string, uint>(StringComparer.Ordinal);
        foreach (var (key, rgb) in Tokens)
            tokens[key] = keep.Contains(key) ? rgb : Desaturate(rgb);
        return new DeskPalette(Name + "+blind", tokens.ToFrozenDictionary(StringComparer.Ordinal));
    }

    /// <summary>"#RRGGBB" of a token.</summary>
    public string Hex(string token) => "#" + Tokens[token].ToString("X6", CultureInfo.InvariantCulture);

    /// <summary>WCAG relative luminance of 0xRRGGBB.</summary>
    public static double Luminance(uint rgb)
    {
        static double Channel(uint c)
        {
            var s = c / 255.0;
            return s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return (0.2126 * Channel((rgb >> 16) & 0xFF)) + (0.7152 * Channel((rgb >> 8) & 0xFF)) + (0.0722 * Channel(rgb & 0xFF));
    }

    /// <summary>WCAG contrast ratio of two colours (1 to 21).</summary>
    public static double Contrast(uint a, uint b)
    {
        var la = Luminance(a);
        var lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    /// <summary>The grey of the same luminance (sRGB), so a desaturated token keeps its contrast with its neighbours.</summary>
    private static uint Desaturate(uint rgb)
    {
        var l = Luminance(rgb);
        var s = l <= 0.0031308 ? l * 12.92 : (1.055 * Math.Pow(l, 1 / 2.4)) - 0.055;
        var g = (uint)Math.Clamp(Math.Round(s * 255), 0, 255);
        return (g << 16) | (g << 8) | g;
    }

    private static DeskPalette Make(string name, Dictionary<string, uint> tokens) => new(name, tokens.ToFrozenDictionary(StringComparer.Ordinal));
}

/// <summary>A light's second and third cue: the shape and the word drawn with every status colour, so no light is colour alone (✓ ok,
/// △ warn, ✕ fault, ■ armed, ○ off, i information).</summary>
public static class StatusShapes
{
    /// <summary>The light words STATE and the desk use, folded to the status they mean: "ok", "warn", "fault", "armed", "info", "off".</summary>
    public static string StatusOf(string? light) => (light ?? "").ToLowerInvariant() switch
    {
        "green" or "live" or "ready" or "good" or "ok" => "ok",
        "amber" or "stale" or "connecting" or "fetching" or "fair" or "warn" => "warn",
        "red" or "down" or "missing" or "poor" or "unplayable" or "fault" => "fault",
        "armed" => "armed",
        "info" or "i" => "info",
        _ => "off",
    };

    public static string Shape(string? light) => StatusOf(light) switch
    {
        "ok" => "✓",
        "warn" => "△",
        "fault" => "✕",
        "armed" => "■",
        "info" => "i",
        _ => "○",
    };

    public static string Word(string? light) => StatusOf(light);

    /// <summary>The status token a light is drawn in.</summary>
    public static string Token(string? light) => StatusOf(light) switch
    {
        "ok" => "LightGreen",
        "warn" => "LightAmber",
        "fault" or "armed" => "LightRed",
        _ => "LightGrey",
    };

    /// <summary>The fastest a light or alarm on the desk may repeat: no alarm faster than 2 Hz.</summary>
    public const int MinAlarmPeriodMs = 500;
}
