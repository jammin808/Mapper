using System.Globalization;
using System.Text.Json;
using PlayMap.Core.Bus;

namespace PlayMap.Core.Preflight;

/// <summary>A preflight report's sums and its words: the counts by light, the worst light, show-ready's five steps (a step with
/// no rows does not apply, counts as done and says so; a step whose check did not finish is open), and the report as JSON —
/// written and read back unchanged.</summary>
public static class PreflightReports
{
    /// <summary>Show-ready's steps in order, each with the sections that decide it.</summary>
    public static readonly IReadOnlyList<(string Step, string[] Sections)> Steps =
    [
        ("outputs", ["outputs", "machine"]),
        ("media", ["media", "disk"]),
        ("mapping", ["mapping", "devices"]),
        ("sync and backup", ["sync", "clock", "fleet", "links"]),
        ("safety", ["safety"]),
    ];

    /// <summary>How many rows carry each light.</summary>
    public static (int Green, int Amber, int Red, int Grey) Counts(IReadOnlyList<PreflightRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        int g = 0, a = 0, r = 0, n = 0;
        foreach (var row in rows)
        {
            switch (row.Light)
            {
                case Light.Green: g++; break;
                case Light.Amber: a++; break;
                case Light.Red: r++; break;
                default: n++; break;
            }
        }
        return (g, a, r, n);
    }

    /// <summary>The worst light among the rows (grey when there are none, or only grey ones).</summary>
    public static Light Worst(IReadOnlyList<PreflightRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var worst = Light.Grey;
        foreach (var row in rows)
        {
            if (row.Light > worst)
                worst = row.Light;
        }
        return worst;
    }

    /// <summary>The row a check leaves when it did not finish (past its budget, or faulted): grey, "unknown", with the words why.
    /// What was not checked is not known to be ready, so its step stays open.</summary>
    public static PreflightRow Unfinished(string node, string section, string note)
    {
        ArgumentNullException.ThrowIfNull(section);
        return new PreflightRow(section + UnfinishedSuffix, node, section, section, Light.Grey, "unknown", note, null);
    }

    /// <summary>True for the row of a check that did not finish.</summary>
    public static bool IsUnfinished(PreflightRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return row.Light == Light.Grey && row.Id.Length == row.Section.Length + UnfinishedSuffix.Length
            && row.Id.StartsWith(row.Section, StringComparison.Ordinal) && row.Id.EndsWith(UnfinishedSuffix, StringComparison.Ordinal);
    }

    private const string UnfinishedSuffix = ":check";

    /// <summary>Show-ready: a step is done when none of its rows is red or amber and every check of it finished (a step with no rows
    /// does not apply and counts as done); the first open step's words name the step and its first problem ("media: Intro is
    /// missing").</summary>
    public static ShowReady Ready(IReadOnlyList<PreflightRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var done = 0;
        var first = "";
        foreach (var (step, sections) in Steps)
        {
            var problem = rows.FirstOrDefault(r => sections.Contains(r.Section, StringComparer.Ordinal) && (r.Light is Light.Red or Light.Amber || IsUnfinished(r)));
            if (problem is null)
            {
                done++;
                continue;
            }
            if (first.Length == 0)
                first = $"{step}: {problem.Item} — {problem.Note}";
        }
        return new ShowReady(done, Steps.Count, first);
    }

    /// <summary>The steps that did not apply (no rows), for the words "sync and backup: nothing to check on this node".</summary>
    public static IReadOnlyList<string> NotApplying(IReadOnlyList<PreflightRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        return [.. Steps.Where(s => !rows.Any(r => s.Sections.Contains(r.Section, StringComparer.Ordinal))).Select(s => s.Step)];
    }

    public static string DepthWord(PreflightDepth depth) => depth == PreflightDepth.Full ? "full" : "quick";

