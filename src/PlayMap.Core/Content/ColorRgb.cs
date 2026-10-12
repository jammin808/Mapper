using System.Globalization;

namespace PlayMap.Core.Content;

/// <summary>A colour as three floats in 0..1, display-referred (what the bytes say, divided by 255). The
/// generators' CPU twins, the particle tints and the palettes all speak it, so a CPU raster and a shader
/// read the same numbers.</summary>
public readonly record struct ColorRgb(float R, float G, float B)
{
    public static readonly ColorRgb Black = new(0, 0, 0);
    public static readonly ColorRgb White = new(1, 1, 1);

    /// <summary>From a packed 0xRRGGBB.</summary>
    public static ColorRgb FromPacked(int rgb) =>
        new(((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f);

    public static ColorRgb FromBytes(byte r, byte g, byte b) => new(r / 255f, g / 255f, b / 255f);

    /// <summary>"#RRGGBB" (or "#RGB", with or without the hash); the fallback when the text is not a colour.
    /// Reads the characters in place, so a frame that parses a colour allocates nothing.</summary>
    public static ColorRgb Parse(string? hex, ColorRgb fallback) =>
        TryParsePacked(hex, out var rgb) ? FromPacked(rgb) : fallback;

    /// <summary>Packed 0xRRGGBB from "#RRGGBB" or "#RGB" text, without allocating.</summary>
    public static bool TryParsePacked(ReadOnlySpan<char> text, out int rgb)
    {
        rgb = 0;
        var t = text.Trim();
        if (t.Length > 0 && t[0] == '#')
            t = t[1..];
        if (t.Length == 3)
        {
            for (var i = 0; i < 3; i++)
            {
                var v = Hex(t[i]);
                if (v < 0)
                    return false;
                rgb = (rgb << 8) | (v << 4) | v;
            }
            return true;
        }
        if (t.Length != 6)
            return false;
        for (var i = 0; i < 6; i++)
        {
            var v = Hex(t[i]);
            if (v < 0)
                return false;
            rgb = (rgb << 4) | v;
        }
        return true;
    }

    private static int Hex(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'f' => c - 'a' + 10,
        >= 'A' and <= 'F' => c - 'A' + 10,
        _ => -1,
    };

    /// <summary>A comma-separated list of colours; the entries that are not colours are skipped, and an
    /// empty result is the fallback alone (a palette is never empty).</summary>
    public static ColorRgb[] ParseList(string? csv, ColorRgb fallback)
    {
        if (string.IsNullOrWhiteSpace(csv))
            return [fallback];
        var parts = csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var list = new List<ColorRgb>(parts.Length);
        foreach (var p in parts)
            if (TryParsePacked(p, out var rgb))
                list.Add(FromPacked(rgb));
        return list.Count == 0 ? [fallback] : [.. list];
    }

    public byte R8 => ToByte(R);
    public byte G8 => ToByte(G);
    public byte B8 => ToByte(B);

    /// <summary>Packed 0xRRGGBB, each channel rounded.</summary>
    public int Packed => (R8 << 16) | (G8 << 8) | B8;

    /// <summary>BT.709 luma of the display-referred values, 0..1.</summary>
    public float Luma => 0.2126f * R + 0.7152f * G + 0.0722f * B;

    public ColorRgb Scaled(float k) => new(R * k, G * k, B * k);

    public ColorRgb Clamped() => new(Math.Clamp(R, 0, 1), Math.Clamp(G, 0, 1), Math.Clamp(B, 0, 1));

    public static ColorRgb Lerp(ColorRgb a, ColorRgb b, float t) =>
        new(a.R + (b.R - a.R) * t, a.G + (b.G - a.G) * t, a.B + (b.B - a.B) * t);

    /// <summary>Hue in degrees [0, 360), saturation and value in 0..1.</summary>
    public (float H, float S, float V) ToHsv()
    {
        var max = Math.Max(R, Math.Max(G, B));
        var min = Math.Min(R, Math.Min(G, B));
        var d = max - min;
        float h;
        if (d <= 0)
            h = 0;
        else if (max == R)
            h = 60 * (((G - B) / d) % 6);
        else if (max == G)
            h = 60 * ((B - R) / d + 2);
        else
            h = 60 * ((R - G) / d + 4);
        if (h < 0)
            h += 360;
        return (h, max <= 0 ? 0 : d / max, max);
    }

    public static ColorRgb FromHsv(float h, float s, float v)
    {
        h = ((h % 360) + 360) % 360;
        var c = v * s;
        var x = c * (1 - Math.Abs((h / 60) % 2 - 1));
        var m = v - c;
        var (r, g, b) = (int)(h / 60) switch
        {
            0 => (c, x, 0f),
            1 => (x, c, 0f),
            2 => (0f, c, x),
            3 => (0f, x, c),
            4 => (x, 0f, c),
            _ => (c, 0f, x),
        };
        return new ColorRgb(r + m, g + m, b + m);
    }

    /// <summary>Turned round the hue wheel; a whole turn is the same colour again.</summary>
    public ColorRgb TurnHue(float turns)
    {
        if (HueStrength(turns) < 0.001f)
            return this;
        var (h, s, v) = ToHsv();
        return FromHsv(h + turns * 360f, s, v);
    }

    /// <summary>How far a turn is from "no change": 0 at every whole turn, 0.5 at a half turn.</summary>
    public static float HueStrength(float turns) => Math.Abs(turns - MathF.Round(turns));

    public override string ToString() => "#" + Packed.ToString("X6", CultureInfo.InvariantCulture);

    private static byte ToByte(float v) => (byte)Math.Clamp((int)MathF.Round(v * 255f), 0, 255);
}
