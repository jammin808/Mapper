using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using PlayMap.Assistant;
using PlayMap.Core.Bus;
using PlayMap.Core.Model;
using PlayMap.Core.Preflight;
using PlayMap.Core.Show;
using PlayMap.Core.Wire;
using PlayMap.Engine.Show;

namespace PlayMap.Engine;

/// <summary>Show Mode: the gate every line passes (ShowPolicy's table over published state), SHOW ARM, DISARM and SET, the
/// refusals' count, STATE.show, the context's show item, the headline's words and the recovery record's facts. Armed is the safe
/// side, so a node that restarts armed comes back armed.</summary>
public sealed partial class EngineCore : IShowDecisions
{
    private volatile ShowMode _showMode = ShowMode.Rehearsal;
    private readonly ShowRefusals _refusals = new();
    private IReadOnlyCollection<string> _operators = [];
    private EngineShowGate? _showGate;

    /// <summary>Show Mode now (any thread).</summary>
    public ShowMode ShowNow => _showMode;

    /// <summary>The node's own show gate: the router's table, as Companion, the desk and heavy work read it.</summary>
    public IShowGate ShowGate => _showGate ?? (IShowGate)OpenShowGate.Instance;

    /// <summary>The double-GO window in force now (armed or rehearsal), in ms.</summary>
    public int MinGoMsNow
    {
        get
        {
            var rules = Store.Project.Show.Rules;
            return _showMode.Armed ? rules.MinGoMsArmed : rules.MinGoMsRehearsal;
        }
    }

    public ShowRole ShowRoleOf(SessionInfo session) => ShowPolicy.RoleOf(session, _operators);

