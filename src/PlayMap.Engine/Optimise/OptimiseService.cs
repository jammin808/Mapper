using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using PlayMap.Core.Bus;
using PlayMap.Core.Media;
using PlayMap.Core.Media.Hap;
using PlayMap.Core.Media.Optimise;
using PlayMap.Core.Model;
using PlayMap.Engine.Host;
using PlayMap.Media;
using PlayMap.Media.Optimise;
using PlayMap.Core.Storage;

namespace PlayMap.Engine.Optimise;

/// <summary>What the optimise service is made with: how its worker starts, its step, how many times a clip may stop the worker
/// before its job fails, the first restart backoff (doubled per restart, at most 30 s), how long a worker asked to end is waited
/// for before it is killed, and how long a killed worker is waited for before the node goes on without it.</summary>
public sealed record OptimiseOptions
{
    public WorkerLaunch Worker { get; init; } = WorkerLaunch.ThisProcess();

    /// <summary>The CPU priority the worker takes: below the show's own (normal) on a node, so the encode yields the CPU to the air.</summary>
    public ProcessPriorityClass WorkerPriority { get; init; } = ProcessPriorityClass.BelowNormal;
    public TimeSpan Step { get; init; } = TimeSpan.FromSeconds(1);
    public int MaxAttempts { get; init; } = 3;
    public TimeSpan FirstBackoff { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan StopGrace { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan KillWait { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>The clock a stop's grace (<see cref="StopGrace"/>) is counted on: the system's on a node; a test moves its own, so a stop
    /// kills its worker when the test says the grace has passed, however slowly the test runs.</summary>
    public TimeProvider Time { get; init; } = TimeProvider.System;

    /// <summary>How a worker is killed: its whole process tree. A test stands in a kill the system takes and a worker does not obey
    /// (a thread held in a driver), which no worker can be made to do.</summary>
    internal Action<Process> Kill { get; init; } = static p => p.Kill(entireProcessTree: true);

    /// <summary>Heard as a stop begins to wait for work begun before it that has not ended (a test holds that work and lets it go
    /// only then, so it sees the stop wait for it whatever the machine's pace).</summary>
    internal Action? StopWaits { get; init; }

    /// <summary>Tests only: the first worker this service starts waits, after this many frames of its first job, for the engine's next
    /// knobs line that holds before its next frame (<see cref="OptimiseWorker.HoldAtWord"/>), so a pause or a kill lands mid-job however fast
    /// the encoder runs. 0: never.</summary>
    internal long HoldFirstWorkerAt { get; init; }
}

/// <summary>What STATE says of the service: the governor, the counts, the worker and the jobs (the running one first, then the
/// queue in order, then the latest finished).</summary>
public sealed record OptimiseView(GovernorKnobs Governor, int Running, int Queued, int Failed, int? Pid, string Gl, string Encoder, int Restarts,
    IReadOnlyList<OptimiseJob> Jobs);

/// <summary>One item's answer to a request: the job queued for it, or why none was.</summary>
public sealed record OptimiseOutcome(string Media, OptimiseJob? Job, string Why);

/// <summary>What a cancel or a hold changed: the jobs, or none and why when it could change none it was asked to (the node is
/// stopping). An empty why with no jobs is a target that names no job the change applies to.</summary>
public sealed record OptimiseSteered(IReadOnlyList<string> Jobs, string Why)
{
    /// <summary>Of <see cref="Jobs"/>, those the change is only asked of: the running job, until its worker says it holds (a pause) or
    /// goes on (a resume). A queued job's change, and a pause of a job its worker holds already, is applied at once.</summary>
    public IReadOnlyList<string> Pending { get; init; } = [];
}

/// <summary>Optimise on import, on this node: a queue of jobs persisted in <c>NodeHome/optimise/jobs.json</c>, one at a time
/// through the worker process (<see cref="OptimiseWorker"/>, restarted with backoff when it stops; a clip that stops it
/// <see cref="OptimiseOptions.MaxAttempts"/> times fails with words), paced by the <see cref="Governor"/> stepped once a
/// second from the engine's published facts (armed, playing, outputs' dropped frames, open decoders' late frames). A finished
/// job's file is hashed, probed, checked against the quality floor, moved into the media store and recorded on its item as one
/// edit; a failed or cancelled job leaves no file. New imports are planned when their probe lands (the project's mode auto
/// queues what the rules ask; never and ask queue nothing).</summary>
public sealed class OptimiseService : IAsyncDisposable
{
    private readonly Lock _gate = new();
    private readonly List<OptimiseJob> _jobs;
    private readonly JobStore _store;
    private readonly OptimiseSupervisor _worker;
    private readonly Governor _governor;
    private readonly OptimiseNodeSettings _settings;
    private readonly OptimiseOptions _options;
    private readonly NodeHome _home;
    private readonly ILogger _log;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _kick = new(0);
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private EngineCore? _engine;
    private Task _loop = Task.CompletedTask;
    private string? _running;
    /// <summary>The running job its worker says it holds between frames (its "held" line), null when the worker says it goes on or
    /// another job runs; and the frame that line names.</summary>
    private string? _held;
    private long _heldFrame;

    /// <summary>The held job STATE shows: <see cref="_held"/> once the journal has the row its line made (a reader that sees the job held
    /// finds the row saying it paused).</summary>
    private string? _shownHeld;

    /// <summary>Running jobs a resume was asked of while their worker held them: journalled as resumed when it goes on.</summary>
    private readonly HashSet<string> _resumeAsked = new(StringComparer.Ordinal);
    private long _restartAtNs = long.MinValue;
    private long _idleSinceNs = long.MinValue;
    private int _restarts;
    private int _crashStreak;
    private long _ledLateSteps;
    private readonly Dictionary<string, Ready> _awaiting = new(StringComparer.Ordinal);
    private readonly HashSet<string> _retrying = new(StringComparer.Ordinal);

    // Closed (under the gate) once the service stops: nothing starts after it, and _busy counts the work begun before it that may
    // still write the queue or the work folder; _quiet is set when the last of it has ended.
    private bool _closed;
    private int _busy;
    private readonly TaskCompletionSource _quiet = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly HashSet<string> _recording = new(StringComparer.Ordinal);
    private string _gl = "";
    private string _encoder = "";
    /// <summary>The knobs last handed to the running worker (null: none runs). Written under the gate before they leave.</summary>
    private WorkerKnobs? _sent;
    private GovernorState _lastState = GovernorState.Run;
    private long _version;

    public OptimiseService(NodeHome home, OptimiseNodeSettings settings, ILogger log, OptimiseOptions? options = null)
    {
        _home = home ?? throw new ArgumentNullException(nameof(home));
        _settings = settings ?? OptimiseNodeSettings.Default;
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _options = options ?? new OptimiseOptions();
        _store = new JobStore(Path.Combine(home.Folder, "optimise"), log);
        _jobs = _store.Load();
        _worker = new OptimiseSupervisor(_options, log);
        _worker.Event += OnEvent;
        _worker.Exited += OnExited;
        _worker.LetGo += OnLetGo;
        _worker.Stuck += OnStuck;
        _governor = new Governor(new GovernorSettings(_settings.TrickleGpuDuty, _settings.TrickleDiskMbps, _settings.PauseAfterDropS, Environment.ProcessorCount));
        SweepWork();
    }

    /// <summary>Moves whenever the view STATE shows may have changed.</summary>
    public long Version => Interlocked.Read(ref _version);

    /// <summary>The words the engine's journal gets (a pause, a version made, a failure).</summary>
    public event Action<string, string, bool>? Said;

    public IReadOnlyList<OptimiseJob> Jobs
    {
        get
        {
            lock (_gate)
                return [.. _jobs.Select(Observed)];
        }
    }

    /// <summary>A job as the node observes it: held while its worker says it holds it. Under the gate.</summary>
    private OptimiseJob Observed(OptimiseJob j) => j.Id == _shownHeld && j.Id == _running ? j with { Held = true } : j;

    public OptimiseView View
    {
        get
        {
            lock (_gate)
            {
                var running = _jobs.Where(j => JobStates.IsRunning(j.State)).Select(Observed).ToList();
                var queued = _jobs.Where(j => j.State == JobStates.Queued).ToList();
                var over = _jobs.Where(j => JobStates.IsOver(j.State)).OrderByDescending(j => j.FinishedUnixMs).ToList();
                return new OptimiseView(_governor.Current, running.Count, queued.Count, _jobs.Count(j => j.State == JobStates.Failed),
                    _worker.Pid, _gl, _encoder, _restarts, [.. running.Concat(queued).Concat(over)]);
            }
        }
    }

    /// <summary>Starts the service on a running engine: its loop, and the import hook on the live store.</summary>
    public void Attach(EngineCore engine)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        lock (_gate)
        {
            foreach (var m in engine.Store.Project.Media)
                if (m.ContentHash.Length > 0)
                    _seen.Add(m.Id + "|" + m.ContentHash);
        }
        engine.Store.Changed += OnProjectChanged;
        _loop = Task.Run(() => LoopAsync(_stop.Token), CancellationToken.None);
    }

    /// <summary>Plans each item (the named target, else the rules as if the mode were auto: a person asked) and queues a job for
    /// those that need one; the others answer why not. Reads files (the probe): call it off the lane.</summary>
    public IReadOnlyList<OptimiseOutcome> Request(IReadOnlyList<MediaItem> items, string? to, EncodeQuality quality)
    {
        ArgumentNullException.ThrowIfNull(items);
        var engine = _engine ?? throw new InvalidOperationException("the optimise service is not attached");
        if (!Enter())
            return [.. items.Select(i => new OptimiseOutcome(i.Id, null, StoppingWords))];
        try
        {
            var settings = engine.Store.Project.Optimise;
            var outcomes = new List<OptimiseOutcome>();
            foreach (var item in items)
            {
                var (plan, why) = Plan(engine, item, to, quality, settings with { Mode = OptimiseMode.Auto });
                outcomes.Add(plan is null ? new OptimiseOutcome(item.Id, null, why) : Queue(item, plan));
            }
            Kick();
            return outcomes;
        }
        finally
        {
            Leave();
        }
    }

    /// <summary>Cancels a job (or every unfinished one): a queued job leaves the queue, a running one stops (its worker is killed)
    /// and its partial file is deleted — at once, or, while the killed worker still holds it, by the sweep once that worker has
    /// gone. Answers the jobs cancelled; once the service has stopped, none, with <see cref="StoppingWords"/>.</summary>
    public OptimiseSteered Cancel(string jobOrAll)
    {
        if (!Enter())
            return new OptimiseSteered([], StoppingWords);
        try
        {
            return new OptimiseSteered(CancelOpen(jobOrAll), "");
        }
        finally
        {
            Leave();
        }
    }

    private List<string> CancelOpen(string jobOrAll)
    {
        var cancelled = new List<string>();
        var all = string.Equals(jobOrAll, "ALL", StringComparison.OrdinalIgnoreCase);
        var killRunning = false;
        lock (_gate)
        {
            for (var i = 0; i < _jobs.Count; i++)
            {
                var j = _jobs[i];
                if (JobStates.IsOver(j.State) || (!all && !string.Equals(j.Id, jobOrAll, StringComparison.Ordinal)))
                    continue;
                if (j.Id == _running)
                {
                    killRunning = true;
                    _running = null;
                }
                _jobs[i] = j with { State = JobStates.Cancelled, Words = "cancelled", FinishedUnixMs = Now() };
                cancelled.Add(j.Id);
            }
            if (cancelled.Count > 0)
                _store.Save(_jobs);
            // Under the lock: no step can hand the next job to the worker being killed.
            if (killRunning)
                _worker.Kill();
        }
        foreach (var id in cancelled)
            DeletePartial(id);
        if (cancelled.Count > 0)
            Changed();
        Kick();
        return cancelled;
    }

    /// <summary>Holds (or releases) a job, or every unfinished one: answers the jobs whose hold changed; once the service has stopped,
    /// none, with <see cref="StoppingWords"/>.</summary>
    public OptimiseSteered Hold(string jobOrAll, bool paused)
    {
        if (!Enter())
            return new OptimiseSteered([], StoppingWords);
        try
        {
            var (changed, pending, heldAlready) = HoldOpen(jobOrAll, paused);
            foreach (var (name, id, frames) in heldAlready)
                Said?.Invoke("optimise", string.Create(CultureInfo.InvariantCulture, $"{name} ({id}) paused at frame {frames}"), false);
            return new OptimiseSteered(changed, "") { Pending = pending };
        }
        finally
        {
            Leave();
        }
    }

    /// <summary>The hold changed, the jobs it is only asked of, and those paused while their worker holds them already — read under the
    /// gate the change is made under, before the loop is kicked, so the worker's answer always comes after.</summary>
    private (List<string> Changed, List<string> Pending, List<(string Name, string Id, long Frames)> HeldAlready) HoldOpen(string jobOrAll, bool paused)
    {
        var changed = new List<string>();
        var pending = new List<string>();
        var heldAlready = new List<(string Name, string Id, long Frames)>();
        var all = string.Equals(jobOrAll, "ALL", StringComparison.OrdinalIgnoreCase);
        lock (_gate)
        {
            for (var i = 0; i < _jobs.Count; i++)
            {
                var j = _jobs[i];
                if (JobStates.IsOver(j.State) || j.Paused == paused || (!all && !string.Equals(j.Id, jobOrAll, StringComparison.Ordinal)))
                    continue;
                _jobs[i] = j with { Paused = paused };
                changed.Add(j.Id);
                // A pause of the running job is applied once its worker holds it, a resume once it goes on. A job its worker holds while
                // the knobs last sent hold it too (the governor's wait) is paused at once, at the frame it held at; knobs on their way
                // that let it go, or hold it, leave the worker's last word stale, so the change waits for its next.
                if (j.Id != _running)
                    continue;
                var knobsHold = _sent is { Holds: true };
                if (paused && j.Id == _held && knobsHold)
                {
                    heldAlready.Add((j.Name, j.Id, _heldFrame));
                }
                else if (paused || j.Id == _held || knobsHold)
                {
                    pending.Add(j.Id);
                    if (!paused)
                        _resumeAsked.Add(j.Id);
                }
            }
            if (changed.Count > 0)
                _store.Save(_jobs);
        }
        if (changed.Count > 0)
            Changed();
        Kick();
        return (changed, pending, heldAlready);
    }

    // ---- planning ---------------------------------------------------------------------------------------------------------

    private (Recipe? Recipe, string Why) Plan(EngineCore engine, MediaItem item, string? to, EncodeQuality quality, OptimiseSettings settings)
    {
        if (item.Kind != MediaKind.Video)
            return (null, "only video clips are optimised this round");
        if (!ContentHasher.IsValid(item.ContentHash))
            return (null, $"{item.Name} is not imported yet (no content hash)");
        if (SourcePath(engine, item) is not { } path)
            return (null, $"{item.Name} is not on this node");
        ProbeFacts probe;
        try
        {
            probe = OptimiseProbe.Read(path, item);
        }
        catch (Exception ex) when (ex is PlayMap.Media.Ffmpeg.MediaOpenException or PlayMap.Media.Ffmpeg.FfmpegUnavailableException or IOException)
        {
            return (null, $"{item.Name} could not be read: {ex.Message}");
        }
        var plan = to is null ? OptimisePlanner.Decide(probe, item.Tags, settings, [Here(engine)]) : OptimisePlanner.Forced(probe, to, quality);
        if (plan.Decision != Decision.Encode || plan.Recipe is null)
            return (null, plan.Words);
        var recipe = to is null ? plan.Recipe : plan.Recipe with { Quality = quality };
        var key = recipe.Key(item.ContentHash);
        if (item.Versions.FirstOrDefault(v => v.Key == key) is { } have)
            return (null, $"already has {VersionChoice.Words(have)} ({have.Id})");
        lock (_gate)
        {
            if (_jobs.FirstOrDefault(j => j.Key == key && !JobStates.IsOver(j.State)) is { } queued)
                return (null, $"already queued ({queued.Id})");
        }
        return (recipe, plan.Words);
    }

    /// <summary>This node as the planner's only target: the block families its renderer samples, no hardware decoder proven
    /// (the decode-path ladder that proves them is wp2-zerocopy's), its cores.</summary>
    private static TargetFacts Here(EngineCore engine)
    {
        var formats = (engine.Services.Media as MediaBackend)?.Formats ?? BlockFormats.Bc1 | BlockFormats.Bc3 | BlockFormats.Bc4;
        return new TargetFacts(engine.Services.Settings.NodeId, formats, [], Environment.ProcessorCount);
    }

    private static string? SourcePath(EngineCore engine, MediaItem item)
    {
        if (engine.Services.Media?.PathForHash(item.ContentHash) is { } indexed)
            return indexed;
        var folder = engine.Services.ProjectFolder ?? (engine.ProjectPath is { } p ? Path.GetDirectoryName(Path.GetFullPath(p)) : null);
        var full = MediaLibrary.ResolvePath(item, folder);
        return File.Exists(full) ? full : null;
    }

    private OptimiseOutcome Queue(MediaItem item, Recipe recipe)
    {
        var id = "oj" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4));
        var job = new OptimiseJob(id, item.Id, item.Name, item.ContentHash, recipe.Key(item.ContentHash), recipe.Canonical(), recipe.To, JobStates.Queued, Now());
        lock (_gate)
        {
            if (_jobs.FirstOrDefault(j => j.Key == job.Key && !JobStates.IsOver(j.State)) is { } queued)
                return new OptimiseOutcome(item.Id, null, $"already queued ({queued.Id})");
            _jobs.Add(job);
            _store.Save(_jobs);
        }
        Changed();
        return new OptimiseOutcome(item.Id, job, "");
    }

