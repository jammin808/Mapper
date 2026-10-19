using PlayMap.Core.Gpu;

namespace PlayMap.Render.Gl;

/// <summary>One-shot GPU signals a <see cref="FenceRing"/> keeps a queue's timeline over (GL sync objects): one inserted after the
/// work queued so far, polled without waiting, deleted once read.</summary>
public interface IFenceSignals
{
    /// <summary>A signal after every command queued so far; never 0.</summary>
    nint Insert();

    /// <summary>True once the GPU passed the signal; never waits.</summary>
    bool IsSignaled(nint signal);

    void Delete(nint signal);
}

/// <summary>A queue's timeline as <see cref="FenceValue"/>s over one-shot signals: each <see cref="Signal"/> takes the queue's next
/// value (1, 2, 3…) and keeps its signal in a ring of <see cref="Capacity"/> slots. A queue completes its work in order, so a value
/// is complete once any later value is: when a new value takes the slot of one still unread, the old signal is read once and deleted,
/// and the old value is answered by the newer ones — the ring wraps without losing a value. <see cref="Completed"/> only grows.
///
/// Threading: <see cref="Signal"/>, the polls and <see cref="Dispose"/> on the thread that made the ring (a GL context's);
/// <see cref="IsComplete"/> from any thread, lock-free — elsewhere it answers from the owner's last poll. Allocation-free.</summary>
public sealed class FenceRing : IDisposable
{
    /// <summary>The slots a ring keeps unless asked for another number.</summary>
    public const int DefaultCapacity = 64;

    private readonly IFenceSignals _signals;
    private readonly ulong[] _values;
    private readonly nint[] _held;
    private readonly int _owner;
    private ulong _last;
    private ulong _completed;
    private bool _disposed;

    public FenceRing(QueueKind queue, IFenceSignals signals, int capacity = DefaultCapacity)
    {
        ArgumentNullException.ThrowIfNull(signals);
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        Queue = queue;
        _signals = signals;
        _values = new ulong[capacity];
        _held = new nint[capacity];
        _owner = Environment.CurrentManagedThreadId;
    }

    public QueueKind Queue { get; }

    public int Capacity => _values.Length;

    /// <summary>The last value signalled (0 before the first).</summary>
    public ulong Last => Volatile.Read(ref _last);

    /// <summary>Every value at or below this is complete.</summary>
    public ulong Completed => Volatile.Read(ref _completed);

    /// <summary>The queue's next value, complete once the GPU passed every command queued before it.</summary>
    public FenceValue Signal()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        AssertOwner();
        var value = _last + 1;
        var slot = (int)(value % (ulong)_values.Length);
        if (_held[slot] != 0)
        {
            // The value capacity behind this one: read once, then answered by the values after it.
            if (_signals.IsSignaled(_held[slot]))
                Advance(_values[slot]);
            _signals.Delete(_held[slot]);
            _held[slot] = 0;
        }
        _held[slot] = _signals.Insert();
        _values[slot] = value;
        Volatile.Write(ref _last, value);
        return new FenceValue(Queue, value);
    }

    /// <summary>Whether the GPU passed the value; never waits. Value 0 is complete; a value not yet signalled is not; once true,
    /// true for it and every value before it. A value of another queue is refused.</summary>
    public bool IsComplete(in FenceValue value)
    {
        if (value.Queue != Queue && value.Value != 0)
            throw new ArgumentException($"A {value.Queue} value asked of the {Queue} queue's ring.", nameof(value));
        if (value.Value <= Completed)
            return true;
        if (value.Value > Last || _disposed || Environment.CurrentManagedThreadId != _owner)
            return false;
        Poll();
        return value.Value <= Completed;
    }

    /// <summary>Reads the held signals oldest first and advances <see cref="Completed"/> past each one passed, stopping at the first
    /// still running.</summary>
    private void Poll()
    {
        var n = (ulong)_values.Length;
        var first = Math.Max(_completed + 1, _last >= n ? _last - n + 1 : 1);
        for (var v = first; v <= _last; v++)
        {
            var slot = (int)(v % n);
            if (_held[slot] == 0 || _values[slot] != v)
                continue;
            if (!_signals.IsSignaled(_held[slot]))
                return;
            Advance(v);
            _signals.Delete(_held[slot]);
            _held[slot] = 0;
        }
    }

    private void Advance(ulong value)
    {
        if (value > _completed)
            Volatile.Write(ref _completed, value);
    }

    private void AssertOwner()
    {
        if (Environment.CurrentManagedThreadId != _owner)
            throw new InvalidOperationException("A fence ring is signalled only on the thread that made it.");
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        AssertOwner();
        for (var i = 0; i < _held.Length; i++)
        {
            if (_held[i] != 0)
                _signals.Delete(_held[i]);
            _held[i] = 0;
        }
        _disposed = true;
    }
}
