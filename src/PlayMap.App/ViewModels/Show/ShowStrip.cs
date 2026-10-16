using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Avalonia.Input;
using PlayMap.App.Infrastructure;
using PlayMap.App.Link;
using PlayMap.App.State;
using PlayMap.Core.Fleet;
using PlayMap.Core.Model;
using PlayMap.Core.Show;
using PlayMap.Core.Wire;

namespace PlayMap.App.ViewModels.Show;

/// <summary>One lock as STATE.show.locks has it.</summary>
public sealed record ShowLockState(string Target, string Kind, string By, string Words, bool Implicit, long AtUnixMs)
{
    /// <summary>The lock as Core's LockGuard holds it, so its words are the guard's own.</summary>
    public ShowLock AsCore() => new(Target, Kind == "area" ? LockKind.Area : LockKind.Entity, By, AtUnixMs, Words);
}

/// <summary>STATE.fleet's door as the desk reads it: the node's place in a hot-backup pair (solo, primary, backup, fenced) and the
/// words a client's edit meets there (null on a node with a show of its own).</summary>
public sealed record FleetDoor(string Role, string? Words)
{
    public static readonly FleetDoor None = new(FleetRoles.Solo, null);

    public static FleetDoor Read(JsonElement? e) => e is { ValueKind: JsonValueKind.Object } f
        ? new FleetDoor(f.Str("role", FleetRoles.Solo), f.StrOrNull("door"))
        : None;
}

/// <summary>One output's part of a panic.</summary>
public sealed record PanicOutputState(string Id, string Outcome, double Level);

/// <summary>STATE.show.blind as the desk reads it: whose draft is open (its owner's name, session and transport as WHOAMI says
/// them), how many changes it holds, and, while its commit waits for its boundary, the boundary in the node's words.</summary>
public sealed record BlindState(string Owner, string Session, string Transport, int Changes, string? CommitWhen)
{
    public static BlindState? Read(JsonElement? e) => e is { ValueKind: JsonValueKind.Object } b && b.Bool("on")
        ? new BlindState(b.Str("owner"), b.Str("session"), b.Str("transport"), (int)b.Long("changes"),
            b.Obj("commit") is { } c ? c.Str("when", "at its boundary") : null)
        : null;
}