    /// <summary>The import hook: an item whose probe has just landed (a content hash it did not have) is planned under the
    /// project's mode — auto queues what the rules ask, ask and never queue nothing.</summary>
    private void OnProjectChanged(ProjectSnapshot snapshot)
    {
        if (_engine is not { } engine || _settings.Role == "never")
            return;
        var fresh = new List<MediaItem>();
        lock (_gate)
        {
            foreach (var m in snapshot.Project.Media)
            {
                if (m.ContentHash.Length > 0 && _seen.Add(m.Id + "|" + m.ContentHash))
                    fresh.Add(m);
            }
        }
        var settings = snapshot.Project.Optimise;
        if (fresh.Count == 0 || settings.Mode != OptimiseMode.Auto)
            return;
        Beside(() =>
        {
            foreach (var item in fresh)
            {
                var (recipe, why) = Plan(engine, item, null, settings.Quality, settings);
                if (recipe is not null)
                {
                    var outcome = Queue(item, recipe);
                    Said?.Invoke("optimise", $"{item.Name}: {why} — queued {outcome.Job!.Id}", true);
                }
            }
            Kick();
            return Task.CompletedTask;
        });
    }

    // ---- the loop -----------------------------------------------------------------------------------------------------------

    private void Kick()
    {
        if (_kick.CurrentCount == 0)
            _kick.Release();
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                StepOnce();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A step that faults is that step's; the next one runs.
                _log.LogWarning(ex, "The optimise step faulted.");
            }
            try
            {
                await _kick.WaitAsync(_options.Step, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>One step: the governor from the engine's facts, the knobs to a running worker, and the next job when the governor
    /// lets work run and nothing runs.</summary>
    internal void StepOnce()
    {
        if (_engine is not { } engine || !Enter())
            return;
        try
        {
            Step(engine);
        }
        finally
        {
            Leave();
        }
    }

    private void Step(EngineCore engine)
    {
        var now = engine.Services.Mono.NowNs;
        RetryAwaiting(engine);
        long dropped = 0;
        foreach (var h in engine.Services.Outputs.Health())
            dropped += h.Dropped;
        long late = 0;
        if (engine.Services.Media is MediaBackend media)
            foreach (var d in media.OpenDecoders())
                late += d.Source.Health.FramesLate;
        // LED output is on the air as outputs are: frames the sender dropped or skipped count as dropped, and each step a node's
        // frames leave late (its p95 over a frame) counts as a late frame, so a late node keeps the pause going while it stays late.
        if (engine.Led.Output?.View() is { } led)
        {
            dropped += led.Dropped;
            _ledLateSteps += led.Nodes.Count(n => n.State == "late");
        }
        late += _ledLateSteps;
        bool working;
        lock (_gate)
            working = _running is not null;
        var knobs = _governor.Step(new GovernorInputs(engine.Services.ShowGate.Armed, engine.IsAnimating, dropped, late, working, now));
        if (knobs.State != _lastState)
        {
            if (knobs.State == GovernorState.Paused)
                Said?.Invoke("optimise", knobs.Words, false);
            _lastState = knobs.State;
            Changed();
        }
        bool held;
        lock (_gate)
            held = _running is { } r && _jobs.Any(j => j.Id == r && j.Paused);
        var wire = new WorkerKnobs(held ? "paused" : knobs.State.ToString().ToLowerInvariant(), knobs.GpuDuty, knobs.CpuThreads, knobs.HwDecode);
        // The knobs sent are written under the gate before they leave: a pause or a resume weighs the worker's last word against them.
        var send = false;
        lock (_gate)
        {
            if (_worker.Running && _sent != wire)
            {
                _sent = wire;
                send = true;
            }
        }
        if (send)
            _ = _worker.Send(WorkerProtocol.Knobs(wire));
        OptimiseJob? next = null;
        lock (_gate)
        {
            var idle = _running is null && !_jobs.Any(j => j.State == JobStates.Queued);
            if (!idle)
                _idleSinceNs = long.MinValue;
            else if (_idleSinceNs == long.MinValue)
                _idleSinceNs = now;
            // A stopping service hands out no job: a step begun before the stop starts nothing after it.
            if (_running is null && !_closed && knobs.MayWork && now >= _restartAtNs && _settings.Role != "never")
            {
                next = _jobs.Where(j => j.State == JobStates.Queued && !j.Paused).OrderBy(j => j.CreatedUnixMs).FirstOrDefault();
                // Reserved under the lock: a cancel from here on sees the job as running and stops its worker.
                if (next is not null)
                    _running = next.Id;
            }
        }
        if (next is not null)
        {
            Dispatch(engine, next, wire);
            return;
        }
        if (_worker.Running && _idleSinceNs != long.MinValue && now - _idleSinceNs >= _settings.IdleExitS * 1_000_000_000L)
        {
            _ = _worker.StopAsync();
            lock (_gate)
                _sent = null;
            Changed();
        }
    }

    private void Dispatch(EngineCore engine, OptimiseJob job, WorkerKnobs knobs)
    {
        var item = engine.Store.Project.FindMedia(job.Media);
        var recipe = Recipe.Parse(job.Recipe);
        var source = item is null ? null : SourcePath(engine, item);
        if (item?.Versions.FirstOrDefault(v => v.Key == job.Key) is { } have)
        {
            Finish(job.Id, JobStates.Done, $"already has {VersionChoice.Words(have)} ({have.Id})");
            return;
        }
        string? refuse = item is null ? "the item was removed from the project"
            : recipe is null ? "the job's recipe is not one this node reads"
            : source is null ? $"{item.Name} is not on this node"
            : DiskShort(item, recipe);
        if (refuse is not null)
        {
            Finish(job.Id, JobStates.Failed, refuse);
            return;
        }
        bool started;
        try
        {
            started = _worker.EnsureStarted();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            Finish(job.Id, JobStates.Failed, "the optimise worker did not start: " + ex.Message);
            return;
        }
        lock (_gate)
        {
            if (_running != job.Id)
                return;   // cancelled between the step and here
            // The stop closes the worker under this gate as it closes the service: once it has begun no worker starts and none is
            // handed a job (a worker started just before it is the stop's to end); the job stays queued for the next start.
            if (!started || _closed)
            {
                _running = null;
                return;
            }
            _held = null;
            _shownHeld = null;
            _resumeAsked.Clear();
            _sent = knobs;
            DeletePartial(job.Id);
            Replace(job.Id, j => j with { State = JobStates.Encoding, FramesDone = 0, Fps = 0, Words = j.Attempts > 0 ? j.Words : "" });
            _store.Save(_jobs);
        }
        var sent = _worker.Send(WorkerProtocol.Knobs(knobs));
        sent = sent && _worker.Send(WorkerProtocol.Run(new WorkerJob(job.Id, source!, _store.PartialFor(job.Id), recipe!.To, Recipe.QualityWord(recipe.Quality),
            recipe.Chunks, recipe.SecondStage.ToString().ToLowerInvariant(), recipe.RawBelowGain, recipe.Audio == "copy")));
        if (!sent)
        {
            // The worker went as it was handed the job: the job waits for the next step and a fresh worker.
            lock (_gate)
            {
                if (_running == job.Id)
                {
                    _running = null;
                    Replace(job.Id, j => j.State == JobStates.Encoding ? j with { State = JobStates.Queued } : j);
                    _store.Save(_jobs);
                }
            }
        }
        Changed();
    }

    /// <summary>The words when the disk lacks room for the job (its uncompressed size as the estimate, plus the margin), or null.</summary>
    private string? DiskShort(MediaItem item, Recipe recipe)
    {
        try
        {
            Directory.CreateDirectory(_store.Work);
            var free = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(_store.Work))!).AvailableFreeSpace;
            var fps = Core.Time.FrameRate.TryParse(item.Info.FrameRate, out var rate) && rate.IsValid ? rate.Numerator / (double)rate.Denominator : 60;
            var frames = item.Info.DurationNs / 1e9 * fps;
            var estimate = (long)(HapCodecs.FrameBytes(recipe.Codec, Math.Max(1, item.Info.Width), Math.Max(1, item.Info.Height)) * frames);
            var margin = _settings.MinFreeGb * 1_000_000_000L;
            if (free >= estimate + margin)
                return null;
            var c = CultureInfo.InvariantCulture;
            return string.Create(c, $"needs {estimate / 1e9:0.0} GB + {_settings.MinFreeGb} GB margin; {free / 1e9:0.0} GB free beside {_store.Work}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return "the work folder's free space could not be read: " + ex.Message;
        }
    }

    // ---- the worker's lines ---------------------------------------------------------------------------------------------------

    internal void OnEvent(WorkerEvent e)
    {
        if (!Enter())
            return;
        try
        {
            Take(e);
        }
        finally
        {
            Leave();
        }
    }

    private void Take(WorkerEvent e)
    {
        switch (e.Kind)
        {
            case "hello":
                lock (_gate)
                {
                    _gl = e.Gl;
                    _encoder = e.Encoder;
                }
                Changed();
                break;
            case "held":
                string? said = null;
                lock (_gate)
                {
                    if (_running != e.Job)
                        return;
                    var was = _held == e.Job;
                    _held = e.On ? e.Job : null;
                    _heldFrame = e.Frames;
                    var job = _jobs.FirstOrDefault(j => j.Id == e.Job);
                    // The pause and the resume are facts once the worker holds or goes on: the journal says so then, not when asked.
                    if (was != e.On && e.On && job is { Paused: true })
                        said = string.Create(CultureInfo.InvariantCulture, $"{job.Name} ({e.Job}) paused at frame {e.Frames}");
                    else if (was != e.On && !e.On && job is not null && _resumeAsked.Remove(e.Job))
                        said = string.Create(CultureInfo.InvariantCulture, $"{job.Name} ({e.Job}) resumed at frame {e.Frames}");
                    if (said is null)
                        _shownHeld = _held;
                }
                if (said is not null)
                {
                    // STATE shows the hold (or its end) once the journal has the row that says so.
                    Said?.Invoke("optimise", said, false);
                    lock (_gate)
                        _shownHeld = _held;
                }
                Changed();
                break;
            case "progress":
                lock (_gate)
                    Replace(e.Job, j => j.State == JobStates.Encoding ? j with { FramesDone = e.Frames, Frames = e.Of, Fps = e.Fps } : j);
                Changed();
                break;
            case "done":
                bool mine;
                lock (_gate)
                {
                    mine = _running == e.Job;
                    if (mine)
                    {
                        // The worker is free once it says done; the recording is the engine's and runs beside the next job.
                        Replace(e.Job, j => j with { State = JobStates.Verifying, FramesDone = e.Frames, Frames = e.Frames });
                        _running = null;
                        _recording.Add(e.Job);
                        _store.Save(_jobs);
                    }
                }
                if (mine)
                    Beside(() => RecordAsync(e));
                Changed();
                break;
            case "failed":
                lock (_gate)
                {
                    if (_running != e.Job)
                        return;
                }
                Finish(e.Job, JobStates.Failed, e.Words);
                break;
        }
    }

    internal void OnExited(int code, bool expected)
    {
        if (!Enter())
            return;
        try
        {
            Ended(code, expected);
        }
        finally
        {
            Leave();
        }
    }

    private void Ended(int code, bool expected)
    {
        string? job;
        lock (_gate)
        {
            job = _running;
            _sent = null;
            _running = null;
            _held = null;
            _shownHeld = null;
            _resumeAsked.Clear();
            // The worker is gone and with it the partial file's writer: the file goes before the job reads as queued or failed.
            if (job is not null)
                DeletePartial(job);
            if (job is not null && expected)
            {
                // Stopped by the engine (idle exit, shutdown) while it held a job: the job waits for the next worker, no attempt spent.
                Replace(job, j => j.State == JobStates.Encoding ? j with { State = JobStates.Queued, FramesDone = 0, Fps = 0 } : j);
                _store.Save(_jobs);
            }
            else if (job is not null)
            {
                _restarts++;
                _crashStreak++;
                var backoff = Math.Min(30_000_000_000L, _options.FirstBackoff.Ticks * 100 * (1L << Math.Min(10, _crashStreak - 1)));
                _restartAtNs = (_engine?.Services.Mono.NowNs ?? 0) + backoff;
                var attempts = (_jobs.FirstOrDefault(j => j.Id == job)?.Attempts ?? 0) + 1;
                var words = string.Create(CultureInfo.InvariantCulture, $"the worker stopped (exit code {code})");
                if (attempts >= _options.MaxAttempts)
                {
                    Replace(job, j => j with
                    {
                        Attempts = attempts,
                        State = JobStates.Failed,
                        Words = string.Create(CultureInfo.InvariantCulture, $"{words} {(attempts == 1 ? "once" : $"{attempts} times")} on this clip — it is not optimised"),
                        FinishedUnixMs = Now(),
                    });
                }
                else
                {
                    Replace(job, j => j with { Attempts = attempts, State = JobStates.Queued, FramesDone = 0, Fps = 0, Words = words + "; restarted, the job starts again from its first frame" });
                }
                _store.Save(_jobs);
            }
        }
        SweepWork();
        if (job is not null && !expected)
            Said?.Invoke("optimise", string.Create(CultureInfo.InvariantCulture, $"the optimise worker stopped (exit code {code}) during {job}"), false);
        Changed();
        Kick();
    }

    /// <summary>A worker replaced on its way out has gone: what it held is swept.</summary>
    internal void OnLetGo()
    {
        if (!Enter())
            return;
        try
        {
            SweepWork();
        }
        finally
        {
            Leave();
        }
    }

    /// <summary>A killed worker that has not gone: the node goes on without it; what it holds is deleted once it is let go (a sweep
    /// after it has gone, else the node's next start).</summary>
    private void OnStuck(int pid) =>
        Said?.Invoke("optimise", string.Create(CultureInfo.InvariantCulture,
            $"the optimise worker (pid {pid}) has not ended {_options.KillWait.TotalSeconds:0.#} s after it was killed (a driver or a disk holds it): the node goes on without it"), false);

    /// <summary>A finished job: hashed, probed, held to the quality floor, moved into the store and recorded on its item.</summary>
    private async Task RecordAsync(WorkerEvent e)
    {
        var engine = _engine!;
        var partial = _store.PartialFor(e.Job);
        OptimiseJob? job;
        lock (_gate)
            job = _jobs.FirstOrDefault(j => j.Id == e.Job);
        if (job is null)
            return;
        try
        {
            var q = e.Quality;
            if (q.Sampled == 0)
            {
                Finish(job.Id, JobStates.Failed, "no frame of the version was compared with its source — the version is not kept");
                return;
            }
            if (q.PsnrMin < _settings.MinPsnr)
            {
                Finish(job.Id, JobStates.Failed, string.Create(CultureInfo.InvariantCulture,
                    $"lowest {q.PsnrMin:0.0} dB at {q.WorstAtMs / 1000.0:0.0} s is below this node's floor of {_settings.MinPsnr:0} dB — the version is not kept"));
                return;
            }
            string hash;
            await using (var file = File.OpenRead(partial))
                hash = "sha256:" + Convert.ToHexStringLower(await SHA256.HashDataAsync(file, _stop.Token));
            var probe = await MediaProbe.ProbeAsync(partial, _stop.Token);
            if (!probe.Decodable)
            {
                Finish(job.Id, JobStates.Failed, "the version does not decode: " + probe.Message);
                return;
            }
            var size = new FileInfo(partial).Length;
            Directory.CreateDirectory(_home.MediaStore);
            var media = engine.Services.Media as MediaBackend;
            var dest = media?.StorePath(hash, ".mov") ?? Path.Combine(_home.MediaStore, hash["sha256:".Length..] + ".mov");
            if (!Verifying(job.Id))
                return;   // cancelled while it was hashed: nothing is recorded
            DurableFile.MoveIntoPlace(partial, dest);
            media?.AddVerified(hash, dest, job.Name + " (" + job.To + ").mov");
            await RecordStepAsync(new Ready(job, hash, size, probe.Info, q, e.Audio, _encoder));
        }
        catch (OperationCanceledException ex) when (_stop.IsCancellationRequested)
        {
            // The node is going: the job stays verifying on disk and is queued again when the node next starts.
            _log.LogDebug(ex, "The recording of {Job} stopped with the node.", job.Id);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Whatever else stopped the recording (a disk, a probe) fails the job with its words, never leaves it verifying.
            Finish(job.Id, JobStates.Failed, "the version could not be recorded: " + ex.Message);
        }
    }

    /// <summary>A finished job's file, in the store and indexed, waiting to be named by a version on its item.</summary>
    private sealed record Ready(OptimiseJob Job, string Hash, long Size, MediaInfo Info, VersionQuality Quality, string Audio, string Encoder);

    /// <summary>The one edit that names the version on its item. Refused because a blind commit lands the item, it waits (the job
    /// stays verifying, with the words why) and is tried again once no commit waits; any other refusal fails the job with its words.</summary>
    private async Task RecordStepAsync(Ready ready)
    {
        var engine = _engine!;
        var job = ready.Job;
        var q = ready.Quality;
        var versionId = "";
        var outcome = "";
        try
        {
            var r = await engine.EditAsync(EngineCore.SystemSession, $"optimised {job.Name} → {job.To}", p =>
            {
                if (p.FindMedia(job.Media) is not { } item)
                {
                    outcome = "gone";
                    return p;
                }
                if (item.Versions.FirstOrDefault(v => v.Key == job.Key) is { } have)
                {
                    (outcome, versionId) = ("has", have.Id);
                    return p;
                }
                if (!Verifying(job.Id))
                    return p;
                versionId = p.NewId(Ids.MediaVersion);
                var v = new MediaVersion(versionId, job.To, job.Recipe, job.Key, ready.Hash, ready.Size, ready.Info, q,
                    Recipe.EncoderVersion + " · " + ready.Encoder, engine.Services.Settings.NodeId, engine.Services.Wall.UnixMs);
                return p with { Media = p.Media.Update(job.Media, m => m with { Versions = m.Versions.Add(v) }) };
            });
            if (!Verifying(job.Id))
                return;
            if (!r.Changed && outcome == "gone")
            {
                Finish(job.Id, JobStates.Failed, "the version was not recorded: the item was removed from the project");
                return;
            }
            if (!r.Changed && outcome != "has" && engine.Snapshot.Pending is not null)
            {
                // A blind commit waiting for its boundary lands this item: the version waits for it, and is recorded after it.
                lock (_gate)
                {
                    _awaiting[job.Id] = ready;
                    Replace(job.Id, j => j with { Words = "recorded when the blind commit lands: " + r.Effect });
                }
                Changed();
                return;
            }
            if (!r.Changed && outcome != "has")
            {
                Finish(job.Id, JobStates.Failed, "the version was not recorded: " + (r.Effect.Length > 0 ? r.Effect : "the edit changed nothing"));
                return;
            }
            lock (_gate)
            {
                _awaiting.Remove(job.Id);
                Replace(job.Id, j => JobStates.IsOver(j.State) ? j : j with { State = JobStates.Done, Version = versionId, PsnrAvg = q.PsnrAvg, Words = string.Create(CultureInfo.InvariantCulture, $"{ready.Audio}; {q.PsnrAvg:0.0} dB average, {q.PsnrMin:0.0} dB lowest"), FinishedUnixMs = Now() });
                _recording.Remove(job.Id);
                _crashStreak = 0;
                _store.Save(_jobs);
            }
            Said?.Invoke("optimise", string.Create(CultureInfo.InvariantCulture, $"{job.Name}: version {versionId} ({job.To}) made, {q.PsnrAvg:0.0} dB"), true);
            Changed();
            Kick();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Finish(job.Id, JobStates.Failed, "the version could not be recorded: " + ex.Message);
        }
        finally
        {
            lock (_gate)
                _retrying.Remove(job.Id);
        }
    }

    /// <summary>Versions that waited for a blind commit are recorded once none waits (it landed, or was discarded).</summary>
    private void RetryAwaiting(EngineCore engine)
    {
        if (engine.Snapshot.Pending is not null)
            return;
        List<Ready> retry;
        lock (_gate)
        {
            retry = [.. _awaiting.Values.Where(r => _retrying.Add(r.Job.Id))];
            foreach (var r in retry)
                _awaiting.Remove(r.Job.Id);
        }
        foreach (var r in retry)
            Beside(() => RecordStepAsync(r));
    }

    private bool Verifying(string jobId)
    {
        lock (_gate)
            return _jobs.Any(j => j.Id == jobId && j.State == JobStates.Verifying);
    }

    /// <summary>Deletes the partial files of jobs that are not running or being recorded (a kill's leftover, an old run's).</summary>
    private void SweepWork()
    {
        HashSet<string> keep;
        lock (_gate)
        {
            keep = [.. _recording];
            if (_running is { } r)
                keep.Add(r);
        }
        try
        {
            if (!Directory.Exists(_store.Work))
                return;
            foreach (var file in Directory.EnumerateFiles(_store.Work, "*.mov.part"))
            {
                var id = Path.GetFileName(file)[..^".mov.part".Length];
                if (!keep.Contains(id))
                    DeletePartial(id);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning(ex, "The optimise work folder could not be swept.");
        }
    }

    private void Finish(string jobId, string state, string words)
    {
        lock (_gate)
        {
            // The partial file goes before the job reads as over: whoever sees it done or failed finds nothing of it in the work folder.
            DeletePartial(jobId);
            // A job already over (cancelled while its file was being recorded) keeps its end.
            Replace(jobId, j => JobStates.IsOver(j.State) ? j : j with { State = state, Words = words, FinishedUnixMs = Now() });
            if (_running == jobId)
                _running = null;
            _recording.Remove(jobId);
            _awaiting.Remove(jobId);
            _store.Save(_jobs);
        }
        if (state == JobStates.Failed)
            Said?.Invoke("optimise", $"{jobId} failed: {words}", false);
        Changed();
        Kick();
    }

    private void Replace(string jobId, Func<OptimiseJob, OptimiseJob> change)
    {
        var i = _jobs.FindIndex(j => j.Id == jobId);
        if (i >= 0)
            _jobs[i] = change(_jobs[i]);
    }

    private void DeletePartial(string jobId)
    {
        try
        {
            var path = _store.PartialFor(jobId);
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning(ex, "The partial file of {Job} could not be deleted.", jobId);
        }
    }

    private long Now() => _engine?.Services.Wall.UnixMs ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private void Changed()
    {
        Interlocked.Increment(ref _version);
        _engine?.FactsChanged();
    }

    /// <summary>What a request, a cancel or a hold answers once the service has stopped.</summary>
    internal const string StoppingWords = "the node is stopping — its optimise queue takes no more changes";

    /// <summary>Begins work that may write the queue or the work folder: false once the service has closed (nothing starts after
    /// its stop). Each true is matched by one <see cref="Leave"/>.</summary>
    private bool Enter()
    {
        lock (_gate)
        {
            if (_closed)
                return false;
            _busy++;
            return true;
        }
    }

    private void Leave()
    {
        lock (_gate)
        {
            if (--_busy == 0 && _closed)
                _quiet.TrySetResult();
        }
    }

    /// <summary>Work beside the caller (a recording, a plan), counted as begun from now: none once the service has closed.</summary>
    private void Beside(Func<Task> work)
    {
        if (!Enter())
            return;
        _ = Task.Run(async () =>
        {
            try
            {
                await work();
            }
            finally
            {
                Leave();
            }
        }, CancellationToken.None);
    }

    /// <summary>Stops the service: closed first, so a line, a step, a worker's line or end and a sweep that come later do nothing;
    /// then it waits for the work begun before (each write of the queue is whole, under the gate), so nothing writes the queue or
    /// the work folder once this returns. A running job stays "encoding" on disk and is queued again when the node next starts.</summary>
    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_closed)
                return;
            _closed = true;
            // No worker starts from now: a step under way starts none (its job stays queued).
            _worker.Close();
            if (_busy == 0)
                _quiet.TrySetResult();
        }
        if (_engine is { } engine)
            engine.Store.Changed -= OnProjectChanged;
        // The node is going: its worker's last words (a job it was told to stop) are not the job's end.
        _worker.Event -= OnEvent;
        _worker.Exited -= OnExited;
        _worker.LetGo -= OnLetGo;
        await _stop.CancelAsync();
        try
        {
            await _loop;
        }
        catch (OperationCanceledException)
        {
            // The loop ends on the stop it was given.
        }
        // A recording cut short by the stop ends at once; a write in flight ends whole.
        if (!_quiet.Task.IsCompleted)
        {
            _options.StopWaits?.Invoke();
            await _quiet.Task;
        }
        // A worker that will not end is said before the node's journal closes; the node's stop does not wait for it.
        await _worker.DisposeAsync();
        _worker.Stuck -= OnStuck;
        _stop.Dispose();
        _kick.Dispose();
    }
}
