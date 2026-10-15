using System.Globalization;
using PlayMap.Core.Numerics;

namespace PlayMap.Core.Align;

/// <summary>One camera picture in colour: interleaved 8-bit RGB at the camera's raster.</summary>
public sealed record CameraShotRgb(byte[] Rgb, int Width, int Height);

/// <summary>A projector's colour as the camera measured it over the core of its footprint, in camera-linear RGB: its black and the
/// light of its full red, green, blue and white (black included).</summary>
public sealed record ProjectorColour(Vec3d Black, Vec3d Red, Vec3d Green, Vec3d Blue, Vec3d White)
{
    /// <summary>How far white is from the sum of its primaries (above 5 % the projector adds a white segment of its own).</summary>
    public double Additivity
    {
        get
        {
            var w = White - Black;
            var sum = Red + Green + Blue - Black * 3;
            return w.Length > 0 ? (w - sum).Length / w.Length : double.NaN;
        }
    }
}

/// <summary>Each projector's white match: diagonal gains (r, g, b) in linear light, at most one, that bring its white to the common
/// white at the common level; and what its white was before (u′v′ and level, the level relative to the brightest).</summary>
public sealed record WhiteGains(double R, double G, double B, double UBefore, double VBefore, double LevelBefore);

/// <summary>The white match of a group: each projector's gains, the common white's u′v′ and its level (relative to the brightest
/// projector's white), and words.</summary>
public sealed record WhiteMatch(IReadOnlyList<WhiteGains> Gains, double U, double V, double Level, string Words);

/// <summary>
/// Colour and level matching, the diagonal half (autoalign §3.5, WHITE): the camera is taken as linear sRGB (its 8-bit levels through
/// <see cref="Photometry.CameraGamma"/>), so a camera RGB gives CIE XYZ and u′v′. Each projector's primaries make a 3 × 3 matrix
/// M (columns red, green and blue less black); the common white is the mean of the projectors' white chromaticities, at the largest
/// level every projector reaches with gains no more than one: gains = M⁻¹ · white. Applied in linear light (the output stage's
/// measured gains), every projector's white lands on that chromaticity and level.
/// </summary>
public static class ColourMatch
{
    /// <summary>Linear sRGB (D65) → CIE XYZ.</summary>
    public static Vec3d ToXyz(Vec3d rgb) => new(
        0.4124 * rgb.X + 0.3576 * rgb.Y + 0.1805 * rgb.Z,
        0.2126 * rgb.X + 0.7152 * rgb.Y + 0.0722 * rgb.Z,
        0.0193 * rgb.X + 0.1192 * rgb.Y + 0.9505 * rgb.Z);

    /// <summary>CIE XYZ → linear sRGB (D65).</summary>
    public static Vec3d FromXyz(Vec3d xyz) => new(
        3.2406 * xyz.X - 1.5372 * xyz.Y - 0.4986 * xyz.Z,
        -0.9689 * xyz.X + 1.8758 * xyz.Y + 0.0415 * xyz.Z,
        0.0557 * xyz.X - 0.2040 * xyz.Y + 1.0570 * xyz.Z);

    /// <summary>A camera-linear RGB's u′v′ (CIE 1976 UCS).</summary>
    public static (double U, double V) Uv(Vec3d rgb)
    {
        var xyz = ToXyz(rgb);
        var d = xyz.X + 15 * xyz.Y + 3 * xyz.Z;
        return d <= 0 ? (double.NaN, double.NaN) : (4 * xyz.X / d, 9 * xyz.Y / d);
    }

    /// <summary>The camera-linear XYZ luminance of an RGB.</summary>
    public static double Luminance(Vec3d rgb) => ToXyz(rgb).Y;

