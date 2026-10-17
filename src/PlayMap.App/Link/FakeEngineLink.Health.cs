using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PlayMap.Core.Bus;
using PlayMap.Core.Fleet;
using PlayMap.Core.Preflight;
using PlayMap.Core.Wire;

namespace PlayMap.App.Link;

/// <summary>Preflight and the hot backup in the demo, with the engine's meaning and shapes: PREFLIGHT [QUICK] checks the demo's
/// outputs, media and show safety with the engine's words and fix lines, keeps the report and publishes STATE.show.preflight as the
/// engine sums it; PREFLIGHT REPORT [&lt;id&gt;] answers the report as Core's <see cref="PreflightReports"/> writes it (the engine's own
/// writer). FAILOVER on a hot backup (STATE.fleet from the backup fixture) takes the show under the next epoch; anywhere else it is
/// refused with the engine's words.</summary>
public sealed partial class FakeEngineLink
{
    private readonly List<PreflightReport> _reports = [];

    /// <summary>Lays a report on the demo as the node's last preflight (a real engine's, in a test): PREFLIGHT REPORT answers it and
    /// STATE.show.preflight sums it.</summary>
    public void PutPreflightReport(PreflightReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        long rev;
        lock (_gate)
        {
            KeepReport(report);
            Publish(projectMoved: false);
            rev = _stateSeq;
        }
        StateChanged?.Invoke(rev);
    }

    private void KeepReport(PreflightReport report)
    {
        _reports.RemoveAll(r => r.Id == report.Id);
        _reports.Add(report);
        var (g, a, r, n) = PreflightReports.Counts(report.Rows);
        Section("show")["preflight"] = new JsonObject
        {
            ["id"] = report.Id, ["at"] = report.AtUnixMs, ["depth"] = PreflightReports.DepthWord(report.Depth),
            ["light"] = ShowContext.LightWord(PreflightReports.Worst(report.Rows)), ["green"] = g, ["amber"] = a, ["red"] = r, ["grey"] = n,
            ["ready"] = PreflightReports.ReadyWords(report.Ready), ["firstOpen"] = report.Ready.FirstOpen, ["running"] = false,
        };
    }

    private WireReply Preflight(WireLine line)
    {
        for (var i = 1; i < line.Count; i++)
        {
            var word = line.WordUpper(i);
            if (word == "FULL")
                return Err("FULL preflight (decoding every clip) is not on this node — PREFLIGHT QUICK reads the facts");
            if (word == "ALL")
                return Err("no fleet fan-out on this node — PREFLIGHT checks this node only");
            if (word is not ("QUICK" or "WAIT"))
                return Err("PREFLIGHT [QUICK] [ALL|WAIT]");
        }
        var id = PlayMap.Core.Model.Ids.New("pf");
        var rows = DemoRows();
        var report = new PreflightReport(id, UnixMs, PreflightDepth.Quick, 0, rows, PreflightReports.Ready(rows), true);
        KeepReport(report);
        Moved();
        return Ok("{\"id\":\"" + id + "\",\"depth\":\"quick\"}");
    }

