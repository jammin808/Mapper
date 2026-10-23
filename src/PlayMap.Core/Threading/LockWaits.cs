namespace PlayMap.Core.Threading;

/// <summary>A first wait allocates twice, at whatever moment it comes — on a loaded machine that can be any frame of a show, when a
/// reader holding a lock is preempted: a <see cref="Lock"/> makes its wait event on the first thread that has to wait for it, and a
/// thread makes its wait record on its own first wait. Every lock the air's threads take beside another thread as they work frame by
/// frame — the render thread; the decode, capture and send threads that hand it frames or take them; the LED sender; each sound
/// output's feeder; the lateness record every deadline wait writes — is prepared as it is made (<see cref="Prepare"/>,
/// <see cref="Prepared"/>). Each of those threads has its record before its first frame: a thread that claims its role makes it with
/// the claim (<see cref="Machine.ThreadClaims.Claim"/>), HAP's read and decode threads, an image sequence's and a capture input's
/// call <see cref="PrepareThisThread"/> as they start, and a send worker's first act is a wait. So no show path allocates for a first
/// wait. Preparing waits, where the lock is made, for one timed take of each lock (a millisecond, up to a timer tick on Windows):
/// off the frame, but for the GL upload arena's two locks, made on the render thread with the context's first frame with an engine,
/// never a warm one.</summary>
public static class LockWaits
{
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

    /// <summary>A new lock with its wait event made (a static lock's initialiser).</summary>
    public static Lock Prepared()
    {
        var gate = new Lock();
        Prepare(gate);
        return gate;
    }
}
