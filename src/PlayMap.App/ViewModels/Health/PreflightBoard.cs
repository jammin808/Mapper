using System.Collections.ObjectModel;
using System.Globalization;
using PlayMap.App.Infrastructure;
using PlayMap.App.Link;
using PlayMap.App.State;
using PlayMap.Core.Bus;
using PlayMap.Core.Preflight;

namespace PlayMap.App.ViewModels.Health;

/// <summary>A preflight report's section (outputs, media, mapping…) on one node: its rows in the report's order.</summary>
public sealed class PreflightSectionViewModel(string key, string title) : ObservableObject
{
    private string _light = "grey";

    public string Key { get; } = key;

    public string Title { get; } = title;

    public string Light
    {
        get => _light;
        set => Set(ref _light, value);
    }

    public ObservableCollection<StatusRow> Rows { get; } = [];
}

/// <summary>Health ▸ Preflight: the node's last preflight report, a section per check, each row a status row whose Fix sends the
/// row's own fix line (the line the check wrote, the same a controller or an assistant would send). The report is read with
/// <c>PREFLIGHT REPORT &lt;id&gt;</c> while the board is shown and whenever STATE.show.preflight names a new report; the header is
/// STATE.show.preflight's show-ready and first open step; Run sends <c>PREFLIGHT QUICK</c>.</summary>
public sealed class PreflightBoardViewModel : ObservableObject
{
    private readonly DeskSession _session;
    private readonly Dictionary<string, (DeskAction Action, string Line)> _fixes = new(StringComparer.Ordinal);
    private string? _summaryId;
    private bool _running;
    private string _fetched = "";
    private Task _fetch = Task.CompletedTask;
    private string _header = "No preflight has run on this node yet.";
    private string _light = "grey";
    private string _lastRun = "";
    private bool _isShown;
    private PreflightReport? _report;

    public PreflightBoardViewModel(DeskSession session)
    {
        _session = session;
        Run = DeskAction.Send(session, "preflight.run", "Run preflight", () => "PREFLIGHT QUICK", isEnabled: () => session.IsLive && !_running).Look("health");
        Actions = [Run];
    }

    /// <summary>Run: a quick preflight of this node (the report follows in STATE.show.preflight).</summary>
    public DeskAction Run { get; }

    /// <summary>The board's toolbar (its right-click menu is the same list).</summary>
    public IReadOnlyList<DeskAction> Actions { get; }

    public IReadOnlyList<DeskAction> Toolbar => Actions;

    public ObservableCollection<PreflightSectionViewModel> Sections { get; } = [];

    /// <summary>"show-ready 3/5 · outputs: Output 1 — this machine has no display for it", "Checking…" while one runs.</summary>
    public string Header
    {
        get => _header;
        private set => Set(ref _header, value);
    }

    public string Light
    {
        get => _light;
        private set => Set(ref _light, value);
    }

    /// <summary>"last run 21:04:05 · 1.8 s · 41 green, 2 amber, 0 red, 1 grey".</summary>
    public string LastRun
    {
        get => _lastRun;
        private set => Set(ref _lastRun, value);
    }

    /// <summary>The report the rows show, or null before one is read.</summary>
    public PreflightReport? Report => _report;

    /// <summary>The read of the report in flight (tests wait on it).</summary>
    public Task Fetching => _fetch;

    /// <summary>Every row's Fix, in the board's order.</summary>
    public IEnumerable<DeskAction> Fixes => Sections.SelectMany(s => s.Rows).Select(r => r.Fix).OfType<DeskAction>();

    /// <summary>The board is on screen: its report is read now if it is not the one shown.</summary>
    public bool IsShown
    {
        get => _isShown;
        set
        {
            if (Set(ref _isShown, value) && value)
                FetchIfNew();
        }
    }

    public void OnState(StateSnapshot state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var show = state.Root.ValueKind == System.Text.Json.JsonValueKind.Object && state.Root.TryGetProperty("show", out var s) ? s : default;
        var pf = show.ValueKind == System.Text.Json.JsonValueKind.Object ? show.Obj("preflight") : null;
        _summaryId = pf?.StrOrNull("id");
        var running = pf?.Bool("running") ?? false;
        if (running != _running)
        {
            _running = running;
            Run.Refresh();
        }
        if (pf is not { } p)
        {
            Header = "No preflight has run on this node yet.";
            Light = "grey";
        }
        else if (running)
        {
            Header = "Checking…";
        }
        else
        {
            var open = p.Str("firstOpen");
            Header = "show-ready " + p.Str("ready") + (open.Length > 0 ? " · " + open : "");
            Light = ShowContext.LightWord(ShowContext.ParseLight(p.Str("light")));
        }
        if (_isShown)
            FetchIfNew();
    }

