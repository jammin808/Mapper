using System.Globalization;

namespace PlayMap.PriorityProbe;

/// <summary>The reader held to hand-made legs: a copy of the fixture's folder (the checkout is never written) is read, and every figure
/// the fixture's expected.csv names (folder, summary.csv column, value, why) is compared with what the reader wrote, as text; then every
/// line expected-summary.tsv names (a row's first cell, text the row must hold, why; tab-separated, since the tables' cells carry commas)
/// is looked for in summary.md, so the tables the owner reads are held too (each repeat's figure and the >= of a lower bound among
/// them). Each one that differs is said with its why, so a reader that regresses fails with the words of what it no longer counts;
/// exit 0 when every figure and line is as expected, 1 when one is not or the reader read no leg.</summary>
internal static class SelfCheck
{
    public const string Expected = "expected.csv";
    public const string ExpectedSummary = "expected-summary.tsv";

    public static int Run(string fixture)
    {
        if (!Directory.Exists(fixture))
            throw new ArgumentException($"{fixture} is not a folder");
        var expected = Files.ReadCsv(Path.Combine(fixture, Expected));
        if (expected.Count == 0)
            throw new ArgumentException($"{Path.Combine(fixture, Expected)} names no figure");
        var copy = Directory.CreateTempSubdirectory("priority-probe-selfcheck-").FullName;
        try
        {
            foreach (var file in Directory.EnumerateFiles(fixture, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(fixture, file);
                if (relative is Expected or ExpectedSummary)
                    continue;
                var target = Path.Combine(copy, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target);
            }
            if (Reader.Run(copy, say: false) != 0)
            {
                Program.Say("selfcheck: the reader read no leg of the fixture");
                return 1;
            }
            var read = Files.ReadCsv(Path.Combine(copy, Reader.SummaryCsv));
            var wrong = 0;
            foreach (var e in expected)
            {
                var row = read.FirstOrDefault(r => r["folder"] == e["folder"]);
                string? said = null;
                if (row is null)
                    said = "no row in summary.csv";
                else if (!row.TryGetValue(e["column"], out var got))
                    said = "summary.csv has no such column";
                else if (got != e["value"])
                    said = $"expected {e["value"]}, read {got}";
                if (said is null)
                    continue;
                wrong++;
                Program.Say($"selfcheck: {e["folder"]} {e["column"]}: {said}: {e["why"]}");
            }
            var lines = SummaryLines(fixture);
            var md = File.ReadAllLines(Path.Combine(copy, Reader.SummaryMd));
            foreach (var (first, holds, why) in lines)
            {
                var rows = md.Where(l => l.StartsWith($"| {first} |", StringComparison.Ordinal)).ToList();
                if (rows.Any(l => l.Contains(holds, StringComparison.Ordinal)))
                    continue;
                wrong++;
                Program.Say(rows.Count == 0
                    ? $"selfcheck: summary.md has no row '| {first} |': {why}"
                    : $"selfcheck: summary.md's row '| {first} |' does not hold '{holds}' (it reads: {string.Join(" / ", rows)}): {why}");
            }
            var all = expected.Count + lines.Count;
            Program.Say(wrong == 0
                ? $"selfcheck: ok, {all.ToString(CultureInfo.InvariantCulture)} figures and lines read as the fixture expects"
                : $"selfcheck: {wrong.ToString(CultureInfo.InvariantCulture)} of {all.ToString(CultureInfo.InvariantCulture)} figures and lines not as the fixture expects");
            return wrong == 0 ? 0 : 1;
        }
        finally
        {
            Directory.Delete(copy, recursive: true);
        }
    }

    /// <summary>expected-summary.tsv's lines after its header: each a row's first cell, the text the row must hold and why, tab-separated;
    /// a line of another shape is refused with words, never skipped.</summary>
    private static List<(string First, string Holds, string Why)> SummaryLines(string fixture)
    {
        var path = Path.Combine(fixture, ExpectedSummary);
        if (!File.Exists(path))
            throw new ArgumentException($"{path} is not there: the fixture names the summary's lines it holds");
        var lines = new List<(string, string, string)>();
        foreach (var line in File.ReadAllLines(path).Skip(1).Where(l => l.Length > 0))
        {
            var cells = line.Split('\t');
            if (cells.Length != 3 || cells.Any(c => c.Length == 0))
                throw new ArgumentException($"{path}: '{line}' is not a row's first cell, the text it holds and why, tab-separated");
            lines.Add((cells[0], cells[1], cells[2]));
        }
        return lines.Count > 0 ? lines : throw new ArgumentException($"{path} names no line");
    }
}
