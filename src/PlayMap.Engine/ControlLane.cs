using System.Collections.Concurrent;

namespace PlayMap.Engine;

/// <summary>The engine's single writer: one dedicated thread runs every command, edit and tick in order,
/// so the playback lanes, the air state and the project are only ever written from one place. Callers on any
/// thread post work and await its result; the render thread never waits on it (it reads published
/// snapshots).</summary>
public sealed class ControlLane : IDisposable
{
    private readonly BlockingCollection<Action> _queue = new(new ConcurrentQueue<Action>());
    private readonly Thread _thread;
    private readonly CancellationTokenSource _stop = new();
    private int _managedThreadId;

    public ControlLane(string name = "PlayMap control")
    {
        _thread = new Thread(Loop) { IsBackground = true, Name = name, Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    /// <summary>True on the lane's own thread (work posted from there runs inline, never deadlocks).</summary>
    public bool IsOnLane => Environment.CurrentManagedThreadId == Volatile.Read(ref _managedThreadId);

    /// <summary>Faults that escaped a work item (logged by the owner; the lane keeps running).</summary>
    public event Action<Exception>? Faulted;

    public Task<T> RunAsync<T>(Func<T> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        if (IsOnLane)
        {
            try
            {
                return Task.FromResult(work());
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                return Task.FromException<T>(ex);
            }
        }
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var posted = TryAdd(() =>
        {
            try
            {
                tcs.SetResult(work());
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                tcs.SetException(ex);
            }
        });
        if (!posted)
            tcs.SetException(new ObjectDisposedException(nameof(ControlLane)));
        return tcs.Task;
    }

    public Task RunAsync(Action work)
    {
        ArgumentNullException.ThrowIfNull(work);
        return RunAsync(() =>
        {
            work();
            return true;
        });
    }

    /// <summary>Posts work without waiting (ticks, network facts).</summary>
    public void Post(Action work)
    {
        ArgumentNullException.ThrowIfNull(work);
        TryAdd(work);
    }

    private bool TryAdd(Action work)
    {
        try
        {
            return _queue.TryAdd(work);
        }
        catch (InvalidOperationException)
        {
            // Added after the lane was stopped or disposed: the work is refused, the caller's task says so.
            return false;
        }
    }

    private void Loop()
    {
        Volatile.Write(ref _managedThreadId, Environment.CurrentManagedThreadId);
        try
        {
            foreach (var work in _queue.GetConsumingEnumerable(_stop.Token))
            {
                try
                {
                    work();
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    Faulted?.Invoke(ex);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Stopped: the remaining work is dropped, its callers' tasks never complete — the engine is going.
        }
    }

    public void Dispose()
    {
        _queue.CompleteAdding();
        _stop.Cancel();
        if (Environment.CurrentManagedThreadId != _managedThreadId)
            _thread.Join(TimeSpan.FromSeconds(2));
        _stop.Dispose();
        _queue.Dispose();
    }
}