    public static void Write(Utf8JsonWriter w, PreflightReport report)
    {
        ArgumentNullException.ThrowIfNull(w);
        ArgumentNullException.ThrowIfNull(report);
        var (g, a, r, n) = Counts(report.Rows);
        w.WriteStartObject();
        w.WriteString("id", report.Id);
        w.WriteNumber("at", report.AtUnixMs);
        w.WriteString("depth", DepthWord(report.Depth));
        w.WriteNumber("durationMs", report.DurationMs);
        w.WriteString("light", ShowContext.LightWord(Worst(report.Rows)));
        w.WriteNumber("green", g);
        w.WriteNumber("amber", a);
        w.WriteNumber("red", r);
        w.WriteNumber("grey", n);
        w.WriteStartObject("ready");
        w.WriteNumber("done", report.Ready.Done);
        w.WriteNumber("of", report.Ready.Of);
        w.WriteString("firstOpen", report.Ready.FirstOpen);
        w.WriteEndObject();
        w.WriteBoolean("complete", report.Complete);
        w.WriteStartArray("rows");
        foreach (var row in report.Rows)
        {
            w.WriteStartObject();
            w.WriteString("id", row.Id);
            w.WriteString("node", row.Node);
            w.WriteString("section", row.Section);
            w.WriteString("item", row.Item);
            w.WriteString("light", ShowContext.LightWord(row.Light));
            w.WriteString("value", row.Value);
            w.WriteString("note", row.Note);
            if (row.Fix is null)
                w.WriteNull("fix");
            else
                w.WriteString("fix", row.Fix);
            w.WriteEndObject();
        }
        w.WriteEndArray();
        // One section per node, in the order the rows name them (a group's preflight lists every member): made from the rows.
        w.WriteStartArray("nodes");
        foreach (var node in report.Rows.Select(r => r.Node).Distinct(StringComparer.Ordinal))
        {
            var mine = report.Rows.Where(r => r.Node == node).ToList();
            var (ng, na, nr, nn) = Counts(mine);
            w.WriteStartObject();
            w.WriteString("node", node);
            w.WriteString("light", ShowContext.LightWord(Worst(mine)));
            w.WriteNumber("green", ng);
            w.WriteNumber("amber", na);
            w.WriteNumber("red", nr);
            w.WriteNumber("grey", nn);
            w.WriteString("ready", ReadyWords(Ready(mine)));
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteEndObject();
    }

    /// <summary>A report read back from its JSON (null when the text is not one).</summary>
    public static PreflightReport? Read(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        try
        {
            using var doc = JsonDocument.Parse(json);
            var e = doc.RootElement;
            var rows = e.GetProperty("rows").EnumerateArray().Select(r => new PreflightRow(
                r.GetProperty("id").GetString() ?? "", r.GetProperty("node").GetString() ?? "", r.GetProperty("section").GetString() ?? "",
                r.GetProperty("item").GetString() ?? "", ShowContext.ParseLight(r.GetProperty("light").GetString()),
                r.GetProperty("value").GetString() ?? "", r.GetProperty("note").GetString() ?? "",
                r.GetProperty("fix").ValueKind == JsonValueKind.String ? r.GetProperty("fix").GetString() : null)).ToList();
            var ready = e.GetProperty("ready");
            return new PreflightReport(e.GetProperty("id").GetString() ?? "", e.GetProperty("at").GetInt64(),
                e.GetProperty("depth").GetString() == "full" ? PreflightDepth.Full : PreflightDepth.Quick, e.GetProperty("durationMs").GetInt64(),
                rows, new ShowReady(ready.GetProperty("done").GetInt32(), ready.GetProperty("of").GetInt32(), ready.GetProperty("firstOpen").GetString() ?? ""),
                e.GetProperty("complete").GetBoolean());
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    /// <summary>"4/5": show-ready as the strip shows it.</summary>
    public static string ReadyWords(ShowReady ready)
    {
        ArgumentNullException.ThrowIfNull(ready);
        return string.Create(CultureInfo.InvariantCulture, $"{ready.Done}/{ready.Of}");
    }
}
