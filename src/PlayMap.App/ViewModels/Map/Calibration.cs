using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using PlayMap.App.Infrastructure;
using PlayMap.App.Link;
using PlayMap.App.State;

namespace PlayMap.App.ViewModels.Map;

/// <summary>One kept result of a target as CALIBRATION HISTORY lists it: its id, name, when, from what, its score, how far it moved
/// the picture, and whether it is the current, the fallback or the staged one.</summary>
public sealed record HistoryEntry(string Id, string Name, long AtUnixMs, string Source, double Score, string Grade, string Words, string Who,
    double DeltaWallPx, bool Current, bool Fallback, bool Staged, bool Pinned)
{
    /// <summary>The entries of a CALIBRATION HISTORY reply, newest first (empty when the text is not one).</summary>
    public static ImmutableArray<HistoryEntry> Read(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        try
        {
            using var doc = JsonDocument.Parse(json);
            return [.. doc.RootElement.Arr("snapshots").Select(s => new HistoryEntry(s.Str("id"), s.Str("name"), s.Long("at"), s.Str("source"),
                s.Num("score", -1), s.Str("grade"), s.Str("words"), s.Str("who"), s.Obj("delta") is { } d ? d.Num("wallPx", -1) : -1,
                s.Bool("current"), s.Bool("fallback"), s.Bool("staged"), s.Bool("pinned"))).Where(e => e.Id.Length > 0)];
        }
        catch (JsonException)
        {
            // Not a history reply: no entries to draw.
            return [];
        }
    }
}

/// <summary>A dot of the history strip: one kept result, its words in its tooltip, and its menu — Recall (CALIBRATION RECALL &lt;cs&gt;),
/// Set as fallback (CALIBRATION ACCEPT &lt;cs&gt;), Forget (CALIBRATION FORGET &lt;cs&gt;) — as the node allows them: the current result
/// is not recalled; only the current or the staged result becomes the fallback (any other is recalled first), and never the one
/// that is the fallback already; the current, fallback and staged ones are never forgotten.</summary>
public sealed class HistoryDotViewModel : ObservableObject
{
    public HistoryDotViewModel(DeskSession session, HistoryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        Entry = entry;
        Recall = DeskAction.Send(session, "calibration.recall", "Recall", () => "CALIBRATION RECALL " + entry.Id, isEnabled: () => session.IsLive).Look("undo");
        SetFallback = DeskAction.Send(session, "calibration.setFallback", "Set as fallback", () => "CALIBRATION ACCEPT " + entry.Id,
            isEnabled: () => session.IsLive).Look("save");
        Forget = DeskAction.Send(session, "calibration.forget", "Forget", () => "CALIBRATION FORGET " + entry.Id, isEnabled: () => session.IsLive)
            .Look("trash", danger: true, separator: true);
        var actions = new List<DeskAction>();
        if (!entry.Current)
            actions.Add(Recall);
        if (!entry.Fallback && (entry.Current || entry.Staged))
            actions.Add(SetFallback);
        if (!entry.Current && !entry.Fallback && !entry.Staged)
            actions.Add(Forget);
        Dot = DeskAction.Local("calibration.dot", Label(entry), () => { }).Look(menuOnly: true);
        foreach (var a in actions)
            Dot.Children.Add(a);
        Actions = actions;
    }

    public HistoryEntry Entry { get; }

    public string Id => Entry.Id;

    public DeskAction Recall { get; }

    public DeskAction SetFallback { get; }

    public DeskAction Forget { get; }

    /// <summary>The dot as a menu button: its label, the entries under it.</summary>
    public DeskAction Dot { get; }

    /// <summary>The entries the node allows on this result (its right-click menu).</summary>
    public IReadOnlyList<DeskAction> Actions { get; }

    /// <summary>"● A 0.2 · 21:04" — filled current, ringed fallback, hollow staged.</summary>
    private static string Label(HistoryEntry e)
    {
        var mark = e.Current ? "●" : e.Fallback ? "◉" : e.Staged ? "○" : "·";
        var score = e.Score < 0 ? "" : " " + e.Score.ToString("0.##", CultureInfo.InvariantCulture);
        var at = e.AtUnixMs > 0 ? " · " + Show.ShowStripViewModel.Clock(e.AtUnixMs, "HH:mm") : "";
        return $"{mark} {e.Grade}{score}{at}";
    }

