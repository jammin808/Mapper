using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using PlayMap.App.Infrastructure;
using PlayMap.App.Link;
using PlayMap.App.State;
using PlayMap.Core.Content;
using PlayMap.Core.Model;

namespace PlayMap.App.ViewModels;

/// <summary>PLAY: the media bin (files and generators), the outputs' previews, every playlist as a card with
/// its own transport, and the inspector for what is selected.</summary>
public sealed class PlayViewModel : ObservableObject
{
    private readonly DeskSession _session;
    private Project? _project;
    private string _mediaSummary = "";
    private int _binTab;
    private string _availabilityKey = "";
    private object? _selected;

    public PlayViewModel(DeskSession session)
    {
        _session = session;
        Inspector = new InspectorHost(session);
        NewPlaylist = DeskAction.Send(session, "newPlaylist", "New playlist",
            () => "PLAYLIST NEW " + VerbText.Word("Playlist " + ((_project?.Playlists.Length ?? 0) + 1).ToString(CultureInfo.InvariantCulture)),
            isEnabled: () => session.IsLive).Look("plus");
        FetchMissing = DeskAction.Send(session, "fetchAll", "Fetch missing files", () => "MEDIA FETCH ALL", isEnabled: () => session.IsLive).Look("fetch");
        foreach (var family in GeneratorCatalog.All.GroupBy(k => k.Family))
        {
            var group = DeskAction.Local("family." + family.Key, FamilyName(family.Key), () => { });
            foreach (var kind in family)
                group.Children.Add(DeskAction.Send(session, "new." + kind.Id, kind.Name, () => "GENERATOR NEW " + kind.Id + " " + VerbText.Word(kind.Name)));
            NewGenerator.Add(group);
        }
    }

    public InspectorHost Inspector { get; }

    public ObservableCollection<MediaItemViewModel> Media { get; } = [];

    public ObservableCollection<GeneratorItemViewModel> Generators { get; } = [];

    public ObservableCollection<PlaylistViewModel> Playlists { get; } = [];

    public ObservableCollection<OutputPreviewViewModel> Previews { get; } = [];

    /// <summary>"New generator" menu: one submenu per family, one item per kind (GENERATOR NEW).</summary>
    public ObservableCollection<DeskAction> NewGenerator { get; } = [];

    public DeskAction NewPlaylist { get; }

    public DeskAction FetchMissing { get; }

    public string MediaSummary
    {
        get => _mediaSummary;
        private set => Set(ref _mediaSummary, value);
    }

    /// <summary>0 = files, 1 = generators.</summary>
    public int BinTab
    {
        get => _binTab;
        set
        {
            if (Set(ref _binTab, value))
                Raise(nameof(IsFilesTab), nameof(IsGeneratorsTab));
        }
    }

    public bool IsFilesTab
    {
        get => _binTab == 0;
        set
        {
            if (value)
                BinTab = 0;
            else
                Raise();
        }
    }

    public bool IsGeneratorsTab
    {
        get => _binTab == 1;
        set
        {
            if (value)
                BinTab = 1;
            else
                Raise();
        }
    }

    /// <summary>The node the desk is on (preview rings are named after it).</summary>
    public string NodeId => _session.NodeId;

    /// <summary>The bin's selection (selecting here clears the other lists' selection).</summary>
    public MediaItemViewModel? SelectedMedia
    {
        get => _selected as MediaItemViewModel;
        set
        {
            if (value is not null || _selected is MediaItemViewModel)
                Selected = value;
        }
    }

    public GeneratorItemViewModel? SelectedGenerator
    {
        get => _selected as GeneratorItemViewModel;
        set
        {
            if (value is not null || _selected is GeneratorItemViewModel)
                Selected = value;
        }
    }

    /// <summary>What the inspector shows (a media item, a generator, a playlist or one of its items).</summary>
    public object? Selected
    {
        get => _selected;
        set
        {
            if (!Set(ref _selected, value))
                return;
            Raise(nameof(SelectedMedia), nameof(SelectedGenerator));
            foreach (var pl in Playlists)
                pl.SelectedItem = value as PlaylistItemViewModel;
            switch (value)
            {
                case MediaItemViewModel m:
                    Inspector.Show(new MediaInspector(_session, m.Id, this));
                    break;
                case GeneratorItemViewModel g:
                    Inspector.Show(new GeneratorInspector(_session, g.Id));
                    break;
                case PlaylistItemViewModel i:
                    Inspector.Show(new PlaylistItemInspector(_session, i.Id, i.PlaylistId));
                    break;
                case PlaylistViewModel p:
                    Inspector.Show(new PlaylistInspector(_session, p.Id));
                    break;
                default:
                    Inspector.Show(null);
                    break;
            }
            if (_project is { } project)
                Inspector.Refresh(project, _session.State);
        }
    }