/// <summary>STATE.show as the desk reads it (absent: the node has no Show Mode, and the strip hides).</summary>
public sealed record ShowSection(bool Present, bool Armed, string By, long SinceUnixMs, string? PanicOutcome, string PanicBy,
    long PanicSinceUnixMs, bool PanicHard, ImmutableArray<PanicOutputState> PanicOutputs, ImmutableArray<ShowLockState> Locks,
    bool RemoteViewOnly, string DisarmFrom, bool ExpectArmed, int RefusedLastMinute, string RefusedLast, string ReadyLight,
    string Ready, string FirstOpen, bool PreflightRunning, BlindState? Blind)
{
    public static readonly ShowSection Absent = new(false, false, "", 0, null, "", 0, false, [], [], false, "paired", false, 0, "", "grey", "", "", false, null);

    public static ShowSection Read(JsonElement? e)
    {
        if (e is not { ValueKind: JsonValueKind.Object } s)
            return Absent;
        string? outcome = null;
        var by = "";
        long panicSince = 0;
        var hard = false;
        var outputs = ImmutableArray<PanicOutputState>.Empty;
        if (s.TryGetProperty("panic", out var p) && p.ValueKind == JsonValueKind.Object)
        {
            outcome = p.Str("outcome", "black");
            by = p.Str("by");
            panicSince = p.Long("since");
            hard = p.Bool("hard");
            outputs = [.. p.Arr("outputs").Select(o => new PanicOutputState(o.Str("id"), o.Str("outcome", outcome), o.Num("level")))];
        }
        var rules = s.Obj("rules");
        var refused = s.Obj("refused");
        var preflight = s.Obj("preflight");
        return new ShowSection(true, s.Bool("armed"), s.Str("by"), s.Long("since"), outcome, by, panicSince, hard, outputs,
            [.. s.Arr("locks").Select(l => new ShowLockState(l.Str("target"), l.Str("kind"), l.Str("by"), l.Str("words"), l.Bool("implicit"), l.Long("at")))],
            rules?.Bool("remoteViewOnly") ?? false, rules?.Str("disarmFrom", "paired") ?? "paired", rules?.Bool("expectArmed") ?? false,
            (int)(refused?.Long("last60s") ?? 0), refused?.Str("last") ?? "",
            preflight?.Str("light", "grey") ?? "grey", preflight?.Str("ready") ?? "", preflight?.Str("firstOpen") ?? "", preflight?.Bool("running") ?? false,
            BlindState.Read(s.TryGetProperty("blind", out var blind) ? blind : null));
    }

    /// <summary>Equal when every field and every list item is (a push that moves nothing of the show repaints nothing of it).</summary>
    public bool Equals(ShowSection? other) =>
        other is not null && (Present, Armed, By, SinceUnixMs, PanicOutcome, PanicBy, PanicSinceUnixMs, PanicHard, RemoteViewOnly, DisarmFrom,
            ExpectArmed, RefusedLastMinute, RefusedLast, ReadyLight, Ready, FirstOpen, PreflightRunning, Blind)
        == (other.Present, other.Armed, other.By, other.SinceUnixMs, other.PanicOutcome, other.PanicBy, other.PanicSinceUnixMs, other.PanicHard,
            other.RemoteViewOnly, other.DisarmFrom, other.ExpectArmed, other.RefusedLastMinute, other.RefusedLast, other.ReadyLight, other.Ready,
            other.FirstOpen, other.PreflightRunning, other.Blind)
        && PanicOutputs.AsSpan().SequenceEqual(other.PanicOutputs.AsSpan()) && Locks.AsSpan().SequenceEqual(other.Locks.AsSpan());

    public override int GetHashCode() => HashCode.Combine(Armed, By, SinceUnixMs, PanicOutcome, Locks.Length, PanicOutputs.Length, Ready, Blind);

    /// <summary>The lock on an entity or area, or null.</summary>
    public ShowLockState? LockOn(string target) => Locks.FirstOrDefault(l => string.Equals(l.Target, target, StringComparison.Ordinal));

    /// <summary>The show's own locks (LOCK, LOCK AREA) as Core's LockGuard holds them in the document; an armed show's implicit
    /// locks are not among them (they lift with SHOW DISARM, and a blind draft's edits pass them).</summary>
    public ImmutableArray<ShowLock> ExplicitLocks => [.. Locks.Where(l => !l.Implicit).Select(l => l.AsCore())];
}

/// <summary>The top bar's show strip: the Ready light, ARM (no password), PANIC (never a dialog), each following
/// STATE.show, never its own press. Its right-click menus are the Show menu, the panic outcomes and the preflight runs.</summary>
public sealed class ShowStripViewModel : ObservableObject
{
    private static readonly string[] Outcomes = ["BLACK", "RUN", "REWIND", "NEXT", "FREEZE", "SAFE", "SHUTTER", "FX-OFF", "HARD"];
    private ShowSection _show = ShowSection.Absent;

