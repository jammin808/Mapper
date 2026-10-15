using System.Collections.Immutable;
using System.Globalization;
using PlayMap.Core.Content;
using PlayMap.Core.Model;
using PlayMap.Core.Playback;
using PlayMap.Core.Time;
using PlayMap.Core.Wire;

namespace PlayMap.App.Link;

/// <summary>The desk's demo engine: an <see cref="IEngineLink"/> that holds a project in a real
/// <see cref="ProjectStore"/>, runs a reasonable subset of the vocabulary with the engine's meaning (the
/// generic edits, PLAYLIST and VIDEO over <see cref="ClipTransport"/>, BLACKOUT, PATTERN, GENERATOR SET, a
/// simulated scan and media fetch) and publishes a STATE shaped like docs/WIRE.md. It makes the desk fully
/// clickable without an engine, and it is what the desk's tests drive: every line sent is recorded in
/// <see cref="SentLines"/>. A verb that would change nothing is refused with the reason, as the engine does.</summary>
public sealed partial class FakeEngineLink : IEngineLink
{
    private readonly Lock _gate = new();
    private readonly ProjectStore _store;
    private readonly IMonoClock _clock;
    private readonly Dictionary<string, PlaylistCursor> _cursors = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _outputPatterns = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FakeTransfer> _transfers = new(StringComparer.Ordinal);
    private readonly HashSet<string> _fetched = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _views = new(StringComparer.Ordinal);
    private readonly List<string> _sent = [];
    private readonly CancellationTokenSource _life = new();
    private readonly Task _ticker = Task.CompletedTask;
    private string? _focused;
    private bool _blackout;
    private bool _frozen;
    private string? _pattern;
    private long _identifyUntilNs;
    private long _savedRevision;
    private long _stateSeq;
    private string _stateJson = "";
    private (string Json, long Revision) _projectMirror;
    private FakeScan? _scan;
    private string _patternsHost = "";
    private string _stagewatchHost = "";
    private string? _syncLead;
    private string _token = PlayMap.Core.Bus.Pairing.NewToken();
    private LinkStatus _status = LinkStatus.Live;
    private int _disposed;
    private readonly SortedDictionary<string, System.Text.Json.Nodes.JsonNode?> _sections = new(StringComparer.Ordinal);
    private bool _extraMoved;

    // One hook per desk package: each answers the round-2 verbs its features need in the demo, in its own file;
    // a hook nobody implements compiles away. A hook that changes STATE sets _extraMoved.
    partial void TryShowVerbs(WireLine line, ref WireReply? reply);

    partial void TryAssistantVerbs(WireLine line, ref WireReply? reply);

    partial void TryContentVerbs(WireLine line, ref WireReply? reply);

    partial void TryMapVerbs(WireLine line, ref WireReply? reply);

    /// <summary>Lays a top-level STATE section over the fake's own (a test's fixture, as the node writes it) and
    /// publishes; objects merge key by key, arrays of objects by id. Null removes the section.</summary>
    public void SetSection(string key, string? json)
    {
        ArgumentNullException.ThrowIfNull(key);
        long stateRev;
        lock (_gate)
        {
            if (json is null)
                _sections.Remove(key);
            else
                _sections[key] = _sections.TryGetValue(key, out var had) && had is not null
                    ? Merge(had, System.Text.Json.Nodes.JsonNode.Parse(json))
                    : System.Text.Json.Nodes.JsonNode.Parse(json);
            Publish(projectMoved: false);
            stateRev = _stateSeq;
        }
        StateChanged?.Invoke(stateRev);
    }

    /// <summary>A section as a hook holds it (created empty when absent), to change in place; the send publishes.</summary>
    private System.Text.Json.Nodes.JsonObject Section(string key)
    {
        if (_sections.TryGetValue(key, out var node) && node is System.Text.Json.Nodes.JsonObject o)
            return o;
        var made = new System.Text.Json.Nodes.JsonObject();
        _sections[key] = made;
        return made;
    }

    /// <summary>Marks STATE as moved by a hook (the send publishes it).</summary>
    private void Moved() => _extraMoved = true;

