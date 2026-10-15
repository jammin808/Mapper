namespace PlayMap.Net.Fleet;

/// <summary>This node's HEALTH for whoever watches it — SUBSCRIBE HEALTH on a line session, a peer's fleet monitor on
/// <c>/health</c>: the latest record's JSON, handed to every watcher when it is published (latest wins; a watcher's own queue keeps
/// only the newest). A new watcher gets the latest at once. <see cref="WatchersChanged"/> tells the publisher when to run its
/// clock (only while someone watches).</summary>
public sealed class HealthHub
{
    private readonly Lock _gate = new();
    private readonly List<Watcher> _watches = [];
    private string? _latest;

    /// <summary>Raised with the number of watchers after one comes or goes (outside the hub's lock).</summary>
    public event Action<int>? WatchersChanged;

    public int Watchers
    {
        get
        {
            lock (_gate)
                return _watches.Count;
        }
    }

    /// <summary>The last record published (null before the first).</summary>
    public string? Latest
    {
        get
        {
            lock (_gate)
                return _latest;
        }
    }

    /// <summary>Watches the records: <paramref name="onRecord"/> gets each one published (and the latest now, when there is one).
    /// Disposing the answer stops it.</summary>
    public IDisposable Watch(Action<string> onRecord)
    {
        ArgumentNullException.ThrowIfNull(onRecord);
        var watch = new Watcher(this, onRecord);
        string? now;
        int count;
        lock (_gate)
        {
            _watches.Add(watch);
            now = _latest;
            count = _watches.Count;
        }
        if (now is not null)
            onRecord(now);
        WatchersChanged?.Invoke(count);
        return watch;
    }

    /// <summary>Hands a record to every watcher.</summary>
    public void Publish(string recordJson)
    {
        ArgumentException.ThrowIfNullOrEmpty(recordJson);
        List<Watcher> watches;
        lock (_gate)
        {
            _latest = recordJson;
            watches = [.. _watches];
        }
        foreach (var w in watches)
            w.Take(recordJson);
    }

    private void Remove(Watcher watch)
    {
        int count;
        lock (_gate)
        {
            if (!_watches.Remove(watch))
                return;
            count = _watches.Count;
        }
        WatchersChanged?.Invoke(count);
    }

    private sealed class Watcher(HealthHub hub, Action<string> onRecord) : IDisposable
    {
        private int _gone;

        public void Take(string json)
        {
            if (Volatile.Read(ref _gone) == 0)
                onRecord(json);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _gone, 1) == 0)
                hub.Remove(this);
        }
    }
}
