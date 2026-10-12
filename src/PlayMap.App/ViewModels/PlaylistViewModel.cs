using System.Collections.ObjectModel;
using System.Globalization;
using PlayMap.App.Infrastructure;
using PlayMap.App.Link;
using PlayMap.App.State;
using PlayMap.Core.Model;

namespace PlayMap.App.ViewModels;

/// <summary>One playlist as a card: its items, its own transport, loop and sync, and one action list that
/// is both the card's toolbar and its right-click menu.</summary>
public sealed class PlaylistViewModel : ObservableObject
{
    private readonly DeskSession _session;
    private readonly PlayViewModel _play;
    private PlaylistState? _state;
    private string _name = "";
    private bool _loop;
    private string? _syncGroup;
    private bool _isFocused;
    private string _summary = "";
    private PlaylistItemViewModel? _selectedItem;

    public PlaylistViewModel(DeskSession session, Playlist list, Project? project, PlayViewModel play)
    {
        _session = session;
        _play = play;
        Id = list.Id;
        Transport = new TransportViewModel(session, () => _state, master: false);
        Loop = DeskAction.Send(session, "loop", "Loop the list", () => $"PLAYLIST LOOP {Id} {(_loop ? "OFF" : "ON")}", isChecked: () => _loop).Look("loop");
        Focus = DeskAction.Send(session, "focus", "Focus (the top bar's transport)", () => "PLAYLIST FOCUS " + Id,
            isEnabled: () => !_isFocused).Look("cue", menuOnly: true, separator: true);
        Sync = DeskAction.Local("sync", "Sync group", () => { }).Look("sync", menuOnly: true);
        Sync.Children.Add(DeskAction.Send(session, "sync.show", "show", () => $"PLAYLIST SYNC {Id} show", isChecked: () => _syncGroup == "show"));
        Sync.Children.Add(DeskAction.Send(session, "sync.b", "group b", () => $"PLAYLIST SYNC {Id} b", isChecked: () => _syncGroup == "b"));
        Sync.Children.Add(DeskAction.Send(session, "sync.off", "No group", () => $"PLAYLIST SYNC {Id} OFF", isChecked: () => _syncGroup is null));
        Rename = DeskAction.Local("rename", "Rename…", () => play.Selected = this).Look("info", menuOnly: true);
        Delete = DeskAction.Send(session, "delete", "Delete playlist", () => "PLAYLIST DELETE " + Id).Look("trash", danger: true, menuOnly: true, separator: true);
        Actions = [.. Transport.Actions, Loop, Focus, Sync, Rename, Delete];
        Update(list, project);
    }

    public string Id { get; }

    public TransportViewModel Transport { get; }

    public DeskAction Loop { get; }

    public DeskAction Focus { get; }

    public DeskAction Sync { get; }

    public DeskAction Rename { get; }

    public DeskAction Delete { get; }

    /// <summary>Everything the card can do: its toolbar (non-menu-only) and its right-click menu (all).</summary>
    public IReadOnlyList<DeskAction> Actions { get; }

    public IReadOnlyList<DeskAction> Toolbar => Actions.Toolbar();

    public ObservableCollection<PlaylistItemViewModel> Items { get; } = [];

    /// <summary>The card's selected item (the inspector shows it).</summary>
    public PlaylistItemViewModel? SelectedItem
    {
        get => _selectedItem;
        set
        {
            if (!Set(ref _selectedItem, value))
                return;
            if (value is not null)
                _play.Selected = value;
        }
    }

    /// <summary>Shows the playlist itself in the inspector.</summary>
    public RelayCommand Inspect => _inspect ??= new RelayCommand(() => _play.Selected = this);

    private RelayCommand? _inspect;

    public string Name
    {
        get => _name;
        private set => Set(ref _name, value);
    }

    public bool IsLooping
    {
        get => _loop;
        private set => Set(ref _loop, value);
    }

    public string? SyncGroup
    {
        get => _syncGroup;
        private set
        {
            if (Set(ref _syncGroup, value))
                Raise(nameof(SyncText));
        }
    }

    public string SyncText => _syncGroup is null ? "" : "sync · " + _syncGroup;

    public bool IsFocused
    {
        get => _isFocused;
        private set => Set(ref _isFocused, value);
    }

    /// <summary>"3 items · 9:50".</summary>
    public string Summary
    {
        get => _summary;
        private set => Set(ref _summary, value);
    }

    public void Update(Playlist list, Project? project)
    {
        Name = list.Name;
        IsLooping = list.Loop;
        SyncGroup = list.SyncGroup;
        Items.SyncById(list.Items, i => i.Id, vm => vm.Id, i => new PlaylistItemViewModel(_session, Id, i, project, _play, Sync),
            (vm, i) => vm.Update(i, project));
        long total = 0;
        foreach (var i in list.Items)
            total += PlaylistItemViewModel.LengthNs(i, project);
        Summary = string.Create(CultureInfo.InvariantCulture, $"{list.Items.Length} item(s){(total > 0 ? " · " + VerbText.Clock(total / 1_000_000) : "")}");
        Actions.RefreshAll();
    }