    public string? FocusedPlaylistId => _session.State.FocusedPlaylist?.Id;

    private static string FamilyName(string family) => family switch
    {
        "pattern" => "Test patterns",
        "fractal" => "Fractals",
        "particles" => "Particles",
        "reactive" => "Reactive scenes",
        "effect" => "Surface effects",
        _ => family,
    };

    public void OnProject(Project p)
    {
        _project = p;
        Media.SyncById(p.Media, m => m.Id, vm => vm.Id, m => new MediaItemViewModel(_session, m, this), (vm, m) => vm.Update(m));
        Generators.SyncById(p.Generators, g => g.Id, vm => vm.Id, g => new GeneratorItemViewModel(_session, g, this), (vm, g) => vm.Update(g));
        Playlists.SyncById(p.Playlists, l => l.Id, vm => vm.Id, l => new PlaylistViewModel(_session, l, p, this), (vm, l) => vm.Update(l, p));
        Previews.SyncById(p.Outputs, o => o.Id, vm => vm.Id, o => new OutputPreviewViewModel(_session, o), (vm, o) => vm.Update(o));
        Raise(nameof(NodeId));
        foreach (var m in Media)
            m.UpdatePlaylists(p.Playlists);
        foreach (var g in Generators)
            g.UpdatePlaylists(p.Playlists);
        Inspector.Refresh(p, _session.State);
        if (_selected is not null && !StillThere(_selected))
            Selected = null;
    }

    private bool StillThere(object selected) => selected switch
    {
        MediaItemViewModel m => Media.Contains(m),
        GeneratorItemViewModel g => Generators.Contains(g),
        PlaylistViewModel l => Playlists.Contains(l),
        PlaylistItemViewModel i => Playlists.Any(l => l.Items.Contains(i)),
        _ => false,
    };

    public void OnState(StateSnapshot s)
    {
        foreach (var pl in Playlists)
            pl.OnState(s);
        foreach (var pv in Previews)
            pv.OnState(s);
        var media = s.Media;
        MediaSummary = media.Items == 0
            ? "No media yet — drop files here"
            : string.Create(CultureInfo.InvariantCulture, $"{media.Items} item(s){(media.Missing > 0 ? $" · {media.Missing} missing" : "")}{(media.Transfers.Length > 0 ? $" · {media.Transfers.Length} copying" : "")}");
        var key = string.Create(CultureInfo.InvariantCulture, $"{media.Items}/{media.Missing}/{media.Transfers.Length}/{_session.ProjectRevision}");
        if (key != _availabilityKey)
        {
            _availabilityKey = key;
            _ = RefreshAvailabilityAsync();
        }
        foreach (var t in media.Transfers)
            Media.FirstOrDefault(m => m.Id == t.MediaId)?.SetAvailability("fetching", t.Progress);
        FetchMissing.Refresh();
        NewPlaylist.Refresh();
        if (_project is { } p)
            Inspector.Refresh(p, s);
    }

    public void Tick()
    {
        foreach (var pl in Playlists)
            pl.Tick();
    }

    /// <summary>Asks the engine where each library item stands (MEDIA LIST) and lights the bin's dots.</summary>
    public async Task RefreshAvailabilityAsync(CancellationToken ct = default)
    {
        var reply = await _session.SendQuietAsync("MEDIA LIST", ct);
        if (!reply.Ok || reply.Text.Length == 0 || reply.Text[0] != '[')
            return;
        try
        {
            using var doc = JsonDocument.Parse(reply.Text);
            foreach (var e in doc.RootElement.EnumerateArray())
            {
                var id = e.Str("id");
                Media.FirstOrDefault(m => m.Id == id)?.SetAvailability(e.Str("state", "ready"), e.Num("progress", 1));
            }
        }
        catch (JsonException)
        {
            // An engine that answers MEDIA LIST in another shape leaves the dots grey (unknown stays unknown).
        }
    }

    /// <summary>Puts a line on the status strip (a view's own failure, never an engine refusal).</summary>
    public void Report(string text) => _session.Notify(ToastKind.Fault, text);

    /// <summary>Files dropped on the bin: one MEDIA IMPORT each.</summary>
    public async Task ImportFilesAsync(IEnumerable<string> paths, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        foreach (var path in paths)
            await _session.SendAsync("MEDIA IMPORT " + VerbText.Word(path), ct);
    }

    /// <summary>A source dropped on (or sent to) a playlist: PLAYLIST ADD.</summary>
    public Task AddToPlaylistAsync(string playlistId, SourceRef source, CancellationToken ct = default) =>
        _session.SendAsync($"PLAYLIST ADD {playlistId} {source}", ct);

