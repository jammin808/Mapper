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

    public ShowRole ShowRoleOf(SessionInfo session) => ShowPolicy.RoleOf(session, _operators, ProvedOnConnection(session));

    /// <summary>Whether a session proved the node's token on its own connection, as far as the session says: on a node that asks
    /// one, a session paired from an address the node does not trust paired by proof (trust is not proof); a node that asks none has
    /// no secret to prove, so only this machine counts — the rule the desk's own role follows. The session carries no proof bit of
    /// its own, so a session from a trusted address (this machine, when loopback is trusted) never counts.</summary>
    private bool ProvedOnConnection(SessionInfo session)
    {
        var settings = Services.Settings;
        // Only the doors a client dials in through can carry a proof; a link this node opened (Patterns, Stagewatch) proved nothing.
        if (!session.Paired || session.Transport is not ("wire" or "ws" or "http" or "companion"))
            return false;
        if (!PlayMap.Net.NetAddress.TokenNeeded(settings))
            return session.IsLoopback;
        // Paired from an address the node does not trust means paired by the token; an address it trusts (this machine with
        // loopback trusted, the trusted list) may have paired without one, so it never counts as proof — fail closed.
        return System.Net.IPAddress.TryParse(session.RemoteAddress, out var remote) && !PlayMap.Net.NetAddress.IsTrusted(remote, settings);
    }

    /// <summary>What the show's table says about a line from a session now, from published state only (any thread).</summary>
    public ShowDecision DecideShow(VerbSpec spec, WireLine line, SessionInfo session)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(line);
        ArgumentNullException.ThrowIfNull(session);
        var rules = Store.Project.Show.Rules;
        var air = _snapshot.Air;
        var facts = new ShowFacts(_showMode.Armed, OwnsDraft(session), air.EffectiveBlackout, rules.RemoteViewOnlyWhenArmed, rules.DisarmFrom);
        return ShowPolicy.Decide(spec, line, ShowRoleOf(session), in facts);
    }

    ShowDecision IShowDecisions.Decide(VerbSpec spec, WireLine line, SessionInfo session) => DecideShow(spec, line, session);

    private void StartShow()
    {
        _operators = [.. Services.Settings.Show.Operators];
        // A blind commit landing is the sanctioned way past the armed show's implicit locks; the show's own locks still hold on it.
        Store.Guard = (before, after) => _promoting ? LockGuard.Check(before, after, armed: false)
            : LockGuard.Check(before, after, _showMode.Armed && !_mirroring) ?? PendingGuard(before, after);
        _showGate = new EngineShowGate(this);
        if (Services.ShowGate is LateShowGate late)
            _ = late.Bind(_showGate);
        // A degrade gain (IOutputGains) is part of the air from the start, before any master or panic moves it.
        RebuildLevels();
    }

    /// <summary>True on the lane while a fleet mirror's edit is applied (<see cref="MirrorEditAsync"/>).</summary>
    private bool _mirroring;

    /// <summary>An edit a fleet mirror applies to keep this node's document identical to the machine it mirrors (a hot backup
    /// copying its primary's mapping, wp2-backup): on the lane, one undo step, past the implicit mapping and layout locks an armed
    /// show adds — the primary's own gate already decided the edit — while the show's explicit locks and a waiting blind commit
    /// still hold it. Never for a line from a client: the caller is the fleet part, after its own proof of the peer.</summary>
    public Task<EditResult> MirrorEditAsync(string label, Func<Project, Project> edit)
    {
        ArgumentNullException.ThrowIfNull(edit);
        return _lane.RunAsync(() =>
        {
            _mirroring = true;
            try
            {
                return Store.Apply(label, edit);
            }
            finally
            {
                _mirroring = false;
            }
        });
    }

    /// <summary>The door: the table's decision, then the network area's lock (the only area with no document to guard). A line
    /// an assistant may only propose waits on this node's list for a person; a draftable line from the blind draft's owner runs
    /// into its draft (<paramref name="draft"/>), refused while that draft is landing.</summary>
    private CommandReply? ShowGateLine(VerbSpec spec, WireLine line, SessionInfo session, out ProjectStore? draft)
    {
        var refused = ShowGateDecide(spec, line, session, out draft);
        // Off the lane the line's edits are applied later, through EditAsync on the lane: they keep this decision.
        if (refused is null && !_lane.IsOnLane)
            NoteOffLaneDecision(session, draft);
        return refused;
    }

    private CommandReply? ShowGateDecide(VerbSpec spec, WireLine line, SessionInfo session, out ProjectStore? draft)
    {
        draft = null;
        // A row no package answers on this node is the router's to refuse ("not available"): nothing to decide or to queue.
        if (!IsAvailable(spec))
            return null;
        // Another show opened under a draft would leave the draft and its commit for a show that is gone.
        if (_blind is { } open && spec.Name is "PROJECT OPEN" or "PROJECT NEW")
            return RefuseAtGate(spec, line, session,
                $"a blind draft is open ({open.OwnerName}, {Changes(open)}) — BLIND COMMIT or BLIND DISCARD before opening another show");
        var d = DecideShow(spec, line, session);
        switch (d.Verdict)
        {
            case ShowVerdict.Allow:
                if (ShowPolicy.ClassOf(spec, line) == ShowClass.Network && Store.Project.Show.Locks.FirstOrDefault(l => l.Kind == LockKind.Area && l.Target == LockAreas.Network) is { } held)
                    return RefuseAtGate(spec, line, session, $"network is locked by {held.By} ({LockGuard.Clock(held.AtUnixMs)}) — UNLOCK AREA network first");
                return null;
            case ShowVerdict.BlindOnly:
                if (_blind is { Commit: { } landing })
                    return RefuseAtGate(spec, line, session, $"the blind draft lands {When(landing)} — no more edits to it; BLIND DISCARD cancels the commit");
                draft = DraftStoreFor(session);
                return draft is null ? RefuseAtGate(spec, line, session, "this session holds no blind draft — BLIND ON first") : null;
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

    /// <summary>SHOW ARM [ALL] [words]: this node arms on the lane; ALL then sends the same line to every member of the group this
    /// node leads and answers with each member's receipt (applied, refused with its words, or silent).</summary>
    private Task<CommandReply> ShowArmAsync(VerbCall c)
    {
        var (all, rest) = TakeAll(c.Args);
        if (all && FanoutMembers() == 0)
            return Task.FromResult(CommandReply.Fail("no fleet fan-out on this node — SHOW ARM arms this node only"));
        var words = string.Join(' ', rest);
        return ShowGroupAsync(c, all, () => ShowArm(c.Session, words), words.Length > 0 ? "SHOW ARM " + Quote(words) : "SHOW ARM",
            "already armed by ");
    }

    /// <summary>SHOW DISARM [ALL], as ARM.</summary>
    private Task<CommandReply> ShowDisarmAsync(VerbCall c)
    {
        var (all, rest) = TakeAll(c.Args);
        if (rest.Count > 0)
            return Task.FromResult(CommandReply.Fail("SHOW DISARM [ALL]"));
        if (all && FanoutMembers() == 0)
            return Task.FromResult(CommandReply.Fail("no fleet fan-out on this node — SHOW DISARM disarms this node only"));
        // The group's members run the line as a peer of this node, past their own remote rules: only an operator here may send it.
        if (all && ShowRoleOf(c.Session) is not (ShowRole.Operator or ShowRole.System))
            return Task.FromResult(RefuseAtGate(c.Spec, c.Line, c.Session, "SHOW DISARM ALL disarms every node of the group: the desk's (an operator's) only"));
        return ShowGroupAsync(c, all, () => ShowDisarm(c.Session), "SHOW DISARM", "not armed");
    }

    /// <summary>This node's half on the lane, then (ALL) the group's: a node already in the asked mode still sends the line on, so
    /// ALL brings every member to it. The reply lists this node and then each member with its receipt.</summary>
    private async Task<CommandReply> ShowGroupAsync(VerbCall c, bool all, Func<CommandReply> here, string line, string alreadyWords)
    {
        var local = await _lane.RunAsync(here).ConfigureAwait(false);
        if (!all)
            return local;
        var already = !local.Ok && (local.Payload ?? "").StartsWith(alreadyWords, StringComparison.Ordinal);
        if (!local.Ok && !already)
            return local;
        var receipts = await FanOutAsync(line, c.Ct).ConfigureAwait(false);
        var mode = _showMode;
        var refused = receipts.Count(r => r.Stage != "applied");
        return new CommandReply(true, JsonOut(w =>
        {
            w.WriteStartObject();
            w.WriteBoolean("armed", mode.Armed);
            w.WriteString("by", mode.By);
            w.WriteNumber("since", mode.SinceUnixMs);
            w.WriteNumber("members", receipts.Count);
            w.WriteStartArray("nodes");
            WriteReceipt(w, new FanoutReceipt(Services.Settings.NodeId, Services.Settings.Name, already ? "already" : "applied", LeaderNowNs,
                already ? local.Payload ?? "" : local.Effect));
            foreach (var r in receipts)
                WriteReceipt(w, r);
            w.WriteEndArray();
            w.WriteEndObject();
        }), $"{line} on this node and {receipts.Count} member{(receipts.Count == 1 ? "" : "s")}{(refused > 0 ? $" ({refused} did not apply it)" : "")}");
    }

    private static void WriteReceipt(Utf8JsonWriter w, FanoutReceipt r)
    {
        w.WriteStartObject();
        w.WriteString("node", r.Node);
        w.WriteString("name", r.Name);
        w.WriteString("stage", r.Stage);
        if (r.Stage == "applied")
            w.WriteNumber("appliedLeaderNs", r.AppliedLeaderNs);
        w.WriteString("words", r.Words);
        w.WriteEndObject();
    }

    private CommandReply ShowArm(SessionInfo session, string words)
    {
        var mode = _showMode;
        if (mode.Armed)
            return CommandReply.Fail($"already armed by {mode.By} since {LockGuard.Clock(mode.SinceUnixMs)}");
        _showMode = new ShowMode(true, session.ClientName, Services.Wall.UnixMs, words);
        TouchShow();
        RaiseEvent("show.armed", LeaderNowNs, w => w.WriteString("by", session.ClientName));
        return CommandReply.Success(JsonOut(w =>
        {
            w.WriteStartObject();
            w.WriteBoolean("armed", true);
            w.WriteString("by", session.ClientName);
            w.WriteNumber("since", _showMode.SinceUnixMs);
            w.WriteEndObject();
        }), $"armed by {session.ClientName}{(words.Length > 0 ? " — " + words : "")}");
    }

    private CommandReply ShowDisarm(SessionInfo session)
    {
        if (!_showMode.Armed)
            return CommandReply.Fail("not armed — the show is in rehearsal");
        _showMode = ShowMode.Rehearsal;
        TouchShow();
        RaiseEvent("show.disarmed", LeaderNowNs, w => w.WriteString("by", session.ClientName));
        return CommandReply.Success(JsonOut(w =>
        {
            w.WriteStartObject();
            w.WriteBoolean("armed", false);
            w.WriteNumber("landed", 0);
            w.WriteNumber("setAside", 0);
            w.WriteEndObject();
        }), $"disarmed by {session.ClientName}");
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
            case "COMMIT-LEAD" when a.Count == 2:
                if (!int.TryParse(a[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var commitLead) || commitLead is < 0 or > 2_000)
                    return CommandReply.Fail("COMMIT-LEAD takes 0 to 2000 ms");
                next = rules with { CommitLeadMs = commitLead };
                said = $"a blind commit lands at least {commitLead} ms ahead on the show clock";
                break;
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

    /// <summary>Sends a line to the group this node leads at the leader's now and waits for every member's receipt (the fan-out
    /// times silent members out with words); each is journalled. A fan-out that faults is one receipt saying so.</summary>
    private async Task<IReadOnlyList<FanoutReceipt>> FanOutAsync(string text, CancellationToken ct)
    {
        if (Services.Fanout is not { } fanout)
            return [];
        IReadOnlyList<FanoutReceipt> receipts;
        try
        {
            receipts = await fanout.SendAsync(text, LeaderNowNs, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or OperationCanceledException))
        {
            Journal.Add("fault", $"{text} to the group: {Journal.Describe(ex)}", ok: false);
            return [new FanoutReceipt("", "the group", "fault", 0, Journal.Describe(ex))];
        }
        foreach (var r in receipts)
            Journal.Add(r.Stage == "applied" ? "fleet" : "refused", $"{FirstLine(text)} on {r.Name}: {r.Stage}{(r.Words.Length > 0 ? " — " + FirstLine(r.Words) : "")}", ok: r.Stage == "applied");
        return receipts;
    }

    /// <summary>The first line of words (a member's report rides in its receipt; the journal keeps one line of it).</summary>
    private static string FirstLine(string words)
    {
        var cut = words.IndexOfAny(['\n', '\r']);
        var line = cut < 0 ? words : words[..cut];
        return line.Length <= 160 ? line : string.Concat(line.AsSpan(0, 157), "…");
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
        w.WriteNumber("commitLeadMs", rules.CommitLeadMs);
        w.WriteEndObject();
        w.WritePropertyName("blind");
        WriteBlind(w);
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
        w.WritePropertyName("sound");
        WriteSound(w);
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
        else if (_blind is { } blind)
        {
            var landing = blind.Commit is { } commit ? $", landing {When(commit)}" : " — BLIND COMMIT or BLIND DISCARD";
            show = new ContextItem("show", "show", "Show", Light.Amber,
                $"{(mode.Armed ? "ARMED · " : "")}blind editing by {blind.OwnerName}: {Changes(blind)} staged{landing}");
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