    public ShowStripViewModel(DeskSession session, DeskPrefs prefs, Action openHealth)
    {
        ArgumentNullException.ThrowIfNull(session);
        Arm = DeskAction.Send(session, "show.arm", "Arm", () => _show.Armed ? "SHOW DISARM" : "SHOW ARM", ArmLabel,
            isChecked: () => _show.Armed, isEnabled: () => session.IsLive).Look("arm", "Ctrl+Shift+A");
        Panic = DeskAction.Send(session, "show.panic", "PANIC", () => "PANIC", isEnabled: () => session.IsLive).Look("panic", "F12", danger: true);
        Ready = DeskAction.Local("show.ready", "Ready", openHealth).Look("health");

        var areas = DeskAction.Local("show.lockArea", "Lock area", () => { }).Look("lock");
        foreach (var area in LockAreas.All)
            areas.Children.Add(DeskAction.Send(session, "show.lockArea." + area, area,
                () => _show.LockOn(area) is { Kind: "area" } ? "UNLOCK AREA " + area : "LOCK AREA " + area,
                isChecked: () => _show.LockOn(area) is { Kind: "area" },
                // An armed show's own locks (mapping, layout) lift with SHOW DISARM, not UNLOCK.
                isEnabled: () => session.IsLive && _show.LockOn(area) is not { Implicit: true }));
        var minGo = DeskAction.Local("show.mingo", "Min GO gap", () => { });
        foreach (var ms in new[] { 0, 300, 1000 })
            minGo.Children.Add(DeskAction.Send(session, "show.mingo." + ms.ToString(CultureInfo.InvariantCulture), ms.ToString(CultureInfo.InvariantCulture) + " ms",
                () => "SHOW SET MINGO " + ms.ToString(CultureInfo.InvariantCulture) + " ARMED"));
        var rules = DeskAction.Local("show.rules", "Rules", () => { });
        rules.Children.Add(minGo);
        rules.Children.Add(DeskAction.Send(session, "show.remoteViewOnly", "Remote view only", () => "SHOW SET REMOTE-VIEW-ONLY " + (_show.RemoteViewOnly ? "OFF" : "ON"),
            isChecked: () => _show.RemoteViewOnly));
        rules.Children.Add(DeskAction.Send(session, "show.expectArmed", "Expect armed while playing", () => "SHOW SET EXPECT-ARMED " + (_show.ExpectArmed ? "OFF" : "ON"),
            isChecked: () => _show.ExpectArmed));
        rules.Children.Add(DeskAction.Local("show.escPanic", "Esc is panic (this desk)", () => prefs.EscIsPanic = !prefs.EscIsPanic, isChecked: () => prefs.EscIsPanic));
        ArmMenu =
        [
            Arm,
            DeskAction.Send(session, "show.armAll", "Arm all nodes", () => "SHOW ARM ALL").Look("arm", menuOnly: true, separator: true),
            areas.Look("lock", menuOnly: true),
            DeskAction.Send(session, "show.unlockAll", "Unlock all", () => "UNLOCK ALL").Look("unlock", menuOnly: true),
            DeskAction.Send(session, "show.blind", "Edit blind", () => "BLIND ON").Look("blind", menuOnly: true),
            rules.Look(menuOnly: true, separator: true),
        ];
        PanicMenu = [Panic, .. Outcomes.Select((o, i) => DeskAction.Send(session, "show.panic." + o, "Panic: " + o.ToLowerInvariant(), () => "PANIC " + o)
            .Look("panic", danger: true, menuOnly: true, separator: i == 0))];
        ReadyMenu =
        [
            Ready,
            DeskAction.Send(session, "show.preflightQuick", "Run preflight", () => "PREFLIGHT QUICK").Look("health", menuOnly: true, separator: true),
            DeskAction.Send(session, "show.preflightFull", "Full preflight", () => "PREFLIGHT").Look("health", menuOnly: true),
            DeskAction.Send(session, "show.fleetNext", "Next problem", () => "FLEET NEXT").Look("fleet", menuOnly: true),
        ];
    }

    public DeskAction Arm { get; }

    public DeskAction Panic { get; }

    public DeskAction Ready { get; }

    /// <summary>ARM's right-click: the Show menu.</summary>
    public IReadOnlyList<DeskAction> ArmMenu { get; }

    /// <summary>PANIC's right-click: the nine outcomes.</summary>
    public IReadOnlyList<DeskAction> PanicMenu { get; }

    /// <summary>The Ready light's right-click: the preflight runs and the next problem.</summary>
    public IReadOnlyList<DeskAction> ReadyMenu { get; }

    public bool IsPresent => _show.Present;

    public bool IsArmed => _show.Armed;

    public bool IsPanic => _show.PanicOutcome is not null;

    public ShowSection Show => _show;

    /// <summary>"Ready 4/5", "Checking…" while a preflight runs, "Ready" before the first.</summary>
    public string ReadyText => _show.PreflightRunning ? "Checking…" : _show.Ready.Length > 0 ? "Ready " + _show.Ready : "Ready";

    public string ReadyLight => _show.ReadyLight;

    public string ReadyTip => _show.FirstOpen.Length > 0 ? "First open: " + _show.FirstOpen + "\nClick: Health · right-click: run preflight" : "Click: Health · right-click: run preflight";

