using System.Diagnostics;
using PlayMap.Engine.Rendering;

namespace PlayMap.Engine.Tests.Rendering;

/// <summary>The render thread tells the engine its facts moved without allocating (an output's light that flips under load once
/// cost a work item a frame), and a move raised while the engine is reading the facts is never lost.</summary>
public sealed class FactsSignalTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    [Fact]
    public void RaisingFromTheRenderThreadAllocatesNothingAndEveryRaiseIsHeard()
    {
        long runs = 0;
        using var signal = new FactsSignal(() => Interlocked.Increment(ref runs));
        long allocated = -1;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                // Warm: the pool's queue and its first worker exist before anything is counted.
                for (var i = 1; i <= 50; i++)
                    RaiseAndWait(signal, ref runs, i);
                var before = GC.GetAllocatedBytesForCurrentThread();
                for (var i = 51; i <= 1050; i++)
                    RaiseAndWait(signal, ref runs, i);
                allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            }
            catch (TimeoutException ex)
            {
                failure = ex;
            }
        }) { Name = "render (test)" };
        thread.Start();
        thread.Join();
        Assert.Null(failure);
        Assert.Equal(1050, Interlocked.Read(ref runs));
        Assert.Equal(0, allocated);
    }

    [Fact]
    public async Task ARaiseWhileTheEngineReadsTheFactsQueuesItAgain()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        long runs = 0;
        using var signal = new FactsSignal(() =>
        {
            if (Interlocked.Increment(ref runs) == 1)
            {
                entered.Set();
                release.Wait(Patience);
            }
        });
        signal.Raise();
        Assert.True(entered.Wait(Patience));
        // The first run is reading the facts: these moves come after what it reads, so the engine must hear them again.
        signal.Raise();
        signal.Raise();
        release.Set();
        Assert.True(await OpsRig.Eventually(() => Interlocked.Read(ref runs) >= 2));

        // Stopped, its thread is gone and a late raise is let go.
        signal.Dispose();
        var after = Interlocked.Read(ref runs);
        signal.Raise();
        Assert.Equal(after, Interlocked.Read(ref runs));
    }

    /// <summary>Raises once and spins until the target has run <paramref name="expected"/> times (spinning allocates nothing).</summary>
    private static void RaiseAndWait(FactsSignal signal, ref long runs, long expected)
    {
        signal.Raise();
        var spin = new SpinWait();
        var start = Stopwatch.GetTimestamp();
        while (Interlocked.Read(ref runs) < expected)
        {
            if (Stopwatch.GetElapsedTime(start) > Patience)
                throw new TimeoutException($"the signal's run {expected} never came");
            spin.SpinOnce();
        }
    }
}
