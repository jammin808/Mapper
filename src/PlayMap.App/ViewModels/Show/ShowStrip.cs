using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Avalonia.Input;
using PlayMap.App.Infrastructure;
using PlayMap.App.Link;
using PlayMap.App.State;
using PlayMap.Core.Model;
using PlayMap.Core.Show;
using PlayMap.Core.Wire;

namespace PlayMap.App.ViewModels.Show;

/// <summary>One lock as STATE.show.locks has it.</summary>
public sealed record ShowLockState(string Target, string Kind, string By, string Words, bool Implicit);

/// <summary>One output's part of a panic.</summary>
public sealed record PanicOutputState(string Id, string Outcome, double Level);

/// <summary>STATE.show as the desk reads it (absent: the node has no Show Mode, and the strip hides).</summary>
public sealed record ShowSection(bool Present, bool Armed, string By, long SinceUnixMs, string? PanicOutcome, string PanicBy,
    long PanicSinceUnixMs, bool PanicHard, ImmutableArray<PanicOutputState> PanicOutputs, ImmutableArray<ShowLockState> Locks,
    bool RemoteViewOnly, string DisarmFrom, bool ExpectArmed, int RefusedLastMinute, string RefusedLast, string ReadyLight,
    string Ready, string FirstOpen, bool PreflightRunning)
{
    public static readonly ShowSection Absent = new(false, false, "", 0, null, "", 0, false, [], [], false, "paired", false, 0, "", "grey", "", "", false);

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
            [.. s.Arr("locks").Select(l => new ShowLockState(l.Str("target"), l.Str("kind"), l.Str("by"), l.Str("words"), l.Bool("implicit")))],
            rules?.Bool("remoteViewOnly") ?? false, rules?.Str("disarmFrom", "paired") ?? "paired", rules?.Bool("expectArmed") ?? false,
            (int)(refused?.Long("last60s") ?? 0), refused?.Str("last") ?? "",
            preflight?.Str("light", "grey") ?? "grey", preflight?.Str("ready") ?? "", preflight?.Str("firstOpen") ?? "", preflight?.Bool("running") ?? false);
    }

    /// <summary>The lock on an entity or area, or null.</summary>
    public ShowLockState? LockOn(string target) => Locks.FirstOrDefault(l => string.Equals(l.Target, target, StringComparison.Ordinal));
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
            areas.Children.Add(DeskAction.Send(session, "show.lockArea." + area, area, () => "LOCK AREA " + area,
                isChecked: () => _show.LockOn(area) is { Kind: "area" }));
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
        ArmMenu.RefreshAll();
        PanicMenu.RefreshAll();
        ReadyMenu.RefreshAll();
        Raise(nameof(ArmTip), nameof(PanicTip));
    }
}

/// <summary>The show package's module: the strip's slot; the panic banner (Clear with a fade) and the rehearsal banner;
/// the gate (the Core show policy for the desk's role and the locks in STATE.show, so a control greys with the engine's
/// own words before the press); Lock/Unlock in every surface, projector, output, playlist and item menu with a padlock
/// badge; F12 and Ctrl+Shift+A; and the Esc ladder's PANIC rung (PLAY only, with the desk's preference, after the guard).</summary>
public sealed class ShowStripModule : DeskModule
{
    private static readonly HashSet<string> Lockable = new(StringComparer.Ordinal) { "surface", "projector", "output", "playlist", "item" };

    private readonly DeskSession _session;
    private readonly DeskPrefs _prefs;
    private readonly Func<bool> _inPlay;
    private readonly DeskAction _clear;
    private readonly DeskAction _clearMenu;
    private readonly DeskAction _armNow;
    private readonly Dictionary<string, DeskAction> _locks = new(StringComparer.Ordinal);
    private ShowSection _show = ShowSection.Absent;

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
    }

    public ShowStripViewModel Strip { get; }

    public override object? Slot(string name) => name == "show" ? Strip : null;

    public override void OnState(StateSnapshot state)
    {
        ArgumentNullException.ThrowIfNull(state);
        _show = state.Section("show", ShowSection.Read);
        Strip.Update(_show);
        foreach (var a in _locks.Values)
            a.Refresh();
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
            yield return new Banner("panic", "red", $"PANIC {(show.PanicHard ? "hard " : "")}{outcome}{since}{by}{outputs}", [_clear, _clearMenu], 0);
        }
        if (show.Present && show.ExpectArmed && !show.Armed && state.Playlists.Any(p => p.IsPlaying))
            yield return new Banner("rehearsal", "amber", "rehearsal while playing — SHOW ARM before doors", [_armNow], 40);
    }

    public override IEnumerable<Shortcut> Shortcuts() =>
    [
        new("F12", "PANIC (a second press within the fade is HARD; never a dialog)", "Show", Key.F12, KeyModifiers.None, Strip.Panic.Command) { WhileTyping = true },
        new("Ctrl+Shift+A", "Arm or disarm the show (SHOW ARM / SHOW DISARM)", "Show", Key.A, KeyModifiers.Control | KeyModifiers.Shift, Strip.Arm.Command) { WhileTyping = true },
    ];

    /// <summary>The engine's own decision for the desk (role Operator) with the facts in STATE.show, then the locks: an
    /// edit that touches a locked area or a locked entity is refused with the lock's words.</summary>
    public override string? Gate(WireLine line, VerbSpec spec)
    {
        ArgumentNullException.ThrowIfNull(line);
        ArgumentNullException.ThrowIfNull(spec);
        if (!_show.Present)
            return null;
        var facts = new ShowFacts(_show.Armed, false, false, _show.RemoteViewOnly, _show.DisarmFrom);
        var decision = ShowPolicy.Decide(spec, line, ShowRole.Operator, facts);
        if (decision.Verdict == ShowVerdict.Refuse)
            return decision.Words;
        // Locks hold edits of the document only: playback, the air, panic and reading go on (LockGuard's rule).
        if (!spec.Mutates || spec.Name is "LOCK" or "UNLOCK" || AreaOf(ShowPolicy.ClassOf(spec, line)) is not { } area)
            return null;
        if (_show.LockOn(area) is { Kind: "area" } al)
            return $"Locked: {area} is locked{(al.By.Length > 0 ? " by " + al.By : "")}{(al.Words.Length > 0 ? " ('" + al.Words + "')" : "")}. UNLOCK AREA {area} first.";
        for (var i = 1; i < line.Count; i++)
        {
            if (_show.LockOn(line.Word(i)) is { Kind: "entity" } el)
                return $"Locked: {el.Target} is locked{(el.By.Length > 0 ? " by " + el.By : "")}{(el.Words.Length > 0 ? " ('" + el.Words + "')" : "")}. UNLOCK {el.Target} first.";
        }
        return null;
    }

    /// <summary>The lock area an edit of a class falls in (null: none, as LockGuard reads the areas).</summary>
    private static string? AreaOf(ShowClass cls) => cls switch
    {
        ShowClass.Mapping => LockAreas.Mapping,
        ShowClass.Layout => LockAreas.Layout,
        ShowClass.Content => LockAreas.Content,
        ShowClass.Playlists => LockAreas.Playlists,
        ShowClass.Media => LockAreas.Media,
        ShowClass.DeviceSafe or ShowClass.DevicePower => LockAreas.Devices,
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
        return [new ObjectBadge("lock", "lock", "", "amber", $"Locked{(l.By.Length > 0 ? " by " + l.By : "")}{(l.Words.Length > 0 ? ": '" + l.Words + "'" : "")} — UNLOCK {l.Target} to change it")];
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
