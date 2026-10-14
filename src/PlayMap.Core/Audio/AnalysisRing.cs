namespace PlayMap.Core.Audio;

/// <summary>The last frames of one listening input, by leader time: one writer (the analysing thread) and any number of readers
/// (the render thread, the lane, STATE), none of them blocking. Each slot carries a sequence number the writer makes odd while it
/// writes and even when done, so a reader that raced the writer sees the change and reads again; frames are written in leader-time
/// order, so a reader finds the frame at an instant by bisection.</summary>
public sealed class AnalysisRing : IAnalysisSink
{
    private readonly AnalysisFrame[] _slots;
    private readonly long[] _stamps;
    private readonly long[] _seq;
    private readonly int _capacity;
    private long _count;
    private long _lastStamp = long.MinValue;

    /// <param name="capacity">Frames kept (1600 = 8 s at 200 frames a second).</param>
    public AnalysisRing(int capacity = 1600)
    {
        if (capacity < 16)
            throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "a ring keeps at least 16 frames");
        _capacity = capacity;
        _slots = new AnalysisFrame[capacity];
        for (var i = 0; i < capacity; i++)
            _slots[i] = new AnalysisFrame();
        _stamps = new long[capacity];
        _seq = new long[capacity];
    }

    /// <summary>Frames written since the ring was made or cleared.</summary>
    public long Count => Volatile.Read(ref _count);

    /// <summary>The newest frame's leader instant (long.MinValue = none yet).</summary>
    public long LatestLeaderNs => Count == 0 ? long.MinValue : Volatile.Read(ref _stamps[(int)((Count - 1) % _capacity)]);

    /// <summary>Keeps a copy of the frame (the writer's thread only). A stamp earlier than the previous frame's is raised to just
    /// after it, so readers always see leader time move forward.</summary>
    public void Frame(AnalysisFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var n = _count;
        var i = (int)(n % _capacity);
        var stamp = Math.Max(frame.LeaderNs, _lastStamp == long.MinValue ? frame.LeaderNs : _lastStamp + 1);
        _lastStamp = stamp;
        Volatile.Write(ref _seq[i], 2 * n + 1);
        frame.CopyTo(_slots[i]);
        _slots[i].LeaderNs = stamp;
        Volatile.Write(ref _stamps[i], stamp);
        Volatile.Write(ref _seq[i], 2 * n + 2);
        Volatile.Write(ref _count, n + 1);
    }

    /// <summary>Forgets every frame (the writer's thread only, or while no writer runs).</summary>
    public void Clear()
    {
        Volatile.Write(ref _count, 0);
        _lastStamp = long.MinValue;
    }

    /// <summary>Copies the newest frame stamped at or before <paramref name="leaderNs"/>; false when there is none.</summary>
    public bool TryReadAt(long leaderNs, AnalysisFrame into)
    {
        ArgumentNullException.ThrowIfNull(into);
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var count = Count;
            if (count == 0)
                return false;
            // The oldest few slots may be overwritten while this reads; they are left out of the search.
            var first = Math.Max(0, count - _capacity + 4);
            var index = FindAtOrBefore(leaderNs, first, count - 1);
            if (index < 0)
                return false;
            if (TryCopy(index, into))
                return true;
        }
        return false;
    }

    /// <summary>Copies the newest frame; false when there is none.</summary>
    public bool TryReadLatest(AnalysisFrame into) => TryReadAt(long.MaxValue, into);

    /// <summary>The index (count since the ring was made) of the newest frame stamped at or before <paramref name="leaderNs"/>, or −1
    /// — for a reader that keeps the frame it copied last and copies again only when the index moves.</summary>
    public long IndexAt(long leaderNs)
    {
        var count = Count;
        return count == 0 ? -1 : FindAtOrBefore(leaderNs, Math.Max(0, count - _capacity + 4), count - 1);
    }

    /// <summary>Copies the frame at <paramref name="index"/>; false when it is gone or being written.</summary>
    public bool TryRead(long index, AnalysisFrame into)
    {
        ArgumentNullException.ThrowIfNull(into);
        var count = Count;
        return index >= 0 && index < count && index > count - _capacity + 4 - 1 && TryCopy(index, into);
    }

    private long FindAtOrBefore(long leaderNs, long lo, long hi)
    {
        if (Stamp(lo) > leaderNs)
            return -1;
        if (Stamp(hi) <= leaderNs)
            return hi;
        while (hi - lo > 1)
        {
            var mid = lo + (hi - lo) / 2;
            if (Stamp(mid) <= leaderNs)
                lo = mid;
            else
                hi = mid;
        }
        return lo;
    }

    private long Stamp(long index) => Volatile.Read(ref _stamps[(int)(index % _capacity)]);

    private bool TryCopy(long index, AnalysisFrame into)
    {
        var i = (int)(index % _capacity);
        var before = Volatile.Read(ref _seq[i]);
        if (before != 2 * index + 2)
            return false;
        _slots[i].CopyTo(into);
        Interlocked.MemoryBarrier();
        return Volatile.Read(ref _seq[i]) == before;
    }
}
