using System.Collections.Concurrent;

namespace PlayMap.Core.Threading;

/// <summary>
/// Where the node's CPU-bound work runs: its own threads, never the shared thread pool. A data-parallel loop handed the thread pool
/// keeps every worker it takes busy until the loop ends, and a solve or decode run on a pool thread holds that thread for its whole
/// length; the work the node's lanes, sockets and timers queue on the pool then waits behind them. Every <c>Parallel.For</c> and
/// <c>Parallel.ForEach</c> passes <see cref="Options"/> or <see cref="With"/>: a loop runs as wide as the processors, on its calling
/// thread and these threads (one fewer than the processors, at least one). A long CPU job is handed to <see cref="RunAsync{T}"/>: it
/// runs on these threads, its caller awaits it without holding a pool thread and resumes on the pool. A loop called while every
/// compute thread is busy is never stalled by them: its calling thread runs its iterations itself.
/// </summary>
public static class Compute
{
    /// <summary>The scheduler of the compute threads.</summary>
    public static TaskScheduler Scheduler => ComputeScheduler.Instance;

    /// <summary>A loop's options on the compute threads, without cancellation (a fresh set each time: a caller may change its own).</summary>
    public static ParallelOptions Options => new() { TaskScheduler = ComputeScheduler.Instance };

    /// <summary>A loop's options on the compute threads, cancelled by <paramref name="ct"/>, at most <paramref name="maxDegree"/> bodies
    /// at once (−1: as many as the threads allow).</summary>
    public static ParallelOptions With(CancellationToken ct, int maxDegree = -1) =>
        new() { TaskScheduler = ComputeScheduler.Instance, CancellationToken = ct, MaxDegreeOfParallelism = maxDegree };

    /// <summary>The compute threads (the calling thread of a loop works beside them).</summary>
    public static int Threads => ComputeScheduler.Instance.Threads;

    /// <summary>A CPU-bound job on the compute threads: its caller awaits it without holding a thread of the pool and resumes on the
    /// pool, never on a compute thread. Cancelled before it starts, it never runs; its exception is the awaiter's.</summary>
    public static Task<T> RunAsync<T>(Func<T> work, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (ct.IsCancellationRequested)
        {
            done.SetCanceled(ct);
            return done.Task;
        }
        _ = Task.Factory.StartNew(() =>
        {
            if (ct.IsCancellationRequested)
            {
                done.TrySetCanceled(ct);
                return;
            }
            try
            {
                done.TrySetResult(work());
            }
            catch (OperationCanceledException ex)
            {
                done.TrySetCanceled(ex.CancellationToken);
            }
            catch (Exception ex)
            {
                done.TrySetException(ex);
            }
        }, CancellationToken.None, TaskCreationOptions.DenyChildAttach, ComputeScheduler.Instance);
        return done.Task;
    }

    /// <summary>A CPU-bound job with no result on the compute threads (<see cref="RunAsync{T}"/>).</summary>
    public static Task RunAsync(Action work, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        return RunAsync(() =>
        {
            work();
            return true;
        }, ct);
    }

    /// <summary>True on a compute thread.</summary>
    public static bool OnComputeThread => ComputeScheduler.IsComputeThread;

    private sealed class ComputeScheduler : TaskScheduler
    {
        public static readonly ComputeScheduler Instance = new(Math.Max(1, Environment.ProcessorCount - 1));

        [ThreadStatic]
        private static bool t_compute;

        private readonly BlockingCollection<Task> _queue = [];
        private readonly int _threads;

        private ComputeScheduler(int threads)
        {
            _threads = threads;
            for (var i = 0; i < threads; i++)
            {
                var thread = new Thread(Work) { IsBackground = true, Name = "PlayMap compute" };
                thread.Start();
            }
        }

        public static bool IsComputeThread => t_compute;

        public int Threads => _threads;

        // A loop's width counts its calling thread beside these.
        public override int MaximumConcurrencyLevel => _threads + 1;

        private void Work()
        {
            t_compute = true;
            // The threads live as long as the process: the queue is never completed.
            foreach (var task in _queue.GetConsumingEnumerable())
                TryExecuteTask(task);
        }

        protected override void QueueTask(Task task) => _queue.Add(task);

        // A loop's own replica runs on the thread that asked for it, so a loop progresses while every compute thread is busy.
        protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued) => TryExecuteTask(task);

        protected override IEnumerable<Task> GetScheduledTasks() => _queue.ToArray();
    }
}
