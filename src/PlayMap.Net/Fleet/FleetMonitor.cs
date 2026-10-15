using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PlayMap.Core.Bus;
using PlayMap.Core.Fleet;
using PlayMap.Net.Bus;

namespace PlayMap.Net.Fleet;

/// <summary>What the fleet monitor is made from: this node's id, the hello's peer table (null until the hello has started), the
/// transport, the clock liveness runs on, and the most peers it subscribes to.</summary>
public sealed record FleetMonitorOptions
{
    public required string SelfId { get; init; }

    public required Func<PeerTable?> Table { get; init; }

    public required IHealthFeeds Feeds { get; init; }

    public TimeProvider Time { get; init; } = TimeProvider.System;

    public int Max { get; init; } = 32;

    /// <summary>How often liveness is aged (stale at 3 s, gone at 6 s, both on <see cref="Time"/>).</summary>
    public TimeSpan SweepEvery { get; init; } = TimeSpan.FromMilliseconds(250);

    public ILogger? Log { get; init; }
}

/// <summary>Every live PlayMap peer's health, subscribed once each (at most <see cref="FleetMonitorOptions.Max"/> at once, a peer dialled
/// again counted like a new one), latest wins. A peer
/// appears when its first record arrives; its liveness is the silence since its last record on the injected clock — stale at 3 s,
/// gone at 6 s — and a gone machine stays, red, until it is heard again or the node restarts. A subscription that ends is dialled
/// again after 0.5 s, doubling to 8 s while nothing is heard (on the same clock). <see cref="Changed"/> is raised when what a board
/// shows moved — a light, a headline, a name, a liveness — never for a record that says the same.</summary>
public sealed class FleetMonitor : IFleetPeers, IAsyncDisposable
{
    private readonly FleetMonitorOptions _o;
    private readonly ILogger _log;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _stop = new();
    private readonly List<Task> _feeds = [];
    private PeerTable? _table;
    private ITimer? _sweep;
    private int _disposed;

    public FleetMonitor(FleetMonitorOptions options)
    {
        _o = options ?? throw new ArgumentNullException(nameof(options));
        _log = options.Log ?? NullLogger.Instance;
    }

    public event Action? Changed;

    /// <summary>Subscriptions running now.</summary>
    public int Subscriptions
    {
        get
        {
            lock (_gate)
                return RunningCount();
        }
    }

    /// <summary>Starts aging liveness, and attaches to the peer table if the hello has started.</summary>
    public void Start()
    {
        lock (_gate)
            _sweep ??= _o.Time.CreateTimer(_ => Sweep(), null, _o.SweepEvery, _o.SweepEvery);
        Attach();
    }

    /// <summary>Takes the hello's peer table once it exists (called again on every engine fact until it does): every PlayMap peer
    /// it holds now, and each one it hears later.</summary>
    public void Attach()
    {
        if (Volatile.Read(ref _table) is not null || Volatile.Read(ref _disposed) != 0 || _o.Table() is not { } table)
            return;
        if (Interlocked.CompareExchange(ref _table, table, null) is not null)
            return;
        table.Changed += OnPeer;
        foreach (var peer in table.Snapshot())
            Consider(peer);
    }

    public IReadOnlyList<FleetPeer> Peers()
    {
        var now = _o.Time.GetTimestamp();
        lock (_gate)
        {
            return _entries.Values
                .Where(e => e.Health is not null || e.Refused)
                .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ThenBy(e => e.Id, StringComparer.Ordinal)
                .Select(e => e.View(_o.Time.GetElapsedTime(e.HeardTs, now)))
                .ToList();
        }
    }

    private void OnPeer(PeerChange change)
    {
        if (change.Kind != PeerChangeKind.Forgotten)
            Consider(change.Peer);
    }

