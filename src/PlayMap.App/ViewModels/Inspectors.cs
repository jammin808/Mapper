using System.Diagnostics;
using System.Globalization;
using PlayMap.App.Infrastructure;
using PlayMap.App.Link;
using PlayMap.App.State;
using PlayMap.Core.Model;

namespace PlayMap.App.ViewModels;

/// <summary>What the window's modules add under an inspector: the entries its right-click menu also carries (shown as
/// the inspector's second toolbar) and the sections drawn below its fields.</summary>
public interface IInspectorExtras
{
    IReadOnlyList<DeskAction> ExtrasFor(IMenuTarget target);

    IReadOnlyList<object> SectionsFor(IMenuTarget target);
}

/// <summary>Holds the inspector for what is selected and refreshes it on every push; an inspector whose
/// object is gone closes. The modules' extras and sections for the object follow it.</summary>
public sealed class InspectorHost : ObservableObject
{
    private readonly DeskSession _session;
    private InspectorViewModel? _current;
    private IInspectorExtras? _modules;
    private IReadOnlyList<DeskAction> _extras = [];
    private IReadOnlyList<object> _sections = [];

    public InspectorHost(DeskSession session) => _session = session;

    /// <summary>The modules' entries for the selected object: the same instances its right-click menu appends.</summary>
    public IReadOnlyList<DeskAction> Extras
    {
        get => _extras;
        private set => Set(ref _extras, value);
    }

    /// <summary>The modules' sections for the selected object (cards, effect slots…), drawn by their registered views.</summary>
    public IReadOnlyList<object> Sections
    {
        get => _sections;
        private set => Set(ref _sections, value);
    }

    /// <summary>Connects the window's modules (once, where the window composes them).</summary>
    public void Use(IInspectorExtras modules)
    {
        _modules = modules;
        RefreshExtras();
    }

    /// <summary>Re-reads the modules' extras and sections for the selected object (a new list only when it changed).</summary>
    public void RefreshExtras()
    {
        IReadOnlyList<DeskAction> extras = _current is { } c && _modules is { } m ? m.ExtrasFor(c) : [];
        IReadOnlyList<object> sections = _current is { } c2 && _modules is { } m2 ? m2.SectionsFor(c2) : [];
        if (!extras.SequenceEqual(_extras))
            Extras = extras;
        if (!sections.SequenceEqual(_sections))
            Sections = sections;
        _extras.RefreshAll();
    }

    public InspectorViewModel? Current
    {
        get => _current;
        private set
        {
            if (Set(ref _current, value))
                Raise(nameof(IsEmpty));
        }
    }

    public bool IsEmpty => _current is null;

    public void Show(InspectorViewModel? inspector)
    {
        Current = inspector;
        if (inspector is not null && _session.Project is { } p)
            Refresh(p, _session.State);
        else
            RefreshExtras();
    }

    public void Refresh(Project p, StateSnapshot s)
    {
        if (_current is { } c && !c.Refresh(p, s))
            Current = null;
        RefreshExtras();
    }
}

/// <summary>An inspector: the fields of one object, each edit a SET (or the object's own verb). Fields are
/// loaded from the mirror on every push; a field the operator changed in the last moments is left alone so
/// a push that lags the typing never pulls the caret back. Sliders and text go through the throttle
/// (latest wins, ≈ 60 Hz); toggles and choices go at once.</summary>
public abstract class InspectorViewModel : ObservableObject, IMenuTarget
{
    IReadOnlyList<DeskAction> IMenuTarget.MenuActions => Actions;

    private static readonly long FreshTicks = Stopwatch.Frequency / 2;
    private readonly Dictionary<string, long> _edited = new(StringComparer.Ordinal);
    private string _title = "";
    private string _subtitle = "";
    private IReadOnlyList<DeskAction> _actions = [];

    protected InspectorViewModel(DeskSession session, string id)
    {
        Session = session;
        Id = id;
    }

    protected DeskSession Session { get; }

    public string Id { get; }

    public string Title
    {
        get => _title;
        protected set => Set(ref _title, value);
    }

    public string Subtitle
    {
        get => _subtitle;
        protected set => Set(ref _subtitle, value);
    }

    public abstract string Icon { get; }

    /// <summary>The object's actions (the same list its right-click menu shows).</summary>
    public IReadOnlyList<DeskAction> Actions
    {
        get => _actions;
        protected set
        {
            if (Set(ref _actions, value))
                Raise(nameof(Toolbar));
        }
    }

