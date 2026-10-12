namespace PlayMap.Media.Frames;

/// <summary>How far a queued source decodes ahead and when it seeks rather than decodes forward.</summary>
public sealed record QueueSettings
{
    /// <summary>Decoded frames held (ahead of the play head plus the few kept behind it).</summary>
    public int QueueFrames { get; init; } = 12;

    /// <summary>Decode this far ahead of the play head at rate 1 (scaled by the rate above 1).</summary>
    public long LookaheadNs { get; init; } = 400_000_000;

    /// <summary>A request this far past the newest decoded frame seeks instead of decoding forward to it.</summary>
    public long SeekThresholdNs { get; init; } = 1_500_000_000;

    /// <summary>Frames are kept this far behind the newest request, so outputs with different display
    /// latencies (asking slightly different times in one vblank) all find theirs.</summary>
    public long KeepBehindNs { get; init; } = 100_000_000;

    public static readonly QueueSettings Default = new();
}

/// <summary>What the producer reads of the consumer's side in one look.</summary>
internal readonly record struct QueueControl(
    long ConsumerNs,
    bool HasConsumer,
    long ShownNs,
    bool HasShown,
    bool Playing,
    double Rate,
    bool Loop,
    long LoopInNs,
    long LoopOutNs,
    long LoopLengthNs,
    long LoopSerial);

/// <summary>The decoded frames of one source on its continuous timeline, and the consumer's side of the
/// contract: <see cref="Acquire"/> answers the newest frame at or before a time without waiting,
/// <see cref="Prepare"/> moves the play head and asks for a seek when the time leaves the decoded window,
/// <see cref="IsReadyAt"/> reports preroll.
/// <para>Looping: frames carry continuous times (file time + iteration × loop length), so the queue never empties
/// across the loop point. A consumer passes the wrapped media time the transport computes; it is unwrapped to the
/// continuous time nearest the previous request, so a wrap forward reads the next iteration's frames.</para>
/// <para>Thread rules: the consumer methods run on any thread and take the lock for microseconds; the producer
/// methods run on the source's decode thread. Frames are ordered by time; a frame that would break the order is
/// refused.</para></summary>
internal sealed class FrameQueue : IDisposable
{
    private readonly Lock _gate = new();
    private readonly FrameSlot?[] _ring;
    private readonly LeasePool _leases;
    private readonly SourceStats _stats;
    private readonly Action _wake;
    private readonly QueueSettings _settings;
    private readonly ManualResetEventSlim _progress = new(false);
    private int _head;
    private int _count;

    private long _lastRequest;
    private bool _hasLastRequest;
    private long _consumer;
    private bool _hasConsumer;
    private long _shown;
    private bool _hasShown;
    private bool _playing;
    private double _rate = 1;

    private bool _seekPending;
    private long _seekTarget;
    private long _windowStart;
    private bool _ended;
    private bool _closed;

    private bool _loop;
    private long _loopIn;
    private long _loopOut;
    private long _durationNs;
    private long _loopSerial;

    public FrameQueue(QueueSettings settings, SourceStats stats, Action wake)
    {
        _settings = settings;
        _ring = new FrameSlot?[Math.Max(2, settings.QueueFrames)];
        _leases = new LeasePool(_ring.Length + 4);
        _stats = stats;
        _wake = wake;
    }

    public int Capacity => _ring.Length;

    // ---- consumer side ------------------------------------------------------------------------------

    /// <summary>The newest frame at or before the media time, or null when none is decoded. Never waits.</summary>
    public MediaFrameLease? Acquire(long mediaNs)
    {
        MediaFrameLease? lease = null;
        var wake = false;
        lock (_gate)
        {
            if (_closed)
                return null;
            var t = Unwrap(mediaNs, commit: true);
            _consumer = t;
            _hasConsumer = true;
            _shown = t;
            _hasShown = true;
            var idx = Find(t);
            if (idx >= 0)
            {
                var slot = At(idx);
                if (idx == _count - 1 && !_ended && t >= slot.PtsNs + Math.Max(1, slot.DurationNs) && !slot.CountedLate)
                {
                    slot.CountedLate = true;
                    _stats.FrameLate();
                }
                slot.Shown = true;
                slot.AddRef();
                lease = _leases.Rent(slot);
                wake = Trim(t);
            }
            if (!InWindow(t))
            {
                RequestSeek(t);
                wake = true;
            }
        }
        if (wake)
            _wake();
        return lease;
    }

