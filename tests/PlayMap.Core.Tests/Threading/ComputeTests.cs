using System.Text.RegularExpressions;
using PlayMap.Core.Tests.Model;
using PlayMap.Core.Threading;

namespace PlayMap.Core.Tests.Threading;

/// <summary>The node's CPU-bound loops run on the compute threads and the calling thread, never on the shared thread pool (whose
/// workers the lanes, sockets and timers need); a loop called while every compute thread is busy runs on its calling thread; and every
/// data-parallel loop in the product passes the compute options.</summary>
[Collection(ComputeCollection.Name)]
public sealed partial class ComputeTests
{
    [Fact]
    public void ALoopsBodiesRunOnItsCallerAndTheComputeThreadsNeverOnThePool()
    {
        var pool = 0;
        var caller = 0;
        var compute = 0;
        var thread = new Thread(() =>
        {
            var me = Environment.CurrentManagedThreadId;
            Parallel.For(0, 2_000, Compute.Options, _ =>
            {
                if (Thread.CurrentThread.IsThreadPoolThread)
                    Interlocked.Increment(ref pool);
                if (Environment.CurrentManagedThreadId == me)
                    Interlocked.Increment(ref caller);
                else if (Compute.OnComputeThread)
                    Interlocked.Increment(ref compute);
                // A little work, so the loop is worth sharing.
                var x = 0.0;
                for (var k = 0; k < 2_000; k++)
                    x += Math.Sqrt(k);
                GC.KeepAlive(x);
            });
        });
        thread.Start();
        thread.Join();
        Assert.Equal(0, pool);
        Assert.Equal(2_000, caller + compute);
        Assert.True(Compute.Threads >= 1);
    }

    [Fact]
    public async Task ALoopCalledWhileEveryComputeThreadIsHeldRunsOnItsCaller()
    {
        using var held = new CountdownEvent(Compute.Threads);
        using var release = new ManualResetEventSlim();
        var holds = Enumerable.Range(0, Compute.Threads).Select(_ => Task.Factory.StartNew(() =>
        {
            held.Signal();
            release.Wait();
        }, CancellationToken.None, TaskCreationOptions.None, Compute.Scheduler)).ToArray();
        try
        {
            held.Wait();
            var me = Environment.CurrentManagedThreadId;
            var elsewhere = 0;
            var done = 0;
            Parallel.For(0, 500, Compute.Options, _ =>
            {
                if (Environment.CurrentManagedThreadId != me)
                    Interlocked.Increment(ref elsewhere);
                Interlocked.Increment(ref done);
            });
            Assert.Equal((500, 0), (done, elsewhere));
        }
        finally
        {
            release.Set();
            await Task.WhenAll(holds);
        }
    }

    /// <summary>A loop runs as wide as the compute threads and its caller together: every one of them holds a body at once.</summary>
    [Fact]
    public void ALoopRunsOnItsCallerAndEveryComputeThreadAtOnce()
    {
        var width = Compute.Threads + 1;
        using var all = new Barrier(width);
        var met = 0;
        var thread = new Thread(() => Parallel.For(0, width, Compute.Options, _ =>
        {
            // A hang guard: the barrier opens as soon as the width's bodies stand at it together.
            if (all.SignalAndWait(TimeSpan.FromSeconds(60)))
                Interlocked.Increment(ref met);
        }));
        thread.Start();
        thread.Join();
        Assert.Equal(width, met);
    }

    /// <summary>A CPU job runs on a compute thread, its caller resumes on the pool; its exception and its cancellation are the
    /// awaiter's.</summary>
    [Fact]
    public async Task AJobRunsOnTheComputeThreadsAndItsCallerResumesOnThePool()
    {
        var (on, pool) = await Compute.RunAsync(() => (Compute.OnComputeThread, Thread.CurrentThread.IsThreadPoolThread));
        Assert.Equal((true, false), (on, pool));
        Assert.False(Compute.OnComputeThread);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Compute.RunAsync(() => throw new InvalidOperationException("no")));
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        var ran = false;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Compute.RunAsync(() => ran = true, cancelled.Token));
        Assert.False(ran);
    }

    [GeneratedRegex(@"Parallel\.(For|ForEach|Invoke)\(|\.AsParallel\(")]
    private static partial Regex Loop();

    /// <summary>Every <c>Parallel.For</c> and <c>Parallel.ForEach</c> under src/ passes the compute options; none uses <c>Parallel.Invoke</c>
    /// or PLINQ, which take the pool (<c>Parallel.ForEachAsync</c>, for awaited I/O, is not a CPU loop).</summary>
    [Fact]
    public void EveryLoopInTheProductPassesTheComputeOptions()
    {
        var src = Path.Combine(RoundTwoSample.RepoRoot(), "src");
        var missing = new List<string>();
        var seen = 0;
        foreach (var file in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(src, file).Replace('\\', '/');
            if (rel.StartsWith("ThirdParty/", StringComparison.Ordinal) || rel.Contains("/obj/", StringComparison.Ordinal) || rel.Contains("/bin/", StringComparison.Ordinal))
                continue;
            var text = File.ReadAllText(file);
            foreach (Match m in Loop().Matches(text))
            {
                seen++;
                var call = text.AsSpan(m.Index, Math.Min(240, text.Length - m.Index)).ToString();
                // The options are the loop's own argument, before its body; BlendSolver3D hands its loops one set made by Compute.With.
                var head = call.Split("=>")[0];
                var given = head.Contains(", po,", StringComparison.Ordinal) && text.Contains("var po = Compute.With(", StringComparison.Ordinal);
                if (m.Value.StartsWith(".AsParallel", StringComparison.Ordinal) || (!head.Contains("Compute.", StringComparison.Ordinal) && !given))
                    missing.Add($"{rel}: {call.Split('\n')[0].Trim()}");
            }
        }
        Assert.True(seen > 30, $"{seen} loops found");
        Assert.Empty(missing);
    }
}

/// <summary>The compute tests hold the compute threads (and measure a loop's width): they run alone, after the parallel tests.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ComputeCollection
{
    public const string Name = "Compute threads";
}
