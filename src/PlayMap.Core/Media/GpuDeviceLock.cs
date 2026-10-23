using PlayMap.Core.Threading;
using PlayMap.Core.Time;

namespace PlayMap.Core.Media;

/// <summary>The renderer's device lock (<see cref="IGpuShare.Enter"/>, <see cref="IGpuShare.Exit"/>): one recursive lock that every
/// thread holds while it issues work on a device whose immediate context the renderer draws with — the render thread around each
/// frame's GPU work, a decoder around FFmpeg's begin–submit–end bracket (its lock callbacks), a texture sharer around its copy.
///
/// <list type="bullet">
/// <item><b>Sharers wait.</b> <see cref="Enter"/> waits as long as the holder takes: a decode or control thread may wait for a frame.</item>
/// <item><b>The frame never waits past its bound.</b> The render thread takes the lock with <see cref="TryEnter"/> and
/// <see cref="FrameBoundMs"/>: a sharer holds it for a handful of driver calls, well inside the bound; past it the frame is drawn on
/// the device's own per-call ordering (the device is multithread-protected), <see cref="TimedOut"/> counts it and
/// <see cref="LastTimedOutWaitNs"/> says how long it waited, read on the taker's thread.</item>
/// <item><b>The frame lets go while it waits.</b> Before a wait on the display (a swap chain's waitable object) the render thread lets
/// go of every hold (<see cref="ExitAll"/>) and takes them back after (<see cref="Reenter"/>, bounded), so sharers get the device
/// between frames and during the vertical-blank wait.</item>
/// </list>
/// Nothing allocates on a take: the lock's wait event is made with it.</summary>
public sealed class GpuDeviceLock
{
    /// <summary>The longest the render thread waits for a sharer to let go (a quarter of a 60 Hz frame).</summary>
    public const int FrameBoundMs = 4;

    private readonly Lock _lock = new();

    // The holder's recursion; read and written only by the thread that holds the lock.
    private int _depth;
    private long _waits;
    private long _timedOut;
    private long _lastTimedOutWaitNs;

    public GpuDeviceLock() => LockWaits.Prepare(_lock);

    /// <summary>The calling thread holds the lock.</summary>
    public bool IsHeldByCurrentThread => _lock.IsHeldByCurrentThread;

    /// <summary>Times a taker found the lock held by another thread and waited.</summary>
    public long Waits => Interlocked.Read(ref _waits);

    /// <summary>Times a bounded take gave up (the work it guarded ran on the device's own per-call ordering).</summary>
    public long TimedOut => Interlocked.Read(ref _timedOut);

    /// <summary>How long the latest bounded take that gave up waited (ns of the monotonic clock, from the start of its wait to its
    /// giving up, read on the taker's thread): its bound, and whatever more the machine kept that thread from running. It is written
    /// before <see cref="TimedOut"/> counts the take, so a reader that has seen the count move reads this take's wait or a later
    /// one's.</summary>
    public long LastTimedOutWaitNs => Interlocked.Read(ref _lastTimedOutWaitNs);

    /// <summary>Takes one hold, waiting as long as another thread holds it (a sharer's thread; never the render thread's frame).</summary>
    public void Enter()
    {
        if (!_lock.TryEnter())
        {
            Interlocked.Increment(ref _waits);
            _lock.Enter();
        }
        _depth++;
    }

    /// <summary>Takes one hold, waiting at most <paramref name="timeoutMs"/>: false (nothing taken, counted) when the bound passed.</summary>
    public bool TryEnter(int timeoutMs)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(timeoutMs);
        if (!_lock.TryEnter())
        {
            Interlocked.Increment(ref _waits);
            var from = MonoClock.Instance.NowNs;
            if (!_lock.TryEnter(timeoutMs))
            {
                Interlocked.Exchange(ref _lastTimedOutWaitNs, MonoClock.Instance.NowNs - from);
                Interlocked.Increment(ref _timedOut);
                return false;
            }
        }
        _depth++;
        return true;
    }

    /// <summary>Lets go of one hold; throws <see cref="SynchronizationLockException"/> when the calling thread holds none.</summary>
    public void Exit()
    {
        if (!_lock.IsHeldByCurrentThread)
            throw new SynchronizationLockException("the device lock is not held by this thread");
        _depth--;
        _lock.Exit();
    }

    /// <summary>Lets go of every hold of the calling thread, answering how many it had (0 when it held none).</summary>
    public int ExitAll()
    {
        if (!_lock.IsHeldByCurrentThread)
            return 0;
        var holds = _depth;
        _depth = 0;
        for (var i = 0; i < holds; i++)
            _lock.Exit();
        return holds;
    }

    /// <summary>Takes back <paramref name="holds"/> holds (what <see cref="ExitAll"/> answered), waiting at most
    /// <paramref name="timeoutMs"/> for the first: all of them, or none (false, counted) when the bound passed.</summary>
    public bool Reenter(int holds, int timeoutMs)
    {
        if (holds <= 0)
            return true;
        if (!TryEnter(timeoutMs))
            return false;
        for (var i = 1; i < holds; i++)
        {
            // Recursive: the thread holds the lock, so each further take is immediate.
            _lock.Enter();
            _depth++;
        }
        return true;
    }
}
