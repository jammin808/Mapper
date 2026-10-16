using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PlayMap.Core.Media.Optimise;
using PlayMap.Core.Model;
using PlayMap.Engine.Host;
using PlayMap.Engine.Optimise;
using PlayMap.Engine.Tests.Host;

namespace PlayMap.Engine.Tests.Optimise;

/// <summary>The worker's end, with a worker that reads nothing and never ends by itself (the system's own sleeper) and a kill the
/// system takes and the worker does not obey — a thread held in a driver, which no real worker can be made to do. A node whose
/// killed worker will not end stops within its bounds, its journal on disk says so and the rest of its stop still happens; a stop
/// that outlives its worker kills that worker, never the fresh one that took a job meanwhile; and the partial file a replaced
/// worker held is swept once that worker has gone, while the fresh one runs on. No FFmpeg: the jobs are written to the node's
/// queue before it starts.</summary>
public sealed class OptimiseWorkerEndTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(60);
    private const string Hash = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    /// <summary>A worker that reads nothing and never ends by itself.</summary>
    private static WorkerLaunch Sleeper() => OperatingSystem.IsWindows()
        // cmd strips the outer quotes and runs the rest; the worker's own word lands after rem.
        ? new WorkerLaunch(Path.Combine(Environment.SystemDirectory, "cmd.exe"), ["/d", "/c", "ping -n 3600 127.0.0.1 >nul & rem"])
        : new WorkerLaunch("/bin/sh", ["-c", "exec sleep 3600"]);

    /// <summary>Ends a sleeper for real: the test's own kill, which the system obeys.</summary>
    private static void End(int pid)
    {
        if (pid == 0)
            return;
        try
        {
            using var p = Process.GetProcessById(pid);
            p.Kill(entireProcessTree: true);
            p.WaitForExit(TimeSpan.FromSeconds(10));
        }
        catch (ArgumentException)
        {
            // It has gone already.
        }
        catch (InvalidOperationException)
        {
            // As above: it ended between the lookup and the kill.
        }
    }

    private static OptimiseJob Job(string id, long created, bool paused)
    {
        var recipe = Recipe.For("hap", EncodeQuality.Fast);
        return new OptimiseJob(id, "md_clip", "Clip", Hash, recipe.Key(Hash), recipe.Canonical(), recipe.To, JobStates.Queued, created) { Paused = paused };
    }

    /// <summary>A clip on this node's disk whose bytes nothing reads (the worker is a sleeper).</summary>
    private static MediaItem Clip(DirectoryInfo dir)
    {
        var path = Path.Combine(dir.FullName, "clip.mp4");
        File.WriteAllBytes(path, [0]);
        return new MediaItem("md_clip", "Clip", MediaKind.Video, path, Hash, 1, MediaInfo.Unknown with { VideoCodec = "h264", Width = 320, Height = 180 }, []);
    }

    private static async Task Until(Func<bool> condition, string what)
    {
        var waited = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(waited.Elapsed < Patience, what + " never came");
            await Task.Delay(50);
        }
    }

    [Fact]
    public async Task ANodeWhoseKilledWorkerWillNotEndStopsWithinItsBoundsAndItsJournalSaysSo()
    {
        var dir = Directory.CreateTempSubdirectory("playmap-host-");
        new JobStore(Path.Combine(dir.FullName, "optimise"), NullLogger.Instance).Save([Job("ojstuck1", 1, paused: true)]);
        var options = new OptimiseOptions
        {
            Worker = Sleeper(),
            Step = TimeSpan.FromMilliseconds(100),
            StopGrace = TimeSpan.FromSeconds(1),
            KillWait = TimeSpan.FromSeconds(1),
            Kill = _ => { },
        };
        var parts = EngineParts.Bare with { Extensions = [setup => new OptimiseExtension(setup, options)] };
        var node = await HostRig.StartAsync("Stuck worker", parts: parts, dir: dir,
            settings: s => s with { Optimise = s.Optimise with { MinFreeGb = 0, PauseAfterDropS = 0 } });
        var pid = 0;
        try
        {
            var item = Clip(dir);
            Assert.True((await node.Engine.EditAsync(EngineCore.SystemSession, "the clip", p => p with { Media = p.Media.Add(item) })).Changed);
            await node.OkAsync("MEDIA OPTIMISE RESUME ojstuck1");
            JsonElement Optimise()
            {
                node.Engine.FactsChanged();
                return node.State().GetProperty("media").GetProperty("optimise");
            }
            await Until(() => Optimise() is var o && o.GetProperty("worker").GetProperty("pid").ValueKind == JsonValueKind.Number
                                                   && o.GetProperty("jobs")[0].GetProperty("state").GetString() == JobStates.Encoding, "the job on its worker");
            pid = Optimise().GetProperty("worker").GetProperty("pid").GetInt32();

            // The worker is asked to end and does not; its kill is not obeyed. The node's stop waits each bound once and goes on.
            await node.StopAsync().AsTask().WaitAsync(options.StopGrace + options.KillWait + TimeSpan.FromSeconds(30));

            var rows = (await File.ReadAllLinesAsync(node.Home.JournalPath)).Select(l => JsonDocument.Parse(l).RootElement.Clone()).ToList();
            var said = string.Create(CultureInfo.InvariantCulture,
                $"the optimise worker (pid {pid}) has not ended 1 s after it was killed (a driver or a disk holds it): the node goes on without it");
            Assert.Contains(rows, r => r.GetProperty("kind").GetString() == "optimise" && r.GetProperty("text").GetString() == said);
            // The rest of the stop happened after it: the recovery record and the journal's last word.
            Assert.Contains(rows, r => r.GetProperty("text").GetString()!.EndsWith("stopped; the recovery record holds the show", StringComparison.Ordinal));
            Assert.True(File.Exists(node.Home.RecoveryPath));
            // The node went on without the worker: it is still there, and its job is queued again when the node next starts.
            using (var worker = Process.GetProcessById(pid))
                Assert.False(worker.HasExited);
            var job = Assert.Single(new JobStore(Path.Combine(dir.FullName, "optimise"), NullLogger.Instance).Load());
            Assert.Equal(("ojstuck1", JobStates.Queued, 0), (job.Id, job.State, job.Attempts));
        }
        finally
        {
            End(pid);
            await node.DisposeAsync();
        }
    }

    [Fact]
    public async Task AStopKillsTheWorkerItAskedToEndNotTheOneThatTookAJobMeanwhile()
    {
        var kills = new ConcurrentQueue<int>();
        var stuck = new ConcurrentQueue<int>();
        var options = new OptimiseOptions
        {
            Worker = Sleeper(),
            StopGrace = TimeSpan.FromMilliseconds(500),
            KillWait = TimeSpan.FromMilliseconds(500),
            Kill = p => kills.Enqueue(p.Id),
        };
        var supervisor = new OptimiseSupervisor(options, NullLogger.Instance);
        supervisor.Stuck += stuck.Enqueue;
        var knobs = WorkerProtocol.Knobs(new WorkerKnobs("run", 1, 1, false));
        int first = 0, second = 0;
        try
        {
            supervisor.EnsureStarted();
            first = supervisor.Pid!.Value;
            // The idle exit asks the worker to end; it reads nothing and does not.
            var stop = supervisor.StopAsync();
            Assert.False(supervisor.Send(knobs));
            // A job comes while it is on its way out: it is killed (and does not obey), and a fresh worker takes the job at once.
            supervisor.EnsureStarted();
            second = supervisor.Pid!.Value;
            Assert.NotEqual(first, second);
            Assert.True(supervisor.Send(knobs));
            Assert.Equal(new[] { first }, kills.ToArray());

            // The stop's grace passes: its kill goes to the worker it asked to end, which has not ended after its bound.
            await stop.WaitAsync(Patience);
            Assert.Equal(new[] { first, first }, kills.ToArray());
            Assert.Equal(new[] { first }, stuck.ToArray());
            // The fresh worker was not touched: it runs, takes work, and a second stop of the old one is the first stop.
            Assert.Equal(second, supervisor.Pid);
            Assert.True(supervisor.Send(knobs));
            using (var fresh = Process.GetProcessById(second))
                Assert.False(fresh.HasExited);
        }
        finally
        {
            End(first);
            End(second);
            await supervisor.DisposeAsync();
        }
    }

    [Fact]
    public async Task APartialFileAReplacedWorkerHeldIsSweptOnceThatWorkerHasGone()
    {
        var dir = Directory.CreateTempSubdirectory("playmap-optimise-");
        var home = new NodeHome(Path.Combine(dir.FullName, "node"));
        new JobStore(Path.Combine(home.Folder, "optimise"), NullLogger.Instance).Save([Job("ojfirst1", 1, paused: false), Job("ojnext01", 2, paused: true)]);
        var kills = new ConcurrentQueue<int>();
        var options = new OptimiseOptions
        {
            Worker = Sleeper(),
            Step = TimeSpan.FromMilliseconds(100),
            StopGrace = TimeSpan.FromMilliseconds(500),
            KillWait = TimeSpan.FromMilliseconds(500),
            Kill = p => kills.Enqueue(p.Id),
        };
        var project = Project.CreateDefault("Optimise") with { Media = [Clip(dir)] };
        await using var rig = OptimiseRig.Start(project, options: options, dir: dir);
        int first = 0, second = 0;
        try
        {
            await rig.Until(() => rig.Job("ojfirst1").State == JobStates.Encoding && rig.Service.View.Pid is not null, "the first job on its worker");
            first = rig.Service.View.Pid!.Value;
            await rig.Ok("MEDIA OPTIMISE RESUME ojnext01");
            await rig.Ok("MEDIA OPTIMISE CANCEL ojfirst1");
            // The cancelled job's partial is still on disk, as a killed worker that still holds it leaves it (Windows refuses the
            // delete); the kill is not obeyed, and the next job goes to a fresh worker while the old one lingers.
            var held = Path.Combine(rig.Home.Folder, "optimise", "work", "ojfirst1.mov.part");
            Directory.CreateDirectory(Path.GetDirectoryName(held)!);
            await File.WriteAllBytesAsync(held, [1]);
            await rig.Until(() => rig.Job("ojnext01").State == JobStates.Encoding && rig.Service.View.Pid is { } pid && pid != first, "the next job on a fresh worker");
            second = rig.Service.View.Pid!.Value;
            Assert.Contains(first, kills);
            Assert.True(File.Exists(held));

            // The old worker goes: what it held is swept, and the fresh worker runs on with its job.
            End(first);
            await rig.Until(() => !File.Exists(held), "the sweep once the replaced worker has gone");
            Assert.Equal((second, JobStates.Encoding), (rig.Service.View.Pid, rig.Job("ojnext01").State));
            Assert.DoesNotContain(second, kills);
            Assert.Equal(0, rig.Service.View.Restarts);
        }
        finally
        {
            End(first);
            End(second);
        }
    }

    /// <summary>A stopped service writes nothing more: the end of its worker, the worker's last line and a replaced worker's going,
    /// reported only now by a reader that had them on the way as the stop began, and a line or a step that come after it, leave the
    /// queue on disk as the stop left it (the running job "encoding", queued again at the next start) and the work folder alone.</summary>
    [Fact]
    public async Task NothingTheServiceHearsOnceItHasStoppedWritesTheNodesFiles()
    {
        var dir = Directory.CreateTempSubdirectory("playmap-optimise-");
        var home = new NodeHome(Path.Combine(dir.FullName, "node"));
        var store = new JobStore(Path.Combine(home.Folder, "optimise"), NullLogger.Instance);
        store.Save([Job("ojfirst1", 1, paused: false), Job("ojnext01", 2, paused: false)]);
        var options = new OptimiseOptions { Worker = Sleeper(), Step = TimeSpan.FromMilliseconds(100), StopGrace = TimeSpan.FromMilliseconds(500) };
        var clip = Clip(dir);
        await using var rig = OptimiseRig.Start(Project.CreateDefault("Optimise") with { Media = [clip] }, options: options, dir: dir);
        await rig.Until(() => rig.Job("ojfirst1").State == JobStates.Encoding && rig.Service.View.Pid is not null, "the first job on its worker");
        // The running job's partial, as its worker writes it.
        var partial = store.PartialFor("ojfirst1");
        Directory.CreateDirectory(store.Work);
        await File.WriteAllBytesAsync(partial, [1]);

        await rig.Service.DisposeAsync();
        var stopped = await File.ReadAllBytesAsync(store.Path);
        var jobs = rig.Service.Jobs;

        rig.Service.OnExited(1, expected: false);
        rig.Service.OnEvent(WorkerProtocol.Parse(WorkerProtocol.Failed("ojfirst1", "the clip ended early"))!);
        rig.Service.OnLetGo();
        Assert.Equal(OptimiseService.StoppingWords, Assert.Single(rig.Service.Request([clip], "hap", EncodeQuality.Fast)).Why);
        Assert.Empty(rig.Service.Hold("ALL", paused: true));
        Assert.Empty(rig.Service.Cancel("ALL"));
        rig.Service.StepOnce();

        Assert.Equal(stopped, await File.ReadAllBytesAsync(store.Path));
        Assert.False(File.Exists(store.Path + ".new"));
        Assert.True(File.Exists(partial));
        Assert.Equal(jobs, rig.Service.Jobs);
        Assert.Equal(0, rig.Service.View.Restarts);
        var next = new JobStore(store.Folder, NullLogger.Instance).Load();
        Assert.Equal([("ojfirst1", JobStates.Queued, 0, false), ("ojnext01", JobStates.Queued, 0, false)],
            next.Select(j => (j.Id, j.State, j.Attempts, j.Paused)));
    }
}