    /// <summary>ARM's tooltip: who armed and since when, and what the gate refused in the last minute.</summary>
    public string ArmTip => Arm.Tip + (_show.RefusedLastMinute > 0
        ? "\n" + _show.RefusedLastMinute.ToString(CultureInfo.InvariantCulture) + " refused in the last minute: " + _show.RefusedLast
        : "");

    public string PanicTip => Panic.Tip + "\nNever a dialog. A second press within the fade is HARD."
        + (_show.PanicOutputs.IsDefaultOrEmpty ? "" : "\n" + string.Join(" · ", _show.PanicOutputs.Select(o => o.Id + ": " + o.Outcome)));

    private string ArmLabel()
    {
        if (!_show.Armed)
            return "ARM";
        var by = _show.By.Length > 0 ? _show.By : "Desk";
        return _show.SinceUnixMs > 0 ? $"ARMED · {by} · {Clock(_show.SinceUnixMs, "HH:mm")}" : "ARMED · " + by;
    }

    /// <summary>A STATE stamp as this desk's local clock reads it.</summary>
    public static string Clock(long unixMs, string format) =>
        DateTimeOffset.FromUnixTimeMilliseconds(unixMs).ToLocalTime().ToString(format, CultureInfo.InvariantCulture);

    public void Update(ShowSection show)
    {
        if (Set(ref _show, show, nameof(Show)))
            Raise(nameof(IsPresent), nameof(IsArmed), nameof(IsPanic), nameof(ReadyText), nameof(ReadyLight), nameof(ReadyTip), nameof(ArmTip), nameof(PanicTip));
    }

    private (string Arm, string Panic) _tips = ("", "");

    /// <summary>Re-reads the strip's actions (after the gate's facts moved); ARM's and PANIC's tooltips carry their tips.</summary>
    public void RefreshActions()
    {
        ArmMenu.RefreshAll();
        PanicMenu.RefreshAll();
        ReadyMenu.RefreshAll();
        var tips = (ArmTip, PanicTip);
        if (tips == _tips)
            return;
        _tips = tips;
        Raise(nameof(ArmTip), nameof(PanicTip));
    }
}

/// <summary>The show package's module: the strip's slot; the panic banner (Clear with a fade), the rehearsal banner and the
/// mirror's banner on a hot backup or a fenced node; the gate (the fleet's door as Core's BackupGate decides it with the node's own
/// words from STATE.fleet.door, then the Core show policy for the desk's role, then the locks in STATE.show with LockGuard's words,
/// in the engine's order, so a control greys with the engine's own words before the press); Lock/Unlock in every surface,
/// projector, output, playlist and item menu with a padlock badge; F12 and Ctrl+Shift+A; and the Esc ladder's PANIC rung (PLAY
/// only, with the desk's preference, after the guard).</summary>
public sealed class ShowStripModule : DeskModule
{
    private static readonly HashSet<string> Lockable = new(StringComparer.Ordinal) { "surface", "projector", "output", "playlist", "item" };

    private readonly DeskSession _session;
    private readonly DeskPrefs _prefs;
    private readonly Func<bool> _inPlay;
    private readonly DeskAction _clear;
    private readonly DeskAction _clearMenu;
    private readonly DeskAction _armNow;
    private readonly ImmutableArray<DeskAction> _panicActions;
    private readonly ImmutableArray<DeskAction> _rehearsalActions;
    private readonly Dictionary<string, DeskAction> _locks = new(StringComparer.Ordinal);
    private ShowSection _show = ShowSection.Absent;
    private FleetDoor _door = FleetDoor.None;
    private Project? _project;
    private bool _airDark;

    /// <summary>The transport the desk's lines arrive on at the node: a client's, never one of the fleet's own parts.</summary>
    private const string DeskTransport = "desk";

