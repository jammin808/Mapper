namespace PlayMap.Engine.Rendering;

/// <summary>Tells the engine its facts moved, from the render thread, without allocating there: a raise releases a semaphore that a
/// thread of its own waits on (the thread pool is never asked, since queueing to it may add a worker on the queueing thread). Raises
/// that arrive before the thread wakes share its run; a raise during its run wakes it again, so no move is lost. Any thread may
/// raise; disposing stops the thread after the run in hand, and a raise after it is let go.
///
/// One state, changed only atomically: Idle → Raised releases once (so the semaphore's count never passes 1), the thread takes
/// Raised → Idle before each run, and Stopped is never left.</summary>
internal sealed class FactsSignal : IDisposable
{
    private const int Idle = 0, Raised = 1, Stopped = 2;
    private readonly Action _target;
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly Thread _thread;
    private int _state;

    public FactsSignal(Action target, string name = "render facts")
    {
        _target = target;
        _thread = new Thread(Loop) { IsBackground = true, Name = name };
        _thread.Start();
    }

    /// <summary>True once disposing has begun (tests let a held run go only after the stop has landed).</summary>
    internal bool IsStopping => Volatile.Read(ref _state) == Stopped;

    /// <summary>True while the signal's thread waits for a raise (tests raise against a parked waiter).</summary>
    internal bool Parked => (_thread.ThreadState & ThreadState.WaitSleepJoin) != 0;

    public void Raise()
    {
        if (Interlocked.CompareExchange(ref _state, Raised, Idle) == Idle)
            _wake.Release();
    }

    private void Loop()
    {
        while (true)
        {
            _wake.Wait();
            // Taken with a full fence before the target reads the facts: a move it might miss raises again.
            if (Interlocked.CompareExchange(ref _state, Idle, Raised) == Stopped)
                return;
            _target();
        }
    }

    public void Dispose()
    {
        var was = Interlocked.Exchange(ref _state, Stopped);
        if (was == Stopped)
            return;
        // Raised: its release is outstanding and wakes the thread into Stopped. Idle: nothing is, so this one does.
        if (was == Idle)
            _wake.Release();
        _thread.Join();
        _wake.Dispose();
    }
}
