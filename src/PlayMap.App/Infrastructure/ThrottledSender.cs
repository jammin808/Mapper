namespace PlayMap.App.Infrastructure;

/// <summary>Coalesces a drag's edits: the latest line per key is kept, and lines go out at most once per
/// interval (≈ 60 Hz). A line superseded before it was sent is never sent — the engine sees the newest
/// position, never a backlog. <see cref="CommitAsync"/> at the end of a drag sends what is pending at once,
/// so the last position always lands.</summary>
public sealed class ThrottledSender : IDisposable
{
    private readonly Func<string, CancellationToken, Task> _send;
    private readonly TimeSpan _interval;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, string> _pending = new(StringComparer.Ordinal);
    private readonly List<string> _order = [];
    private readonly CancellationTokenSource _disposed = new();
    private Task _pump = Task.CompletedTask;
    private bool _scheduled;
    private bool _isDisposed;

    public ThrottledSender(Func<string, CancellationToken, Task> send, TimeSpan? interval = null, TimeProvider? time = null)
    {
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

    /// <summary>Sends everything pending now (a drag ended) and returns when it has been sent.</summary>
    public async Task CommitAsync(CancellationToken ct = default)
    {
        await FlushAsync(ct);
    }

    private async Task PumpAsync()
    {
        try
        {
            await Task.Delay(_interval, _time, _disposed.Token);
            lock (_gate)
                _scheduled = false;
            await FlushAsync(_disposed.Token);
        }
        catch (OperationCanceledException)
        {
            // Disposed mid-drag: the desk is closing and the engine keeps the last line it received.
        }
    }

    private async Task FlushAsync(CancellationToken ct)
    {
        List<string> lines;
        lock (_gate)
        {
            lines = new List<string>(_order.Count);
            foreach (var key in _order)
                lines.Add(_pending[key]);
            _order.Clear();
            _pending.Clear();
        }
        foreach (var line in lines)
            await _send(line, ct);
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
