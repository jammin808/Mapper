namespace PlayMap.Media.Frames;

/// <summary>A first wait allocates twice, at whatever moment it comes — on a loaded machine that can be any frame of a show, when
/// a consumer holding a lock is preempted: a <see cref="Lock"/> makes its wait event on the first thread that has to wait for
/// it, and a thread makes its wait record on its own first wait. <see cref="Prepare(Lock[])"/> makes the locks' events and
/// <see cref="PrepareThisThread"/> the thread's record before the first frame, so no frame path allocates for a first wait. A
/// <see cref="ManualResetEventSlim"/> likewise makes its lock object on the first wait that blocks:
/// <see cref="Prepare(ManualResetEventSlim[])"/> makes it.</summary>
internal static class LockWaits
{
    /// <summary>Makes each event's lock object: one wait that blocks on each, so it is called before anything can set them (the
    /// owner's constructor, before its threads start). A warm-up wait on a frame thread is no substitute: an event another thread
    /// sets while the wait spins returns at once and makes nothing, and the first wait that blocks — at any frame — makes it.</summary>
    public static void Prepare(params ManualResetEventSlim[] events)
    {
        ArgumentNullException.ThrowIfNull(events);
        foreach (var e in events)
        {
            e.Reset();
            e.Wait(1);
        }
    }

    /// <summary>Makes the calling thread's wait record (a frame thread calls it as it starts).</summary>
    public static void PrepareThisThread()
    {
        using var once = new AutoResetEvent(false);
        once.WaitOne(0);
    }

    /// <summary>Makes each lock's wait event: this thread holds every lock while a short-lived thread waits a moment on each.</summary>
    public static void Prepare(params Lock[] gates)
    {
        ArgumentNullException.ThrowIfNull(gates);
        foreach (var gate in gates)
            gate.Enter();
        try
        {
            // Every lock is held, so each wait times out after it has made its event.
            var waiter = new Thread(() =>
            {
                foreach (var gate in gates)
                {
                    if (gate.TryEnter(1))
                        gate.Exit();
                }
            })
            { IsBackground = true, Name = "lock waits" };
            waiter.Start();
            waiter.Join();
        }
        finally
        {
            foreach (var gate in gates)
                gate.Exit();
        }
    }
}
