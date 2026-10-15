using System.Text;
using System.Text.Json;
using PlayMap.Core.Bus;
using PlayMap.Core.Fleet;
using PlayMap.Core.Wire;
using PlayMap.Net;
using PlayMap.Net.Fleet;

namespace PlayMap.Engine.Fleet;

/// <summary>The fleet package's runtime inside the engine: HEALTH's publisher (a clock runs only while someone watches: a record at
/// least once a second, and at once when any light moves), the control lane's tick gaps (the watchdog's measure), and the machines this
/// node has known (kept across a restart, so preflight can say who is expected).</summary>
internal sealed class FleetParts
{
    private readonly EngineCore _engine;
    private readonly TimeProvider _time;
    private readonly TimeSpan _every;
    private readonly Lock _gate = new();
    private ITimer? _timer;
    private string _lastSignature = "";
    private int _watchers;
    private readonly Lock _lane = new();
    private int _queued;
    private long _lastTickNs;
    private long _windowNs;
    private long _gapNs;
    private long _prevGapNs;
    private bool _measuring;
    private readonly Lock _viewGate = new();
    private string _focus = "";
    private FleetLens _lens = FleetLens.All;
    private (string Focus, FleetLens Lens)? _before;
    private readonly Lock _knownGate = new();
    private readonly SortedDictionary<string, string> _known = new(StringComparer.Ordinal);

    public FleetParts(EngineCore engine, TimeSpan? every = null)
    {
        _engine = engine;
        _time = engine.Services.FleetTime;
        _every = every ?? TimeSpan.FromSeconds(1);
        Hub = engine.Services.HealthHub ?? new HealthHub();
        Hub.WatchersChanged += OnWatchers;
    }

    public HealthHub Hub { get; }

    /// <summary>SUBSCRIBE HEALTH: the record now, then at least once a second and on any light change, latest wins. An open read.</summary>
    public PushOpen OpenHealth(IReadOnlyList<string> args, SessionInfo session, IPushSink sink)
    {
        if (args.Count > 0)
            return PushOpen.Refuse("SUBSCRIBE HEALTH takes no words after it");
        return PushOpen.Open(Hub.Watch(json => sink.Latest("health", "HEALTH " + json)));
    }

    /// <summary>STATE moved: a light may have; checked off the lane, at most one check queued.</summary>
    public void OnStateMoved()
    {
        if (Hub.Watchers == 0 || Interlocked.Exchange(ref _queued, 1) != 0)
            return;
        _ = Task.Run(() =>
        {
            Interlocked.Exchange(ref _queued, 0);
            Publish(due: false);
        });
    }

    /// <summary>Builds the record now and hands it to the watchers when it is due or a light moved.</summary>
    public void Publish(bool due)
    {
        var record = _engine.HealthNow();
        var signature = Signature(record);
        lock (_gate)
        {
            if (!due && signature == _lastSignature)
                return;
            _lastSignature = signature;
        }
        Hub.Publish(HealthJson.Write(record));
    }

    /// <summary>The lights a watcher is told about at once when they move: the machine's, each category's, each output's.</summary>
    internal static string Signature(HealthRecord r)
    {
        var sb = new StringBuilder();
        sb.Append(ShowContext.LightWord(r.Light));
        foreach (var c in r.Categories)
            sb.Append('|').Append(c.Name).Append('=').Append(ShowContext.LightWord(c.Light));
        foreach (var o in r.Facts.Outputs)
            sb.Append('|').Append(o.Id).Append('=').Append(ShowContext.LightWord(r.LightOf(o)));
        return sb.ToString();
    }

    /// <summary>Runs the clock while anyone watches; a new watcher is handed a record built now (the hub's latest may be old).</summary>
    private void OnWatchers(int count)
    {
        bool joined;
        lock (_gate)
        {
            joined = count > _watchers;
            _watchers = count;
            if (count > 0 && _timer is null)
            {
                _timer = _time.CreateTimer(_ => Publish(due: true), null, _every, _every);
            }
            else if (count == 0 && _timer is not null)
            {
                _timer.Dispose();
                _timer = null;
            }
        }
        if (joined)
            Publish(due: true);
    }

    /// <summary>On the lane, each 10 ms tick: the widest gap between ticks, kept per one-second window on the engine's monotonic
    /// clock.</summary>
    public void LaneTick()
    {
        var now = _engine.Services.Mono.NowNs;
        lock (_lane)
        {
            if (_lastTickNs == 0)
                _windowNs = now;
            else if (_measuring && now - _lastTickNs > _gapNs)
                _gapNs = now - _lastTickNs;
            _lastTickNs = now;
            if (now - _windowNs >= 1_000_000_000L)
            {
                // The lane's first second is the node starting (a project put back, the doors opening): measured from the next one.
                _prevGapNs = _measuring ? _gapNs : 0;
                _gapNs = 0;
                _windowNs = now;
                _measuring = true;
            }
        }
    }

