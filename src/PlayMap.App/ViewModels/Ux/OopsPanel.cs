using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using PlayMap.App.Infrastructure;
using PlayMap.App.Link;
using PlayMap.App.State;

namespace PlayMap.App.ViewModels.Ux;

/// <summary>One undo step as UNDO LIST answers it: its number, the edit's field words, who made it and when, and whether it is what UNDO
/// takes back next. "Undo to here" sends UNDO TO its number (that step and every newer one, as one action); it is the step's button and
/// its right-click menu.</summary>
public sealed class OopsStep : IMenuTarget
{
    public OopsStep(DeskSession session, long number, string label, string who, long atUnixMs, bool next)
    {
        Number = number;
        Label = label;
        Who = who;
        AtUnixMs = atUnixMs;
        IsNext = next;
        var n = number.ToString(CultureInfo.InvariantCulture);
        UndoTo = DeskAction.Send(session, "oops.undoTo." + n, next ? "Undo this" : "Undo to here", () => "UNDO TO " + n,
            isEnabled: () => session.IsLive).Look("undo");
        Actions = [UndoTo];
    }

    public long Number { get; }

    public string Label { get; }

    public string Who { get; }

    public long AtUnixMs { get; }

    /// <summary>The newest step: what UNDO takes back next.</summary>
    public bool IsNext { get; }

    /// <summary>"Desk · 20:31:05".</summary>
    public string Meta => (Who.Length > 0 ? Who : "the node") + (AtUnixMs > 0 ? " · " + Show.ShowStripViewModel.Clock(AtUnixMs, "HH:mm:ss") : "");

    public DeskAction UndoTo { get; }

    public IReadOnlyList<DeskAction> Actions { get; }

    public string Id => "undo:" + Number.ToString(CultureInfo.InvariantCulture);

    public IReadOnlyList<DeskAction> MenuActions => Actions;

    public bool Same(OopsStep other) => (Number, Label, Who, AtUnixMs, IsNext) == (other.Number, other.Label, other.Who, other.AtUnixMs, other.IsNext);
}

/// <summary>The Oops history: the undo steps newest first (UNDO LIST, read when the panel opens and again whenever the project, or this
/// desk's blind draft, moves while it is open), each with "Undo to here". For the blind owner the steps are its draft's, said in the
/// panel's head. Nothing here undoes anything by itself: every press is the catalogue's line.</summary>
public sealed class OopsPanelViewModel : ObservableObject
{
    /// <summary>How many steps the panel asks for.</summary>
    public const int Depth = 50;

    private readonly DeskSession _session;
    private bool _isOpen;
    private bool _isDraft;
    private string _words = "";
    private Task _fetch = Task.CompletedTask;
    private long _fetchedRevision = -1;
    private int _fetchedChanges = -1;

    public OopsPanelViewModel(DeskSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        _session = session;
        Toggle = DeskAction.Local("oops.toggle", "Oops history", () => IsOpen = !IsOpen, isChecked: () => IsOpen).Look("undo");
        Close = new RelayCommand(() => IsOpen = false);
    }

    /// <summary>Opens or closes the panel (Ctrl+Alt+Z, its button beside Undo, Undo's right-click).</summary>
    public DeskAction Toggle { get; }

    public RelayCommand Close { get; }

    public ObservableCollection<OopsStep> Steps { get; } = [];

    /// <summary>The read in flight (tests wait on it).</summary>
    public Task Fetching => _fetch;

    public bool IsOpen
    {
        get => _isOpen;
        set
        {
            if (!Set(ref _isOpen, value))
                return;
            Toggle.Refresh();
            if (value)
                Reload();
        }
    }

    /// <summary>True when the steps are this desk's blind draft's own.</summary>
    public bool IsDraft
    {
        get => _isDraft;
        private set => Set(ref _isDraft, value);
    }

    /// <summary>"Nothing to undo", the node's words when the list could not be read, or "".</summary>
    public string Words
    {
        get => _words;
        private set => Set(ref _words, value);
    }

    public string Title => IsDraft ? "Oops — this desk's blind draft" : "Oops — the show's edits";

    /// <summary>The project or this desk's draft moved: an open panel reads the list again.</summary>
    public void Moved(long revision, int draftChanges)
    {
        if (!_isOpen || (revision == _fetchedRevision && draftChanges == _fetchedChanges))
            return;
        Reload(revision, draftChanges);
    }

    public void RefreshActions()
    {
        Toggle.Refresh();
        foreach (var s in Steps)
            s.UndoTo.Refresh();
    }

    private void Reload(long? revision = null, int? draftChanges = null)
    {
        _fetchedRevision = revision ?? _session.ProjectRevision;
        _fetchedChanges = draftChanges ?? _fetchedChanges;
        _fetch = FetchAsync();
    }

    private async Task FetchAsync()
    {
        var reply = await _session.SendQuietAsync("UNDO LIST " + Depth.ToString(CultureInfo.InvariantCulture));
        if (!reply.Ok)
        {
            Steps.Clear();
            Words = DeskSession.Plain(reply.Text);
            return;
        }
        List<OopsStep> steps;
        try
        {
            using var doc = JsonDocument.Parse(reply.Text);
            var root = doc.RootElement;
            IsDraft = root.Bool("draft");
            steps = [.. root.Arr("steps").Select(s => new OopsStep(_session, s.Long("step"), s.Str("label"), s.Str("who"), s.Long("at"), s.Bool("next")))];
        }
        catch (JsonException)
        {
            Steps.Clear();
            Words = "The node's undo list could not be read.";
            return;
        }
        Raise(nameof(Title));
        Words = steps.Count == 0 ? "Nothing to undo" : "";
        if (steps.Count == Steps.Count && steps.Zip(Steps).All(p => p.First.Same(p.Second)))
            return;
        Steps.Clear();
        foreach (var s in steps)
            Steps.Add(s);
    }
}
