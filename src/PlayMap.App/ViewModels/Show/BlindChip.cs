using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Text.Json;
using PlayMap.App.Infrastructure;
using PlayMap.App.Link;
using PlayMap.App.State;
using PlayMap.Core.Show;

namespace PlayMap.App.ViewModels.Show;

/// <summary>One entity a blind draft changes, as BLIND DIFF words it: its label and kind, what happens to it, its fields in words,
/// and whether it also changed on the air since the draft began (a conflict).</summary>
public sealed record BlindChange(string Id, string Kind, string Label, string Op, ImmutableArray<string> Words, bool Conflict)
{
    /// <summary>"Left projector (output) changed".</summary>
    public string Title => $"{Label} ({Kind}) {Op}{(Conflict ? " — changed on the air too" : "")}";

    public string Lines => string.Join("\n", Words);

    public bool Equals(BlindChange? other) =>
        other is not null && (Id, Kind, Label, Op, Conflict) == (other.Id, other.Kind, other.Label, other.Op, other.Conflict)
        && Words.AsSpan().SequenceEqual(other.Words.AsSpan());

    public override int GetHashCode() => HashCode.Combine(Id, Op, Words.Length);

    /// <summary>The changes of a BLIND DIFF reply (empty when the text is not one).</summary>
    public static ImmutableArray<BlindChange> Read(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var conflicts = root.Arr("conflicts").Where(c => c.ValueKind == JsonValueKind.String).Select(c => c.GetString()!).ToHashSet(StringComparer.Ordinal);
            return [.. root.Arr("changes").Select(c => new BlindChange(c.Str("id"), c.Str("kind"), c.Str("label", c.Str("id")), c.Str("op"),
                [.. c.Arr("words").Where(w => w.ValueKind == JsonValueKind.String).Select(w => w.GetString()!)], conflicts.Contains(c.Str("id"))))];
        }
        catch (JsonException)
        {
            // Not a diff: no rows to show.
            return [];
        }
    }
}

/// <summary>The show strip's blind chip, shown while a blind draft is open (STATE.show.blind): "BLIND 3", and while its commit waits,
/// the boundary in the node's words. A press opens the blind panel: the draft's changes in field words (BLIND DIFF, read when the
/// panel opens and again when the draft's change count moves), Commit now (BLIND COMMIT NOW), Commit at the next item (BLIND COMMIT
/// NEXT, the focused playlist's), Discard (BLIND DISCARD) — with FORCE added to a commit while the diff names conflicts — and the
/// draft view: MAP's 3D view drawing this desk's draft instead of the air (VIEW … SHOW editor DRAFT), offered only while the draft is
/// this desk's.</summary>
public sealed class BlindChipViewModel : ObservableObject
{
    private readonly DeskSession _session;
    private readonly Func<bool> _draftShown;
    private readonly Action<bool> _showDraft;
    private BlindState? _blind;
    private bool _owns;
    private bool _ownerKnown = true;
    private bool _isOpen;
    private int _fetchedChanges = -1;
    private Task _fetch = Task.CompletedTask;
    private string _diffWords = "";

    public BlindChipViewModel(DeskSession session, Func<bool> draftShown, Action<bool> showDraft)
    {
        _session = session;
        _draftShown = draftShown;
        _showDraft = showDraft;
        Toggle = new RelayCommand(() => IsOpen = !IsOpen, () => _blind is not null);
        Diff = DeskAction.Run(session, "blind.diff", "Diff", _ => FetchDiffAsync(), () => "BLIND DIFF", isEnabled: () => session.IsLive && _blind is not null).Look("blind");
        CommitNow = DeskAction.Send(session, "blind.commitNow", "Commit now", () => _blind is null ? null : "BLIND COMMIT NOW" + Force,
            () => HasConflicts ? "Commit now anyway (FORCE)" : "Commit now", isEnabled: CanCommit).Look("play", danger: true);
        CommitNext = DeskAction.Send(session, "blind.commitNext", "Commit at the next item", () => _blind is null ? null : "BLIND COMMIT NEXT" + Force,
            () => HasConflicts ? "Commit at the next item anyway (FORCE)" : "Commit at the next item", isEnabled: CanCommit).Look("next");
        Discard = DeskAction.Send(session, "blind.discard", "Discard", () => _blind is null ? null : "BLIND DISCARD",
            isEnabled: () => session.IsLive && _blind is not null).Look("trash", danger: true, separator: true);
        DraftView = DeskAction.Local("blind.draftView", "Show the draft in MAP", () => _showDraft(!_draftShown()), isChecked: _draftShown,
            isEnabled: () => _owns).Look("eye", menuOnly: true);
        Actions = [Diff, CommitNow, CommitNext, Discard, DraftView];
    }