    public void OnState(StateSnapshot s)
    {
        _state = s.FindPlaylist(Id);
        IsFocused = _state?.Focused ?? false;
        Transport.Update();
        var current = _state is { IsIdle: false } st ? st.Item?.Id : null;
        var next = _state?.Next?.Id;
        foreach (var item in Items)
            item.SetPlayState(item.Id == current, item.Id == next, item.Id == current ? Transport.Progress : 0, _state?.State ?? "empty");
        Actions.RefreshAll();
    }

    public void Tick()
    {
        if (!Transport.IsRunning)
            return;
        Transport.Advance();
        foreach (var item in Items)
            if (item.IsCurrent)
                item.SetPlayState(true, false, Transport.Progress, "playing");
    }
}

/// <summary>One item in a playlist card.</summary>
public sealed class PlaylistItemViewModel : ObservableObject
{
    private string _label = "";
    private string _detail = "";
    private string _icon = "media";
    private bool _isCurrent;
    private bool _isNext;
    private double _progress;
    private string _playState = "";
    private EndAction _end;

    public PlaylistItemViewModel(DeskSession session, string playlistId, PlaylistItem item, Project? project, PlayViewModel play, DeskAction playlistSync)
    {
        Id = item.Id;
        PlaylistId = playlistId;
        Actions =
        [
            DeskAction.Send(session, "go", "Go", () => $"PLAYLIST GO {PlaylistId} {Id}").Look("play"),
            DeskAction.Send(session, "cue", "Cue", () => $"PLAYLIST CUE {PlaylistId} {Id}").Look("cue"),
            DeskAction.Send(session, "loop", "Loop this item", () => VerbText.SetEnum(Id, "end", _end == EndAction.Loop ? EndAction.Next : EndAction.Loop),
                isChecked: () => _end == EndAction.Loop).Look("loop"),
            playlistSync,
            DeskAction.Local("edit", "Edit…", () => play.Selected = this).Look("info", separator: true),
            DeskAction.Send(session, "remove", "Remove from playlist", () => $"PLAYLIST REMOVE {PlaylistId} {Id}").Look("trash", danger: true, separator: true),
        ];
        Update(item, project);
    }

    public string Id { get; }

    public string PlaylistId { get; }

    public IReadOnlyList<DeskAction> Actions { get; }

    public string Label
    {
        get => _label;
        private set => Set(ref _label, value);
    }

    public string Detail
    {
        get => _detail;
        private set => Set(ref _detail, value);
    }

    public string Icon
    {
        get => _icon;
        private set => Set(ref _icon, value);
    }

    public bool IsCurrent
    {
        get => _isCurrent;
        private set => Set(ref _isCurrent, value);
    }

    public bool IsNext
    {
        get => _isNext;
        private set => Set(ref _isNext, value);
    }

    public double Progress
    {
        get => _progress;
        private set => Set(ref _progress, value);
    }

    /// <summary>"playing", "paused", "cued", "ended" on the current item; "" otherwise.</summary>
    public string PlayState
    {
        get => _playState;
        private set => Set(ref _playState, value);
    }

    public static long LengthNs(PlaylistItem i, Project? project)
    {
        if (i.OutNs > 0)
            return i.OutNs - i.InNs;
        if (i.DurationNs > 0)
            return i.DurationNs;
        return i.Source.Kind == SourceKind.Media && project?.FindMedia(i.Source.Id) is { } m ? Math.Max(0, m.Info.DurationNs - i.InNs) : 0;
    }

    public void Update(PlaylistItem item, Project? project)
    {
        Label = item.Label;
        _end = item.End;
        Icon = item.Source.Kind switch
        {
            SourceKind.Generator => "generator",
            SourceKind.Canvas => "outputs",
            _ => "media",
        };
        var len = LengthNs(item, project);
        var parts = new List<string> { len > 0 ? VerbText.Clock(len / 1_000_000) : "until told" };
        if (item.End != EndAction.Next)
            parts.Add(item.End.ToString().ToLowerInvariant());
        if (item.Transition != TransitionKind.Cut)
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"{item.Transition.ToString().ToLowerInvariant()} {item.TransitionNs / 1e9:0.#} s"));
        if (Math.Abs(item.Rate - 1) > 1e-9)
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"×{item.Rate:0.##}"));
        Detail = string.Join(" · ", parts);
        Actions.RefreshAll();
    }

    public void SetPlayState(bool current, bool next, double progress, string state)
    {
        IsCurrent = current;
        IsNext = next;
        Progress = current ? progress : 0;
        PlayState = current ? state : next ? "next" : "";
    }
}
