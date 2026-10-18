namespace PlayMap.Render.Timing;

/// <summary>An output's whole-frame delay: <c>d + 1</c> pictures in a ring, the newest written each frame, the one written <c>d</c>
/// frames ago presented. Until <c>d</c> frames have been written the oldest written one is presented (never one not yet drawn).
/// Swaps indices only; the pictures are made when the delay changes (off the steady frame path), so a warm frame allocates nothing.
/// Render thread.</summary>
public sealed class DelayRing<T> : IDisposable
    where T : class, IDisposable
{
    private T[] _slots = [];
    private int _head = -1;
    private int _written;

    public int Delay { get; private set; }

    /// <summary>Pictures held now (d + 1, or 0 with no delay).</summary>
    public int Count => _slots.Length;

    /// <summary>One of the pictures held (they are all made alike), or null with none.</summary>
    public T? First => _slots.Length > 0 ? _slots[0] : null;

    /// <summary>Sets the delay; a changed one (or a picture of another size, which the caller says by <paramref name="remake"/>) makes
    /// its pictures again with <paramref name="make"/>. 0 lets them all go.</summary>
    public void Configure(int delay, Func<T> make, bool remake = false)
    {
        ArgumentNullException.ThrowIfNull(make);
        ArgumentOutOfRangeException.ThrowIfNegative(delay);
        if (delay == Delay && !remake && (delay == 0 || _slots.Length == delay + 1))
            return;
        Release();
        Delay = delay;
        if (delay == 0)
            return;
        _slots = new T[delay + 1];
        for (var i = 0; i < _slots.Length; i++)
            _slots[i] = make();
    }

    /// <summary>This frame's picture to write (the newest slot).</summary>
    public T Next()
    {
        if (_slots.Length == 0)
            throw new InvalidOperationException("a ring with no delay holds no pictures");
        _head = (_head + 1) % _slots.Length;
        _written = Math.Min(_written + 1, _slots.Length);
        return _slots[_head];
    }

    /// <summary>The picture to present this frame, after <see cref="Next"/> was written: the one written <see cref="Delay"/> frames
    /// ago, or the oldest written while the ring fills.</summary>
    public T Presented()
    {
        if (_head < 0)
            throw new InvalidOperationException("nothing written yet");
        var back = Math.Min(Delay, _written - 1);
        return _slots[(_head - back + _slots.Length) % _slots.Length];
    }

    /// <summary>Forgets what was written (the pictures stay): the next frame presents what it writes, then the delay builds again.</summary>
    public void Restart()
    {
        _head = -1;
        _written = 0;
    }

    private void Release()
    {
        foreach (var s in _slots)
            s.Dispose();
        _slots = [];
        _head = -1;
        _written = 0;
        Delay = 0;
    }

    public void Dispose() => Release();
}
