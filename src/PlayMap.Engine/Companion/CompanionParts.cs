using System.Text.Json;
using PlayMap.Core.Wire;
using PlayMap.Net;

namespace PlayMap.Engine.Companion;

/// <summary>The node's side of two-way control, held by the engine: the controllers it knows, the event ring, INDEX (cached per
/// project revision, pushed to SUBSCRIBE INDEX sessions when its structure or labels move), the thumbnails and the THUMBS
/// watchers, and the fault diff that raises fault.raised and fault.cleared. Pushes are worked off the publishing thread, latest
/// wins: a burst of edits is one INDEX.</summary>
internal sealed class CompanionParts
{
    private readonly EngineCore _engine;
    private readonly Lock _gate = new();
    /// <summary>One INDEX push at a time, and a subscription's first INDEX inside it: a push made from an older snapshot never lands
    /// after one made from a newer.</summary>
    private readonly Lock _indexPush = new();
    /// <summary>One THUMBS round at a time, for the same reason.</summary>
    private readonly Lock _thumbsPush = new();
    private readonly List<IndexWatch> _indexWatches = [];
    private readonly List<ThumbWatch> _thumbWatches = [];
    private Dictionary<string, string> _faults = new(StringComparer.Ordinal);
    private IndexDocument? _index;
    private int _indexQueued;
    private int _thumbsQueued;
    private int _diffing;
    private int _diffAgain;

    public CompanionParts(EngineCore engine)
    {
        _engine = engine;
        Clients = new ClientTable();
        Events = new EventRing(() => engine.Services.Wall.UnixMs);
        var home = engine.Services.JournalPath is { } journal ? Path.GetDirectoryName(Path.GetFullPath(journal)) : null;
        Thumbs = new ThumbnailService(engine, home is null ? null : Path.Combine(home, "thumbs"));
        Clients.Changed += engine.FactsChanged;
        Events.Changed += engine.FactsChanged;
    }

    public ClientTable Clients { get; }

    public EventRing Events { get; }

    public ThumbnailService Thumbs { get; }

    /// <summary>INDEX now. <paramref name="fresh"/> writes it again (the sections' rows read published state); otherwise the copy
    /// made for this project revision serves (STATE's project.index).</summary>
    public IndexDocument Index(bool fresh = true)
    {
        var snap = _engine.Snapshot;
        var cached = Volatile.Read(ref _index);
        if (!fresh && cached is not null && cached.Revision == snap.Project.Revision)
            return cached;
        var doc = IndexWriter.Write(snap.Project.Project, snap.Project.Revision, snap, _engine.Routes.IndexSections);
        Volatile.Write(ref _index, doc);
        return doc;
    }

    /// <summary>A show event into the ring, timed when it happened: an item's end at its scheduled instant, not when the lane noticed.</summary>
    public void TakeEvent(ShowEvent e)
    {
        var lateNs = Math.Max(0, _engine.LeaderNowNs - e.AtLeaderNs);
        Events.Add(e.Seq, e.Kind, e.AtUnixMs - lateNs / 1_000_000, e.AtLeaderNs, e.FieldsJson);
    }

    public void OnProjectMoved()
    {
        QueueIndex();
        QueueThumbs();
    }

    public void OnStateMoved()
    {
        DiffFaults();
        QueueThumbs();
    }

    private void QueueIndex()
    {
        lock (_gate)
        {
            if (_indexWatches.Count == 0)
                return;
        }
        if (Interlocked.Exchange(ref _indexQueued, 1) != 0)
            return;
        _ = Task.Run(() =>
        {
            Interlocked.Exchange(ref _indexQueued, 0);
            PushIndex();
        });
    }

    private void QueueThumbs()
    {
        lock (_gate)
        {
            if (_thumbWatches.Count == 0)
                return;
        }
        if (Interlocked.Exchange(ref _thumbsQueued, 1) != 0)
            return;
        _ = Task.Run(() =>
        {
            Interlocked.Exchange(ref _thumbsQueued, 0);
            PushThumbs();
        });
    }

    /// <summary>SUBSCRIBE INDEX: INDEX at once, then again whenever its structure or labels move.</summary>
    public PushOpen OpenIndex(IReadOnlyList<string> args, SessionInfo session, IPushSink sink)
    {
        if (args.Count > 0)
            return PushOpen.Refuse("SUBSCRIBE INDEX takes no words after it");
        var watch = new IndexWatch(this, sink);
        lock (_indexPush)
        {
            // Listed before its first INDEX is made, so a change after that INDEX queues a push that finds it.
            lock (_gate)
                _indexWatches.Add(watch);
            var doc = Index();
            watch.Sig = doc.Sig;
            watch.Labels = doc.Labels;
            sink.Latest("index", "INDEX " + doc.Json);
        }
        return PushOpen.Open(watch);
    }

    private void PushIndex()
    {
        lock (_indexPush)
        {
            List<IndexWatch> watches;
            lock (_gate)
                watches = [.. _indexWatches];
            if (watches.Count == 0)
                return;
            var doc = Index();
            foreach (var w in watches)
            {
                if (w.Sig == doc.Sig && w.Labels == doc.Labels)
                    continue;
                w.Sig = doc.Sig;
                w.Labels = doc.Labels;
                w.Sink.Latest("index", "INDEX " + doc.Json);
            }
        }
    }