    public ShowStripModule(DeskSession session, DeskPrefs prefs, Func<bool> inPlay, Action openHealth)
    {
        _session = session;
        _prefs = prefs;
        _inPlay = inPlay;
        Strip = new ShowStripViewModel(session, prefs, openHealth);
        _clear = DeskAction.Send(session, "panic.clear", "Clear", () => "PANIC CLEAR FADE 1000").Look("play");
        _clearMenu = DeskAction.Local("panic.clearFade", "Clear with a fade", () => { });
        foreach (var ms in new[] { 0, 1000, 2000, 5000 })
            _clearMenu.Children.Add(DeskAction.Send(session, "panic.clear." + ms.ToString(CultureInfo.InvariantCulture),
                ms == 0 ? "At once" : (ms / 1000).ToString(CultureInfo.InvariantCulture) + " s", () => "PANIC CLEAR FADE " + ms.ToString(CultureInfo.InvariantCulture)));
        _armNow = DeskAction.Send(session, "show.armBanner", "Arm", () => "SHOW ARM").Look("arm");
        // One array per banner for the module's life: a banner that has not changed compares equal, so a push during a
        // panic fade leaves its buttons (and an open fade menu) where they are.
        _panicActions = [_clear, _clearMenu];
        _rehearsalActions = [_armNow];
    }

    public ShowStripViewModel Strip { get; }

    public override object? Slot(string name) => name == "show" ? Strip : null;

    public override void OnState(StateSnapshot state)
    {
        ArgumentNullException.ThrowIfNull(state);
        _show = state.Section("show", ShowSection.Read);
        _door = state.Section("fleet", FleetDoor.Read);
        _airDark = state.Air.Blackout;
        Strip.Update(_show);
    }

    public override void RefreshActions()
    {
        Strip.RefreshActions();
        foreach (var a in _locks.Values)
            a.Refresh();
    }

    public override void OnProject(Project project)
    {
        _project = project;
        // Only objects that still exist keep their Lock entry.
        foreach (var id in _locks.Keys.Where(id => ShowEntities.Find(project, id) is null).ToList())
            _locks.Remove(id);
    }

    public override IEnumerable<Banner> Banners(StateSnapshot state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return BannersOf(state);
    }

    private IEnumerable<Banner> BannersOf(StateSnapshot state)
    {
        var show = state.Section("show", ShowSection.Read);
        if (show.PanicOutcome is { } outcome)
        {
            var since = show.PanicSinceUnixMs > 0 ? " since " + ShowStripViewModel.Clock(show.PanicSinceUnixMs, "HH:mm:ss") : "";
            var by = show.PanicBy.Length > 0 ? " by " + show.PanicBy : "";
            var outputs = show.PanicOutputs.IsDefaultOrEmpty ? "" : " — " + string.Join(" · ", show.PanicOutputs.Select(o => o.Id + ": " + o.Outcome));
            yield return new Banner("panic", "red", $"PANIC {(show.PanicHard ? "hard " : "")}{outcome}{since}{by}{outputs}", _panicActions, 0);
        }
        if (show.Present && show.ExpectArmed && !show.Armed && state.Playlists.Any(p => p.IsPlaying))
            yield return new Banner("rehearsal", "amber", "rehearsal while playing — SHOW ARM before doors", _rehearsalActions, 40);
        // A hot backup's or a fenced node's show is another node's: its edits are refused with these words (FAILOVER is on its card).
        if (state.Section("fleet", FleetDoor.Read) is { Words: { } door } d)
            yield return new Banner("fleet", d.Role == FleetRoles.Fenced ? "red" : "amber", door, [], 20);
    }

    public override IEnumerable<Shortcut> Shortcuts() =>
    [
        new("F12", "PANIC (a second press within the fade is HARD; never a dialog)", "Show", Key.F12, KeyModifiers.None, Strip.Panic.Command) { WhileTyping = true },
        new("Ctrl+Shift+A", "Arm or disarm the show (SHOW ARM / SHOW DISARM)", "Show", Key.A, KeyModifiers.Control | KeyModifiers.Shift, Strip.Arm.Command) { WhileTyping = true },
    ];