    private void Consider(ShowBusPeer peer)
    {
        if (peer.Kind != ShowBusConstants.KindPlayMap || peer.Id == _o.SelfId || peer.Liveness != PeerLiveness.Live || peer.Hello.Port("http") <= 0)
            return;
        Entry entry;
        lock (_gate)
        {
            if (Volatile.Read(ref _disposed) != 0)
                return;
            if (_entries.TryGetValue(peer.Id, out var known))
            {
                known.Dial = DialOf(peer);
                if (known.Running || RunningCount() >= _o.Max)
                    return;
                entry = known;
            }
            else
            {
                if (RunningCount() >= _o.Max || !RoomForOneMore())
                    return;
                entry = new Entry(peer.Id) { Name = peer.Name, Dial = DialOf(peer), HeardTs = _o.Time.GetTimestamp() };
                _entries[peer.Id] = entry;
            }
            entry.Running = true;
            _feeds.RemoveAll(t => t.IsCompleted);
            var token = _stop.Token;
            _feeds.Add(Task.Run(() => FeedAsync(entry, token), CancellationToken.None));
        }
    }

    /// <summary>The most machines the monitor keeps (twice the subscriptions, at least 64): a machine the board shows stays until the
    /// node restarts; one never heard is let go to make room.</summary>
    public int Kept => Math.Max(64, _o.Max * 2);

    /// <summary>Machines kept now (heard or not).</summary>
    public int Count
    {
        get
        {
            lock (_gate)
                return _entries.Count;
        }
    }

    private int RunningCount() => _entries.Values.Count(e => e.Running);

    /// <summary>Under the gate: true when one more machine can be kept, after letting go of one that is not dialled and was never
    /// heard (it is on no board); false when every kept machine is one the board shows.</summary>
    private bool RoomForOneMore()
    {
        if (_entries.Count < Kept)
            return true;
        var unheard = _entries.Values.FirstOrDefault(e => !e.Running && e.Health is null && !e.Refused);
        if (unheard is null)
            return false;
        _entries.Remove(unheard.Id);
        return true;
    }

    private static FleetDial DialOf(ShowBusPeer peer) =>
        new(peer.Id, peer.Name, peer.Address, peer.Hello.Port("https") > 0 ? peer.Hello.Port("https") : peer.Hello.Port("http"),
            peer.Hello.Port("https") > 0 ? peer.Hello.TlsFingerprint : null);

    /// <summary>One peer's subscription, dialled again with a backoff while the peer is on the table.</summary>
    private async Task FeedAsync(Entry entry, CancellationToken ct)
    {
        var attempt = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                FleetDial dial;
                lock (_gate)
                    dial = entry.Dial;
                var end = await _o.Feeds.RunAsync(dial, record => Take(entry, record), pin => Pin(entry, pin), ct);
                Ended(entry, end);
                attempt = end.Heard ? 0 : Math.Min(attempt + 1, 4);
                if (_table?.Find(entry.Id) is null)
                    break;   // forgotten by the hello: the machine stays on the board as it was last heard
                await Task.Delay(Backoff(entry.Id, attempt), _o.Time, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The monitor stops: the subscription ends with it.
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _log.LogWarning(ex, "The health feed of {Peer} faulted.", entry.Name);
        }
        finally
        {
            lock (_gate)
                entry.Running = false;
        }
    }

    /// <summary>0.5 s, doubling to 8 s while nothing is heard, spread by up to a fifth by the node's id (the same peer, the same spread).</summary>
    public static TimeSpan Backoff(string nodeId, int attempt)
    {
        var baseMs = Math.Min(8000, 500 * Math.Pow(2, Math.Clamp(attempt, 0, 4)));
        var hash = 0u;
        foreach (var c in nodeId ?? "")
            hash = unchecked(hash * 31 + c);
        var spread = hash % 100 / 500.0;
        return TimeSpan.FromMilliseconds(baseMs * (1 + spread));
    }