    /// <summary>SUBSCRIBE EVENTS: every event from now on, in order (EVENTS SINCE replays what came before).</summary>
    public PushOpen OpenEvents(IReadOnlyList<string> args, SessionInfo session, IPushSink sink) =>
        args.Count > 0 ? PushOpen.Refuse("SUBSCRIBE EVENTS takes no words after it — EVENTS SINCE <seq> replays") : PushOpen.Open(Events.Subscribe(sink));

    /// <summary>SUBSCRIBE THUMBS &lt;ref&gt;…: the watched set (it replaces the session's last); THUMBS {ref: etag} at once and whenever
    /// one of them moves (null: no picture of it now).</summary>
    public PushOpen OpenThumbs(IReadOnlyList<string> args, SessionInfo session, IPushSink sink)
    {
        if (!session.Paired)
            return PushOpen.Refuse(Net.Commands.WireWords.NotPaired);
        var max = _engine.Services.Settings.Companion.MaxThumbsPerSession;
        var refs = args.Distinct(StringComparer.Ordinal).ToList();
        if (refs.Count > max)
            return PushOpen.Refuse($"SUBSCRIBE THUMBS watches at most {max} pictures; this asked {refs.Count}");
        var watch = new ThumbWatch(this, sink, refs);
        lock (_thumbsPush)
        {
            lock (_gate)
                _thumbWatches.Add(watch);
            watch.Push(Etags(refs));
        }
        return PushOpen.Open(watch);
    }

    private Dictionary<string, string?> Etags(IEnumerable<string> refs)
    {
        var map = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var r in refs)
            map[r] = Thumbs.EtagOf(r, ThumbnailService.DefaultSize, ThumbnailService.DefaultSize, Net.Companion.ThumbFormat.Jpeg).Etag;
        return map;
    }

    private void PushThumbs()
    {
        lock (_thumbsPush)
        {
            List<ThumbWatch> watches;
            lock (_gate)
                watches = [.. _thumbWatches];
            foreach (var w in watches)
                w.Push(Etags(w.Refs));
        }
    }

    /// <summary>fault.raised and fault.cleared from the node's faults as STATE lists them (a part's new words raise it again). One
    /// diff runs at a time; a STATE move while one runs (its own events' moves among them) makes it look once more, so a change that
    /// came in meanwhile is never lost.</summary>
    private void DiffFaults()
    {
        if (_engine.Services.Faults is not { } source)
            return;
        Interlocked.Exchange(ref _diffAgain, 1);
        while (Volatile.Read(ref _diffAgain) != 0 && Interlocked.CompareExchange(ref _diffing, 1, 0) == 0)
        {
            try
            {
                while (Interlocked.Exchange(ref _diffAgain, 0) != 0)
                    DiffOnce(source);
            }
            finally
            {
                Interlocked.Exchange(ref _diffing, 0);
            }
        }
    }

    private void DiffOnce(INodeFaults source)
    {
        var now = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var f in source.Faults())
            now[f.Part] = f.Words;
        Dictionary<string, string> before;
        lock (_gate)
        {
            before = _faults;
            _faults = now;
        }
        var at = _engine.LeaderNowNs;
        foreach (var (part, words) in now)
            if (!before.TryGetValue(part, out var old) || old != words)
                _engine.RaiseEvent("fault.raised", at, w => { w.WriteString("part", part); w.WriteString("words", words); });
        foreach (var (part, words) in before)
            if (!now.ContainsKey(part))
                _engine.RaiseEvent("fault.cleared", at, w => { w.WriteString("part", part); w.WriteString("words", words); });
    }

    private void Drop(IndexWatch w)
    {
        lock (_gate)
            _indexWatches.Remove(w);
    }

    private void Drop(ThumbWatch w)
    {
        lock (_gate)
            _thumbWatches.Remove(w);
    }

    private sealed class IndexWatch(CompanionParts parts, IPushSink sink) : IDisposable
    {
        public IPushSink Sink => sink;

        public string Sig { get; set; } = "";

        public string Labels { get; set; } = "";

        public void Dispose() => parts.Drop(this);
    }

    private sealed class ThumbWatch(CompanionParts parts, IPushSink sink, IReadOnlyList<string> refs) : IDisposable
    {
        private readonly Lock _gate = new();
        private string _last = "";

        public IReadOnlyList<string> Refs => refs;

        /// <summary>The whole watched set's ETags when any moved: one latest-wins line is always exact.</summary>
        public void Push(Dictionary<string, string?> etags)
        {
            var json = EngineCore.JsonOut(w =>
            {
                w.WriteStartObject();
                foreach (var r in refs)
                {
                    if (etags.GetValueOrDefault(r) is { } e)
                        w.WriteString(r, e);
                    else
                        w.WriteNull(r);
                }
                w.WriteEndObject();
            });
            lock (_gate)
            {
                if (json == _last)
                    return;
                _last = json;
            }
            sink.Latest("thumbs", "THUMBS " + json);
        }

        public void Dispose() => parts.Drop(this);
    }
}
