using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;
using PlayMap.Core.Bus;
using PlayMap.Core.Model;
using PlayMap.Core.Preflight;
using PlayMap.Engine.Show;
using PlayMap.Core.Wire;

namespace PlayMap.Engine;

/// <summary>PREFLIGHT and PREFLIGHT REPORT: the node's checks (its own five and every package's) run off the lane, side by side
/// under their budgets; the report is a receipt (accepted with its id, then STATE.show.preflight, the context's preflight item,
/// a journal row and the preflight.done event when it is done). The last twenty reports are kept, and each is written beside
/// the journal as preflight/&lt;id&gt;.json.</summary>
public sealed partial class EngineCore
{
    private const int ReportsKept = 20;
    private readonly Lock _preflightGate = new();
    private readonly List<PreflightReport> _reports = [];
    private volatile PreflightReport? _preflight;
    private volatile string? _preflightRunning;
    private ImmutableArray<IPreflightCheck> _builtInChecks = [];

    /// <summary>Every check a preflight runs: the show's own, then each package's from the services.</summary>
    public IReadOnlyList<IPreflightCheck> PreflightChecks => [.. _builtInChecks, .. Services.PreflightChecks];

    /// <summary>Completes with each report as it is published (tests and automations wait on it).</summary>
    public event Action<PreflightReport>? PreflightDone;

    /// <summary>Each check of a running preflight once the runner has taken its row: from then on no clock can cut it.</summary>
    internal event Action<IPreflightCheck>? PreflightCheckFinished;

    private void StartPreflight() =>
        _builtInChecks = [new OutputsCheck(), new MediaCheck(), new DevicesCheck(), new DiskCheck(), new SafetyCheck(), new MappingCheck(),
            new ClockCheck(), new LinksCheck()];

    private async Task<CommandReply> PreflightAsync(VerbCall c)
    {
        const string Syntax = "PREFLIGHT [QUICK] [ALL|WAIT]";
        var all = false;
        var wait = false;
        foreach (var word in c.Args)
        {
            if (word.Equals("ALL", StringComparison.OrdinalIgnoreCase))
                all = true;
            else if (word.Equals("WAIT", StringComparison.OrdinalIgnoreCase))
                wait = true;
            else if (word.Equals("FULL", StringComparison.OrdinalIgnoreCase))
                return CommandReply.Fail("FULL preflight (decoding every clip) is not on this node — PREFLIGHT QUICK reads the facts");
            else if (!word.Equals("QUICK", StringComparison.OrdinalIgnoreCase))
                return CommandReply.Fail(Syntax);
        }
        if (all && wait)
            return CommandReply.Fail(Syntax + " — ALL answers at once with the id; PREFLIGHT REPORT <id> reads the group's report");
        if (all && FanoutMembers() == 0)
            return CommandReply.Fail("no fleet fan-out on this node — PREFLIGHT checks this node only");
        var id = Ids.New("pf");
        lock (_preflightGate)
        {
            if (_preflightRunning is { } running)
                return CommandReply.Fail($"a preflight is running ({running}) — PREFLIGHT REPORT {running} when it ends");
            _preflightRunning = id;
        }
        FactsChanged();
        var run = RunPreflightAsync(id, c.Session.ClientName, all);
        if (wait)
        {
            // The group's leader asks each member this way: the member's receipt carries its finished report.
            var report = await run.ConfigureAwait(false);
            return CommandReply.Success(JsonOut(w => PreflightReports.Write(w, report)), $"preflight {id} by {c.Session.ClientName}");
        }
        var payload = JsonOut(w =>
        {
            w.WriteStartObject();
            w.WriteString("id", id);
            w.WriteString("depth", "quick");
            if (all)
                w.WriteNumber("members", FanoutMembers());
            w.WriteEndObject();
        });
        return new CommandReply(true, payload, $"preflight {id} started by {c.Session.ClientName}{(all ? " on the group" : "")}", Applied: false);
    }

    private async Task<PreflightReport> RunPreflightAsync(string id, string by, bool all)
    {
        var start = Services.Mono.NowNs;
        var name = Services.Settings.Name;
        PreflightReport report;
        try
        {
            var members = all ? FanOutAsync("PREFLIGHT QUICK WAIT", CancellationToken.None) : Task.FromResult<IReadOnlyList<FanoutReceipt>>([]);
            var context = new PreflightContext(this, PreflightDepth.Quick, _showMode.Armed);
            var (rows, complete) = await PreflightRunner.RunAsync(PreflightChecks, context, Services.Mono, name, CancellationToken.None,
                c => CheckFinished(id, c)).ConfigureAwait(false);
            // Every row of this node's checks names this node, whichever word a check used for it.
            List<PreflightRow> merged = [.. rows.Select(r => r.Node == name ? r : r with { Node = name })];
            foreach (var r in await members.ConfigureAwait(false))
            {
                if (r.Stage == "applied" && PreflightReports.Read(r.Words) is { } theirs)
                {
                    merged.AddRange(theirs.Rows);
                    merged.Add(new PreflightRow("fleet:" + r.Node, r.Name, "fleet", r.Name, theirs.Complete ? Light.Green : Light.Grey, "answered",
                        theirs.Complete ? $"{r.Name} checked itself" : $"{r.Name} answered, but not every check finished", null));
                    complete &= theirs.Complete;
                }
                else
                {
                    merged.Add(new PreflightRow("fleet:" + r.Node, r.Name.Length > 0 ? r.Name : r.Node, "fleet", r.Name.Length > 0 ? r.Name : r.Node,
                        Light.Red, r.Stage, r.Words.Length > 0 ? r.Words : "no report came back", null));
                    complete = false;
                }
            }
            report = new PreflightReport(id, Services.Wall.UnixMs, PreflightDepth.Quick, (Services.Mono.NowNs - start) / 1_000_000, merged,
                PreflightReports.Ready(merged), complete);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            report = new PreflightReport(id, Services.Wall.UnixMs, PreflightDepth.Quick, (Services.Mono.NowNs - start) / 1_000_000,
                [new PreflightRow("preflight:fault", name, "preflight", "Preflight", Light.Grey, "fault", "the preflight faulted: " + ex.Message, null)],
                new ShowReady(0, PreflightReports.Steps.Count, "the preflight faulted"), false);
        }
        lock (_preflightGate)
        {
            _reports.Add(report);
            if (_reports.Count > ReportsKept)
                _reports.RemoveAt(0);
            _preflight = report;
            _preflightRunning = null;
        }
        KeepReport(report);
        var (g, am, red, _) = PreflightReports.Counts(report.Rows);
        var nodes = report.Rows.Select(r => r.Node).Distinct(StringComparer.Ordinal).Count();
        Journal.Add("preflight", string.Create(CultureInfo.InvariantCulture,
            $"preflight QUICK by {by}{(nodes > 1 ? $" on {nodes} nodes" : "")}: {g} green, {am} amber, {red} red in {report.DurationMs / 1000.0:0.0} s · show-ready {PreflightReports.ReadyWords(report.Ready)}"),
            by, ok: red == 0);
        FactsChanged();
        RaiseEvent("preflight.done", LeaderNowNs, w =>
        {
            w.WriteString("id", report.Id);
            w.WriteString("light", ShowContext.LightWord(PreflightReports.Worst(report.Rows)));
        });
        PreflightDone?.Invoke(report);
        return report;
    }

