namespace PlayMap.App.Infrastructure;

/// <summary>Coalesces a drag's edits: the latest line per key is kept, and lines go out at most once per
/// interval (≈ 60 Hz). A line superseded before it was sent is never sent — the engine sees the newest
/// position, never a backlog. <see cref="CommitAsync"/> at the end of a drag sends what is pending at once,
/// so the last position always lands — except a line equal to the last one the interval sent under its key since the last commit
/// (a person who stops, then lets go): that one would move nothing, and the node refuses a verb that changes nothing. A refused send
/// is not remembered, so its line goes again at the commit; each commit forgets what the intervals sent, so the next gesture starts
/// afresh.</summary>
public sealed class ThrottledSender : IDisposable
{
    private readonly Func<string, CancellationToken, Task<bool>> _send;
    private readonly TimeSpan _interval;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, string> _pending = new(StringComparer.Ordinal);
    private readonly List<string> _order = [];
    private readonly Dictionary<string, string> _flushed = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _disposed = new();
    private long _commits;
    private Task _pump = Task.CompletedTask;
    private bool _scheduled;
    private bool _isDisposed;

    /// <param name="send">Sends a line; its task completes when the node answered, and every line counts as taken.</param>
    public ThrottledSender(Func<string, CancellationToken, Task> send, TimeSpan? interval = null, TimeProvider? time = null)
        : this(Taken(send), interval, time)
    {
    }

    /// <param name="send">Sends a line; its task answers whether the node took it (false: refused, or not sent at all).</param>
    public ThrottledSender(Func<string, CancellationToken, Task<bool>> send, TimeSpan? interval = null, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(send);
        _send = send;
        _interval = interval ?? TimeSpan.FromMilliseconds(16);
        _time = time ?? TimeProvider.System;
    }

    /// <summary>How many lines are waiting.</summary>
    public int Pending
    {
        get
        {
            lock (_gate)
                return _order.Count;
        }
    }

    /// <summary>Queues a line under a key ("op_1 warp 2 3"); a newer line under the same key replaces it.</summary>
    public void Post(string key, string line)
    {
        lock (_gate)
        {
            if (_isDisposed)
                return;
            if (!_pending.ContainsKey(key))
                _order.Add(key);
            _pending[key] = line;
            if (_scheduled)
                return;
            _scheduled = true;
        }
        _pump = PumpAsync();
    }

    /// <summary>Sends everything pending now (a drag ended) and returns when it has been sent; a pending line equal to the last line the
    /// interval sent under its key is dropped.</summary>
    public async Task CommitAsync(CancellationToken ct = default)
    {
        await FlushAsync(commit: true, ct);
    }

    private static Func<string, CancellationToken, Task<bool>> Taken(Func<string, CancellationToken, Task> send)
    {
        ArgumentNullException.ThrowIfNull(send);
        return async (line, ct) =>
        {
            await send(line, ct);
            return true;
        };
    }

    private async Task PumpAsync()
    {
        try
        {
            await Task.Delay(_interval, _time, _disposed.Token);
            lock (_gate)
                _scheduled = false;
            await FlushAsync(commit: false, _disposed.Token);
        }
        catch (OperationCanceledException)
        {
            // Disposed mid-drag: the desk is closing and the engine keeps the last line it received.
        }
    }

    private async Task FlushAsync(bool commit, CancellationToken ct)
    {
        List<(string Key, string Line)> lines;
        long gesture;
        lock (_gate)
        {
            lines = new List<(string, string)>(_order.Count);
            foreach (var key in _order)
            {
                var line = _pending[key];
                if (commit && _flushed.TryGetValue(key, out var last) && string.Equals(last, line, StringComparison.Ordinal))
                    continue;
                lines.Add((key, line));
            }
            _order.Clear();
            _pending.Clear();
            if (commit)
            {
                _flushed.Clear();
                _commits++;
            }
            gesture = _commits;
        }
        foreach (var (key, line) in lines)
        {
            if (!commit)
                Remember(key, line, gesture);
            if (!await _send(line, ct) && !commit)
                Forget(key, line);
        }
    }

    /// <summary>The interval sent this line under its key, in the gesture it was taken in (a commit since then forgot it).</summary>
    private void Remember(string key, string line, long gesture)
    {
        lock (_gate)
        {
            if (gesture == _commits)
                _flushed[key] = line;
        }
    }

    /// <summary>The node refused the line: it is not the node's, so a commit sends it again.</summary>
    private void Forget(string key, string line)
    {
        lock (_gate)
        {
            if (_flushed.TryGetValue(key, out var last) && string.Equals(last, line, StringComparison.Ordinal))
                _flushed.Remove(key);
        }
    }

    /// <summary>The scheduled flush, for tests that wait on the interval.</summary>
    public Task Pump => _pump;

    public void Dispose()
    {
        lock (_gate)
        {
            if (_isDisposed)
                return;
            _isDisposed = true;
        }
        _disposed.Cancel();
        _disposed.Dispose();
    }
}