    /// <summary>What the show's table says about a line from a session now, from published state only (any thread).</summary>
    public ShowDecision DecideShow(VerbSpec spec, WireLine line, SessionInfo session)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(line);
        ArgumentNullException.ThrowIfNull(session);
        var rules = Store.Project.Show.Rules;
        var air = _snapshot.Air;
        var facts = new ShowFacts(_showMode.Armed, false, air.EffectiveBlackout, rules.RemoteViewOnlyWhenArmed, rules.DisarmFrom);
        return ShowPolicy.Decide(spec, line, ShowRoleOf(session), in facts);
    }

    ShowDecision IShowDecisions.Decide(VerbSpec spec, WireLine line, SessionInfo session) => DecideShow(spec, line, session);

    private void StartShow()
    {
        _operators = [.. Services.Settings.Show.Operators];
        Store.Guard = (before, after) => LockGuard.Check(before, after, _showMode.Armed);
        _showGate = new EngineShowGate(this);
        if (Services.ShowGate is LateShowGate late)
            _ = late.Bind(_showGate);
        // A degrade gain (IOutputGains) is part of the air from the start, before any master or panic moves it.
        RebuildLevels();
    }

    /// <summary>The door: the table's decision, then the network area's lock (the only area with no document to guard). A line
    /// an assistant may only propose waits on this node's list for a person.</summary>
    private CommandReply? ShowGateLine(VerbSpec spec, WireLine line, SessionInfo session)
    {
        // A row no package answers on this node is the router's to refuse ("not available"): nothing to decide or to queue.
        if (!IsAvailable(spec))
            return null;
        var d = DecideShow(spec, line, session);
        switch (d.Verdict)
        {
            case ShowVerdict.Allow:
                if (ShowPolicy.ClassOf(spec, line) == ShowClass.Network && Store.Project.Show.Locks.FirstOrDefault(l => l.Kind == LockKind.Area && l.Target == LockAreas.Network) is { } held)
                    return RefuseAtGate(spec, line, session, $"network is locked by {held.By} ({LockGuard.Clock(held.AtUnixMs)}) — UNLOCK AREA network first");
                return null;
            case ShowVerdict.Propose:
                return ProposeAtGate(spec, line, session, d.Words ?? "this line waits for a person");
            default:
                return RefuseAtGate(spec, line, session, d.Words ?? "refused by Show Mode");
        }
    }

    private CommandReply RefuseAtGate(VerbSpec spec, WireLine line, SessionInfo session, string words)
    {
        var now = Services.Wall.UnixMs;
        var raise = _refusals.Note(now, $"{session.ClientName} — {VerbCatalog.Redacted(line)} — {words}");
        FactsChanged();
        if (raise)
            RaiseEvent("refused", LeaderNowNs, w =>
            {
                w.WriteString("verb", spec.Name);
                w.WriteString("who", session.ClientName);
                w.WriteString("words", words);
            });
        return CommandReply.Fail(words);
    }

    /// <summary>An assistant's line the armed table holds for a person: queued on this node's own list (never run), answered with
    /// the proposal's id so the assistant reads "waiting" rather than a bare refusal.</summary>
    private CommandReply ProposeAtGate(VerbSpec spec, WireLine line, SessionInfo session, string words)
    {
        if (Services.Assistant is not { } assistant)
            return RefuseAtGate(spec, line, session, words + " — this node keeps no list of proposals, so a person runs it from the desk");
        var check = CommandGate.Check(line.Raw);
        var text = check.Ok ? check.Line : line.Raw;
        var from = session.Transport == "mcp" ? "an assistant over MCP" : "the assistant";
        // The queue lists a proposal as soon as it is asked (an approval desk elsewhere is offered it after).
        _ = assistant.Queue.EnqueueAsync(new ProposalDraft(text, spec, "proposed while the show is armed", from, session.SessionId,
            session.SessionId + "/armed", 0), CancellationToken.None);
        var id = assistant.Queue.All.LastOrDefault(p => p.SessionId == session.SessionId && p.Line == text)?.Id ?? "on this node's list";
        FactsChanged();
        return CommandReply.Fail($"{words} — queued for a person as proposal {id}");
    }

    private CommandReply ShowArm(VerbCall c)
    {
        var (all, rest) = TakeAll(c.Args);
        if (all && FanoutMembers() == 0)
            return CommandReply.Fail("no fleet fan-out on this node — SHOW ARM arms this node only");
        var mode = _showMode;
        if (mode.Armed)
            return CommandReply.Fail($"already armed by {mode.By} since {LockGuard.Clock(mode.SinceUnixMs)}");
        var words = string.Join(' ', rest);
        _showMode = new ShowMode(true, c.Session.ClientName, Services.Wall.UnixMs, words);
        TouchShow();
        RaiseEvent("show.armed", LeaderNowNs, w => w.WriteString("by", c.Session.ClientName));
        var members = all ? FanOut(words.Length > 0 ? "SHOW ARM " + Quote(words) : "SHOW ARM") : 0;
        return CommandReply.Success(JsonOut(w =>
        {
            w.WriteStartObject();
            w.WriteBoolean("armed", true);
            w.WriteString("by", c.Session.ClientName);
            w.WriteNumber("since", _showMode.SinceUnixMs);
            if (all)
                w.WriteNumber("members", members);
            w.WriteEndObject();
        }), $"armed by {c.Session.ClientName}{(words.Length > 0 ? " — " + words : "")}");
    }

    private CommandReply ShowDisarm(VerbCall c)
    {
        var (all, rest) = TakeAll(c.Args);
        if (rest.Count > 0)
            return CommandReply.Fail("SHOW DISARM [ALL]");
        if (all && FanoutMembers() == 0)
            return CommandReply.Fail("no fleet fan-out on this node — SHOW DISARM disarms this node only");
        if (!_showMode.Armed)
            return CommandReply.Fail("not armed — the show is in rehearsal");
        _showMode = ShowMode.Rehearsal;
        TouchShow();
        RaiseEvent("show.disarmed", LeaderNowNs, w => w.WriteString("by", c.Session.ClientName));
        var members = all ? FanOut("SHOW DISARM") : 0;
        return CommandReply.Success(JsonOut(w =>
        {
            w.WriteStartObject();
            w.WriteBoolean("armed", false);
            w.WriteNumber("landed", 0);
            w.WriteNumber("setAside", 0);
            if (all)
                w.WriteNumber("members", members);
            w.WriteEndObject();
        }), $"disarmed by {c.Session.ClientName}");
    }

    private CommandReply ShowSet(VerbCall c)
    {
        const string Syntax = "SHOW SET MINGO <ms> [ARMED|REHEARSAL] | REMOTE-VIEW-ONLY ON|OFF | DISARM-FROM PAIRED|DESK | EXPECT-ARMED ON|OFF | PANIC-LEAD <ms>";
        var a = c.Args;
        if (a.Count < 2)
            return CommandReply.Fail(Syntax);
        var rules = Edits.Project.Show.Rules;
        ShowRules next;
        string said;
        switch (a[0].ToUpperInvariant())
        {
            case "MINGO":
                if (!int.TryParse(a[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ms) || ms is < 0 or > 10_000)
                    return CommandReply.Fail("MINGO takes 0 to 10000 ms");
                var when = a.Count > 2 ? a[2].ToUpperInvariant() : "ARMED";
                if (when is not ("ARMED" or "REHEARSAL") || a.Count > 3)
                    return CommandReply.Fail(Syntax);
                next = when == "ARMED" ? rules with { MinGoMsArmed = ms } : rules with { MinGoMsRehearsal = ms };
                said = $"double-GO protection {ms} ms {(when == "ARMED" ? "armed" : "in rehearsal")}";
                break;
            case "REMOTE-VIEW-ONLY" when a.Count == 2 && TryOnOff(a[1], out var viewOnly):
                next = rules with { RemoteViewOnlyWhenArmed = viewOnly };
                said = viewOnly ? "remote clients only read while armed" : "remote clients run the show while armed";
                break;
            case "DISARM-FROM" when a.Count == 2 && a[1].ToUpperInvariant() is "PAIRED" or "DESK":
                var from = a[1].ToLowerInvariant();
                next = rules with { DisarmFrom = from };
                said = from == "desk" ? "only the desk disarms" : "any paired operator disarms";
                break;
            case "EXPECT-ARMED" when a.Count == 2 && TryOnOff(a[1], out var expect):
                next = rules with { ExpectArmed = expect };
                said = expect ? "playing unarmed is amber" : "playing unarmed is fine";
                break;
            case "PANIC-LEAD" when a.Count == 2:
                if (!int.TryParse(a[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var lead) || lead is < 0 or > 2_000)
                    return CommandReply.Fail("PANIC-LEAD takes 0 to 2000 ms");
                next = rules with { PanicLeadMs = lead };
                said = $"a panic lands {lead} ms ahead on the show clock";
                break;
            case "COMMIT-LEAD":
                return CommandReply.Fail("COMMIT-LEAD times blind commits, and this node has no blind editing yet");
            default:
                return CommandReply.Fail(Syntax);
        }
        if (next == rules)
            return CommandReply.Fail($"already so: {said}");
        return FromEdit(Edits.Apply("show rule: " + said, p => p with { Show = p.Show with { Rules = next } }));
    }

    /// <summary>"ALL" first in the words: the group form, and the words after it.</summary>
    private static (bool All, List<string> Words) TakeAll(List<string> args) =>
        args.Count > 0 && args[0].Equals("ALL", StringComparison.OrdinalIgnoreCase) ? (true, args[1..]) : (false, args);

    private int FanoutMembers() => Services.Fanout?.Members.Count ?? 0;

    /// <summary>Sends an air line to the group this node leads; each member's receipt is journalled when it comes. Answers how many
    /// members it went to.</summary>
    private int FanOut(string text)
    {
        if (Services.Fanout is not { } fanout)
            return 0;
        var members = fanout.Members.Count;
        var at = LeaderNowNs;
        _ = Task.Run(async () =>
        {
            try
            {
                foreach (var r in await fanout.SendAsync(text, at, CancellationToken.None).ConfigureAwait(false))
                    Journal.Add(r.Stage == "refused" ? "refused" : "fleet", $"{text} on {r.Name}: {r.Stage}{(r.Words.Length > 0 ? " — " + r.Words : "")}", ok: r.Stage != "refused");
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Journal.Add("fault", $"{text} to the group: {Journal.Describe(ex)}", ok: false);
            }
        }, CancellationToken.None);
        return members;
    }

    private static string Quote(string words) => "\"" + words.Replace("\"", "'", StringComparison.Ordinal) + "\"";

    /// <summary>Show Mode moved: STATE's revision and the recovery record follow the air's revision.</summary>
    private void TouchShow()
    {
        _air = _air with { Revision = _air.Revision + 1 };
        Republish();
    }

    /// <summary>STATE.show (and SHOW's reply), from published state only.</summary>
    internal void WriteShow(Utf8JsonWriter w)
    {
        var mode = _showMode;
        var project = Store.Project;
        var rules = project.Show.Rules;
        var air = _snapshot.Air;
        w.WriteStartObject();
        w.WriteBoolean("armed", mode.Armed);
        w.WriteString("by", mode.By);
        w.WriteNumber("since", mode.SinceUnixMs);
        w.WriteString("words", mode.Words);
        w.WriteStartObject("rules");
        w.WriteNumber("minGoMs", rules.MinGoMsArmed);
        w.WriteNumber("minGoMsRehearsal", rules.MinGoMsRehearsal);
        w.WriteBoolean("remoteViewOnly", rules.RemoteViewOnlyWhenArmed);
        w.WriteString("disarmFrom", rules.DisarmFrom);
        w.WriteBoolean("expectArmed", rules.ExpectArmed);
        w.WriteString("defaultOutcome", PanicMath.Json(rules.DefaultOutcome));
        w.WriteNumber("defaultFadeMs", rules.DefaultFadeMs);
        w.WriteNumber("panicLeadMs", rules.PanicLeadMs);
        w.WriteEndObject();
        w.WriteNull("blind");
        w.WritePropertyName("panic");
        WritePanic(w, air.Panic);
        w.WritePropertyName("locks");
        WriteLocks(w);
        var (count, last) = _refusals.Read(Services.Wall.UnixMs);
        w.WriteStartObject("refused");
        w.WriteNumber("last60s", count);
        w.WriteString("last", last);
        w.WriteEndObject();
        w.WritePropertyName("preflight");
        WritePreflightSummary(w);
        w.WriteNumber("minGoMs", mode.Armed ? rules.MinGoMsArmed : rules.MinGoMsRehearsal);
        w.WriteEndObject();
    }

    /// <summary>The context's show item (always present, first), the preflight item, and the show's links: it controls every
    /// output it can take dark, and Patterns' link controls the show.</summary>
    private void AddShowContext(List<ContextItem> items, List<ContextLink> links)
    {
        var mode = _showMode;
        var air = _snapshot.Air;
        var rules = Store.Project.Show.Rules;
        ContextItem show;
        if (air.Panic is { } panic)
        {
            show = new ContextItem("show", "show", "Show", Light.Red,
                $"PANIC {PanicMath.Json(panic.Outcome)}{(panic.Hard ? " (hard)" : "")} since {Seconds(panic.SinceUnixMs)} — PANIC CLEAR");
        }
        else if (mode.Armed)
        {
            show = new ContextItem("show", "show", "Show", Light.Green, $"ARMED by {mode.By} since {LockGuard.Clock(mode.SinceUnixMs)}");
        }
        else if (rules.ExpectArmed && IsAnimating)
        {
            show = new ContextItem("show", "show", "Show", Light.Amber, "rehearsal while playing — SHOW ARM before doors");
        }
        else
        {
            show = new ContextItem("show", "show", "Show", Light.Grey, "rehearsal");
        }
        items.Insert(0, show);
        if (_preflight is { } report)
        {
            var (g, a, r, _) = PreflightReports.Counts(report.Rows);
            var ago = Math.Max(0, Services.Wall.UnixMs - report.AtUnixMs) / 60_000;
            var words = string.Create(CultureInfo.InvariantCulture,
                $"{g} green · {a} amber{(r > 0 ? $" · {r} red" : "")} · show-ready {PreflightReports.ReadyWords(report.Ready)} · {(ago == 0 ? "just now" : $"{ago} min ago")}");
            items.Add(new ContextItem("preflight", "preflight", "Preflight", PreflightReports.Worst(report.Rows), words));
        }
        var levels = AirLevelsNow;
        var now = LeaderNowNs;
        foreach (var o in Store.Project.Outputs.Where(o => o.Enabled && o.Kind != OutputKind.Virtual))
        {
            var light = air.Panic?.Outputs.Any(x => x.OutputId == o.Id) == true ? Light.Red
                : levels is not null && levels.Gain(o.Id, now) < 0.999f ? Light.Amber : Light.Green;
            links.Add(new ContextLink("show", "output:" + o.Id, "controls", light));
        }
        for (var i = 0; i < links.Count; i++)
        {
            if (links[i] is { From: "link:patterns", To: "air" } l)
                links[i] = l with { To = "show" };
        }
        if (items.FirstOrDefault(i => i.Id == "link:stagewatch") is { } sw)
            links.Add(new ContextLink("link:stagewatch", "show", "feeds", sw.Light));
    }

    private string ShowHeadline(string headline)
    {
        if (_snapshot.Air.Panic is { } panic)
            return $"PANIC {PanicMath.Json(panic.Outcome)} · {headline}";
        return _showMode.Armed ? "ARMED · " + headline : headline;
    }

    private static string Seconds(long unixMs) =>
        DateTimeOffset.FromUnixTimeMilliseconds(unixMs).ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>The recovery record's show facts: armed (by whom, since, why) and a panic in force with each output's outcome.</summary>
    private string ShowRecoveryJson() => JsonOut(w =>
    {
        var mode = _showMode;
        w.WriteStartObject();
        w.WriteBoolean("armed", mode.Armed);
        w.WriteString("by", mode.By);
        w.WriteNumber("since", mode.SinceUnixMs);
        w.WriteString("words", mode.Words);
        if (_air.Panic is { } panic)
        {
            w.WriteStartObject("panic");
            w.WriteString("outcome", PanicMath.Json(panic.Outcome));
            w.WriteString("by", panic.By);
            w.WriteNumber("since", panic.SinceUnixMs);
            w.WriteBoolean("hard", panic.Hard);
            w.WriteStartArray("outputs");
            foreach (var o in panic.Outputs)
            {
                w.WriteStartObject();
                w.WriteString("id", o.OutputId);
                w.WriteString("outcome", PanicMath.Json(o.Outcome));
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        w.WriteEndObject();
    });

    /// <summary>Puts the show facts back after a restart: armed as it was, and a panic at its outcome at once (no fade: the air was
    /// already dark when the node went away).</summary>
    private void LoadShowRecovery(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var e = doc.RootElement;
            if (e.TryGetProperty("armed", out var armed) && armed.GetBoolean())
            {
                _showMode = new ShowMode(true, e.GetProperty("by").GetString() ?? "", e.GetProperty("since").GetInt64(),
                    e.TryGetProperty("words", out var words) ? words.GetString() ?? "" : "");
                Journal.Add("recovery", $"armed again: the show was armed by {_showMode.By} when the node went away");
            }
            if (e.TryGetProperty("panic", out var p) && p.ValueKind == JsonValueKind.Object)
                RestorePanic(p);
            TouchShow();
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            Journal.Add("fault", "the show's recovery facts could not be read: " + ex.Message, ok: false);
        }
    }
}