    /// <summary>A listener that faults is its own fault: the preflight and its checks go on.</summary>
    private void CheckFinished(string id, IPreflightCheck check)
    {
        try
        {
            PreflightCheckFinished?.Invoke(check);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Journal.Add("fault", $"preflight {id}: a listener of {check.Section} faulted: {ex.Message}", ok: false);
        }
    }

    /// <summary>Writes a report beside the journal (preflight/&lt;id&gt;.json); a node without a journal file keeps it in memory only.</summary>
    private void KeepReport(PreflightReport report)
    {
        if (Services.JournalPath is not { } journal || Path.GetDirectoryName(journal) is not { Length: > 0 } dir)
            return;
        try
        {
            var folder = Path.Combine(dir, "preflight");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, report.Id + ".json"), JsonOut(w => PreflightReports.Write(w, report)), Encoding.UTF8);
            foreach (var old in Directory.GetFiles(folder, "pf*.json").OrderByDescending(File.GetLastWriteTimeUtc).Skip(ReportsKept))
                File.Delete(old);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Journal.Add("fault", $"preflight {report.Id} could not be written: {ex.Message}", ok: false);
        }
    }

    /// <summary>Off the lane: an older report is read from disk, and the lane never waits on a file.</summary>
    private async Task<CommandReply> PreflightReportAsync(VerbCall c)
    {
        if (c.Args.Count > 1)
            return CommandReply.Fail("PREFLIGHT REPORT [<id>]");
        PreflightReport? report;
        lock (_preflightGate)
            report = c.Args.Count == 0 ? _preflight : _reports.FirstOrDefault(r => r.Id == c.Args[0]);
        if (report is null && c.Args.Count > 0)
            report = await ReadKeptReportAsync(c.Args[0], c.Ct).ConfigureAwait(false);
        if (report is null)
            return CommandReply.Fail(c.Args.Count == 0 ? "no preflight has run on this node — PREFLIGHT runs one" : $"no preflight report {c.Args[0]}");
        return CommandReply.Success(JsonOut(w => PreflightReports.Write(w, report)));
    }

    private async Task<PreflightReport?> ReadKeptReportAsync(string id, CancellationToken ct)
    {
        if (Services.JournalPath is not { } journal || Path.GetDirectoryName(journal) is not { Length: > 0 } dir
            || id.Length != 10 || !id.StartsWith("pf", StringComparison.Ordinal) || !id.Skip(2).All(char.IsAsciiHexDigitLower))
            return null;
        var path = Path.Combine(dir, "preflight", id + ".json");
        try
        {
            return File.Exists(path) ? PreflightReports.Read(await File.ReadAllTextAsync(path, ct).ConfigureAwait(false)) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Journal.Add("fault", $"preflight {id} could not be read: {ex.Message}", ok: false);
            return null;
        }
    }

    /// <summary>STATE.show.preflight: the last report's sums, or null before the first; running while one runs.</summary>
    private void WritePreflightSummary(Utf8JsonWriter w)
    {
        var report = _preflight;
        var running = _preflightRunning;
        if (report is null)
        {
            if (running is null)
            {
                w.WriteNullValue();
                return;
            }
            w.WriteStartObject();
            w.WriteString("id", running);
            w.WriteBoolean("running", true);
            w.WriteEndObject();
            return;
        }
        var (g, a, r, n) = PreflightReports.Counts(report.Rows);
        w.WriteStartObject();
        w.WriteString("id", report.Id);
        w.WriteNumber("at", report.AtUnixMs);
        w.WriteString("depth", PreflightReports.DepthWord(report.Depth));
        w.WriteString("light", ShowContext.LightWord(PreflightReports.Worst(report.Rows)));
        w.WriteNumber("green", g);
        w.WriteNumber("amber", a);
        w.WriteNumber("red", r);
        w.WriteNumber("grey", n);
        w.WriteString("ready", PreflightReports.ReadyWords(report.Ready));
        w.WriteString("firstOpen", report.Ready.FirstOpen);
        w.WriteBoolean("running", running is not null);
        w.WriteEndObject();
    }
}
