using System.Globalization;
using PlayMap.Core.Numerics;

namespace PlayMap.Core.Site;

/// <summary>The column order of a control-point file: point name, then easting and northing in either order, height, and the
/// surveyor's code (PENZD is name, E, N, Z, description; PNEZD the common US export; ENH and NEH carry no name).</summary>
public enum ControlOrder { Penzd, Pnezd, Enh, Neh }

/// <summary>A point read from a control file: its name, its grid coordinates in metres, its code and the line it came from.</summary>
public sealed record ControlRow(string Name, Vec3d Grid, string Code, int Line);

/// <summary>A line of a control file that was not a point, and why.</summary>
public sealed record ControlSkip(int Line, string Why);

/// <summary>What a control file gave: the points, the lines it skipped, the order it read them in and how it knew.</summary>
public sealed record ControlRead(IReadOnlyList<ControlRow> Points, IReadOnlyList<ControlSkip> Skipped, ControlOrder Order, string OrderFrom);

/// <summary>Total-station control-point files: comma, semicolon, tab or whitespace separated; numbers culture-invariant; the order
/// from the argument, else from a header naming the columns ("Point", "Easting", "Northing", "Elev", "Code"), else PENZD. Units
/// scale the coordinates to metres (m, cm, mm, ft, usft). A name used twice is refused with both line numbers.</summary>
public static class ControlPointFile
{
    /// <summary>Metres per unit: m, cm, mm, ft (international), usft (US survey foot, 1200/3937 m).</summary>
    public static double? UnitScale(string unit) => unit.ToUpperInvariant() switch
    {
        "M" => 1,
        "CM" => 0.01,
        "MM" => 0.001,
        "FT" => 0.3048,
        "USFT" => 1200.0 / 3937.0,
        _ => null,
    };

    public static ControlOrder? OrderOf(string word) => word.ToUpperInvariant() switch
    {
        "PENZD" => ControlOrder.Penzd,
        "PNEZD" => ControlOrder.Pnezd,
        "ENH" => ControlOrder.Enh,
        "NEH" => ControlOrder.Neh,
        _ => null,
    };

    private static readonly char[] Separators = [',', ';', '\t'];

    private static string[] Fields(string line)
    {
        var parts = line.IndexOfAny(Separators) >= 0 ? line.Split(Separators) : line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return [.. parts.Select(p => p.Trim().Trim('"'))];
    }

    private static bool Num(string s, out double v) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v) && double.IsFinite(v);

    private static readonly string[] EastWords = ["e", "x", "east", "easting", "eastings", "e(m)"];
    private static readonly string[] NorthWords = ["n", "y", "north", "northing", "northings", "n(m)"];

    /// <summary>A column names easting or northing when it is one of the words, or a longer word starting "east" or "north".</summary>
    private static bool Names(string field, string[] words, string stem)
    {
        var f = field.Trim().ToLowerInvariant();
        return words.Contains(f) || (f.Length > stem.Length && f.StartsWith(stem, StringComparison.Ordinal));
    }

    /// <summary>The order a header line names, or null when the line is not a header: the easting and northing columns found by
    /// name, and a name column whenever anything comes before them.</summary>
    private static ControlOrder? FromHeader(string[] f)
    {
        if (f.Any(x => Num(x, out _)))
            return null;
        var e = Array.FindIndex(f, x => Names(x, EastWords, "east"));
        var n = Array.FindIndex(f, x => Names(x, NorthWords, "north"));
        if (e < 0 || n < 0 || e == n)
            return null;
        var named = Math.Min(e, n) > 0;
        return (named, e < n) switch
        {
            (true, true) => ControlOrder.Penzd,
            (true, false) => ControlOrder.Pnezd,
            (false, true) => ControlOrder.Enh,
            _ => ControlOrder.Neh,
        };
    }

    public static ControlRead Parse(TextReader reader, ControlOrder? order = null, double unitScale = 1)
    {
        ArgumentNullException.ThrowIfNull(reader);
        var points = new List<ControlRow>();
        var skipped = new List<ControlSkip>();
        var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var from = order is null ? "default" : "argument";
        var use = order ?? ControlOrder.Penzd;
        var lineNo = 0;
        var unnamed = 0;
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            lineNo++;
            var text = line.Trim();
            if (text.Length == 0 || text[0] == '#' || text.StartsWith("//", StringComparison.Ordinal))
                continue;
            var f = Fields(text);
            if (order is null && points.Count == 0 && FromHeader(f) is { } header)
            {
                use = header;
                from = "header";
                continue;
            }
            var named = use is ControlOrder.Penzd or ControlOrder.Pnezd;
            var need = named ? 4 : 3;
            if (f.Length < need)
            {
                skipped.Add(new ControlSkip(lineNo, $"{f.Length} fields — a point needs {need}"));
                continue;
            }
            var at = named ? 1 : 0;
            if (!Num(f[at], out var a) || !Num(f[at + 1], out var b) || !Num(f[at + 2], out var h))
            {
                skipped.Add(new ControlSkip(lineNo, "its coordinates are not numbers"));
                continue;
            }
            var eastFirst = use is ControlOrder.Penzd or ControlOrder.Enh;
            var (e, n) = eastFirst ? (a, b) : (b, a);
            var name = named ? f[0] : "P" + (++unnamed).ToString(CultureInfo.InvariantCulture);
            if (name.Length == 0)
            {
                skipped.Add(new ControlSkip(lineNo, "it has no point name"));
                continue;
            }
            if (seen.TryGetValue(name, out var first))
                throw new FormatException(string.Create(CultureInfo.InvariantCulture, $"point {name} is on line {first} and again on line {lineNo} — names must be unique"));
            seen[name] = lineNo;
            var code = string.Join(' ', f.Skip(at + 3).Where(x => x.Length > 0));
            points.Add(new ControlRow(name, new Vec3d(e * unitScale, n * unitScale, h * unitScale), code, lineNo));
        }
        return new ControlRead(points, skipped, use, from);
    }
}