    /// <summary>The actions the inspector's bar shows (the menu holds them all).</summary>
    public IReadOnlyList<DeskAction> Toolbar => _actions.Toolbar();

    /// <summary>Why the gate would refuse an edit of this object now (armed and it is mapping or layout, or a lock holds
    /// it): the fields grey and show these words; null while its fields are live. Asked with the line a field sends.</summary>
    public string? EditRefusal => Session.GateWords(VerbText.Set(Id, "name", "\"\""));

    public bool IsEditable => EditRefusal is null;

    /// <summary>True while fields are being loaded from the mirror (a load never sends).</summary>
    protected bool Loading { get; private set; }

    /// <summary>Loads the fields from the mirror; false when the object is gone.</summary>
    public bool Refresh(Project p, StateSnapshot s)
    {
        Loading = true;
        try
        {
            var ok = Load(p, s);
            if (ok)
            {
                Actions.RefreshAll();
                Raise(nameof(EditRefusal), nameof(IsEditable));
            }
            return ok;
        }
        finally
        {
            Loading = false;
        }
    }

    protected abstract bool Load(Project p, StateSnapshot s);

    /// <summary>False for a field edited in the last half second (its load is skipped).</summary>
    protected bool Settled(string path) =>
        !_edited.TryGetValue(path, out var at) || Stopwatch.GetTimestamp() - at > FreshTicks;

    /// <summary>Sends SET &lt;id&gt; &lt;path&gt; &lt;json&gt; (never while loading).</summary>
    protected void Edit(string path, string json, bool throttle = false) => EditOf(Id, path, json, throttle);

    protected void EditOf(string id, string path, string json, bool throttle = false)
    {
        if (Loading)
            return;
        _edited[path] = Stopwatch.GetTimestamp();
        var line = VerbText.Set(id, path, json);
        if (throttle)
            Session.Throttle.Post(id + " " + path, line);
        else
            Session.Fire(line);
    }

    /// <summary>Sends a verb (never while loading).</summary>
    protected void Send(string line)
    {
        if (!Loading)
            Session.Fire(line);
    }

    protected static string Seconds(long ns) => ns <= 0 ? "" : (ns / 1e9).ToString("0.###", CultureInfo.InvariantCulture);
}

/// <summary>A playlist item: in and out, duration, what happens at its end, its transition, rate, volume.</summary>
public sealed class PlaylistItemInspector : InspectorViewModel
{
    private readonly string _playlistId;
    private string _label = "";
    private string _source = "";
    private string _inText = "";
    private string _outText = "";
    private string _durationText = "";
    private EndAction _end;
    private TransitionKind _transition;
    private string _transitionText = "";
    private double _rate = 1;
    private double _volume = 1;
    private string? _gotoId;
    private IReadOnlyList<ItemChoice> _gotoChoices = [];

    public PlaylistItemInspector(DeskSession session, string id, string playlistId) : base(session, id)
    {
        _playlistId = playlistId;
        Actions =
        [
            DeskAction.Send(session, "go", "Go", () => $"PLAYLIST GO {_playlistId} {Id}").Look("play"),
            DeskAction.Send(session, "cue", "Cue", () => $"PLAYLIST CUE {_playlistId} {Id}").Look("cue"),
            DeskAction.Send(session, "remove", "Remove", () => $"PLAYLIST REMOVE {_playlistId} {Id}").Look("trash", danger: true),
        ];
    }

    public override string Icon => "media";

    public IReadOnlyList<EndAction> EndChoices { get; } = Enum.GetValues<EndAction>();

    public IReadOnlyList<TransitionKind> TransitionChoices { get; } = Enum.GetValues<TransitionKind>();

    public string Label
    {
        get => _label;
        set
        {
            if (Set(ref _label, value) && value.Trim().Length > 0)
                Edit("label", VerbText.JsonString(value.Trim()), throttle: true);
        }
    }

    public string Source
    {
        get => _source;
        private set => Set(ref _source, value);
    }

    /// <summary>The in point in seconds ("12.5", "1:02.5"); typed text that does not read yet is not sent.</summary>
    public string InText
    {
        get => _inText;
        set
        {
            if (Set(ref _inText, value) && ParseNs(value) is { } ns)
                Edit("inNs", VerbText.Int(ns), throttle: true);
        }
    }

    public string OutText
    {
        get => _outText;
        set
        {
            if (Set(ref _outText, value) && ParseNs(value) is { } ns)
                Edit("outNs", VerbText.Int(ns), throttle: true);
        }
    }