    private bool CanCommit() => _session.IsLive && _blind is { CommitWhen: null };

    private string Force => HasConflicts ? " FORCE" : "";

    /// <summary>Opens or closes the panel.</summary>
    public RelayCommand Toggle { get; }

    public DeskAction Diff { get; }

    public DeskAction CommitNow { get; }

    public DeskAction CommitNext { get; }

    public DeskAction Discard { get; }

    /// <summary>MAP's 3D view draws this desk's draft (a desk-local choice; the view's line carries DRAFT).</summary>
    public DeskAction DraftView { get; }

    /// <summary>The panel's buttons (the chip's right-click menu is the same list).</summary>
    public IReadOnlyList<DeskAction> Actions { get; }

    public IReadOnlyList<DeskAction> Toolbar => Actions.Toolbar();

    /// <summary>The draft's changes as the last BLIND DIFF worded them.</summary>
    public ObservableCollection<BlindChange> Changes { get; } = [];

    public bool HasConflicts => Changes.Any(c => c.Conflict);

    /// <summary>The read of the diff in flight (tests wait on it).</summary>
    public Task Fetching => _fetch;

    public bool IsPresent => _blind is not null;

    /// <summary>True when the draft is this desk's own session's.</summary>
    public bool OwnsDraft => _owns;

    /// <summary>"BLIND 3".</summary>
    public string Text => _blind is { } b ? "BLIND " + b.Changes.ToString(System.Globalization.CultureInfo.InvariantCulture) : "";

    /// <summary>"Blind editing by Desk: 3 changes staged · lands with the next GO on Show".</summary>
    public string Words => _blind is { } b
        ? $"Blind editing by {(b.Owner.Length > 0 ? b.Owner : "a session")}{(_owns ? " (this desk)" : "")}: {BlindWords.Changes(b.Changes)} staged{(b.CommitWhen is { } w ? " · lands " + w : "")}"
        : "";

    /// <summary>The diff's own words when it could not be read ("not blind — …"), else "".</summary>
    public string DiffWords
    {
        get => _diffWords;
        private set => Set(ref _diffWords, value);
    }

    public bool IsOpen
    {
        get => _isOpen;
        set
        {
            if (!Set(ref _isOpen, value))
                return;
            if (value && _blind is not null)
                _fetch = FetchDiffAsync();
        }
    }

    /// <summary>Takes STATE.show.blind and whether it names this desk's session; <paramref name="ownerKnown"/> is false while the node
    /// has not yet said which session is this desk's on the link (a new link, before WHOAMI), and a draft view shown is kept until it has:
    /// the new link is asked for it with every other view, and it goes once the node says the draft is not this desk's.</summary>
    public void Update(BlindState? blind, bool owns, bool ownerKnown = true)
    {
        var had = _blind;
        if (blind == had && owns == _owns && ownerKnown == _ownerKnown)
            return;
        _blind = blind;
        _owns = owns;
        _ownerKnown = ownerKnown;
        if (blind is null)
        {
            IsOpen = false;
            Changes.Clear();
            _fetchedChanges = -1;
            if (_draftShown())
                _showDraft(false);
        }
        else if (!owns && ownerKnown && _draftShown())
            _showDraft(false);
        else if (_isOpen && blind.Changes != _fetchedChanges)
            _fetch = FetchDiffAsync();
        Raise(nameof(IsPresent), nameof(OwnsDraft), nameof(Text), nameof(Words), nameof(HasConflicts));
        Toggle.RaiseCanExecuteChanged();
    }

    public void RefreshActions() => Actions.RefreshAll();

    private async Task FetchDiffAsync()
    {
        _fetchedChanges = _blind?.Changes ?? -1;
        var reply = await _session.SendQuietAsync("BLIND DIFF");
        if (!reply.Ok)
        {
            DiffWords = DeskSession.Plain(reply.Text);
            return;
        }
        DiffWords = "";
        var changes = BlindChange.Read(reply.Text);
        if (!changes.SequenceEqual(Changes))
        {
            Changes.Clear();
            foreach (var c in changes)
                Changes.Add(c);
        }
        Raise(nameof(HasConflicts));
        Actions.RefreshAll();
    }
}
