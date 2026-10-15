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

namespace PlayMap.Engine.Optimise;

/// <summary>What the optimise service is made with: how its worker starts, its step, how many times a clip may stop the worker
/// before its job fails, and the first restart backoff (doubled per restart, at most 30 s).</summary>
public sealed record OptimiseOptions
{
    public WorkerLaunch Worker { get; init; } = WorkerLaunch.ThisProcess();
    public TimeSpan Step { get; init; } = TimeSpan.FromSeconds(1);
    public int MaxAttempts { get; init; } = 3;
    public TimeSpan FirstBackoff { get; init; } = TimeSpan.FromSeconds(1);
}

/// <summary>What STATE says of the service: the governor, the counts, the worker and the jobs (the running one first, then the
/// queue in order, then the latest finished).</summary>
public sealed record OptimiseView(GovernorKnobs Governor, int Running, int Queued, int Failed, int? Pid, string Gl, string Encoder, int Restarts,
    IReadOnlyList<OptimiseJob> Jobs);

/// <summary>One item's answer to a request: the job queued for it, or why none was.</summary>
public sealed record OptimiseOutcome(string Media, OptimiseJob? Job, string Why);

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
    private long _restartAtNs = long.MinValue;
    private long _idleSinceNs = long.MinValue;
    private int _restarts;
    private int _crashStreak;
    private bool _disposed;
    private readonly HashSet<string> _recording = new(StringComparer.Ordinal);
    private string _gl = "";
    private string _encoder = "";
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
        _worker = new OptimiseSupervisor(_options.Worker, log);
        _worker.Event += OnEvent;
        _worker.Exited += OnExited;
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
                return [.. _jobs];
        }
    }

    public OptimiseView View
    {
        get
        {
            lock (_gate)
            {
                var running = _jobs.Where(j => JobStates.IsRunning(j.State)).ToList();
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

    /// <summary>Cancels a job (or every unfinished one): a queued job leaves the queue, a running one stops (its worker is killed)
    /// and its partial file is deleted. Answers the jobs cancelled.</summary>
    public IReadOnlyList<string> Cancel(string jobOrAll)
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

    /// <summary>Holds (or releases) a job, or every unfinished one: answers the jobs whose hold changed.</summary>
    public IReadOnlyList<string> Hold(string jobOrAll, bool paused)
    {
        var changed = new List<string>();
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
            }
            if (changed.Count > 0)
                _store.Save(_jobs);
        }
        if (changed.Count > 0)
            Changed();
        Kick();
        return changed;
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
        _ = Task.Run(() =>
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
        }, CancellationToken.None);
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
        if (_engine is not { } engine)
            return;
        var now = engine.Services.Mono.NowNs;
        long dropped = 0;
        foreach (var h in engine.Services.Outputs.Health())
            dropped += h.Dropped;
        long late = 0;
        if (engine.Services.Media is MediaBackend media)
            foreach (var d in media.OpenDecoders())
                late += d.Source.Health.FramesLate;
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
        if (_worker.Running && _sent != wire)
        {
            _ = _worker.Send(WorkerProtocol.Knobs(wire));
            _sent = wire;
        }
        OptimiseJob? next = null;
        lock (_gate)
        {
            var idle = _running is null && !_jobs.Any(j => j.State == JobStates.Queued);
            if (!idle)
                _idleSinceNs = long.MinValue;
            else if (_idleSinceNs == long.MinValue)
                _idleSinceNs = now;
            if (_running is null && knobs.MayWork && now >= _restartAtNs && _settings.Role != "never")
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
        try
        {
            _worker.EnsureStarted();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            Finish(job.Id, JobStates.Failed, "the optimise worker did not start: " + ex.Message);
            return;
        }
        DeletePartial(job.Id);
        lock (_gate)
        {
            if (_running != job.Id)
                return;   // cancelled between the step and here
            Replace(job.Id, j => j with { State = JobStates.Encoding, FramesDone = 0, Fps = 0, Words = j.Attempts > 0 ? j.Words : "" });
            _store.Save(_jobs);
        }
        var sent = _worker.Send(WorkerProtocol.Knobs(knobs));
        _sent = knobs;
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

    private void OnEvent(WorkerEvent e)
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
                    _ = Task.Run(() => RecordAsync(e), CancellationToken.None);
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

    private void OnExited(int code, bool expected)
    {
        string? job;
        lock (_gate)
        {
            job = _running;
            _sent = null;
            _running = null;
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
            File.Move(partial, dest, overwrite: true);
            media?.AddVerified(hash, dest, job.Name + " (" + job.To + ").mov");
            var encoder = _encoder;
            var versionId = "";
            var r = await engine.EditAsync(EngineCore.SystemSession, $"optimised {job.Name} → {job.To}", p =>
            {
                if (p.FindMedia(job.Media) is not { } item || item.Versions.Any(v => v.Key == job.Key) || !Verifying(job.Id))
                    return p;
                versionId = p.NewId(Ids.MediaVersion);
                var v = new MediaVersion(versionId, job.To, job.Recipe, job.Key, hash, size, probe.Info, q, Recipe.EncoderVersion + " · " + encoder,
                    engine.Services.Settings.NodeId, engine.Services.Wall.UnixMs);
                return p with { Media = p.Media.Update(job.Media, m => m with { Versions = m.Versions.Add(v) }) };
            });
            if (!r.Changed && !Verifying(job.Id))
                return;
            if (!r.Changed)
            {
                Finish(job.Id, JobStates.Failed, "the version was not recorded: " + (r.Effect.Length > 0 ? r.Effect : "the item is gone or has it already"));
                return;
            }
            lock (_gate)
            {
                Replace(job.Id, j => JobStates.IsOver(j.State) ? j : j with { State = JobStates.Done, Version = versionId, PsnrAvg = q.PsnrAvg, Words = string.Create(CultureInfo.InvariantCulture, $"{e.Audio}; {q.PsnrAvg:0.0} dB average, {q.PsnrMin:0.0} dB lowest"), FinishedUnixMs = Now() });
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
            // Whatever stopped the recording (a disk, a probe, the node going) fails the job with its words, never leaves it verifying.
            Finish(job.Id, JobStates.Failed, "the version could not be recorded: " + ex.Message);
        }
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
            // A job already over (cancelled while its file was being recorded) keeps its end.
            Replace(jobId, j => JobStates.IsOver(j.State) ? j : j with { State = state, Words = words, FinishedUnixMs = Now() });
            if (_running == jobId)
                _running = null;
            _recording.Remove(jobId);
            _store.Save(_jobs);
        }
        DeletePartial(jobId);
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

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (_engine is { } engine)
            engine.Store.Changed -= OnProjectChanged;
        // The node is going: its worker's last words (a job it was told to stop) are not the job's end. A running job stays
        // "encoding" on disk and is queued again when the node next starts.
        _worker.Event -= OnEvent;
        _worker.Exited -= OnExited;
        await _stop.CancelAsync();
        try
        {
            await _loop;
        }
        catch (OperationCanceledException)
        {
            // The loop ends on the stop it was given.
        }
        await _worker.DisposeAsync();
        _stop.Dispose();
        _kick.Dispose();
    }
}