    public string DurationText
    {
        get => _durationText;
        set
        {
            if (Set(ref _durationText, value) && ParseNs(value) is { } ns)
                Edit("durationNs", VerbText.Int(ns), throttle: true);
        }
    }

    public EndAction End
    {
        get => _end;
        set
        {
            if (Set(ref _end, value))
            {
                Raise(nameof(IsGoto));
                Edit("end", VerbText.Json(value));
            }
        }
    }

    public bool IsGoto => _end == EndAction.Goto;

    public IReadOnlyList<ItemChoice> GotoChoices
    {
        get => _gotoChoices;
        private set => Set(ref _gotoChoices, value);
    }

    public ItemChoice? Goto
    {
        get => _gotoChoices.FirstOrDefault(c => c.Id == _gotoId);
        set
        {
            if (value is null || value.Id == _gotoId)
                return;
            _gotoId = value.Id;
            Raise();
            Edit("gotoItemId", VerbText.JsonString(value.Id));
        }
    }

    public TransitionKind Transition
    {
        get => _transition;
        set
        {
            if (Set(ref _transition, value))
                Edit("transition", VerbText.Json(value));
        }
    }

    public string TransitionText
    {
        get => _transitionText;
        set
        {
            if (Set(ref _transitionText, value) && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var s) && s >= 0 && s <= 60)
                Edit("transitionNs", VerbText.Int((long)Math.Round(s * 1e9)), throttle: true);
        }
    }

    public double Rate
    {
        get => _rate;
        set
        {
            if (Set(ref _rate, Math.Round(value, 3)))
            {
                Raise(nameof(RateText));
                Edit("rate", VerbText.Num(_rate), throttle: true);
            }
        }
    }

    public string RateText => string.Create(CultureInfo.InvariantCulture, $"×{_rate:0.##}");

    public double Volume
    {
        get => _volume;
        set
        {
            if (Set(ref _volume, Math.Round(value, 3)))
            {
                Raise(nameof(VolumeText));
                Edit("volume", VerbText.Num(_volume), throttle: true);
            }
        }
    }

    public string VolumeText => string.Create(CultureInfo.InvariantCulture, $"{_volume * 100:0} %");

    private static long? ParseNs(string text) => text.Trim().Length == 0 ? 0 : VerbText.ParseSeconds(text);

    protected override bool Load(Project p, StateSnapshot s)
    {
        var list = p.FindPlaylist(_playlistId);
        var item = list?.Items.Find(Id);
        if (list is null || item is null)
            return false;
        Title = item.Label;
        Subtitle = "Item in " + list.Name;
        if (Settled("label"))
            Label = item.Label;
        Source = item.Source.Kind switch
        {
            SourceKind.Media => p.FindMedia(item.Source.Id)?.Name ?? item.Source.ToString(),
            SourceKind.Generator => (p.FindGenerator(item.Source.Id)?.Name ?? item.Source.Id) + " (generator)",
            _ => item.Source.ToString(),
        };
        if (Settled("inNs"))
            InText = Seconds(item.InNs);
        if (Settled("outNs"))
            OutText = Seconds(item.OutNs);
        if (Settled("durationNs"))
            DurationText = Seconds(item.DurationNs);
        End = item.End;
        GotoChoices = list.Items.Where(i => i.Id != Id).Select(i => new ItemChoice(i.Id, i.Label)).ToList();
        _gotoId = item.GotoItemId;
        Raise(nameof(Goto));
        Transition = item.Transition;
        if (Settled("transitionNs"))
            TransitionText = (item.TransitionNs / 1e9).ToString("0.##", CultureInfo.InvariantCulture);
        if (Settled("rate"))
            Rate = item.Rate;
        if (Settled("volume"))
            Volume = item.Volume;
        return true;
    }
}

/// <summary>One line of facts in an inspector.</summary>
public sealed record Fact(string Key, string Value);

/// <summary>A choice in a picker: an id and the words shown.</summary>
public sealed record ItemChoice(string Id, string Name)
{
    public override string ToString() => Name;
}

/// <summary>A playlist's own settings: its name, whether it wraps, its sync group.</summary>
public sealed class PlaylistInspector : InspectorViewModel
{
    private string _name = "";
    private bool _loop;
    private string _syncGroup = "";
    private string _facts = "";

    public PlaylistInspector(DeskSession session, string id) : base(session, id)
    {
        Actions =
        [
            DeskAction.Send(session, "focus", "Focus", () => "PLAYLIST FOCUS " + Id).Look("cue"),
            DeskAction.Send(session, "delete", "Delete playlist", () => "PLAYLIST DELETE " + Id).Look("trash", danger: true),
        ];
    }