    private void Take(Entry entry, HealthRecord record)
    {
        bool moved;
        lock (_gate)
        {
            var projection = Projection(record);
            moved = projection != entry.Projection || entry.Heard != FleetHeard.Live || entry.Health is null || entry.Refused;
            entry.Projection = projection;
            entry.Health = record;
            entry.Name = record.Facts.Name.Length > 0 ? record.Facts.Name : entry.Name;
            entry.HeardTs = _o.Time.GetTimestamp();
            entry.Heard = FleetHeard.Live;
            entry.Refused = false;
            entry.Words = entry.Pin == "plain" ? "subscribed (no TLS offered)" : $"subscribed over TLS, certificate {entry.Pin}";
        }
        if (moved)
            Changed?.Invoke();
    }

    private void Pin(Entry entry, string pin)
    {
        lock (_gate)
            entry.Pin = pin;
    }

    private void Ended(Entry entry, FeedEnd end)
    {
        bool moved;
        lock (_gate)
        {
            moved = end.Refused && !entry.Refused;
            entry.Words = end.Words;
            entry.Refused = end.Refused;
            if (end.Refused)
                entry.Pin = "refused";
        }
        if (moved)
            Changed?.Invoke();
    }

    /// <summary>Ages every machine by its silence; raises <see cref="Changed"/> when one goes stale or gone.</summary>
    public void Sweep()
    {
        Attach();
        var now = _o.Time.GetTimestamp();
        var moved = false;
        lock (_gate)
        {
            foreach (var e in _entries.Values)
            {
                if (e.Health is null)
                    continue;
                var heard = FleetPeer.HeardOf((long)_o.Time.GetElapsedTime(e.HeardTs, now).TotalMilliseconds);
                if (heard == e.Heard)
                    continue;
                e.Heard = heard;
                moved = true;
            }
        }
        if (moved)
            Changed?.Invoke();
    }

    /// <summary>What a board shows of a record: lights, names, headline, role, build, revision, links between machines.</summary>
    private static string Projection(HealthRecord r)
    {
        var f = r.Facts;
        var sb = new StringBuilder();
        sb.Append(f.Name).Append('|').Append(ShowContext.LightWord(r.Light)).Append('|').Append(r.Headline).Append('|').Append(f.Role).Append('|')
            .Append(f.Of).Append('|').Append(f.Build).Append('|').Append(f.Rev).Append('|').Append(f.SyncRole).Append('|').Append(f.SyncLeader);
        foreach (var o in f.Outputs)
            sb.Append('|').Append(o.Id).Append(':').Append(o.Name).Append(':').Append(ShowContext.LightWord(r.LightOf(o)));
        foreach (var p in f.Projectors)
            sb.Append('|').Append(p.Id).Append(':').Append(p.Name).Append(':').Append(ShowContext.LightWord(p.Light)).Append(':').Append(p.Words);
        return sb.ToString();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        if (Volatile.Read(ref _table) is { } table)
            table.Changed -= OnPeer;
        Task[] feeds;
        lock (_gate)
        {
            _sweep?.Dispose();
            _sweep = null;
            feeds = [.. _feeds];
        }
        await _stop.CancelAsync();
        await Task.WhenAll(feeds);
        _stop.Dispose();
    }

    private sealed class Entry(string id)
    {
        public string Id { get; } = id;
        public string Name { get; set; } = id;
        public FleetDial Dial { get; set; } = new(id, id, "", 0, null);
        public HealthRecord? Health { get; set; }
        public long HeardTs { get; set; }
        public FleetHeard Heard { get; set; } = FleetHeard.Live;
        public string Words { get; set; } = "";
        public string Pin { get; set; } = "plain";
        public string Projection { get; set; } = "";
        public bool Refused { get; set; }
        public bool Running { get; set; }

        public FleetPeer View(TimeSpan silent) =>
            new(Id, Name, Dial.Host, Health, Health is null ? FleetHeard.Gone : Heard, Words)
            {
                PinRefused = Refused,
                Pin = Pin,
                SilentS = Math.Round(silent.TotalSeconds, 1),
            };
    }
}