    /// <summary>The trimmed mean camera-linear RGB of a picture over a mask (the brightest and darkest tenth of each channel left out).</summary>
    public static Vec3d Mean(byte[] rgb, bool[] mask)
    {
        ArgumentNullException.ThrowIfNull(rgb);
        ArgumentNullException.ThrowIfNull(mask);
        Span<double> m = stackalloc double[3];
        Span<int> histogram = stackalloc int[256];
        for (var c = 0; c < 3; c++)
        {
            histogram.Clear();
            var n = 0;
            for (var i = 0; i < mask.Length; i++)
            {
                if (!mask[i])
                    continue;
                histogram[rgb[i * 3 + c]]++;
                n++;
            }
            var skip = n / 10;
            var take = n - 2 * skip;
            double sum = 0;
            var seen = 0;
            var taken = 0;
            for (var v = 0; v < 256 && taken < take; v++)
            {
                var from = Math.Max(0, skip - seen);
                var use = Math.Min(Math.Max(0, histogram[v] - from), take - taken);
                sum += (double)use * Photometry.Linear((byte)v);
                taken += use;
                seen += histogram[v];
            }
            m[c] = taken > 0 ? sum / taken : 0;
        }
        return new Vec3d(m[0], m[1], m[2]);
    }

    /// <summary>The white match of the projectors measured; null with the words when one cannot be matched (a primary the camera did
    /// not see, a white too dim).</summary>
    public static (WhiteMatch? Match, string Words) White(IReadOnlyList<ProjectorColour> colours)
    {
        ArgumentNullException.ThrowIfNull(colours);
        if (colours.Count == 0)
            return (null, "no projector to match");
        var inverses = new Mat3d[colours.Count];
        double su = 0, sv = 0, brightest = 0;
        for (var j = 0; j < colours.Count; j++)
        {
            var c = colours[j];
            var m = Mat3d.FromColumns(c.Red - c.Black, c.Green - c.Black, c.Blue - c.Black);
            if (Math.Abs(m.Determinant) < 1e-12)
                return (null, $"output {j + 1}'s primaries were not told apart by the camera — is it a colour camera, and is the output lit?");
            inverses[j] = m.Inverse();
            var (u, v) = Uv(c.White - c.Black);
            if (!double.IsFinite(u))
                return (null, $"output {j + 1}'s white was too dim to measure");
            su += u;
            sv += v;
            brightest = Math.Max(brightest, Luminance(c.White - c.Black));
        }
        var tu = su / colours.Count;
        var tv = sv / colours.Count;
        // The target white's direction in camera RGB at luminance one (from u′v′ with Y = 1).
        var x = 9 * tu / (4 * tv);
        var z = (12 - 3 * tu - 20 * tv) / (4 * tv);
        var target = FromXyz(new Vec3d(x, 1, z));
        var level = double.PositiveInfinity;
        foreach (var inv in inverses)
        {
            var g = inv * target;
            foreach (var gi in new[] { g.X, g.Y, g.Z })
                if (gi > 0)
                    level = Math.Min(level, 1 / gi);
        }
        if (!double.IsFinite(level) || level <= 0)
            return (null, "no common white every projector reaches");
        var gains = new List<WhiteGains>(colours.Count);
        for (var j = 0; j < colours.Count; j++)
        {
            var g = inverses[j] * (target * level);
            var c = colours[j];
            var (u, v) = Uv(c.White - c.Black);
            gains.Add(new WhiteGains(Math.Clamp(g.X, 0, 1), Math.Clamp(g.Y, 0, 1), Math.Clamp(g.Z, 0, 1), u, v, Luminance(c.White - c.Black) / brightest));
        }
        var spread = 0.0;
        for (var a = 0; a < gains.Count; a++)
            for (var b = a + 1; b < gains.Count; b++)
                spread = Math.Max(spread, Math.Sqrt(Math.Pow(gains[a].UBefore - gains[b].UBefore, 2) + Math.Pow(gains[a].VBefore - gains[b].VBefore, 2)));
        var words = string.Create(CultureInfo.InvariantCulture,
            $"whites matched to u′v′ {tu:0.0000}, {tv:0.0000} at {level / brightest * 100:0} % of the brightest (they were up to Δu′v′ {spread:0.0000} apart)");
        return (new WhiteMatch(gains, tu, tv, level / brightest, words), words);
    }
}