    /// <summary>The dot's tooltip: its name, words, who and how far it moved the picture.</summary>
    public string Tip => string.Create(CultureInfo.InvariantCulture,
        $"{Entry.Name}{(Entry.Pinned ? " (pinned)" : "")} — {Entry.Words}\n{Entry.Source} by {Entry.Who}{(Entry.DeltaWallPx >= 0 ? $" · moved the picture {Entry.DeltaWallPx:0.##} px" : "")}");
}

/// <summary>A calibration target's score card (a projector's or an output's, from STATE.calibration.targets): the grade, score and
/// words, the confidence and the verified error, every term of the score as a status row whose Fix sends the term's own fix line,
/// the target's lines (accept a staged result while one waits, re-solve, accept as fallback, revert to the fallback, save — each
/// greyed with the node's reason while STATE.calibration and the history say the node would refuse it), and the history strip read
/// with CALIBRATION HISTORY &lt;target&gt; while the card is shown (Health ▸ Calibration, or an inspector holding it) and again when the
/// target's result count or current result moves.</summary>
public sealed class ScoreCardViewModel : ObservableObject, IMenuTarget, IInspectorSection
{
    private readonly DeskSession _session;
    private readonly Dictionary<string, (DeskAction Action, string Line)> _fixes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HistoryDotViewModel> _dots = new(StringComparer.Ordinal);
    private readonly IReadOnlyList<DeskAction> _lines;
    private readonly IReadOnlyList<DeskAction> _linesStaged;
    private readonly IReadOnlyList<DeskAction> _toolbar;
    private readonly IReadOnlyList<DeskAction> _toolbarStaged;
    private string _light = "grey";
    private string _name = "";
    private string _grade = "";
    private string _score = "";
    private string _words = "";
    private string _detail = "";
    private string? _staged;
    private string? _current;
    private string? _fallback;
    private string? _historyKey;
    private string _fetched = "";
    private Task _fetch = Task.CompletedTask;
    private bool _inTab;
    private bool _inInspector;
    private bool _failed;

    public ScoreCardViewModel(DeskSession session, string id, string kind)
    {
        _session = session;
        Id = id;
        TargetKind = kind;
        var lines = new List<DeskAction>();
        if (kind == "projector")
            lines.Add(DeskAction.Send(session, "calibration.resolve", "Re-solve", () => "CALIBRATION RESOLVE " + id, isEnabled: () => session.IsLive).Look("solve"));
        lines.Add(DeskAction.Send(session, "calibration.accept", "Accept as fallback", () => "CALIBRATION ACCEPT " + id,
            isEnabled: () => session.IsLive && WhyNotAccept() is null, whyNot: WhyNotAccept).Look("save"));
        lines.Add(DeskAction.Send(session, "calibration.fallback", "Revert to fallback", () => "CALIBRATION FALLBACK " + id,
            isEnabled: () => session.IsLive && WhyNotFallback() is null, whyNot: WhyNotFallback).Look("undo"));
        lines.Add(DeskAction.Send(session, "calibration.save", "Save snapshot", () => "CALIBRATION SAVE " + id,
            isEnabled: () => session.IsLive && WhyNotSave() is null, whyNot: WhyNotSave).Look("save", menuOnly: true));
        AcceptStaged = DeskAction.Send(session, "calibration.acceptStaged", "Accept the staged result", () => _staged is { } s ? "CALIBRATION ACCEPT " + s : null,
            isEnabled: () => session.IsLive && _staged is not null).Look("play");
        _lines = lines;
        _linesStaged = [AcceptStaged, .. lines];
        _toolbar = _lines.Toolbar();
        _toolbarStaged = _linesStaged.Toolbar();
    }

    /// <summary>Why the node would refuse CALIBRATION ACCEPT &lt;target&gt; (its own words), or null: with nothing staged, a current result
    /// that is the fallback already, or an output with no result.</summary>
    private string? WhyNotAccept() => _staged is not null ? null
        : _current is { } cur && cur == _fallback ? $"{Name}'s current result is its fallback already"
        : _current is null && TargetKind == "output" ? $"{Name} has no result to accept"
        : null;

    /// <summary>Why the node would refuse CALIBRATION FALLBACK &lt;target&gt;, or null: no fallback, or the target stands on it already.</summary>
    private string? WhyNotFallback() => _fallback is not { } fb ? $"{Name} has no fallback — CALIBRATION ACCEPT {Id} makes its current calibration the fallback"
        : fb == _current ? $"{Name} stands as {NameOf(fb)} ({fb}) already"
        : null;

    /// <summary>Why the node would refuse CALIBRATION SAVE &lt;target&gt;, or null: an output with no result, or a current result that is
    /// kept (pinned) already, as the history says.</summary>
    private string? WhyNotSave()
    {
        if (_current is not { } cur)
            return TargetKind == "output" ? $"{Name} has no result to keep" : null;
        if (!_dots.TryGetValue(cur, out var dot) || !dot.Entry.Pinned)
            return null;
        return TargetKind == "output" ? $"{Name}'s result {cur} is kept already" : $"{Name} is kept already as {dot.Entry.Name} ({cur})";
    }

    private string NameOf(string snapshot) => _dots.TryGetValue(snapshot, out var d) && d.Entry.Name.Length > 0 ? d.Entry.Name : snapshot;

    /// <summary>The target's id (a projector's or an output's).</summary>
    public string Id { get; }

    /// <summary>"projector" or "output".</summary>
    public string TargetKind { get; }

    public string Light
    {
        get => _light;
        private set => Set(ref _light, value);
    }

    public string Name
    {
        get => _name;
        private set => Set(ref _name, value);
    }

    /// <summary>The grade letter ("A"), or "—" with no result.</summary>
    public string Grade
    {
        get => _grade;
        private set => Set(ref _grade, value);
    }

    /// <summary>"0.2 px", or "" with no result.</summary>
    public string Score
    {
        get => _score;
        private set => Set(ref _score, value);
    }

    public string Words
    {
        get => _words;
        private set => Set(ref _words, value);
    }

    /// <summary>"confidence 100 % · verified 0.31 px · 7 results".</summary>
    public string Detail
    {
        get => _detail;
        private set => Set(ref _detail, value);
    }

    /// <summary>The target's lines (its right-click menu and its toolbar, the menu-only ones left off the toolbar), the staged
    /// result's Accept first while one is staged. The same instances from push to push.</summary>
    public IReadOnlyList<DeskAction> Actions => _staged is null ? _lines : _linesStaged;

    public IReadOnlyList<DeskAction> Toolbar => _staged is null ? _toolbar : _toolbarStaged;

    IReadOnlyList<DeskAction> IMenuTarget.MenuActions => Actions;

    /// <summary>CALIBRATION ACCEPT &lt;the staged result&gt;, on the card and in its menu while one is staged.</summary>
    public DeskAction AcceptStaged { get; }

    public bool HasStaged => _staged is not null;

    /// <summary>The score's terms, each a status row with its Fix.</summary>
    public ObservableCollection<StatusRow> Terms { get; } = [];

    /// <summary>Every term's Fix, in order.</summary>
    public IEnumerable<DeskAction> Fixes => Terms.Select(t => t.Fix).OfType<DeskAction>();

    /// <summary>The history strip, newest first.</summary>
    public ObservableCollection<HistoryDotViewModel> History { get; } = [];

    public bool HasHistory => History.Count > 0;

    /// <summary>The CALIBRATION HISTORY read in flight (tests wait on it).</summary>
    public Task Fetching => _fetch;

    /// <summary>The card is on screen — on Health ▸ Calibration or in an inspector: its history is read when it is not the one shown.</summary>
    public bool IsShown => _inTab || _inInspector;

    /// <summary>Health ▸ Calibration shows the card.</summary>
    public bool InTab
    {
        get => _inTab;
        set => Shown(ref _inTab, value);
    }

    /// <summary>A projector's or an output's inspector holds the card.</summary>
    public bool InInspector
    {
        get => _inInspector;
        set => Shown(ref _inInspector, value);
    }

    private void Shown(ref bool field, bool value)
    {
        if (field == value)
            return;
        var was = IsShown;
        field = value;
        if (was == IsShown)
            return;
        Raise(nameof(IsShown));
        if (!IsShown)
            return;
        if (_failed)
        {
            _failed = false;
            _fetched = "";
        }
        FetchIfNew();
    }

    /// <summary>Takes the target's block of STATE.calibration.targets.</summary>
    public void Update(JsonElement t)
    {
        Name = t.Str("name", Id);
        var score = t.Num("score", -1);
        Grade = t.Str("grade") is { Length: > 0 } g ? g : "—";
        Score = score < 0 ? "" : score.ToString("0.###", CultureInfo.InvariantCulture) + " " + t.Str("unit", "px");
        Light = CardReader.LightWord(t.Str("light", "grey"));
        Words = t.Str("words");
        var confidence = t.Num("confidence", -1);
        var verified = t.Num("verifiedP95", -1);
        var count = t.Long("history");
        Detail = string.Join(" · ", new[]
        {
            confidence < 0 ? "" : string.Create(CultureInfo.InvariantCulture, $"confidence {confidence * 100:0} %"),
            verified < 0 ? "" : string.Create(CultureInfo.InvariantCulture, $"verified {verified:0.##} px"),
            string.Create(CultureInfo.InvariantCulture, $"{count} result{(count == 1 ? "" : "s")}"),
        }.Where(x => x.Length > 0));
        var staged = t.StrOrNull("staged");
        _current = t.StrOrNull("snapshot");
        _fallback = t.StrOrNull("fallback");
        if (staged != _staged)
        {
            var hadStaged = _staged is not null;
            _staged = staged;
            Raise(nameof(HasStaged));
            if (hadStaged != (staged is not null))
                Raise(nameof(Actions), nameof(Toolbar));
        }
        var rows = new List<StatusRow>();
        var i = 0;
        foreach (var term in t.Arr("terms"))
        {
            var key = term.Str("code") + "#" + i.ToString(CultureInfo.InvariantCulture);
            var row = Terms.FirstOrDefault(r => r.Id == key) ?? new StatusRow(key);
            var hint = term.Str("hint");
            var value = term.Num("value", double.NaN);
            row.Update(CardReader.LightWord(term.Str("light", "grey")), term.Str("code"),
                double.IsNaN(value) ? "" : value.ToString("0.###", CultureInfo.InvariantCulture) + " " + term.Str("unit"),
                term.Str("words") + (hint.Length > 0 ? " — " + hint : ""), FixFor(key, term.Str("fix")));
            rows.Add(row);
            i++;
        }
        foreach (var k in _fixes.Keys.Where(k => rows.All(r => r.Id != k)).ToList())
            _fixes.Remove(k);
        if (!rows.SequenceEqual(Terms))
        {
            Terms.Clear();
            foreach (var r in rows)
                Terms.Add(r);
        }
        _historyKey = count.ToString(CultureInfo.InvariantCulture) + "/" + t.Str("snapshot") + "/" + t.Str("fallback") + "/" + (staged ?? "");
        if (IsShown)
            FetchIfNew();
    }

    private DeskAction? FixFor(string key, string line)
    {
        if (line.Length == 0)
        {
            _fixes.Remove(key);
            return null;
        }
        if (_fixes.TryGetValue(key, out var had) && had.Line == line)
            return had.Action;
        var action = DeskAction.Send(_session, "calibration.fix", "Fix", () => line, isEnabled: () => _session.IsLive).Look("play");
        _fixes[key] = (action, line);
        return action;
    }

    public void RefreshActions()
    {
        _linesStaged.RefreshAll();
        foreach (var (a, _) in _fixes.Values)
            a.Refresh();
        foreach (var d in History)
            d.Actions.RefreshAll();
    }

    private void FetchIfNew()
    {
        if (_historyKey is not { } key || key == _fetched || !_session.IsLive)
            return;
        _fetched = key;
        _fetch = FetchAsync();
    }

    private async Task FetchAsync()
    {
        var reply = await _session.SendQuietAsync("CALIBRATION HISTORY " + Id);
        if (!reply.Ok)
        {
            // Read again when the card is next shown or the target's results move, not on every push.
            _failed = true;
            return;
        }
        var entries = HistoryEntry.Read(reply.Text);
        var dots = entries.Select(e => _dots.TryGetValue(e.Id, out var d) && d.Entry == e ? d : new HistoryDotViewModel(_session, e)).ToList();
        _dots.Clear();
        foreach (var d in dots)
            _dots[d.Id] = d;
        History.Clear();
        foreach (var d in dots)
            History.Add(d);
        Raise(nameof(HasHistory));
        // Save and Revert read the kept results' pins and names.
        _linesStaged.RefreshAll();
    }
}

/// <summary>Health ▸ Calibration: a score card per projector and output STATE.calibration names, the running or last calibration task
/// in the node's words, and the projectors a check found moved.</summary>
public sealed class CalibrationTabViewModel : ObservableObject
{
    private string _task = "";
    private string _moved = "";
    private bool _isShown;

    /// <summary>The cards, in STATE's order.</summary>
    public ObservableCollection<ScoreCardViewModel> Cards { get; } = [];

    public bool IsEmpty => Cards.Count == 0;

    public string EmptyWords => "No calibration results on this node yet — a projector solve or CALIBRATION SAVE makes one.";

    /// <summary>The calibration task's words ("Projector 2 staged: C 3.8 — …"), "" when none ran.</summary>
    public string TaskWords
    {
        get => _task;
        set => Set(ref _task, value);
    }

    /// <summary>"Moved: Projector 2" when a check found projectors moved.</summary>
    public string Moved
    {
        get => _moved;
        set => Set(ref _moved, value);
    }

    /// <summary>The tab is on screen: every card reads its history.</summary>
    public bool IsShown
    {
        get => _isShown;
        set
        {
            if (!Set(ref _isShown, value))
                return;
            foreach (var c in Cards)
                c.InTab = value;
        }
    }

    internal void Raised() => Raise(nameof(IsEmpty));
}

/// <summary>The calibration package's module: Health ▸ Calibration, and a projector's score card in its inspector (the same card).</summary>
public sealed class CalibrationModule : DeskModule
{
    private readonly DeskSession _session;
    private readonly DrawerHostViewModel _drawers;
    private readonly Dictionary<string, ScoreCardViewModel> _cards = new(StringComparer.Ordinal);

    public CalibrationModule(DeskSession session, DrawerHostViewModel drawers)
    {
        ArgumentNullException.ThrowIfNull(drawers);
        _session = session;
        _drawers = drawers;
        drawers.Opened += _ => Shown();
        drawers.Health.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(DrawerViewModel.Selected))
                Shown();
        };
    }