    /// <summary>An item dragged to a new place: PLAYLIST MOVE.</summary>
    public Task MoveItemAsync(string playlistId, string itemId, int index, CancellationToken ct = default) =>
        _session.SendAsync($"PLAYLIST MOVE {playlistId} {itemId} {index.ToString(CultureInfo.InvariantCulture)}", ct);
}

/// <summary>One library item in the bin: its kind, length, where it stands on this node (the dot), and its
/// right-click menu.</summary>
public sealed class MediaItemViewModel : ObservableObject, IMenuTarget
{
    IReadOnlyList<DeskAction> IMenuTarget.MenuActions => Actions;

    private readonly DeskSession _session;
    private readonly PlayViewModel _play;
    private string _name = "";
    private string _detail = "";
    private string _icon = "media";
    private string _availability = "unknown";
    private double _progress = 1;

    public MediaItemViewModel(DeskSession session, MediaItem m, PlayViewModel play)
    {
        _session = session;
        _play = play;
        Id = m.Id;
        AddTo = DeskAction.Local("addTo", "Add to playlist", () => { }).Look("plus");
        Actions =
        [
            DeskAction.Send(session, "addFocused", "Add to focused playlist",
                () => _play.FocusedPlaylistId is { } pl ? $"PLAYLIST ADD {pl} media:{Id}" : null).Look("plus"),
            AddTo,
            DeskAction.Local("info", "Show info", () => _play.Selected = this).Look("info", separator: true),
            DeskAction.Send(session, "fetch", "Fetch from a peer", () => "MEDIA FETCH " + Id, isEnabled: () => Availability == "missing").Look("fetch"),
            DeskAction.Send(session, "remove", "Remove from library", () => "MEDIA REMOVE " + Id).Look("trash", danger: true, separator: true),
        ];
        Update(m);
    }

    public string Id { get; }

    public DeskAction AddTo { get; }

    public IReadOnlyList<DeskAction> Actions { get; }

    public string Name
    {
        get => _name;
        private set => Set(ref _name, value);
    }

    /// <summary>"3:30 · 1920×1080 · h264".</summary>
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

    /// <summary>"ready", "missing", "fetching", "unplayable", "unknown".</summary>
    public string Availability
    {
        get => _availability;
        private set
        {
            if (Set(ref _availability, value))
            {
                Raise(nameof(AvailabilityTip));
                Actions.RefreshAll();
            }
        }
    }

    public double Progress
    {
        get => _progress;
        private set
        {
            if (Set(ref _progress, value))
                Raise(nameof(AvailabilityTip));
        }
    }

    public string AvailabilityTip => _availability switch
    {
        "ready" => "Here and playable",
        "missing" => "Not on this node — Fetch copies it from a peer that has it",
        "fetching" => string.Create(CultureInfo.InvariantCulture, $"Copying from a peer · {_progress * 100:0} %"),
        "unplayable" => "Here, but this node cannot decode it",
        _ => "Not checked yet",
    };

    public void SetAvailability(string state, double progress)
    {
        Availability = state;
        Progress = progress;
    }

    public void Update(MediaItem m)
    {
        Name = m.Name;
        Icon = m.Kind switch
        {
            MediaKind.Image or MediaKind.ImageSequence => "image",
            MediaKind.Audio => "audio",
            MediaKind.Web or MediaKind.Embed or MediaKind.Stream => "network",
            _ => "media",
        };
        var parts = new List<string>();
        if (m.Info.DurationNs > 0)
            parts.Add(VerbText.Clock(m.Info.DurationNs / 1_000_000));
        if (m.Info.Width > 0)
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"{m.Info.Width}×{m.Info.Height}"));
        if (m.Info.VideoCodec.Length > 0)
            parts.Add(m.Info.VideoCodec);
        else if (m.Kind is MediaKind.Image)
            parts.Add("still");
        else if (m.Kind is MediaKind.Audio)
            parts.Add("audio");
        else if (m.Kind is MediaKind.Web or MediaKind.Embed or MediaKind.Stream)
            parts.Add(m.Kind.ToString().ToLowerInvariant());
        Detail = string.Join(" · ", parts);
    }

    public void UpdatePlaylists(IReadOnlyList<Playlist> playlists)
    {
        AddTo.Children.SyncById(playlists.ToList(), p => p.Id, a => a.Id[3..], p =>
            DeskAction.Send(_session, "to." + p.Id, p.Name, () => $"PLAYLIST ADD {p.Id} media:{Id}"), (_, _) => { });
        Raise(nameof(Actions));
    }
}

/// <summary>A generator in the bin's second tab.</summary>
public sealed class GeneratorItemViewModel : ObservableObject, IMenuTarget
{
    IReadOnlyList<DeskAction> IMenuTarget.MenuActions => Actions;

