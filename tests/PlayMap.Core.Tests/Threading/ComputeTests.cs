using System.Text.RegularExpressions;
using PlayMap.Core.Tests.Model;
using PlayMap.Core.Threading;

namespace PlayMap.Core.Tests.Threading;

/// <summary>The node's CPU-bound loops run on the compute threads and the calling thread, never on the shared thread pool (whose
/// workers the lanes, sockets and timers need); a loop called while every compute thread is busy runs on its calling thread; and every
/// data-parallel loop in the product passes the compute options.</summary>
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

    [GeneratedRegex(@"Parallel\.(For|ForEach)\(")]
    private static partial Regex Loop();

    /// <summary>Every <c>Parallel.For</c> and <c>Parallel.ForEach</c> under src/ passes the compute options (third argument).</summary>
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
                // BlendSolver3D hands its loops one set of options, made by Compute.With.
                var given = call.Contains(", po,", StringComparison.Ordinal) && text.Contains("var po = Compute.With(", StringComparison.Ordinal);
                if (!call.Contains("Compute.", StringComparison.Ordinal) && !given)
                    missing.Add($"{rel}: {call.Split('\n')[0].Trim()}");
            }
        }
        Assert.True(seen > 30, $"{seen} loops found");
        Assert.Empty(missing);
    }
}