    /// <summary>The engine's doors in the engine's order, each with the node's own words. The router's: an unpaired session's line
    /// that would change something is refused (WHOAMI's paired). The fleet's: on a hot backup or a fenced node (STATE.fleet.door) a
    /// line that would change the show is refused, while queries, panic, Show Mode, the node's administration and the fleet's verbs
    /// (FAILOVER among them) stay. The show's: while a blind draft is open another show is not opened; then the show policy's
    /// decision for this desk's role (WHOAMI) with the facts in STATE.show, the draft's owner among them (STATE.show.blind names its
    /// session: this desk's document edits then go to its draft, armed or not, refused only while its commit waits); a network line
    /// meets the network area's lock. Then the locks as the engine's LockGuard holds them: a generic edit (SET, ADD, REMOVE, MOVE)
    /// is put on the mirrored project and LockGuard decides by what it changes, the armed show's implicit areas left out for a
    /// draft's edit as the draft's guard leaves them; any other line by the area its class edits and the entity it names first.
    /// Playback, the air, panic, commands to devices and reading are never held by a lock.</summary>
    public override GateRefusal? Gate(WireLine line, VerbSpec spec)
    {
        ArgumentNullException.ThrowIfNull(line);
        ArgumentNullException.ThrowIfNull(spec);
        if (!_session.Paired && (spec.Mutates || spec.PairedOnly))
            return new GateRefusal(PlayMap.Net.Commands.WireWords.NotPaired, "not paired");
        if (_door.Words is { } door && BackupGate.Refused(spec, line, DeskTransport, _door.Role, door) is { } mirrored)
            return new GateRefusal(mirrored, _door.Role == FleetRoles.Fenced ? "fenced" : "mirror");
        var draft = false;
        if (_show.Present)
        {
            if (_show.Blind is { } open && BlindWords.OpensAnotherShow(spec))
                return new GateRefusal(BlindWords.DraftOpen(open.Owner, open.Changes), "blind draft");
            var role = Enum.TryParse<ShowRole>(_session.Role, ignoreCase: true, out var r) ? r : ShowRole.Remote;
            var facts = new ShowFacts(_show.Armed, OwnsDraft, _airDark, _show.RemoteViewOnly, _show.DisarmFrom);
            var decision = ShowPolicy.Decide(spec, line, role, facts);
            switch (decision.Verdict)
            {
                case ShowVerdict.Refuse:
                    return new GateRefusal(decision.Words ?? "refused by Show Mode", _show.Armed ? "armed" : "show");
                case ShowVerdict.BlindOnly when _show.Blind is { CommitWhen: { } when }:
                    return new GateRefusal(BlindWords.Landing(when), "blind draft");
                case ShowVerdict.BlindOnly:
                    draft = true;
                    break;
                case ShowVerdict.Allow when ShowPolicy.ClassOf(spec, line) == ShowClass.Network
                    && _show.ExplicitLocks.FirstOrDefault(l => l.Kind == LockKind.Area && l.Target == LockAreas.Network) is { } held:
                    return new GateRefusal(LockGuard.AreaWords(held), "locked");
            }
        }
        return LockWords(line, spec, draft) is { } words ? new GateRefusal(words, "locked") : null;
    }

    /// <summary>True when STATE.show.blind names this desk's own session as the draft's owner.</summary>
    private bool OwnsDraft => _show.Blind is { } b && _session.SessionId.Length > 0
        && string.Equals(b.Session, _session.SessionId, StringComparison.Ordinal) && string.Equals(b.Transport, _session.Transport, StringComparison.Ordinal);

    /// <summary>LockGuard's words for a line's edit, or null. A draft's edit meets the show's own locks only (its guard is not armed).</summary>
    private string? LockWords(WireLine line, VerbSpec spec, bool draft)
    {
        if (!spec.Mutates || spec.Name is "LOCK" or "UNLOCK")
            return null;
        var locks = _show.ExplicitLocks;
        var armed = _show.Armed && !draft;
        // Nothing to hold an edit: no lock, not armed, no surface or projector with its own Locked flag.
        if (locks.IsEmpty && !armed && _project is { } open && !open.Stage.Surfaces.Any(s => s.Locked) && !open.Stage.Projectors.Any(j => j.Locked))
            return null;
        if (_project is { } p)
        {
            var before = p.Show.Locks.SequenceEqual(locks) ? p : p with { Show = p.Show with { Locks = locks } };
            if (Edited(before, spec, line) is { } after)
                return LockGuard.Check(before, after, armed);
        }
        if (AreaOf(ShowPolicy.ClassOf(spec, line)) is not { } area)
            return null;
        if (locks.FirstOrDefault(l => l.Kind == LockKind.Area && l.Target == area) is { } al)
            return LockGuard.AreaWords(al);
        if (armed && LockGuard.ArmedAreas.Contains(area))
            return LockGuard.ArmedWords(area);
        var words = spec.Name.Split(' ').Length;
        if (line.Count <= words)
            return null;
        var target = line.Word(words);
        if (locks.FirstOrDefault(l => l.Kind == LockKind.Entity && l.Target == target) is { } el && _project is { } q && ShowEntities.Find(q, target) is { } entity)
            return LockGuard.EntityWords(el, entity);
        if (_project is { } f)
        {
            if (f.Stage.Surfaces.Find(target) is { Locked: true } sf)
                return LockGuard.FlagWords(sf);
            if (f.Stage.Projectors.Find(target) is { Locked: true } pj)
                return LockGuard.FlagWords(pj);
        }
        return null;
    }