    private readonly DeskSession _session;
    private string _name = "";
    private string _kindName = "";

    public GeneratorItemViewModel(DeskSession session, Generator g, PlayViewModel play)
    {
        _session = session;
        Id = g.Id;
        AddTo = DeskAction.Local("addTo", "Add to playlist", () => { }).Look("plus");
        Actions =
        [
            DeskAction.Send(session, "addFocused", "Add to focused playlist",
                () => play.FocusedPlaylistId is { } pl ? $"PLAYLIST ADD {pl} generator:{Id}" : null).Look("plus"),
            AddTo,
            DeskAction.Local("edit", "Edit parameters", () => play.Selected = this).Look("generator", separator: true),
            DeskAction.Send(session, "remove", "Remove", () => "REMOVE " + Id).Look("trash", danger: true, separator: true),
        ];
        Update(g);
    }

    public string Id { get; }

    public DeskAction AddTo { get; }

    public IReadOnlyList<DeskAction> Actions { get; }

    public string Name
    {
        get => _name;
        private set => Set(ref _name, value);
    }

    public string KindName
    {
        get => _kindName;
        private set => Set(ref _kindName, value);
    }

    public void Update(Generator g)
    {
        Name = g.Name;
        KindName = GeneratorCatalog.Find(g.Kind)?.Name ?? g.Kind;
    }

    public void UpdatePlaylists(IReadOnlyList<Playlist> playlists) =>
        AddTo.Children.SyncById(playlists.ToList(), p => p.Id, a => a.Id[3..], p =>
            DeskAction.Send(_session, "to." + p.Id, p.Name, () => $"PLAYLIST ADD {p.Id} generator:{Id}"), (_, _) => { });
}

/// <summary>An output's preview tile in PLAY (the engine's picture of it when a preview ring is up).</summary>
public sealed class OutputPreviewViewModel : ObservableObject, IMenuTarget
{
    IReadOnlyList<DeskAction> IMenuTarget.MenuActions => Actions;

    private string _name = "";
    private string _light = "grey";
    private string _detail = "";
    private bool _enabled;
    private string? _pattern;

    public OutputPreviewViewModel(DeskSession session, Output o)
    {
        Id = o.Id;
        var patterns = DeskAction.Local("pattern", "Test pattern", () => { }).Look("pattern");
        patterns.Children.Add(DeskAction.Send(session, "p.off", "Off", () => $"PATTERN OFF ON {Id}"));
        foreach (var k in GeneratorCatalog.All.Where(k => k.Family == "pattern"))
            patterns.Children.Add(DeskAction.Send(session, "p." + k.Id, k.Name, () => $"PATTERN {k.Id[8..]} ON {Id}"));
        Actions =
        [
            DeskAction.Send(session, "enable", "Open", () => $"OUTPUT ENABLE {Id} {(_enabled ? "OFF" : "ON")}", () => _enabled ? "Close output" : "Open output",
                isChecked: () => _enabled).Look("eye", danger: true),
            DeskAction.Send(session, "identify", "Identify", () => "OUTPUT IDENTIFY " + Id).Look("identify"),
            patterns,
        ];
        Update(o);
    }

    public IReadOnlyList<DeskAction> Actions { get; }

    public string Id { get; }

    /// <summary>The preview view this tile asks the engine for.</summary>
    public string ViewId => "op-" + Id;

    public string Name
    {
        get => _name;
        private set => Set(ref _name, value);
    }

    public string Light
    {
        get => _light;
        private set => Set(ref _light, value);
    }

    public string Detail
    {
        get => _detail;
        private set => Set(ref _detail, value);
    }

    public bool Enabled
    {
        get => _enabled;
        private set => Set(ref _enabled, value);
    }

    public string? Pattern
    {
        get => _pattern;
        private set => Set(ref _pattern, value);
    }

    public double Aspect { get; private set; } = 16 / 9.0;

    public void Update(Output o)
    {
        Name = o.Name;
        Enabled = o.Enabled;
        Actions.RefreshAll();
        Aspect = o.Height > 0 ? o.Width / (double)o.Height : 16 / 9.0;
        Detail = string.Create(CultureInfo.InvariantCulture, $"{o.Width}×{o.Height} · {o.Mode.ToString().ToLowerInvariant()}");
    }

    public void OnState(StateSnapshot s)
    {
        if (s.FindOutput(Id) is { } o)
        {
            Light = o.Light;
            Pattern = o.Pattern;
            Detail = string.Create(CultureInfo.InvariantCulture,
                $"{o.Width}×{o.Height} · {o.Mode}{(o.Open ? $" · {o.Fps:0} fps" : " · closed")}{(o.Pattern is { } p ? " · " + p : "")}");
        }
    }
}