    /// <summary>The demo's checks in the engine's words: each open output on a display this machine has, each media item present, the
    /// show's safety (armed or rehearsal, the double-GO rule, a panic up).</summary>
    private List<PreflightRow> DemoRows()
    {
        const string Node = "Demo node";
        var p = _store.Project;
        var displays = new Dictionary<string, string>(StringComparer.Ordinal) { ["DISPLAY1"] = "Desk monitor", ["DISPLAY2"] = "Projector A", ["DISPLAY3"] = "Projector B" };
        var rows = new List<PreflightRow>();
        foreach (var o in p.Outputs.Where(o => o.Enabled && o.Kind != PlayMap.Core.Model.OutputKind.Virtual))
        {
            var id = "outputs:" + o.Id;
            if (o.DisplayKey.Length == 0)
                rows.Add(new PreflightRow(id, Node, "outputs", o.Name, Light.Green, "Desk monitor", "takes the first free display", null));
            else if (displays.TryGetValue(o.DisplayKey, out var shown))
                rows.Add(new PreflightRow(id, Node, "outputs", o.Name, Light.Green, shown,
                    string.Create(CultureInfo.InvariantCulture, $"1920×1080 at {o.RefreshHz:0.##} Hz"), null));
            else
                rows.Add(new PreflightRow(id, Node, "outputs", o.Name, Light.Red, o.DisplayKey, $"its display {o.DisplayKey} is not connected (3 others are)",
                    $"SET {o.Id} displayKey \"DISPLAY1\""));
        }
        foreach (var m in p.Media)
        {
            rows.Add(Availability(m).State == "missing"
                ? new PreflightRow("media:" + m.Id, Node, "media", m.Name, Light.Red, "missing", $"{m.Name} is not on this machine", $"MEDIA FETCH {m.Id}")
                : new PreflightRow("media:" + m.Id, Node, "media", m.Name, Light.Green, "ready", "present and readable", null));
        }
        var show = _sections.TryGetValue("show", out var s) ? s as JsonObject : null;
        var rules = show?["rules"] as JsonObject;
        var expect = rules?["expectArmed"]?.GetValue<bool>() == true;
        rows.Add(Armed
            ? new PreflightRow("safety:armed", Node, "safety", "Show Mode", Light.Green, "armed", "armed by " + (show?["by"]?.GetValue<string>() ?? "Desk"), null)
            : new PreflightRow("safety:armed", Node, "safety", "Show Mode", expect ? Light.Amber : Light.Grey, "rehearsal",
                expect ? "the show expects to be armed" : "rehearsal: every edit goes live", "SHOW ARM"));
        if ((rules?["minGoMs"]?.GetValue<int>() ?? 0) == 0)
            rows.Add(new PreflightRow("safety:mingo", Node, "safety", "Double-GO protection", Light.Amber, "off", "armed, two GOs in a row both run", "SHOW SET MINGO 300"));
        if (show?["panic"] is JsonObject panic)
            rows.Add(new PreflightRow("safety:in-panic", Node, "safety", "Panic", Light.Red, panic["outcome"]?.GetValue<string>() ?? "black",
                "in panic — the air is dark", "PANIC CLEAR"));
        return rows;
    }

    private WireReply PreflightReport(WireLine line)
    {
        if (line.Count > 3)
            return Err("PREFLIGHT REPORT [<id>]");
        var report = line.Count == 3 ? _reports.FirstOrDefault(r => r.Id == line.Word(2)) : _reports.LastOrDefault();
        if (report is null)
            return Err(line.Count == 3 ? "no preflight report " + line.Word(2) : "no preflight has run on this node — PREFLIGHT runs one");
        var buffer = new System.Buffers.ArrayBufferWriter<byte>(1024);
        using (var w = new Utf8JsonWriter(buffer))
            PreflightReports.Write(w, report);
        return Ok(Encoding.UTF8.GetString(buffer.WrittenSpan));
    }

    /// <summary>FAILOVER [&lt;primary&gt;]: a hot backup takes its primary's show under the next epoch (STATE.fleet follows, as the engine's
    /// takeover leaves it); any other node refuses with the engine's words.</summary>
    private WireReply Failover(WireLine line)
    {
        if (line.Count > 2)
            return Err("FAILOVER [<primary node>]");
        var fleet = Section("fleet");
        var role = fleet["role"] is JsonValue r && r.GetValueKind() == JsonValueKind.String ? r.GetValue<string>() : FleetRoles.Solo;
        var of = fleet["of"] is JsonValue o && o.GetValueKind() == JsonValueKind.String ? o.GetValue<string>() : "";
        if (role != FleetRoles.Backup || of.Length == 0)
            return Err(role == FleetRoles.Fenced
                ? $"{NodeName} is fenced: {of} has the show — FLEET BACKUP {of} to mirror it"
                : $"{NodeName} backs up no primary — FAILOVER runs on a hot backup (FLEET BACKUP <primary> first)");
        var primary = fleet["mirror"] is JsonObject m && m["name"] is JsonValue n ? n.GetValue<string>() : of;
        if (line.Count == 2 && line.Word(1) != of && !line.Word(1).Equals(primary, StringComparison.OrdinalIgnoreCase))
            return Err($"{NodeName} backs up {primary}, not {line.Word(1)}");
        var epoch = (fleet["epoch"] is JsonValue e && e.GetValueKind() == JsonValueKind.Number ? e.GetValue<long>() : 0) + 1;
        var words = string.Create(CultureInfo.InvariantCulture,
            $"takeover tk00000000 of {primary} by {NodeName} (pressed) — epoch {epoch} committed → serving");
        fleet["role"] = FleetRoles.Primary;
        fleet["epoch"] = epoch;
        fleet["state"] = "serving";
        fleet["mirror"] = null;
        fleet["alarm"] = null;
        fleet["door"] = null;
        fleet["last"] = new JsonObject { ["kind"] = "takeover", ["id"] = "tk00000000", ["at"] = UnixMs, ["words"] = words };
        Moved();
        return Ok(words);
    }
}
