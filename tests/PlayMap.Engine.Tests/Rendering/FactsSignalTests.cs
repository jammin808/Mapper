using System.Diagnostics;
using PlayMap.Engine.Rendering;
using PlayMap.Tests;

namespace PlayMap.Engine.Tests.Rendering;

/// <summary>The render thread tells the engine its facts moved without allocating (an output's light that flips under load once
/// cost a work item a frame), a move raised while the engine is reading the facts is never lost, and stopping the signal while a
/// run holds and a raise is pending ends its thread.</summary>
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
                // Warm: the semaphore's monitor exists before anything is counted.
                for (var i = 1; i <= 20; i++)
                    RaiseAndWait(signal, ref runs, i);
                var before = Allocations.Start();
                for (var i = 21; i <= 520; i++)
                    RaiseAndWait(signal, ref runs, i);
                allocated = Allocations.Since(before);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        }) { Name = "render (test)" };
        thread.Start();
        thread.Join();
        Assert.Null(failure);
        Assert.Equal(520, Interlocked.Read(ref runs));
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

    [Fact]
    public async Task StoppingWhileARunHoldsAndARaiseIsPendingEndsTheThread()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        long runs = 0;
        var signal = new FactsSignal(() =>
        {
            Interlocked.Increment(ref runs);
            entered.Set();
            release.Wait(Patience);
        });
        signal.Raise();
        Assert.True(entered.Wait(Patience));
        // A raise is pending (its release outstanding) when the stop comes: the stop must not wait on a wake that never comes.
        signal.Raise();
        var stopping = Task.Run(signal.Dispose);
        Assert.True(await OpsRig.Eventually(() => signal.IsStopping));
        release.Set();
        await stopping.WaitAsync(Patience);
        Assert.Equal(1, Interlocked.Read(ref runs));
        signal.Raise();
        signal.Dispose();
        Assert.Equal(1, Interlocked.Read(ref runs));
    }

    /// <summary>Raises against a parked waiter (the path a frame takes, raises being frames apart) and spins until the target has
    /// run <paramref name="expected"/> times. Spinning and reading the clock allocate nothing.</summary>
    private static void RaiseAndWait(FactsSignal signal, ref long runs, long expected)
    {
        WaitParked(signal);
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

    /// <summary>Until the signal's thread has been waiting for 2 ms without a break (past the semaphore's own spinning).</summary>
    private static void WaitParked(FactsSignal signal)
    {
        var start = Stopwatch.GetTimestamp();
        long parkedSince = 0;
        var spin = new SpinWait();
        while (true)
        {
            if (!signal.Parked)
                parkedSince = 0;
            else if (parkedSince == 0)
                parkedSince = Stopwatch.GetTimestamp();
            else if (Stopwatch.GetElapsedTime(parkedSince) > TimeSpan.FromMilliseconds(2))
                return;
            if (Stopwatch.GetElapsedTime(start) > Patience)
                throw new TimeoutException("the signal's thread never parked");
            spin.SpinOnce();
        }
    }
}