    /// <summary>The widest lane gap (ms) over the last one to two seconds, or the silence since the last tick when the lane is stuck
    /// now; −1 until the lane has run its first second.</summary>
    public double LaneGapMs()
    {
        var now = _engine.Services.Mono.NowNs;
        lock (_lane)
        {
            if (!_measuring)
                return -1;
            var worst = Math.Max(Math.Max(_prevGapNs, _gapNs), now - _lastTickNs);
            return worst / 1e6;
        }
    }

    // ---- The board's view: what FLEET FOCUS, LENS, NEXT, PREV and RESET move (a view: not journalled, no revision). ----

    public (string Focus, FleetLens Lens) View
    {
        get
        {
            lock (_viewGate)
                return (_focus, _lens);
        }
    }

    public CommandReply Focus(FleetGraph board, string words)
    {
        if (words.Trim().Length == 0)
            return CommandReply.Fail("FLEET FOCUS takes a machine, an output, an id or a label");
        if (board.Resolve(words) is not { } id)
            return CommandReply.Fail($"nothing on the board answers to '{words.Trim()}' — FLEET lists every machine, output and projector");
        return Move(id, null, "already focused on " + id);
    }

    public CommandReply Lens(IReadOnlyList<string> args)
    {
        if (args.Count != 1 || FleetGraph.ParseLens(args[0]) is not { } lens)
            return CommandReply.Fail("FLEET LENS takes one of ALL, OUTPUTS, PROJECTORS, SYNC, BACKUP, PROBLEMS");
        return Move(null, lens, "the board already shows " + FleetGraph.LensWord(lens));
    }

    public CommandReply Step(FleetGraph board, bool next)
    {
        var (focus, _) = View;
        var id = next ? board.Next(focus.Length > 0 ? focus : null) : board.Prev(focus.Length > 0 ? focus : null);
        if (id is null)
            return CommandReply.Fail("no problems on the board: every thing is green or unknown");
        return Move(id, null, $"{id} is the only problem on the board, and it is focused");
    }

    public CommandReply Reset()
    {
        lock (_viewGate)
        {
            if (_before is not { } before)
                return CommandReply.Fail("the board is at its own view already: nothing focused since the last reset");
            (_focus, _lens) = before;
            _before = null;
            return CommandReply.Success(ViewJson(_focus, _lens));
        }
    }

    private CommandReply Move(string? focus, FleetLens? lens, string same)
    {
        lock (_viewGate)
        {
            var nextFocus = focus ?? _focus;
            var nextLens = lens ?? _lens;
            if (nextFocus == _focus && nextLens == _lens)
                return CommandReply.Fail(same);
            _before ??= (_focus, _lens);
            (_focus, _lens) = (nextFocus, nextLens);
            return CommandReply.Success(ViewJson(_focus, _lens));
        }
    }

    private static string ViewJson(string focus, FleetLens lens) => EngineCore.JsonOut(w =>
    {
        w.WriteStartObject();
        w.WriteString("focus", focus);
        w.WriteString("lens", FleetGraph.LensWord(lens));
        w.WriteEndObject();
    });

    // ---- The machines this node has known: kept across a restart, so preflight can say who is expected. ----

    /// <summary>Every machine heard with a record, by id, with its name.</summary>
    public IReadOnlyDictionary<string, string> Known
    {
        get
        {
            lock (_knownGate)
                return new SortedDictionary<string, string>(_known, StringComparer.Ordinal);
        }
    }

    public void Remember(IReadOnlyList<FleetPeer> peers)
    {
        lock (_knownGate)
        {
            foreach (var p in peers)
            {
                if (p.Health is not null && _known.Count < KnownMax)
                    _known[p.Id] = p.Name;
            }
        }
    }

    /// <summary>The most machines remembered (the board keeps at most 32 live ones).</summary>
    public const int KnownMax = 64;

    public string KnownJson() => EngineCore.JsonOut(w =>
    {
        w.WriteStartObject();
        w.WriteStartArray("known");
        foreach (var (id, name) in Known)
        {
            w.WriteStartObject();
            w.WriteString("id", id);
            w.WriteString("name", name);
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteEndObject();
    });

    public void LoadKnown(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("known", out var known) || known.ValueKind != JsonValueKind.Array)
                return;
            lock (_knownGate)
            {
                foreach (var k in known.EnumerateArray().Take(KnownMax))
                {
                    if (k.ValueKind == JsonValueKind.Object && k.TryGetProperty("id", out var id) && id.GetString() is { Length: > 0 } nodeId)
                        _known[nodeId] = k.TryGetProperty("name", out var name) ? name.GetString() ?? nodeId : nodeId;
                }
            }
        }
        catch (JsonException)
        {
            // A recovery record that does not read leaves no machine expected: preflight says so by listing none.
        }
    }
}
