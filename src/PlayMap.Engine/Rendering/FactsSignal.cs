namespace PlayMap.Engine.Rendering;

/// <summary>Tells the engine its facts moved, from the render thread, without allocating there: a raise releases a semaphore that a
/// thread of its own waits on (the thread pool is never asked, since queueing to it may add a worker on the queueing thread). Raises
/// that arrive before the thread wakes share its run; a raise during its run wakes it again, so no move is lost. Any thread may
/// raise; disposing stops the thread after the run in hand.</summary>
internal sealed class FactsSignal : IDisposable
{
    private readonly Action _target;
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly Thread _thread;
    private int _raised;
    private volatile bool _stopping;

    public FactsSignal(Action target, string name = "render facts")
    {
        _target = target;
        _thread = new Thread(Loop) { IsBackground = true, Name = name };
        _thread.Start();
    }

    public void Raise()
    {
        // At most one release is outstanding (the semaphore's count never passes 1).
        if (Interlocked.Exchange(ref _raised, 1) == 0)
            _wake.Release();
    }

    private void Loop()
    {
        while (true)
        {
            _wake.Wait();
            if (_stopping)
                return;
            // Cleared before the target reads the facts: a move it might miss raises again.
            Volatile.Write(ref _raised, 0);
            _target();
        }
    }

    public void Dispose()
    {
        if (_stopping)
            return;
        _stopping = true;
        if (Interlocked.Exchange(ref _raised, 1) == 0)
            _wake.Release();
        _thread.Join();
        _wake.Dispose();
    }
}