    /// <summary>The lock area an edit of a class falls in: the document's areas LockGuard compares, and the node's links
    /// (the engine's door holds the network area). Device commands are not document edits, so no area holds them.</summary>
    private static string? AreaOf(ShowClass cls) => cls switch
    {
        ShowClass.Mapping => LockAreas.Mapping,
        ShowClass.Layout => LockAreas.Layout,
        ShowClass.Content => LockAreas.Content,
        ShowClass.Playlists => LockAreas.Playlists,
        ShowClass.Media => LockAreas.Media,
        ShowClass.Network => LockAreas.Network,
        _ => null,
    };

    public override IEnumerable<DeskAction> MenuExtras(IMenuTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (!_show.Present || !Lockable.Contains(target.Kind))
            return [];
        var id = target.Id;
        if (!_locks.TryGetValue(id, out var action))
        {
            action = DeskAction.Send(_session, "show.lock", "Lock for the show", () => _show.LockOn(id) is { Kind: "entity" } ? "UNLOCK " + id : "LOCK " + id,
                () => _show.LockOn(id) is { Kind: "entity" } ? "Unlock" : "Lock for the show", isChecked: () => _show.LockOn(id) is { Kind: "entity" }).Look("lock");
            _locks[id] = action;
        }
        return [action];
    }

    public override IEnumerable<ObjectBadge> Badges(IMenuTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (_show.LockOn(target.Id) is not { Kind: "entity" } l)
            return [];
        var words = _project is { } p && ShowEntities.Find(p, l.Target) is { } entity
            ? LockGuard.EntityWords(l.AsCore(), entity)
            : $"{l.Target} is locked by {l.By} — UNLOCK {l.Target} first";
        return [new ObjectBadge("lock", "lock", "", "amber", words)];
    }

    /// <summary>The project a generic edit would leave (the engine's edit as the desk's demo engine applies it), or null when the
    /// line is not one, or the desk cannot apply it (the node then says why).</summary>
    private static Project? Edited(Project p, VerbSpec spec, WireLine line)
    {
        try
        {
            return spec.Name switch
            {
                "SET" when line.Count >= 4 => ProjectEdits.SetField(p, line.Word(1), line.Word(2), line.RestAfter(3)),
                "REMOVE" when line.Count == 2 => ProjectEdits.Remove(p, line.Word(1)),
                "MOVE" when line.Count == 3 && int.TryParse(line.Word(2), NumberStyles.Integer, CultureInfo.InvariantCulture, out var index) =>
                    ProjectEdits.Move(p, line.Word(1), index),
                "ADD" when line.Count >= 3 && line.RestAfter(2).StartsWith('{') => ProjectEdits.Add(p, line.Word(1), line.RestAfter(2), out _),
                _ => null,
            };
        }
        catch (Exception ex) when (ex is EditRefusedException or ArgumentException or InvalidOperationException or FormatException or JsonException)
        {
            // An edit the desk cannot put on its mirror is decided by the class's area below; the node says what it makes of it.
            return null;
        }
    }

    /// <summary>Esc's PANIC rung: in PLAY, with the desk's preference on, on a node with Show Mode.</summary>
    public override bool TryEscape()
    {
        if (!_prefs.EscIsPanic || !_inPlay() || !_show.Present || !_session.IsLive)
            return false;
        Strip.Panic.Command.Execute(null);
        return true;
    }

    public override int EscapeOrder => EscapeLadder.Panic;
}
