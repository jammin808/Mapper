using System.Collections.Immutable;
using System.Globalization;
using PlayMap.Core.Numerics;

namespace PlayMap.Core.Led;

/// <summary>One fixture read from a pixel list: its name, its pixels in row order (positions as written, times the scale), its type
/// word ("" = none given) and the first pixel's universe and address (0 = none given).</summary>
public sealed record LedCsvFixture(string Name, ImmutableArray<Vec3d> Points, string Type, int Universe, int Address);

/// <summary>A pixel list in the columns MADRIX and Capture export (case-insensitive): Fixture or Display Name, Pixel or Index, X or
/// Position X, Y or Position Y, Z or Position Z, Universe, Channel or Address, DMX Address ("U.A", or absolute from 1 in 512-slot
/// universes), Type or Mode. A header row, then one pixel a row; comma, semicolon or tab; invariant culture. Rows of the same fixture
/// make one points fixture in the order its rows come (sorted by Pixel when that column is given). Every error names its line and
/// column.</summary>
public static class LedCsvReader
{
    public const int MaxRows = 200_000;

    public static bool TryRead(string text, double scale, out ImmutableArray<LedCsvFixture> fixtures, out string why)
    {
        ArgumentNullException.ThrowIfNull(text);
        fixtures = [];
        why = "";
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var first = Array.FindIndex(lines, l => l.Trim().Length > 0);
        if (first < 0)
        {
            why = "the file is empty";
            return false;
        }
        var sep = lines[first].Contains('\t', StringComparison.Ordinal) ? '\t' : lines[first].Contains(';', StringComparison.Ordinal) ? ';' : ',';
        var header = Split(lines[first], sep).Select(h => h.Trim().Trim('"').ToLowerInvariant()).ToList();
        int Col(params string[] names) => header.FindIndex(h => names.Contains(h));
        var cFixture = Col("fixture", "display name", "name");
        var cPixel = Col("pixel", "index");
        var cX = Col("x", "position x");
        var cY = Col("y", "position y");
        var cZ = Col("z", "position z");
        var cUniverse = Col("universe");
        var cChannel = Col("channel", "address");
        var cDmx = Col("dmx address");
        var cType = Col("type", "mode");
        if (cX < 0 || cY < 0)
        {
            why = $"line {first + 1}: the header has no X and Y columns (it has {string.Join(", ", header)})";
            return false;
        }
        var rows = new Dictionary<string, List<(int Order, int Line, Vec3d At, string Type, int U, int A)>>(StringComparer.Ordinal);
        var names = new List<string>();
        var count = 0;
        for (var i = first + 1; i < lines.Length; i++)
        {
            if (lines[i].Trim().Length == 0)
                continue;
            if (++count > MaxRows)
            {
                why = $"line {i + 1}: more than {MaxRows} pixels";
                return false;
            }
            var cells = Split(lines[i], sep);
            string Cell(int c) => c >= 0 && c < cells.Count ? cells[c].Trim().Trim('"') : "";
            bool Number(int c, out double v)
            {
                v = 0;
                if (c < 0 || Cell(c).Length == 0)
                    return true;
                if (double.TryParse(Cell(c), NumberStyles.Float, CultureInfo.InvariantCulture, out v) && double.IsFinite(v))
                    return true;
                return false;
            }
            string Fail(int c, string what) => $"line {i + 1}, column {c + 1} ({header[c]}): '{Cell(c)}' is not {what}";
            if (!Number(cX, out var x) || Cell(cX).Length == 0)
            {
                why = Fail(cX, "a number");
                return false;
            }
            if (!Number(cY, out var y) || Cell(cY).Length == 0)
            {
                why = Fail(cY, "a number");
                return false;
            }
            if (!Number(cZ, out var z))
            {
                why = Fail(cZ, "a number");
                return false;
            }
            var order = count;
            if (cPixel >= 0 && Cell(cPixel).Length > 0)
            {
                if (!int.TryParse(Cell(cPixel), NumberStyles.Integer, CultureInfo.InvariantCulture, out order))
                {
                    why = Fail(cPixel, "a whole number");
                    return false;
                }
            }
            int u = 0, a = 0;
            if (cDmx >= 0 && Cell(cDmx).Length > 0)
            {
                if (!TryDmx(Cell(cDmx), out u, out a))
                {
                    why = Fail(cDmx, "a DMX address (U.A, or a slot from 1)");
                    return false;
                }
            }
            else if (cUniverse >= 0 || cChannel >= 0)
            {
                if (cUniverse >= 0 && Cell(cUniverse).Length > 0 && !int.TryParse(Cell(cUniverse), NumberStyles.Integer, CultureInfo.InvariantCulture, out u))
                {
                    why = Fail(cUniverse, "a universe");
                    return false;
                }
                if (cChannel >= 0 && Cell(cChannel).Length > 0 && !(int.TryParse(Cell(cChannel), NumberStyles.Integer, CultureInfo.InvariantCulture, out a) && a is >= 1 and <= 512))
                {
                    why = Fail(cChannel, "a channel from 1 to 512");
                    return false;
                }
            }
            var name = cFixture >= 0 && Cell(cFixture).Length > 0 ? Cell(cFixture) : "Imported";
            if (!rows.TryGetValue(name, out var list))
            {
                rows[name] = list = [];
                names.Add(name);
            }
            list.Add((order, i + 1, new Vec3d(x * scale, y * scale, z * scale), Cell(cType), u, a));
        }
        if (count == 0)
        {
            why = $"line {first + 1}: a header and no pixels";
            return false;
        }
        var result = ImmutableArray.CreateBuilder<LedCsvFixture>(names.Count);
        foreach (var name in names)
        {
            var list = rows[name];
            if (cPixel >= 0)
                list = [.. list.OrderBy(r => r.Order).ThenBy(r => r.Line)];
            if (list.Count > LedLayouts.MaxPixels)
            {
                why = $"{name} has {list.Count} pixels — a fixture holds at most {LedLayouts.MaxPixels}";
                return false;
            }
            var lead = list[0];
            result.Add(new LedCsvFixture(name, [.. list.Select(r => r.At)], lead.Type, lead.U, lead.A));
        }
        fixtures = result.ToImmutable();
        return true;
    }

    /// <summary>"U.A", or an absolute slot from 1 counted in universes of 512.</summary>
    private static bool TryDmx(string d, out int universe, out int address)
    {
        universe = 0;
        address = 0;
        var dot = d.IndexOf('.', StringComparison.Ordinal);
        if (dot > 0)
            return int.TryParse(d.AsSpan(0, dot), NumberStyles.Integer, CultureInfo.InvariantCulture, out universe) && universe >= 0
                && int.TryParse(d.AsSpan(dot + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out address) && address is >= 1 and <= 512;
        if (!int.TryParse(d, NumberStyles.Integer, CultureInfo.InvariantCulture, out var absolute) || absolute < 1)
            return false;
        universe = (absolute - 1) / 512;
        address = (absolute - 1) % 512 + 1;
        return true;
    }

    /// <summary>One CSV row's cells (quotes keep separators inside them).</summary>
    private static List<string> Split(string line, char sep)
    {
        var cells = new List<string>();
        var start = 0;
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] == '"')
                quoted = !quoted;
            else if (line[i] == sep && !quoted)
            {
                cells.Add(line[start..i]);
                start = i + 1;
            }
        }
        cells.Add(line[start..]);
        return cells;
    }
}