    public CalibrationTabViewModel Tab { get; } = new();

    private void Shown() => Tab.IsShown = _drawers.Open == _drawers.Health && ReferenceEquals(_drawers.Health.Selected?.Content, Tab);

    public override IEnumerable<DrawerTab> DrawerTabs() => [new DrawerTab("health", "calibration", "Calibration", Tab, 20)];

    public override void OnState(StateSnapshot state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var calibration = state.Root.ValueKind == JsonValueKind.Object && state.Root.TryGetProperty("calibration", out var c) && c.ValueKind == JsonValueKind.Object ? c : default;
        var want = new List<ScoreCardViewModel>();
        if (calibration.ValueKind == JsonValueKind.Object)
        {
            foreach (var t in calibration.Arr("targets"))
            {
                var id = t.Str("id");
                if (id.Length == 0)
                    continue;
                if (!_cards.TryGetValue(id, out var card))
                {
                    card = new ScoreCardViewModel(_session, id, t.Str("kind", "projector")) { InTab = Tab.IsShown };
                    _cards[id] = card;
                }
                card.Update(t);
                want.Add(card);
            }
            Tab.TaskWords = calibration.Obj("task") is { } task ? task.Str("words") : "";
            var moved = calibration.Arr("moved").Select(m => m.ValueKind == JsonValueKind.String ? m.GetString() ?? "" : "").Where(m => m.Length > 0).ToList();
            Tab.Moved = moved.Count == 0 ? "" : "Moved: " + string.Join(", ", moved.Select(m => _cards.TryGetValue(m, out var k) ? k.Name : m));
        }
        else
        {
            Tab.TaskWords = "";
            Tab.Moved = "";
        }
        foreach (var id in _cards.Keys.Where(id => want.All(w => w.Id != id)).ToList())
            _cards.Remove(id);
        if (!want.SequenceEqual(Tab.Cards))
        {
            Tab.Cards.Clear();
            foreach (var w in want)
                Tab.Cards.Add(w);
            Tab.Raised();
        }
    }

    public override void RefreshActions()
    {
        foreach (var c in Tab.Cards)
            c.RefreshActions();
    }

    public override IEnumerable<object> InspectorSections(IMenuTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return target.Kind is "projector" or "output" && _cards.TryGetValue(target.Id, out var card) ? [card] : [];
    }
}