    public void RefreshActions()
    {
        Run.Refresh();
        foreach (var (a, _) in _fixes.Values)
            a.Refresh();
    }

    /// <summary>Reads the report STATE names when it is not the one the board shows (one read per report, none while it runs).</summary>
    private void FetchIfNew()
    {
        if (_summaryId is not { Length: > 0 } id || _running || id == _fetched || !_session.IsLive)
            return;
        _fetched = id;
        _fetch = FetchAsync(id);
    }

    private async Task FetchAsync(string id)
    {
        var reply = await _session.SendQuietAsync("PREFLIGHT REPORT " + id);
        if (!reply.Ok)
        {
            // Not readable now (the node restarted, the report aged out): the next push asks again.
            _fetched = "";
            Header = DeskSession.Plain(reply.Text);
            return;
        }
        if (PreflightReports.Read(reply.Text) is { } report && report.Id == id)
            Lay(report);
    }

    /// <summary>Lays a report on the board: a section per node and check in the report's order, each row a status row; a row's Fix
    /// sends its fix line and is the same action from report to report while the line stays.</summary>
    private void Lay(PreflightReport report)
    {
        _report = report;
        Raise(nameof(Report));
        var nodes = report.Rows.Select(r => r.Node).Distinct(StringComparer.Ordinal).Count();
        var keep = new HashSet<string>(StringComparer.Ordinal);
        var sections = new List<PreflightSectionViewModel>();
        foreach (var group in report.Rows.GroupBy(r => (r.Node, r.Section)))
        {
            var key = group.Key.Node + "/" + group.Key.Section;
            var title = nodes > 1 ? group.Key.Node + " · " + group.Key.Section : group.Key.Section;
            var section = Sections.FirstOrDefault(x => x.Key == key) ?? new PreflightSectionViewModel(key, title);
            var rows = new List<StatusRow>();
            foreach (var row in group)
            {
                var rowKey = key + "/" + row.Id;
                keep.Add(rowKey);
                var status = section.Rows.FirstOrDefault(x => x.Id == rowKey) ?? new StatusRow(rowKey);
                status.Update(ShowContext.LightWord(row.Light), row.Item, row.Value, row.Note, FixFor(rowKey, row.Fix));
                rows.Add(status);
            }
            Sync(section.Rows, rows);
            section.Light = ShowContext.LightWord(PreflightReports.Worst([.. group]));
            sections.Add(section);
        }
        foreach (var k in _fixes.Keys.Where(k => !keep.Contains(k)).ToList())
            _fixes.Remove(k);
        Sync(Sections, sections);
        var (g, a, r, n) = PreflightReports.Counts(report.Rows);
        LastRun = string.Create(CultureInfo.InvariantCulture,
            $"last run {ViewModels.Show.ShowStripViewModel.Clock(report.AtUnixMs, "HH:mm:ss")} · {report.DurationMs / 1000.0:0.0} s · {g} green, {a} amber, {r} red, {n} grey{(report.Complete ? "" : " · not every check finished")}");
        Light = ShowContext.LightWord(PreflightReports.Worst(report.Rows));
    }

    /// <summary>The Fix of a row: an action sending the row's fix line, or null when nothing on the wire fixes it.</summary>
    private DeskAction? FixFor(string rowKey, string? line)
    {
        if (line is not { Length: > 0 } fix)
        {
            _fixes.Remove(rowKey);
            return null;
        }
        if (_fixes.TryGetValue(rowKey, out var had) && had.Line == fix)
            return had.Action;
        var action = DeskAction.Send(_session, "preflight.fix", "Fix", () => fix, isEnabled: () => _session.IsLive).Look("play");
        _fixes[rowKey] = (action, fix);
        return action;
    }

    private static void Sync<T>(ObservableCollection<T> into, List<T> want)
    {
        if (into.SequenceEqual(want))
            return;
        into.Clear();
        foreach (var x in want)
            into.Add(x);
    }
}