    public override string Icon => "media";

    public string Name
    {
        get => _name;
        set
        {
            if (Set(ref _name, value) && value.Trim().Length > 0)
                Edit("name", VerbText.JsonString(value.Trim()), throttle: true);
        }
    }

    public bool Loop
    {
        get => _loop;
        set
        {
            if (Set(ref _loop, value))
                Send($"PLAYLIST LOOP {Id} {(value ? "ON" : "OFF")}");
        }
    }

    /// <summary>The sync group ("" = none); sent when the field is committed.</summary>
    public string SyncGroup
    {
        get => _syncGroup;
        set => Set(ref _syncGroup, value);
    }

    public string Facts
    {
        get => _facts;
        private set => Set(ref _facts, value);
    }

    public RelayCommand CommitSyncCommand => _commitSync ??= new RelayCommand(CommitSync);

    private RelayCommand? _commitSync;

    public void CommitSync()
    {
        var g = _syncGroup.Trim();
        Send($"PLAYLIST SYNC {Id} {(g.Length == 0 ? "OFF" : VerbText.Word(g))}");
    }

    protected override bool Load(Project p, StateSnapshot s)
    {
        var list = p.FindPlaylist(Id);
        if (list is null)
            return false;
        Title = list.Name;
        Subtitle = "Playlist";
        if (Settled("name"))
            Name = list.Name;
        Loop = list.Loop;
        SyncGroup = list.SyncGroup ?? "";
        var st = s.FindPlaylist(Id);
        Facts = st is null ? "" : string.Create(CultureInfo.InvariantCulture,
            $"{st.State}{(st.Item is { } i ? " · " + i.Label : "")}{(st.Leader is { } l ? " · leader " + l : "")}");
        return true;
    }
}

/// <summary>What the probe read about a library item, and where it stands on this node.</summary>
public sealed class MediaInspector : InspectorViewModel
{
    private readonly PlayViewModel _play;
    private string _name = "";
    private IReadOnlyList<Fact> _facts = [];

    public MediaInspector(DeskSession session, string id, PlayViewModel play) : base(session, id) => _play = play;

    public override string Icon => "media";

    public string Name
    {
        get => _name;
        set
        {
            if (Set(ref _name, value) && value.Trim().Length > 0)
                Edit("name", VerbText.JsonString(value.Trim()), throttle: true);
        }
    }

    public IReadOnlyList<Fact> Facts
    {
        get => _facts;
        private set => Set(ref _facts, value);
    }

    protected override bool Load(Project p, StateSnapshot s)
    {
        var m = p.FindMedia(Id);
        if (m is null)
            return false;
        Title = m.Name;
        Subtitle = m.Kind.ToString();
        if (Settled("name"))
            Name = m.Name;
        var vm = _play.Media.FirstOrDefault(x => x.Id == Id);
        if (vm is not null)
            Actions = vm.Actions;
        var i = m.Info;
        var facts = new List<Fact>();
        void F(string k, string v)
        {
            if (v.Length > 0)
                facts.Add(new Fact(k, v));
        }
        F("Availability", vm?.AvailabilityTip ?? "");
        F("Length", i.DurationNs > 0 ? VerbText.Clock(i.DurationNs / 1_000_000) : "");
        F("Picture", i.Width > 0 ? string.Create(CultureInfo.InvariantCulture, $"{i.Width}×{i.Height}{(i.FrameRate.Length > 0 ? " @ " + i.FrameRate : "")}{(i.HasAlpha ? " · alpha" : "")}") : "");
        F("Video", string.Join(" · ", new[] { i.VideoCodec, i.PixelFormat }.Where(x => x.Length > 0)));
        F("Audio", i.AudioCodec.Length > 0 ? string.Create(CultureInfo.InvariantCulture, $"{i.AudioCodec} · {i.AudioChannels} ch · {i.AudioRate} Hz") : "");
        F("Container", i.Container);
        F("Size", m.SizeBytes > 0 ? string.Create(CultureInfo.InvariantCulture, $"{m.SizeBytes / 1048576.0:0.#} MB") : "");
        F("Where", m.Location);
        F("Content hash", m.ContentHash.Length > 20 ? m.ContentHash[..20] + "…" : m.ContentHash);
        F("Tags", string.Join(", ", m.Tags));
        Facts = facts;
        return true;
    }
}
