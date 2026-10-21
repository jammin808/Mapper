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

    /// <summary>Work handed the shared pool: a run (any result type), a factory start, a work item, a continuation (on the pool unless
    /// handed another scheduler), a task started on the default scheduler.</summary>
    [GeneratedRegex(@"\bTask\.Run\b|\bTask\.Factory\.StartNew\b|\bThreadPool\.(Unsafe)?QueueUserWorkItem\b|\.ContinueWith\b|\.Start\(TaskScheduler\.Default\)")]
    private static partial Regex PoolJob();

    /// <summary>The work each product file hands the shared pool, read and found to be no CPU job: a loop that waits on sockets, timers
    /// or a queue; awaited file or process I/O; an async run whose CPU parts go to <see cref="Compute"/>; the still decode the air waits
    /// for (StillBesideComputeTests measures why it stays). A file whose count grows hands the pool something new: review it, and give CPU
    /// work to <see cref="Compute.RunAsync{T}"/>.</summary>
    private static readonly Dictionary<string, int> ReviewedPoolJobs = new(StringComparer.Ordinal)
    {
        ["PlayMap.Devices/Drivers/PjLink/PjLinkUdp.cs"] = 1, // receive loop
        ["PlayMap.Devices/Manager/DeviceManager.cs"] = 1, // device loop
        ["PlayMap.Engine/Align/AlignParts.cs"] = 1, // files read
        ["PlayMap.Engine/Align/AlignRunner.cs"] = 1, // the run's async steps (its solves through Compute)
        ["PlayMap.Engine/Align/DisplayShooter.cs"] = 1, // camera pictures collected
        ["PlayMap.Engine/Align/EngineCore.AlignVerbs.cs"] = 2, // archive read, files checked
        ["PlayMap.Engine/Align/EngineCore.NativeGuard.cs"] = 1, // a wait
        ["PlayMap.Engine/Align/MeasuredLibrary.cs"] = 1, // files read
        ["PlayMap.Engine/Assistant/StagewatchProposalSink.cs"] = 1, // send loop
        ["PlayMap.Engine/Audio/AudioRuntime.cs"] = 1, // a device opened
        ["PlayMap.Engine/Audio/EngineCore.Listen.cs"] = 1, // devices listed
        ["PlayMap.Engine/Companion/Announcer.cs"] = 1, // announce loop
        ["PlayMap.Engine/Companion/CompanionParts.cs"] = 2, // facts handed on
        ["PlayMap.Engine/Companion/ThumbnailService.cs"] = 1, // a source opened and waited for (its encode through Compute)
        ["PlayMap.Engine/Control/ControlService.cs"] = 3, // input loop, ticker, an LTC input opened or closed after the one before
        ["PlayMap.Engine/EngineCore.Audio.cs"] = 1, // listen loop
        ["PlayMap.Engine/EngineCore.BlendSolve.cs"] = 3, // the solver's async run (its solve through Compute), its end posted to the lane, the wait for it
        ["PlayMap.Engine/EngineCore.Content.cs"] = 3, // a scan's, an import's and a fetch's end journalled
        ["PlayMap.Engine/EngineCore.Mapping.cs"] = 1, // a mesh written
        ["PlayMap.Engine/EngineCore.Optimise.cs"] = 1, // probes read
        ["PlayMap.Engine/EngineCore.Router.cs"] = 1, // a network verb's end journalled
        ["PlayMap.Engine/EngineCore.Sync.cs"] = 1, // a clock step's fault journalled
        ["PlayMap.Engine/Fleet/BackupNode.cs"] = 4, // takeover steps, a fault logged, the stop's wait for its work
        ["PlayMap.Engine/Fleet/FleetParts.cs"] = 1, // a health record handed on
        ["PlayMap.Engine/Host/EngineEntry.cs"] = 1, // a wait
        ["PlayMap.Engine/Host/EngineHost.cs"] = 1, // recovery loop
        ["PlayMap.Engine/Host/MediaBackend.cs"] = 2, // work queue, sweep loop
        ["PlayMap.Engine/Host/NetworkHost.cs"] = 2, // a fault logged, the stop's wait for its work
        ["PlayMap.Engine/Journal.cs"] = 1, // write loop
        ["PlayMap.Engine/Optimise/OptimiseService.cs"] = 2, // service loop, a recording's I/O
        ["PlayMap.Engine/Optimise/OptimiseSupervisor.cs"] = 4, // the worker's pipes and its end
        ["PlayMap.Engine/Phone/EngineCore.Multiscan.cs"] = 1, // a mesh written
        ["PlayMap.Engine/Rendering/EngineSources.cs"] = 1, // decoders opened and closed
        ["PlayMap.Engine/Rendering/RenderHost.Output.cs"] = 1, // a fact handed on
        ["PlayMap.Engine/Rendering/StageResources.cs"] = 2, // the builder's own thread (its loops through Compute), the blend loop (its solve through Compute)
        ["PlayMap.Engine/Rendering/TimerView.cs"] = 1, // poll loop
        ["PlayMap.Engine/Scanning/PhoneCamera.cs"] = 1, // picture pump
        ["PlayMap.Engine/Scanning/ScanRunner.Views.cs"] = 1, // archive written
        ["PlayMap.Engine/Scanning/ScanRunner.cs"] = 3, // scan read, pictures collected, the scan's async run (its decodes and solves through Compute)
        ["PlayMap.Engine/Scanning/ScanViews.cs"] = 1, // camera pump
        ["PlayMap.Engine/Show/AirAudio.cs"] = 1, // a sound opened
        ["PlayMap.Engine/Show/EngineCore.Panic.cs"] = 1, // a wait
        ["PlayMap.Engine/Show/PreflightChecks.cs"] = 1, // checks run (their own CPU through Compute)
        ["PlayMap.Engine/Site/EngineCore.Survey.cs"] = 1, // the import's own long-running thread
        ["PlayMap.Engine/Site/SiteParts.cs"] = 1, // watch loop
        ["PlayMap.Engine/Sync/SyncNode.cs"] = 3, // settings written, a fault logged, the stop's wait for its work
        ["PlayMap.Engine/Timecode/EngineCore.Timecode.cs"] = 1, // devices listed
        ["PlayMap.Media/Sources/ImageSource.cs"] = 1, // the still the air waits for
        ["PlayMap.Net/Bus/HelloService.cs"] = 3, // socket loops
        ["PlayMap.Net/Common/LineWriter.cs"] = 1, // write loop
        ["PlayMap.Net/Discovery/MdnsResponder.cs"] = 3, // socket loops
        ["PlayMap.Net/Fleet/FleetMonitor.cs"] = 1, // feeds
        ["PlayMap.Net/Led/LedSender.cs"] = 1, // a thread joined
        ["PlayMap.Net/Links/PatternsLink.cs"] = 1, // socket loop
        ["PlayMap.Net/Links/StagewatchLink.cs"] = 2, // socket loops
        ["PlayMap.Net/Links/WireClient.cs"] = 4, // socket loops
        ["PlayMap.Net/Media/MediaTransfer.cs"] = 1, // transfer workers
        ["PlayMap.Net/Phone/PhoneSession.cs"] = 1, // an ack's failure logged
        ["PlayMap.Net/Sync/ClockSync.cs"] = 3, // socket loops
        ["PlayMap.Net/Wire/LineSessions.cs"] = 1, // push loop
        ["PlayMap.Net/Wire/WireServer.cs"] = 1, // accept loop
        ["PlayMap.Render/Effects/FxLane.cs"] = 1, // a program made after its lane went, let go when its make lands (its pictures decode through Compute)
    };

    /// <summary>The scan finds pool work in each form it takes: a run of any result type (a nested generic among them), a factory start,
    /// a work item queued, a continuation (on the pool unless handed another scheduler) and a task started on the default scheduler.</summary>
    [Theory]
    [InlineData("_ = Task.Run(() => Work());")]
    [InlineData("var t = Task.Run<List<int>>(() => Work());")]
    [InlineData("return Task.Run<IReadOnlyList<PreflightRow>>(() => Rows(), ct);")]
    [InlineData("_ = Task.Factory.StartNew<Dictionary<string, int>>(() => Work());")]
    [InlineData("ThreadPool.UnsafeQueueUserWorkItem(static w => w(), a, preferLocal: false);")]
    [InlineData("_ = x.ContinueWith(t => Work(), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);")]
    [InlineData("_ = x.ContinueWith(_ => Work());")]
    [InlineData("start.Start(TaskScheduler.Default);")]
    public void TheScanFindsPoolWorkInEachForm(string line) => Assert.Single(PoolJob().Matches(line));

    /// <summary>A CPU job handed to the shared pool is found: every product file's pool work is one reviewed as no CPU job
    /// (<see cref="ReviewedPoolJobs"/>); one more in any file, or a file not reviewed, fails with where it is.</summary>
    [Fact]
    public void EveryJobHandedToThePoolWasReviewedAsNoCpuJob()
    {
        var src = Path.Combine(RoundTwoSample.RepoRoot(), "src");
        var found = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(src, file).Replace('\\', '/');
            if (rel.StartsWith("ThirdParty/", StringComparison.Ordinal) || rel.Contains("/obj/", StringComparison.Ordinal) || rel.Contains("/bin/", StringComparison.Ordinal)
                || rel == "PlayMap.Core/Threading/Compute.cs")
                continue;
            var text = File.ReadAllText(file);
            foreach (Match m in PoolJob().Matches(text))
            {
                var lineStart = text.LastIndexOf('\n', m.Index) + 1;
                if (text.AsSpan(lineStart, m.Index - lineStart).TrimStart().StartsWith("//", StringComparison.Ordinal))
                    continue;
                var line = text[..m.Index].Count(c => c == '\n') + 1;
                var call = text.AsSpan(m.Index, Math.Min(160, text.Length - m.Index)).ToString().Split('\n')[0].Trim();
                if (!found.TryGetValue(rel, out var list))
                {
                    list = [];
                    found[rel] = list;
                }
                list.Add($"{rel}:{line}: {call}");
            }
        }
        var unreviewed = found.Where(f => f.Value.Count > ReviewedPoolJobs.GetValueOrDefault(f.Key)).SelectMany(f => f.Value).ToList();
        Assert.True(unreviewed.Count == 0, "pool work not reviewed (CPU work goes through Compute.RunAsync):\n" + string.Join("\n", unreviewed));
        // A file that hands the pool less than it was reviewed for keeps its count honest.
        Assert.Empty(ReviewedPoolJobs.Where(r => found.GetValueOrDefault(r.Key)?.Count != r.Value).Select(r => $"{r.Key}: {found.GetValueOrDefault(r.Key)?.Count ?? 0} found, {r.Value} reviewed"));
    }
}

/// <summary>The compute tests hold the compute threads (and measure a loop's width): they run alone, after the parallel tests.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ComputeCollection
{
    public const string Name = "Compute threads";
}