    /// <summary>Moves the play head (and its rate); seeks when the time is outside the decoded window.</summary>
    public void Prepare(long mediaNs, bool playing, double rate)
    {
        lock (_gate)
        {
            if (_closed)
                return;
            _playing = playing;
            _rate = rate > 0 && double.IsFinite(rate) ? rate : 1;
            var t = Unwrap(mediaNs, commit: true);
            _consumer = t;
            _hasConsumer = true;
            // Frames wholly behind the play head make room for the ones ahead of it.
            Trim(t);
            if (!InWindow(t))
                RequestSeek(t);
        }
        _wake();
    }

    /// <summary>True when the frame covering the media time is decoded and waiting.</summary>
    public bool IsReadyAt(long mediaNs)
    {
        lock (_gate)
        {
            if (_closed || _seekPending)
                return false;
            var t = Unwrap(mediaNs, commit: false);
            var idx = Find(t);
            if (idx < 0)
                return false;
            if (idx < _count - 1 || _ended)
                return true;
            var slot = At(idx);
            return t < slot.PtsNs + Math.Max(1, slot.DurationNs);
        }
    }

    /// <summary>Waits (on a signal the producer raises per frame) until <see cref="IsReadyAt"/> holds or the
    /// timeout passes. For lanes and tests, never the render thread.</summary>
    public bool WaitUntilReady(long mediaNs, TimeSpan timeout)
    {
        var deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        while (true)
        {
            _progress.Reset();
            if (IsReadyAt(mediaNs))
                return true;
            var left = deadline - Environment.TickCount64;
            if (left <= 0)
                return false;
            _progress.Wait((int)Math.Min(left, 20));
        }
    }

    /// <summary>Sets the loop: on, from the in point to the out point (0 = the end). The decoded frames belong
    /// to the old timeline, so the queue is cleared and refilled at the play head.</summary>
    public void SetLoop(bool loop, long inNs, long outNs)
    {
        lock (_gate)
        {
            var fileTime = _hasConsumer ? FileTime(_consumer) : inNs;
            _loop = loop;
            _loopIn = Math.Max(0, inNs);
            _loopOut = Math.Max(0, outNs);
            _loopSerial++;
            _hasLastRequest = false;
            _consumer = Unwrap(fileTime, commit: true);
            RequestSeek(_consumer);
        }
        _wake();
    }

    /// <summary>Sets the loop before the producer starts (no seek: nothing is decoded yet).</summary>
    public void SetLoopInitial(bool loop, long inNs, long outNs)
    {
        lock (_gate)
        {
            _loop = loop;
            _loopIn = Math.Max(0, inNs);
            _loopOut = Math.Max(0, outNs);
            _windowStart = _loopIn;
        }
    }

    // ---- producer side ------------------------------------------------------------------------------

    public QueueControl Control()
    {
        lock (_gate)
            return new QueueControl(_consumer, _hasConsumer, _shown, _hasShown, _playing, _rate, _loop && LoopLength > 0,
                _loopIn, LoopOut, LoopLength, _loopSerial);
    }

    /// <summary>Sets the clip's length (the out point when none is set) once known.</summary>
    public void SetDuration(long durationNs)
    {
        lock (_gate)
            _durationNs = Math.Max(0, durationNs);
    }

    /// <summary>Takes a pending seek: the queue is already empty; the window restarts at the target.</summary>
    public bool TakeSeek(out long target)
    {
        lock (_gate)
        {
            target = _seekTarget;
            if (!_seekPending)
                return false;
            _seekPending = false;
            _windowStart = target;
            _ended = false;
            return true;
        }
    }

    /// <summary>Where the decoder starts when nothing has asked yet (the in point).</summary>
    public void SetStart(long t)
    {
        lock (_gate)
        {
            if (!_seekPending && _count == 0)
                _windowStart = t;
        }
    }

    /// <summary>True while a seek is waiting for the producer (frames being made now are stale).</summary>
    public bool SeekPending
    {
        get
        {
            lock (_gate)
                return _seekPending;
        }
    }

    /// <summary>Appends a frame (the queue takes the producer's reference). Refused — and released — while a
    /// seek is pending or when it is not after the newest frame.</summary>
    public bool Publish(FrameSlot slot)
    {
        var accepted = false;
        lock (_gate)
        {
            if (!_closed && !_seekPending && _count < _ring.Length && (_count == 0 || slot.PtsNs > At(_count - 1).PtsNs))
            {
                _ring[(_head + _count) % _ring.Length] = slot;
                _count++;
                accepted = true;
            }
        }
        if (!accepted)
            slot.Release();
        _progress.Set();
        return accepted;
    }

