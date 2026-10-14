using System.Buffers;
using System.Diagnostics;
using System.Text;

namespace PlayMap.Net;

/// <summary>What a push carries; each kind keeps one slot per connection.</summary>
public enum PushKind
{
    State,
    Context,
    Project,
}

/// <summary>One writer per connection (Patterns' rule): replies leave in the order they were said and are never
/// dropped — a peer that leaves more than the limit unread is cut, not buffered without end; pushes are latest-wins,
/// one slot per kind, so a slow reader gets the newest STATE and never a backlog, and a push identical to the last
/// of its kind this connection was sent is skipped. Every write has a deadline; a write that misses it closes the
/// connection. Nothing here blocks the caller: <see cref="Say(string)"/> and <see cref="Push"/> only queue.</summary>
public sealed class LineWriter : IAsyncDisposable
{
    private static readonly int Kinds = Enum.GetValues<PushKind>().Length;

    private readonly Func<ReadOnlyMemory<byte>, CancellationToken, Task> _write;
    private readonly int _maxQueued;
    private readonly TimeSpan _writeDeadline;
    private readonly Action _cut;
    private readonly object _gate = new();
    private readonly Queue<Item> _replies = new();
    private readonly string?[] _pushes = new string?[Kinds];
    private readonly string?[] _lastWritten = new string?[Kinds];
    private readonly Dictionary<string, string> _keyed = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _signal = new(0, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;
    private bool _writing;
    private volatile bool _closed;

    /// <param name="stream">The connection's stream; the writer owns writing to it, never reading.</param>
    /// <param name="maxQueued">Replies a peer may leave unwritten before it is cut.</param>
    /// <param name="writeDeadline">How long one write may take.</param>
    /// <param name="cut">Closes the connection (aborts the socket so the reader wakes); called at most once.</param>
    public LineWriter(Stream stream, int maxQueued, TimeSpan writeDeadline, Action cut)
    {
        ArgumentNullException.ThrowIfNull(stream);
        _write = async (bytes, ct) =>
        {
            await stream.WriteAsync(bytes, ct);
            await stream.FlushAsync(ct);
        };
        _maxQueued = maxQueued;
        _writeDeadline = writeDeadline;
        _cut = cut;
        _loop = Task.Run(RunAsync, CancellationToken.None);
    }

    /// <summary>The connection was cut (overflow, a missed write deadline, a write error) or the writer stopped.</summary>
    public bool Closed => _closed;

    /// <summary>Lines written since start (replies and pushes).</summary>
    public long Written { get; private set; }

    /// <summary>Queues a reply, in order.</summary>
    public void Say(string line) => Enqueue(new Item(line, null, null));

    /// <summary>Queues a reply whose text is made at the moment it is written (a PONG's t3).</summary>
    public void Say(Func<string> lineAtWrite) => Enqueue(new Item(null, lineAtWrite, null));

    /// <summary>Queues a push in order with the replies (the greeting STATE after HELLO) and records it as the last
    /// of its kind once written, so a latest-wins push that repeats it is skipped.</summary>
    public void SayPush(PushKind kind, string line) => Enqueue(new Item(line, null, kind));

    /// <summary>Sets the latest push of a kind; an older one not yet written is replaced.</summary>
    public void Push(PushKind kind, string line)
    {
        lock (_gate)
        {
            if (_closed)
                return;
            _pushes[(int)kind] = line;
            Wake();
        }
    }

    /// <summary>Sets the latest push of a topic's key (INDEX, a thumbnail, HEALTH); an older one not yet written is replaced.</summary>
    public void PushKeyed(string key, string line)
    {
        lock (_gate)
        {
            if (_closed)
                return;
            _keyed[key] = line;
            Wake();
        }
    }

    /// <summary>Queues a topic's line in order with the replies while fewer than <paramref name="bound"/> wait (and never more than half
    /// the connection's limit); false (nothing queued, the connection kept) when the bound is reached, so the topic closes and the
    /// connection's own replies are never cut by it.</summary>
    public bool TrySay(string line, int bound)
    {
        lock (_gate)
        {
            if (_closed || _replies.Count >= Math.Min(bound, _maxQueued / 2))
                return false;
            _replies.Enqueue(new Item(line, null, null));
            Wake();
            return true;
        }
    }

    /// <summary>Waits (at most <paramref name="max"/>) until everything queued has been written — the last word
    /// before a close.</summary>
    public async Task FlushAsync(TimeSpan max)
    {
        var started = Stopwatch.GetTimestamp();
        while (!_closed && Stopwatch.GetElapsedTime(started) < max)
        {
            lock (_gate)
            {
                if (_replies.Count == 0 && !_writing && _keyed.Count == 0 && Array.TrueForAll(_pushes, p => p is null))
                    return;
            }
            await Task.Delay(5, CancellationToken.None);
        }
    }

    private void Enqueue(Item item)
    {
        var overflow = false;
        lock (_gate)
        {
            if (_closed)
                return;
            if (_replies.Count >= _maxQueued)
            {
                overflow = true;
            }
            else
            {
                _replies.Enqueue(item);
                Wake();
            }
        }
        if (overflow)
            Cut();
    }

    private void Wake()
    {
        if (_signal.CurrentCount == 0)
            _signal.Release();
    }

    private void Cut()
    {
        if (_closed)
            return;
        _closed = true;
        try
        {
            _cut();
        }
        catch (ObjectDisposedException)
        {
            // The socket is gone already: the cut has happened.
        }
    }

    private async Task RunAsync()
    {
        var batch = new List<(string Text, PushKind? Kind)>();
        var bytes = new ArrayBufferWriter<byte>(4096);
        try
        {
            while (!_closed)
            {
                await _signal.WaitAsync(_stop.Token);
                batch.Clear();
                lock (_gate)
                {
                    while (_replies.TryDequeue(out var item))
                        batch.Add((item.Text ?? item.Late!(), item.Kind));
                    for (var k = 0; k < Kinds; k++)
                    {
                        if (_pushes[k] is { } push)
                        {
                            batch.Add((push, (PushKind)k));
                            _pushes[k] = null;
                        }
                    }
                    foreach (var keyed in _keyed.Values)
                        batch.Add((keyed, null));
                    _keyed.Clear();
                    _writing = batch.Count > 0;
                }
                if (batch.Count == 0)
                    continue;
                bytes.ResetWrittenCount();
                var count = 0;
                foreach (var (text, kind) in batch)
                {
                    if (kind is { } k)
                    {
                        if (string.Equals(_lastWritten[(int)k], text, StringComparison.Ordinal))
                            continue;
                        _lastWritten[(int)k] = text;
                    }
                    Encoding.UTF8.GetBytes(text, bytes);
                    bytes.Write("\n"u8);
                    count++;
                }
                if (count > 0)
                {
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                    deadline.CancelAfter(_writeDeadline);
                    await _write(bytes.WrittenMemory, deadline.Token);
                    Written += count;
                }
                lock (_gate)
                    _writing = false;
            }
        }
        catch (OperationCanceledException)
        {
            // Stopped, or a write missed its deadline: either way the connection is done.
            Cut();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // The peer went away mid-write: the reader sees the close too.
            Cut();
        }
        finally
        {
            lock (_gate)
                _writing = false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _closed = true;
        await _stop.CancelAsync();
        try
        {
            await _loop;
        }
        catch (OperationCanceledException)
        {
            // The loop ends on the stop it was given.
        }
        _stop.Dispose();
        _signal.Dispose();
    }

    private sealed record Item(string? Text, Func<string>? Late, PushKind? Kind);
}
