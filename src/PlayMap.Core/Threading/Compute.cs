using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

namespace PlayMap.Core.Threading;

/// <summary>
/// Where the node's CPU-bound loops run: its own threads, never the shared thread pool. A data-parallel loop given the thread pool
/// takes every worker until it ends (its replicas never yield between iterations), so the work the node's lanes, sockets and timers
/// queue there waits behind it — a scan decode or a simulated camera held a sync group's GO for a second. Every <c>Parallel.For</c>
/// and <c>Parallel.ForEach</c> passes <see cref="Options"/> or <see cref="With"/>: its bodies run on the calling thread and on these
/// threads (one fewer than the processors, at least one), so the pool always has its own workers for the rest. A loop called while
/// these threads are busy with another is never stalled by it: its calling thread runs its iterations itself.
/// </summary>
public static class Compute
{
    /// <summary>The scheduler of the compute threads.</summary>
    public static TaskScheduler Scheduler => ComputeScheduler.Instance;

    /// <summary>A loop's options on the compute threads, without cancellation (never written: shared by every caller).</summary>
    public static ParallelOptions Options { get; } = new() { TaskScheduler = ComputeScheduler.Instance };

    /// <summary>A loop's options on the compute threads, cancelled by <paramref name="ct"/>, at most <paramref name="maxDegree"/> bodies
    /// at once (−1: as many as the threads allow).</summary>
    public static ParallelOptions With(CancellationToken ct, int maxDegree = -1) =>
        new() { TaskScheduler = ComputeScheduler.Instance, CancellationToken = ct, MaxDegreeOfParallelism = maxDegree };

    /// <summary>The compute threads (the calling thread of a loop works beside them).</summary>
    public static int Threads => ComputeScheduler.Instance.MaximumConcurrencyLevel;

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

        public override int MaximumConcurrencyLevel => _threads;

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

        [ExcludeFromCodeCoverage]
        protected override IEnumerable<Task> GetScheduledTasks() => _queue.ToArray();
    }
}
