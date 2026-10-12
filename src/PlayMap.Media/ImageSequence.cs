using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace PlayMap.Media;

/// <summary>An image sequence's location: a folder of numbered images, or a pattern naming them with a printf
/// number ("shot_%04d.exr", "shot_%d.png") or a run of hashes ("shot_####.dpx"). Frames are ordered by their
/// number (a folder: by name, numbers compared as numbers, so frame_9 comes before frame_10).</summary>
public static class ImageSequence
{
    private static readonly string[] ImageExtensions =
        [".png", ".jpg", ".jpeg", ".tif", ".tiff", ".exr", ".dpx", ".webp", ".bmp", ".tga", ".jxl", ".j2k", ".jp2", ".hdr"];

    private static readonly Regex PrintfNumber = new(@"%(0?\d*)d", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    /// <summary>True for a folder or a pattern (a printf number or a run of hashes in the file name).</summary>
    public static bool IsSequenceLocation(string location)
    {
        if (string.IsNullOrWhiteSpace(location))
            return false;
        if (Directory.Exists(location))
            return true;
        var name = Path.GetFileName(location);
        return PrintfNumber.IsMatch(name) || name.Contains('#', StringComparison.Ordinal);
    }

    public static bool IsImageFile(string path) =>
        ImageExtensions.Contains(Path.GetExtension(path).ToLowerInvariant(), StringComparer.Ordinal);

    /// <summary>The frames of a sequence in order (empty when none match).</summary>
    public static IReadOnlyList<string> Resolve(string location)
    {
        if (Directory.Exists(location))
        {
            var files = Directory.EnumerateFiles(location).Where(IsImageFile).ToList();
            files.Sort(NaturalComparer.Instance);
            return files;
        }
        var dir = Path.GetDirectoryName(location);
        if (string.IsNullOrEmpty(dir))
            dir = ".";
        if (!Directory.Exists(dir))
            return [];
        var regex = PatternRegex(Path.GetFileName(location));
        var numbered = new List<(long Number, string Path)>();
        foreach (var file in Directory.EnumerateFiles(dir))
        {
            var m = regex.Match(Path.GetFileName(file));
            if (m.Success && long.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var n))
                numbered.Add((n, file));
        }
        numbered.Sort((a, b) => a.Number.CompareTo(b.Number));
        return numbered.ConvertAll(x => x.Path);
    }

    // The file-name pattern as an anchored regex whose one group is the frame number.
    private static Regex PatternRegex(string name)
    {
        var sb = new StringBuilder("^");
        var i = 0;
        var numbered = false;
        while (i < name.Length)
        {
            var printf = numbered ? Match.Empty : PrintfNumber.Match(name, i);
            if (printf.Success && printf.Index == i)
            {
                var width = printf.Groups[1].Value.TrimStart('0');
                sb.Append(width.Length > 0 ? @"(\d{" + width + ",})" : @"(\d+)");
                i += printf.Length;
                numbered = true;
                continue;
            }
            if (!numbered && name[i] == '#')
            {
                var run = 0;
                while (i + run < name.Length && name[i + run] == '#')
                    run++;
                sb.Append(@"(\d{").Append(run.ToString(CultureInfo.InvariantCulture)).Append(",})");
                i += run;
                numbered = true;
                continue;
            }
            sb.Append(Regex.Escape(name[i].ToString()));
            i++;
        }
        sb.Append('$');
        return new Regex(sb.ToString(), RegexOptions.CultureInvariant | (OperatingSystem.IsWindows() ? RegexOptions.IgnoreCase : RegexOptions.None),
            TimeSpan.FromSeconds(1));
    }

    /// <summary>Orders names with their digit runs compared as numbers.</summary>
    public sealed class NaturalComparer : IComparer<string>
    {
        public static readonly NaturalComparer Instance = new();

        public int Compare(string? x, string? y)
        {
            if (ReferenceEquals(x, y))
                return 0;
            if (x is null)
                return -1;
            if (y is null)
                return 1;
            int i = 0, j = 0;
            while (i < x.Length && j < y.Length)
            {
                if (char.IsAsciiDigit(x[i]) && char.IsAsciiDigit(y[j]))
                {
                    var si = i;
                    var sj = j;
                    while (i < x.Length && char.IsAsciiDigit(x[i]))
                        i++;
                    while (j < y.Length && char.IsAsciiDigit(y[j]))
                        j++;
                    var a = x.AsSpan(si, i - si).TrimStart('0');
                    var b = y.AsSpan(sj, j - sj).TrimStart('0');
                    if (a.Length != b.Length)
                        return a.Length.CompareTo(b.Length);
                    var c = a.SequenceCompareTo(b);
                    if (c != 0)
                        return c;
                    continue;
                }
                var d = x[i].CompareTo(y[j]);
                if (d != 0)
                    return d;
                i++;
                j++;
            }
            return (x.Length - i).CompareTo(y.Length - j);
        }
    }
}