    /// <summary>True when a frame can be appended now (frames wholly behind the play head are let go first).</summary>
    public bool HasRoom()
    {
        lock (_gate)
        {
            if (_count >= _ring.Length && _hasConsumer)
                Trim(_consumer);
            return _count < _ring.Length;
        }
    }

    /// <summary>True when the producer should decode another frame: room in the queue and the newest frame
    /// less than the lookahead (× rate) past the play head.</summary>
    public bool WantMore()
    {
        lock (_gate)
        {
            if (_closed)
                return false;
            if (_count >= _ring.Length && _hasConsumer)
                Trim(_consumer);
            if (_count >= _ring.Length)
                return false;
            if (_count == 0)
                return true;
            var head = _hasConsumer ? _consumer : _windowStart;
            var ahead = At(_count - 1).PtsNs - head;
            return ahead < (long)(_settings.LookaheadNs * Math.Max(1.0, _rate));
        }
    }

    /// <summary>The decoder reached the end and will not loop: the last frame holds.</summary>
    public void MarkEnded(bool ended)
    {
        lock (_gate)
            _ended = ended;
        _progress.Set();
    }

    public bool Ended
    {
        get
        {
            lock (_gate)
                return _ended;
        }
    }

    public int Count
    {
        get
        {
            lock (_gate)
                return _count;
        }
    }

    /// <summary>The newest frame's continuous time, or null when empty.</summary>
    public long? NewestNs
    {
        get
        {
            lock (_gate)
                return _count > 0 ? At(_count - 1).PtsNs : null;
        }
    }

    /// <summary>Releases every frame and refuses new ones (the source is closing).</summary>
    public void Close()
    {
        lock (_gate)
        {
            _closed = true;
            Flush();
        }
        _progress.Set();
    }

    public void Dispose() => _progress.Dispose();

    // ---- internals (under the lock) -----------------------------------------------------------------

    private long LoopOut => _loopOut > 0 ? _loopOut : _durationNs;

    private long LoopLength
    {
        get
        {
            var len = LoopOut - _loopIn;
            return len > 0 ? len : 0;
        }
    }

    private FrameSlot At(int i) => _ring[(_head + i) % _ring.Length]!;

    private int Find(long t)
    {
        for (var i = _count - 1; i >= 0; i--)
        {
            if (At(i).PtsNs <= t)
                return i;
        }
        return -1;
    }

    // The continuous time nearest the previous request for a wrapped media time; a time already continuous maps
    // to itself. Content is a function of file time, so a misread iteration still shows the right picture.
    private long Unwrap(long mediaNs, bool commit)
    {
        var len = _loop ? LoopLength : 0;
        if (len <= 0)
            return mediaNs;
        var reference = _hasLastRequest ? _lastRequest : _loopIn;
        var k = Math.Max(0, (long)Math.Floor((reference - mediaNs) / (double)len + 0.5));
        var t = mediaNs + k * len;
        if (commit)
        {
            _lastRequest = t;
            _hasLastRequest = true;
        }
        return t;
    }

    private long FileTime(long t)
    {
        var len = _loop ? LoopLength : 0;
        if (len <= 0 || t < _loopIn)
            return t;
        return _loopIn + (t - _loopIn) % len;
    }

    private bool InWindow(long t)
    {
        if (_seekPending)
            return t >= _seekTarget && t <= _seekTarget + _settings.SeekThresholdNs;
        long lo, hi;
        if (_count > 0)
        {
            lo = At(0).PtsNs;
            hi = At(_count - 1).PtsNs;
        }
        else
        {
            lo = hi = _windowStart;
        }
        if (t < lo)
            return false;
        return _ended || t <= hi + _settings.SeekThresholdNs;
    }

    private void RequestSeek(long t)
    {
        _seekPending = true;
        _seekTarget = t;
        _ended = false;
        Flush();
    }

    private void Flush()
    {
        while (_count > 0)
        {
            var slot = At(0);
            _ring[_head] = null;
            _head = (_head + 1) % _ring.Length;
            _count--;
            slot.Release();
        }
        _head = 0;
    }

    // Releases frames wholly behind the keep window: a frame goes when its successor starts at or before it.
    private bool Trim(long t)
    {
        var cut = t - _settings.KeepBehindNs;
        var released = false;
        while (_count >= 2 && At(1).PtsNs <= cut)
        {
            var slot = At(0);
            _ring[_head] = null;
            _head = (_head + 1) % _ring.Length;
            _count--;
            if (!slot.Shown && _playing)
                _stats.FrameDropped();
            slot.Release();
            released = true;
        }
        return released;
    }
}