    /// <param name="project">The project to open (the demo show when null).</param>
    /// <param name="clock">The show clock; tests pass a <see cref="ManualClock"/> and call <see cref="Tick"/>.</param>
    /// <param name="autoTick">Runs the transports and the simulated scan on a background timer (the demo).</param>
    public FakeEngineLink(Project? project = null, IMonoClock? clock = null, bool autoTick = false)
    {
        _clock = clock ?? MonoClock.Instance;
        _store = new ProjectStore(project ?? DemoProject.Create(), () => _clock.NowNs / 1_000_000);
        _savedRevision = _store.Current.Revision;
        _focused = _store.Project.Playlists.IsDefaultOrEmpty ? null : _store.Project.Playlists[0].Id;
        lock (_gate)
            Publish(projectMoved: true);
        if (autoTick)
            _ticker = RunTickerAsync(_life.Token);
    }

    public LinkStatus Status => _status;

    public string NodeName => "Demo node";

    public string NodeId => "demo0001";

    /// <summary>The pairing token this demo node shows (machine-local; never sent on a line).</summary>
    public string PairingToken
    {
        get
        {
            lock (_gate)
                return _token;
        }
    }

    public (string Json, long Revision) State
    {
        get
        {
            lock (_gate)
                return (_stateJson, _stateSeq);
        }
    }

    public (string Json, long Revision) Project
    {
        get
        {
            lock (_gate)
                return _projectMirror;
        }
    }

    /// <summary>The project as the store holds it now (tests read the truth here).</summary>
    public Project CurrentProject => _store.Project;

    /// <summary>Verbs this demo node leaves out of its VERBS answer (a node without a package), for tests.</summary>
    public HashSet<string> WithoutVerbs { get; } = new(StringComparer.Ordinal);

    /// <summary>Every line the desk sent, in order.</summary>
    public IReadOnlyList<string> SentLines
    {
        get
        {
            lock (_gate)
                return [.. _sent];
        }
    }

    /// <summary>The last camera each preview view was given (VIEW &lt;id&gt; CAMERA …).</summary>
    public IReadOnlyDictionary<string, string> Views
    {
        get
        {
            lock (_gate)
                return new Dictionary<string, string>(_views, StringComparer.Ordinal);
        }
    }

    public event Action<LinkStatus>? StatusChanged;

    public event Action<long>? StateChanged;

    public event Action<long>? ProjectChanged;

    public void ClearSent()
    {
        lock (_gate)
            _sent.Clear();
    }

    /// <summary>Takes the link down or up (a test of the desk's link pill and of refusing while down).</summary>
    public void SetStatus(LinkStatus status)
    {
        _status = status;
        StatusChanged?.Invoke(status);
    }

    public Task<WireReply> SendAsync(string line, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (_status != LinkStatus.Live)
            return Task.FromResult(new WireReply(false, "the link is down — nothing was sent"));
        WireReply reply;
        long stateRev;
        long projectRev = -1;
        lock (_gate)
        {
            _sent.Add(line);
            var before = _store.Current.Revision;
            var beforeAir = (_blackout, _frozen, _pattern);
            _extraMoved = false;
            reply = Handle(line);
            var projectMoved = _store.Current.Revision != before;
            var mutated = reply.Ok && (projectMoved || IsMutating(line) || _extraMoved || beforeAir != (_blackout, _frozen, _pattern));
            if (!mutated)
                return Task.FromResult(reply);
            Publish(projectMoved);
            stateRev = _stateSeq;
            if (projectMoved)
                projectRev = _projectMirror.Revision;
        }
        if (projectRev >= 0)
            ProjectChanged?.Invoke(projectRev);
        StateChanged?.Invoke(stateRev);
        return Task.FromResult(reply);
    }

    /// <summary>Advances the transports, the scan and the transfers to the clock's now; publishes STATE when
    /// anything moves (and at least on every call while something plays, the engine's once-a-second rule).</summary>
    public void Tick()
    {
        long stateRev;
        long projectRev = -1;
        lock (_gate)
        {
            var before = _store.Current.Revision;
            // Every lane advances on every tick (no short circuit): each one's own state must move on.
            var transports = TickTransports();
            var scan = TickScan();
            var transfers = TickTransfers();
            var moving = transports || scan || transfers;
            if (_identifyUntilNs != 0 && _clock.NowNs >= _identifyUntilNs)
            {
                _identifyUntilNs = 0;
                moving = true;
            }
            if (!moving)
                return;
            var projectMoved = _store.Current.Revision != before;
            Publish(projectMoved);
            stateRev = _stateSeq;
            if (projectMoved)
                projectRev = _projectMirror.Revision;
        }
        if (projectRev >= 0)
            ProjectChanged?.Invoke(projectRev);
        StateChanged?.Invoke(stateRev);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        await _life.CancelAsync();
        try
        {
            await _ticker;
        }
        catch (OperationCanceledException)
        {
            // The ticker ends by cancellation; that is its normal end.
        }
        _life.Dispose();
        _status = LinkStatus.Down;
    }

    private async Task RunTickerAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
        while (await timer.WaitForNextTickAsync(ct))
        {
            try
            {
                Tick();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The demo keeps running through a fault in one tick; the next tick starts from the store's truth.
                System.Diagnostics.Trace.WriteLine("PlayMap demo engine: a tick failed: " + ex.Message);
            }
        }
    }

    private static bool IsMutating(string line) =>
        WireGrammar.TryParse(line, out var parsed, out _) && (VerbCatalog.Match(parsed)?.Spec.Mutates ?? parsed.Verb is "PAIRING" or "SCAN");

    private void Publish(bool projectMoved)
    {
        if (projectMoved || _projectMirror.Json is null)
        {
            var snap = _store.Current;
            _projectMirror = (ProjectJson.SerializeEntity(snap.Project), snap.Revision);
        }
        _stateSeq++;
        _stateJson = BuildState();
    }

    private static WireReply Ok(string text = "") => new(true, text);

    private static WireReply Err(string text) => new(false, text);

    private WireReply Edit(string label, Func<Project, Project> edit, string? coalesceKey = null, string okText = "")
    {
        var r = _store.Apply(label, edit, coalesceKey);
        return r.Changed ? Ok(okText) : Err(r.Effect);
    }

    private WireReply Handle(string text)
    {
        if (!WireGrammar.TryParse(text, out var line, out var error))
            return Err(error?.Reason ?? "an unreadable line");
        // Verbs the desk needs that the catalogue does not name yet (proposed in the desk's report).
        switch (line.Verb)
        {
            case "VIEW":
                return View(line);
            case "PAIRING":
                return Pairing(line);
            case "SCAN" when line.WordUpper(1) == "SURFACE":
                return ScanSurface(line);
        }
        WireReply? extra = null;
        TryShowVerbs(line, ref extra);
        if (extra is null)
            TryAssistantVerbs(line, ref extra);
        if (extra is null)
            TryContentVerbs(line, ref extra);
        if (extra is null)
            TryMapVerbs(line, ref extra);
        if (extra is not null)
            return extra;
        var match = VerbCatalog.Match(line);
        if (match is null)
            return Err($"unknown verb '{line.Word(0)}' — HELP lists the vocabulary");
        var (spec, words) = match.Value;
        try
        {
            return spec.Name switch
            {
                "HELLO" or "PING" or "NONCE" or "AUTH" or "AUTH-PROOF" or "SUBSCRIBE" => Ok(),
                "STATE" => Ok(_stateJson),
                "PROJECT GET" => Ok(_projectMirror.Json),
                "HELP" => Ok(string.Join(' ', VerbCatalog.All.Select(v => v.Name))),
                // The engine's shape: one row per verb (the desk reads the names).
                "VERBS" => Ok(System.Text.Json.JsonSerializer.Serialize(VerbCatalog.All.Where(v => !WithoutVerbs.Contains(v.Name))
                    .Select(v => new Dictionary<string, string> { ["name"] = v.Name }))),
                "SET" => Set(line),
                "ADD" => Add(line),
                "REMOVE" => line.Count < 2 ? Err("REMOVE <id>") : Edit("remove " + line.Word(1), p => ProjectEdits.Remove(p, line.Word(1))),
                "MOVE" => Move(line),
                "UNDO" => Undo(_store.Undo()),
                "REDO" => Undo(_store.Redo()),
                "PROJECT SAVE" => Save(),
                "PROJECT NEW" => NewProject(line),
                "PLAYLIST NEW" => PlaylistNew(line),
                "PLAYLIST ADD" => PlaylistAdd(line),
                "PLAYLIST REMOVE" => PlaylistRemove(line),
                "PLAYLIST MOVE" => PlaylistMove(line),
                "PLAYLIST DELETE" => PlaylistDelete(line),
                "PLAYLIST GO" => PlaylistGo(line, play: true),
                "PLAYLIST CUE" => PlaylistGo(line, play: false),
                "PLAYLIST NEXT" => PlaylistStep(line, +1),
                "PLAYLIST PREV" => PlaylistStep(line, -1),
                "PLAYLIST LOOP" => PlaylistLoop(line),
                "PLAYLIST SYNC" => PlaylistSync(line),
                "PLAYLIST FOCUS" => PlaylistFocus(line),
                "PLAYLIST LIST" or "PLAYLIST GET" => Ok(_stateJson),
                "VIDEO PLAY" or "VIDEO PAUSE" or "VIDEO STOP" or "VIDEO RESTART" or "VIDEO SEEK" or "VIDEO END" or "VIDEO RATE" => Video(spec.Name, line, words),
                "BLACKOUT" => Toggle(line, ref _blackout, "blacked out", "back on air"),
                "FREEZE" => Toggle(line, ref _frozen, "frozen", "running"),
                "PATTERN" => Pattern(line),
                "GENERATOR SET" => GeneratorSet(line),
                "GENERATOR NEW" => GeneratorNew(line),
                "GENERATOR KINDS" => Ok(string.Join(' ', GeneratorCatalog.All.Select(k => k.Id))),
                "MEDIA IMPORT" => MediaImport(line),
                "MEDIA REMOVE" => line.Count < 3 ? Err("MEDIA REMOVE <media>") : Edit("remove media", p => p.FindMedia(line.Word(2)) is null
                    ? throw new EditRefusedException($"no media '{line.Word(2)}'")
                    : p with { Media = p.Media.RemoveId(line.Word(2)) }),
                "MEDIA FETCH" => MediaFetch(line),
                "MEDIA LIST" => Ok(MediaListJson()),
                "MEDIA STATUS" => Ok(_stateJson),
                "DISPLAYS" => Ok("[{\"key\":\"DISPLAY1\",\"name\":\"Desk monitor\",\"width\":2560,\"height\":1440,\"refreshHz\":60,\"primary\":true},"
                    + "{\"key\":\"DISPLAY2\",\"name\":\"Projector A\",\"width\":1920,\"height\":1080,\"refreshHz\":60,\"primary\":false},"
                    + "{\"key\":\"DISPLAY3\",\"name\":\"Projector B\",\"width\":1920,\"height\":1080,\"refreshHz\":60,\"primary\":false}]"),
                "OUTPUT ENABLE" => OutputEnable(line),
                "OUTPUT SOURCE" => OutputSource(line),
                "OUTPUT IDENTIFY" => Identify(),
                "OUTPUT LIST" => Ok(_stateJson),
                "SURFACE SOURCE" => SurfaceSource(line),
                "SURFACE SHOW" => SurfaceShow(line),
                "PROJECTOR SOLVE" => ProjectorSolve(line),
                "BLEND SOLVE" => Ok("blend maps computed"),
                "WARP RESET" => WarpReset(line),
                "WARP NODE" => WarpNode(line),
                "SCAN START" => ScanStart(line),
                "SCAN CANCEL" => ScanCancel(),
                "SCAN APPLY" => ScanApply(line),
                "SCAN STATUS" => Ok(_stateJson),
                "LATENCY MEASURE" => Ok("{\"latencyMs\":112}"),
                "LINK PATTERNS" => LinkTo(line, ref _patternsHost),
                "LINK STAGEWATCH" => LinkTo(line, ref _stagewatchHost),
                "SYNC LEAD" => SyncLead(line),
                "SYNC FOLLOW" => line.Count < 4 ? Err("SYNC FOLLOW <group> <node>") : SyncFollow(line),
                "NODES" or "SYNC STATUS" or "CONTEXT" or "GET" => Ok(_stateJson),
                _ => Err($"the demo engine does not run {spec.Name} — a real engine does"),
            };
        }
        catch (EditRefusedException refused)
        {
            return Err(refused.Message);
        }
    }

    private static WireReply Undo(EditResult r) => r.Changed ? Ok(r.Effect) : Err(r.Effect);

    private WireReply Set(WireLine line)
    {
        if (line.Count < 4)
            return Err("SET <id> <field.path> <json>");
        var id = line.Word(1);
        var path = line.Word(2);
        var json = line.RestAfter(3);
        return Edit($"set {id} {path}", p => ProjectEdits.SetField(p, id, path, json), coalesceKey: id + " " + path);
    }

    private WireReply Add(WireLine line)
    {
        if (line.Count < 3)
            return Err("ADD <kind> <json>");
        var newId = "";
        var r = Edit("add " + line.Word(1), p => ProjectEdits.Add(p, line.Word(1), line.RestAfter(2), out newId));
        return r.Ok ? Ok(newId) : r;
    }

    private WireReply Move(WireLine line)
    {
        if (line.Count < 3 || !int.TryParse(line.Word(2), NumberStyles.Integer, CultureInfo.InvariantCulture, out var index))
            return Err("MOVE <id> <index>");
        return Edit("move " + line.Word(1), p => ProjectEdits.Move(p, line.Word(1), index));
    }

    private WireReply Save()
    {
        if (_savedRevision == _store.Current.Revision)
            return Err("already saved — nothing changed since");
        _savedRevision = _store.Current.Revision;
        return Ok("saved");
    }

    private WireReply NewProject(WireLine line)
    {
        var name = line.Count > 2 ? line.Word(2) : "Untitled";
        _store.Replace(PlayMap.Core.Model.Project.CreateDefault(name), "new project");
        _cursors.Clear();
        _focused = null;
        return Ok("new project");
    }

    private WireReply Toggle(WireLine line, ref bool flag, string onWords, string offWords)
    {
        var word = line.WordUpper(1);
        bool target;
        switch (word)
        {
            case "ON" or "1":
                target = true;
                break;
            case "OFF" or "0":
                target = false;
                break;
            case "TOGGLE":
                target = !flag;
                break;
            default:
                return Err($"{line.Verb} ON|OFF|TOGGLE");
        }
        if (target == flag)
            return Err("already " + (flag ? onWords : offWords));
        flag = target;
        return Ok(flag ? onWords : offWords);
    }

    private WireReply Pattern(WireLine line)
    {
        if (line.Count < 2)
            return Err("PATTERN <kind>|OFF [ON <output>|ALL]");
        var kindWord = line.Word(1);
        var target = line.WordUpper(2) == "ON" ? line.Word(3) : "ALL";
        string? kind = null;
        if (!kindWord.Equals("OFF", StringComparison.OrdinalIgnoreCase))
        {
            kind = GeneratorCatalog.FindLoose(kindWord)?.Id;
            if (kind is null || !kind.StartsWith("pattern.", StringComparison.Ordinal))
                return Err($"no test pattern '{kindWord}' — GENERATOR KINDS lists them");
        }
        if (target.Equals("ALL", StringComparison.OrdinalIgnoreCase))
        {
            if (kind == _pattern && _outputPatterns.Count == 0)
                return Err(kind is null ? "no pattern is up" : "that pattern is up already");
            _pattern = kind;
            _outputPatterns.Clear();
            return Ok(kind is null ? "patterns off" : kind + " on every output");
        }
        if (_store.Project.FindOutput(target) is null)
            return Err($"no output '{target}'");
        if (kind is null)
        {
            if (!_outputPatterns.Remove(target))
                return Err("no pattern is up on " + target);
        }
        else
        {
            if (_outputPatterns.TryGetValue(target, out var had) && had == kind)
                return Err("that pattern is up already");
            _outputPatterns[target] = kind;
        }
        _stateSeq++;
        return Ok(kind is null ? "pattern off on " + target : kind + " on " + target);
    }

    private WireReply GeneratorSet(WireLine line)
    {
        if (line.Count < 5)
            return Err("GENERATOR SET <generator> <param> <value>");
        var id = line.Word(2);
        var name = line.Word(3);
        var value = line.Word(4);
        var gen = _store.Project.FindGenerator(id);
        if (gen is null)
            return Err($"no generator '{id}'");
        var kind = GeneratorCatalog.Find(gen.Kind);
        var spec = kind?.Param(name);
        if (kind is null || spec is null)
            return Err($"{gen.Kind} has no parameter '{name}'");
        if (!spec.TryNormalise(value, out var normalised, out var why))
            return Err(why);
        return Edit($"set {id} {spec.Name}", p => p with
        {
            Generators = p.Generators.Update(id, g => g.Params.Get(spec.Name) == normalised ? g : g with { Params = g.Params.With(spec.Name, normalised) }),
        }, coalesceKey: id + " " + spec.Name);
    }

    private WireReply GeneratorNew(WireLine line)
    {
        var kind = GeneratorCatalog.FindLoose(line.Word(2));
        if (kind is null)
            return Err($"no generator kind '{line.Word(2)}'");
        var name = line.Count > 3 ? line.Word(3) : kind.Name;
        var id = _store.Project.NewId(Ids.Generator);
        return Edit("new generator", p => p with { Generators = p.Generators.Add(new Generator(id, name, kind.Id, ParamSet.Empty)) }, okText: id);
    }

    private WireReply MediaImport(WireLine line)
    {
        if (line.Count < 3)
            return Err("MEDIA IMPORT <path|url> [<name>]");
        var location = line.Word(2);
        var name = line.Count > 3 ? line.Word(3) : Path.GetFileName(location.TrimEnd('/'));
        if (_store.Project.Media.Any(m => string.Equals(m.Location, location, StringComparison.Ordinal)))
            return Err($"'{name}' is in the library already");
        var ext = Path.GetExtension(location).ToLowerInvariant();
        var kind = ext switch
        {
            ".png" or ".jpg" or ".jpeg" or ".tif" or ".tiff" or ".exr" or ".webp" => MediaKind.Image,
            ".wav" or ".mp3" or ".flac" or ".aac" or ".ogg" => MediaKind.Audio,
            _ when location.StartsWith("http", StringComparison.OrdinalIgnoreCase) => MediaKind.Web,
            _ => MediaKind.Video,
        };
        var info = kind == MediaKind.Video
            ? new MediaInfo(30 * DemoProject.Second, 1920, 1080, "25", "h264", "yuv420p", "aac", 2, 48000, false, ext.TrimStart('.'))
            : MediaInfo.Unknown;
        var id = _store.Project.NewId(Ids.Media);
        return Edit("import " + name, p => p with { Media = p.Media.Add(new MediaItem(id, name, kind, location, "", 0, info, [])) }, okText: id);
    }

    private WireReply MediaFetch(WireLine line)
    {
        var what = line.Word(2);
        var items = what.Equals("ALL", StringComparison.OrdinalIgnoreCase)
            ? _store.Project.Media.Where(m => Availability(m).State == "missing").ToList()
            : _store.Project.FindMedia(what) is { } one ? [one] : [];
        if (items.Count == 0)
            return Err(what.Equals("ALL", StringComparison.OrdinalIgnoreCase) ? "nothing is missing" : $"no media '{what}'");
        var started = 0;
        foreach (var m in items)
        {
            if (Availability(m).State != "missing" || _transfers.ContainsKey(m.Id))
                continue;
            _transfers[m.Id] = new FakeTransfer(m.Id, m.Name, "Stage right", _clock.NowNs);
            started++;
        }
        return started == 0 ? Err("already here or on its way") : Ok($"{{\"stage\":\"accepted\",\"effect\":\"fetching {started}\"}}");
    }

    private WireReply OutputEnable(WireLine line)
    {
        var id = line.Word(2);
        var on = line.WordUpper(3) switch { "ON" => true, "OFF" => false, _ => (bool?)null };
        if (on is null)
            return Err("OUTPUT ENABLE <output> ON|OFF");
        var output = _store.Project.FindOutput(id);
        if (output is null)
            return Err($"no output '{id}'");
        if (output.Enabled == on)
            return Err(on.Value ? "already open" : "already closed");
        return Edit((on.Value ? "open " : "close ") + output.Name, p => p with { Outputs = p.Outputs.Update(id, o => o with { Enabled = on.Value }) });
    }

    private WireReply OutputSource(WireLine line)
    {
        if (!SourceRef.TryParse(line.Word(3), out var src))
            return Err("a source is media:…, playlist:…, generator:…, canvas:…, color:#RRGGBB or none");
        var id = line.Word(2);
        return Edit("output source", p => p.FindOutput(id) is null
            ? throw new EditRefusedException($"no output '{id}'")
            : p with { Outputs = p.Outputs.Update(id, o => o.Source == src ? o : o with { Source = src }) });
    }

    private WireReply Identify()
    {
        _identifyUntilNs = _clock.NowNs + 5 * DemoProject.Second;
        _stateSeq++;
        return Ok("identifying for 5 s");
    }

    private WireReply SurfaceSource(WireLine line)
    {
        if (!SourceRef.TryParse(line.Word(3), out var src))
            return Err("a source is media:…, playlist:…, generator:…, canvas:…, color:#RRGGBB or none");
        var id = line.Word(2);
        return Edit("surface source", p => p.FindSurface(id) is null
            ? throw new EditRefusedException($"no surface '{id}'")
            : p.WithStage(s => s with { Surfaces = s.Surfaces.Update(id, f => f.Content.Source == src ? f : f with { Content = f.Content with { Source = src } }) }));
    }

    private WireReply SurfaceShow(WireLine line)
    {
        var id = line.Word(2);
        var on = line.WordUpper(3) switch { "ON" => true, "OFF" => false, _ => (bool?)null };
        if (on is null)
            return Err("SURFACE SHOW <surface> ON|OFF");
        return Edit((on.Value ? "show " : "hide ") + id, p => p.FindSurface(id) is null
            ? throw new EditRefusedException($"no surface '{id}'")
            : p.WithStage(s => s with { Surfaces = s.Surfaces.Update(id, f => f.Visible == on.Value ? f : f with { Visible = on.Value }) }));
    }

    private WireReply ProjectorSolve(WireLine line)
    {
        var id = line.Word(2);
        var pj = _store.Project.FindProjector(id);
        if (pj is null)
            return Err($"no projector '{id}'");
        var pins = pj.Pins.Where(p => p.Enabled).ToList();
        if (pins.Count < 6)
            return Err($"a solve needs six or more pins — {pj.Name} has {pins.Count}");
        var rms = PinResiduals.Rms(pj);
        var locks = line.WordUpper(3) == "LOCK"
            ? line.WordUpper(4) switch { "SHIFT" => SolveLocks.Shift, "FOCAL" => SolveLocks.Focal, "NONE" => SolveLocks.None, _ => pj.Locks }
            : pj.Locks;
        return Edit("solve " + pj.Name, p => p.WithStage(s => s with
        {
            Projectors = s.Projectors.Update(id, j => j with { SolveResidualPx = Math.Round(rms, 3), Locks = locks }),
        }), okText: string.Create(CultureInfo.InvariantCulture, $"{{\"residualPx\":{rms:0.###},\"pins\":{pins.Count}}}"));
    }

    private WireReply WarpReset(WireLine line)
    {
        var id = line.Word(2);
        var o = _store.Project.FindOutput(id);
        if (o is null)
            return Err($"no output '{id}'");
        if (o.Warp.Pin.IsNone && (o.Warp.Lattice is null || o.Warp.Lattice.IsAtRest))
            return Err("the warp is at rest already");
        return Edit("reset warp", p => p with
        {
            Outputs = p.Outputs.Update(id, x => x with
            {
                Warp = x.Warp with { Pin = CornerPin.None, Lattice = x.Warp.Lattice is { } l ? WarpLattice.AtRest(l.Columns, l.Rows, l.Interpolation) : null },
            }),
        });
    }

    private WireReply WarpNode(WireLine line)
    {
        var id = line.Word(2);
        if (!int.TryParse(line.Word(3), NumberStyles.Integer, CultureInfo.InvariantCulture, out var c)
            || !int.TryParse(line.Word(4), NumberStyles.Integer, CultureInfo.InvariantCulture, out var r)
            || !double.TryParse(line.Word(5), NumberStyles.Float, CultureInfo.InvariantCulture, out var dx)
            || !double.TryParse(line.Word(6), NumberStyles.Float, CultureInfo.InvariantCulture, out var dy))
            return Err("WARP NODE <output> <column> <row> <dx> <dy>");
        var o = _store.Project.FindOutput(id);
        if (o is null)
            return Err($"no output '{id}'");
        var lattice = o.Warp.Lattice ?? WarpLattice.AtRest(4, 4);
        if (c < 0 || r < 0 || c >= lattice.Columns || r >= lattice.Rows)
            return Err($"the lattice is {lattice.Columns}×{lattice.Rows}");
        var next = lattice with { Offsets = lattice.Offsets.SetItem(r * lattice.Columns + c, new PlayMap.Core.Numerics.Vec2d(dx, dy)) };
        return Edit("warp node", p => p with { Outputs = p.Outputs.Update(id, x => x with { Warp = x.Warp with { Enabled = true, Lattice = next } }) },
            coalesceKey: $"warp {id} {c} {r}");
    }

    private WireReply LinkTo(WireLine line, ref string host)
    {
        var target = line.Word(2);
        if (target.Length == 0)
            return Err(line.Word(0) + " " + line.Word(1) + " <host[:port]>|OFF [<token>]");
        var next = target.Equals("OFF", StringComparison.OrdinalIgnoreCase) ? "" : target;
        if (next == host)
            return Err(next.Length == 0 ? "not linked" : "already linked to " + next);
        host = next;
        return Ok(next.Length == 0 ? "unlinked" : "linking to " + next);
    }

    private WireReply SyncLead(WireLine line)
    {
        var group = line.Word(2);
        if (group.Length == 0)
            return Err("SYNC LEAD <group>");
        if (_syncLead == group)
            return Err("already leading " + group);
        _syncLead = group;
        return Ok("leading " + group);
    }

    private WireReply SyncFollow(WireLine line)
    {
        _syncLead = null;
        return Ok($"following {line.Word(3)} in {line.Word(2)}");
    }

    private WireReply View(WireLine line)
    {
        // VIEW <view> SHOW <what> [<w>x<h>] | VIEW <view> CAMERA <json> | VIEW <view> KEEP | VIEW <view> HIDE
        var view = line.Word(1);
        switch (line.WordUpper(2))
        {
            case "CAMERA":
                _views[view] = line.RestAfter(3);
                return Ok();
            case "SHOW":
                _views[view] = line.Word(3);
                return Ok();
            case "KEEP":
                return _views.ContainsKey(view) ? Ok() : Err($"no view {view} is shown");
            case "HIDE":
                return _views.Remove(view) ? Ok() : Err($"no view '{view}'");
            default:
                return Err("VIEW <view> SHOW <what> [<w>x<h>] | CAMERA <json> | KEEP | HIDE");
        }
    }

    private WireReply Pairing(WireLine line)
    {
        if (line.WordUpper(1) != "NEW")
            return Err("PAIRING NEW");
        _token = PlayMap.Core.Bus.Pairing.NewToken();
        return Ok("a new pairing token — paired peers pair again");
    }

    /// <summary>A copy in flight from a peer (simulated: eight seconds).</summary>
    private sealed record FakeTransfer(string MediaId, string Name, string From, long StartNs);

    private bool TickTransfers()
    {
        if (_transfers.Count == 0)
            return false;
        foreach (var t in _transfers.Values.ToList())
        {
            if (_clock.NowNs - t.StartNs >= 8 * DemoProject.Second)
            {
                _transfers.Remove(t.MediaId);
                _fetched.Add(t.MediaId);
            }
        }
        return true;
    }

    /// <summary>Where a library item stands on this node: ready, missing, fetching (with progress).</summary>
    public (string State, double Progress) Availability(MediaItem m)
    {
        ArgumentNullException.ThrowIfNull(m);
        if (_transfers.TryGetValue(m.Id, out var t))
            return ("fetching", Math.Clamp((_clock.NowNs - t.StartNs) / (8.0 * DemoProject.Second), 0, 1));
        if (m.Location.Contains("/missing/", StringComparison.Ordinal) && !_fetched.Contains(m.Id))
            return ("missing", 0);
        return ("ready", 1);
    }

    private string MediaListJson()
    {
        var sb = new System.Text.StringBuilder("[");
        var first = true;
        foreach (var m in _store.Project.Media)
        {
            var (state, progress) = Availability(m);
            if (!first)
                sb.Append(',');
            first = false;
            sb.Append(CultureInfo.InvariantCulture, $"{{\"id\":\"{m.Id}\",\"state\":\"{state}\",\"progress\":{progress:0.###}}}");
        }
        return sb.Append(']').ToString();
    }
}

/// <summary>Pin residuals: how far each pin's world point lands from its pixel under the projector's current
/// pose — what the desk shows live while a pin is dragged, and what a solve reports as its RMS.</summary>
public static class PinResiduals
{
    public static double Of(Projector pj, CalibrationPin pin)
    {
        ArgumentNullException.ThrowIfNull(pj);
        ArgumentNullException.ThrowIfNull(pin);
        var p = PlayMap.Core.Geometry.CameraModel.Project(pin.World, pj.Intrinsics, pj.Lens, pj.Pose);
        return p is { } px ? (px - pin.Pixel).Length : double.PositiveInfinity;
    }

    public static double Rms(Projector pj)
    {
        ArgumentNullException.ThrowIfNull(pj);
        double sum = 0;
        var n = 0;
        foreach (var pin in pj.Pins)
        {
            if (!pin.Enabled)
                continue;
            var r = Of(pj, pin);
            if (!double.IsFinite(r))
                continue;
            sum += r * r;
            n++;
        }
        return n == 0 ? 0 : Math.Sqrt(sum / n);
    }
}
