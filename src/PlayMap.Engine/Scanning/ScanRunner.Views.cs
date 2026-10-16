using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using PlayMap.Assistant;
using PlayMap.Core.Model;
using PlayMap.Core.Scan;

namespace PlayMap.Engine.Scanning;

/// <summary>wp2-multiscan's options of the scan runner.</summary>
public sealed partial record ScanRunnerOptions
{
    /// <summary>A hand on the simulated camera for one view's run of one projector (view id, projector id, the camera as the scene
    /// has it → the camera that takes the pictures): a test nudges it between two projectors' runs, as a camera knocked mid-view
    /// is. Null: the scene's camera.</summary>
    public Func<string, string, Core.Scan.SimDevice, Core.Scan.SimDevice>? SimulatedNudge { get; init; }

    /// <summary>Makes the model SCAN SUGGEST … ASSISTANT asks, from the node's assistant settings and key (null:
    /// <see cref="AssistantProviders.Create"/>, the node's own assistant). Tests hand in a recording one.</summary>
    public Func<AssistantSettings, string?, IAssistantProvider?>? SuggestModel { get; init; }
}

/// <summary>STATE.scan.coverage's row: the share of a projector's scanned raster its views decoded once and twice.</summary>
public sealed record CoverageState(string ProjectorId, double Seen, double Twice);

/// <summary>STATE.scan's multi-view part (wp2-multiscan).</summary>
public sealed partial record ScanView
{
    /// <summary>The view this run adds (or the scan's last view).</summary>
    public string ViewId { get; init; } = "v1";

    public ImmutableArray<ScanViewData> Views { get; init; } = [];

    public ImmutableArray<CoverageState> Coverage { get; init; } = [];
}

/// <summary>
/// wp2-multiscan's part of the runner: a view added INTO a scan, the scan's views solved again (SCAN SOLVE, with projectors held)
/// or without one (SCAN DROP), and the scan shown in STATE with its views, coverage, regions across projectors and its waiting
/// suggestions. A solve runs off the lane, one per scan at a time, never while that scan takes a view or has its suggestions made
/// (and no suggestion is made while it takes a view or is solved), and not at all once the runner has begun to stop; the node's stop
/// waits for one under way.
/// </summary>
public sealed partial class ScanRunner
{
    /// <summary>Scans being solved now (under the gate).</summary>
    private readonly HashSet<string> _solving = new(StringComparer.Ordinal);

    /// <summary>Solves under way and their cancellation (under the gate).</summary>
    private readonly Dictionary<Task, CancellationTokenSource> _work = [];

    private readonly Dictionary<string, ImmutableArray<ScanSuggestion>> _suggestions = new(StringComparer.Ordinal);

    /// <summary>The options the runner was made with.</summary>
    internal ScanRunnerOptions Options => _options;

    /// <summary>A run's pictures as a view of its scan: the camera and its optics as they were, each projector's decode, how the
    /// pictures were paired, and the camera latency its flashes measured for each projector (none for a cued camera or the simulator).</summary>
    private static ScanViewData NewView(Setup setup, List<ProjectorCapture> captures, ScanPairing? pairing, long nowUnixMs,
        IReadOnlyList<(string ProjectorId, LatencyEstimate Latency)> latencies)
    {
        var c = setup.Camera;
        return new ScanViewData(setup.ViewId, c.Id, c.Name, c.Intrinsics, c.Lens, c.Calibrated, [.. captures.Select(x => x.Projector.Id)],
            pairing?.Mode ?? (setup.Simulated ? "simulated" : "stream"), pairing?.Exposure ?? (setup.Simulated ? "simulated" : "auto"), nowUnixMs)
        {
            Maps = captures.ToImmutableDictionary(x => x.Projector.Id, x => x.Map, StringComparer.Ordinal),
            Latencies = [.. latencies.Select(l => new ViewLatency(l.ProjectorId, l.Latency))],
        };
    }

    /// <summary>A single view placed by its scan's own solve: the camera's pose (in the site frame the stage had then) and its mean
    /// distance to the projectors solved.</summary>
    private static ScanViewData Placed(ScanViewData view, ScanSolution solution, SiteFrame site)
    {
        var solved = solution.Projectors.Where(p => p.Solved).ToList();
        var baseline = solved.Count > 0 ? solved.Average(p => (p.Pose.Position - solution.Camera.Pose.Position).Length) : 0;
        return view with
        {
            Pose = solution.Camera.Pose,
            Baseline = baseline,
            RmsPx = Math.Round(solved.Select(p => p.RmsPx).DefaultIfEmpty(0).Max(), 4),
            Tracks = solved.Sum(p => p.Inliers),
            Site = site,
        };
    }

    /// <summary>SCAN START … INTO: the scan the view joins. One this node holds in memory is checked now; one it would read from its
    /// folder is read by the run (<see cref="Joining"/>), so no file is read on the lane or under the runner's gate.</summary>
    private Setup IntoSetup(EngineCore engine, Setup setup, string into)
    {
        var record = setup.Project.Stage.Scans.Find(into) ?? throw new InvalidOperationException($"no scan {into}");
        if (record.Kind != ScanKind.StructuredLight)
            throw new InvalidOperationException($"{record.Name} is not a structured-light scan: views are added to those only");
        if (_solving.Contains(record.Id))
            throw new InvalidOperationException($"{record.Name} is being solved — add the view once it is done");
        // Suggestions name regions of the solve they are made from: a view that lands meanwhile would leave them naming a solve that is gone.
        if (_suggesting.Contains(record.Id))
            throw new InvalidOperationException($"{record.Name}'s suggestions are being made — add the view once they are queued");
        var held = Find(record.Id);
        if (held is null && engine.ProjectFolderForFiles is null)
            throw new InvalidOperationException($"{record.Name}'s data is not on this node — views join a scan this node made and keeps");
        return held is null ? setup with { Id = record.Id, IntoRecord = record } : Joining(setup, record, held);
    }

    /// <summary>The scan the view joins — this node's, with its views kept, the same kind of camera (simulated or real) — and the
    /// view's id: one past the highest the scan has ever used (a view that no longer reads keeps its number).</summary>
    private Setup Joining(Setup setup, ScanRecord record, ScanOutcome? existing)
    {
        if (existing is null)
            throw new InvalidOperationException($"{record.Name}'s data is not on this node — views join a scan this node made and keeps");
        if (existing.Captured.IsDefaultOrEmpty)
            throw new InvalidOperationException($"{record.Name} keeps no views (it was made before scans kept them) — scan again, then add views INTO the new scan");
        if (existing.Simulated != setup.Simulated)
            throw new InvalidOperationException(existing.Simulated
                ? $"{record.Name} is a simulated scan: its views come from the simulator"
                : $"{record.Name} was scanned through a real camera: the simulator cannot add a view to it");
        var numbers = existing.Captured.Select(v => ViewNumber(v.Id)).ToList();
        if (setup.Folder is { } folder && Directory.Exists(Path.Combine(folder, record.DataPath, "views")))
            numbers.AddRange(Directory.EnumerateDirectories(Path.Combine(folder, record.DataPath, "views")).Select(d => ViewNumber(Path.GetFileName(d))));
        var next = numbers.DefaultIfEmpty(0).Max() + 1;
        return setup with
        {
            Id = record.Id,
            Into = existing,
            IntoRecord = record,
            ViewId = "v" + next.ToString(CultureInfo.InvariantCulture),
            ViewIndex = existing.Captured.Length,
        };
    }

    private static int ViewNumber(string id) =>
        id.Length > 1 && id[0] == 'v' && int.TryParse(id.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 0;

    /// <summary>A finished run's view joins its scan: every view solved together, carried onto the scan's frame, and kept.</summary>
    private async Task<string> AddViewAsync(EngineCore engine, Setup setup, Track track, ScanOutcome into, ScanViewData view, List<string> notes,
        CancellationToken ct)
    {
        ImmutableArray<ScanViewData> views = [.. into.Captured, view];
        var active = views.Count(v => v.Solvable);
        track.Step("running", "calibrating", string.Create(CultureInfo.InvariantCulture, $"solving {active} views of {into.Name} together"), overall: 0.88);
        var project = engine.Store.Project;
        if (MultiViewScan.FrameMoved(views, project))
            notes.Add(MultiViewScan.CarriedWords + ".");
        if (MultiViewScan.UnreadWords(views) is { } unread)
            notes.Add(unread + ".");
        var (multi, placed) = await Task.Run(() => MultiViewScan.Solve(views, project, [], into.Solution.Metric, null, ct), ct).ConfigureAwait(false);
        var outcome = Combined(into, multi, placed, project, into.Solution.Metric || multi.Result.Metric);
        track.Regions(outcome.Regions);
        if (outcome.Simulated)
            notes.Add(TruthWords(outcome));
        await CommitAsync(engine, outcome, notes, ct).ConfigureAwait(false);
        track.Solved(outcome);
        var worst = outcome.Solution.Projectors.Where(p => p.Solved).Select(p => p.Quality).DefaultIfEmpty(0).Min();
        track.Quality(worst / 100.0, MultiViewScan.Words(multi));
        track.End("done", string.Create(CultureInfo.InvariantCulture,
            $"{view.Id} added to {into.Name}: {MultiViewScan.Words(multi)} SCAN APPLY {into.Id} puts it on the stage."));
        return into.Id;
    }

    /// <summary>The scan as its views solved together make it: the solution SCAN APPLY reads, the regions across projectors, a smart
    /// scan per projector from the view that decoded most of it.</summary>
    private static ScanOutcome Combined(ScanOutcome scan, MultiViewOutcome multi, ImmutableArray<ScanViewData> views, Project project, bool metric)
    {
        var solution = MultiViewScan.ToSolution(multi, views, project, metric);
        var smart = new List<(string, ProjectorView)>();
        foreach (var p in multi.Result.Projectors)
        {
            var best = views.Where(v => v.State == "done" && v.Maps.ContainsKey(p.Id)).Select(v => v.Maps[p.Id]).MaxBy(m => m.Stats.Valid);
            if (best is { Axes: ScanAxes.Both, Stats.Valid: > 0 })
                smart.Add((p.Id, ProjectorView.Build(best)));
        }
        return new ScanOutcome(scan.Id, scan.Name, solution.Camera.Id, scan.Simulated, scan.Scene, solution, smart, scan.Truth)
        {
            Captured = views,
            Multi = multi,
            Regions = MultiViewScan.Regions(multi),
            Latencies = MultiViewScan.Latencies(views),
        };
    }

    /// <summary>A re-solved scan kept: its folder written, held in memory, its record brought up to date in one edit (a new mesh
    /// takes the record off the stage's old mesh: SCAN APPLY puts the new one on).</summary>
    private async Task CommitAsync(EngineCore engine, ScanOutcome outcome, List<string> notes, CancellationToken ct)
    {
        // A lock taken on the scan (or the stage) while it was solved refuses its record's edit: said before the folder is written.
        if (engine.WouldRefuse(ScanRecordTouched(outcome.Id)) is { } locked)
            throw new InvalidOperationException(locked + " — the solve was not kept");
        var folder = engine.ProjectFolderForFiles;
        var dataPath = "";
        if (folder is not null)
        {
            try
            {
                dataPath = await Task.Run(() => ScanArchive.Save(folder, outcome), ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                notes.Add("the scan's files could not be written (" + ex.Message + "); it is kept in memory until the node stops");
            }
        }
        else
        {
            notes.Add("the project is not saved yet: the scan is kept in memory until the node stops");
        }
        Keep(outcome);
        ViewsOf()?.Finished(outcome);
        var solution = outcome.Solution;
        var words = string.Join(" ", new[] { solution.Words }.Concat(notes.Where(n => n.Length > 0)));
        // Suggestions name regions and corners of the solve they were made from: once it is solved again they no longer hold.
        WithdrawSuggestions(engine, outcome.Id, "the scan was solved again — SCAN SUGGEST again");
        var label = $"scan {outcome.Name}";
        var recorded = await engine.EditAsync(EngineCore.SystemSession, label, p => p.WithStage(s =>
        {
            // A scan removed meanwhile stays removed.
            if (s.Scans.Find(outcome.Id) is not { } current)
                return s;
            // The camera an earlier SCAN APPLY placed stays the scan's; the new mesh is not on the stage until SCAN APPLY puts it there.
            var camera = current.CameraId is { } c && p.Stage.Cameras.Find(c) is not null ? c : outcome.CameraId;
            var record = current with
            {
                CameraId = camera,
                ProjectorIds = [.. solution.Projectors.Select(x => x.ProjectorId)],
                DataPath = dataPath.Length > 0 ? dataPath : current.DataPath,
                MeshId = null,
                Residual = outcome.WorstResidualPx,
                Points = solution.Points,
                Notes = words,
            };
            return s with { Scans = s.Scans.Update(outcome.Id, _ => record) };
        })).ConfigureAwait(false);
        // Attempts are not facts: a record the store refused (a lock taken in the last instant) is said, not taken as written.
        if (!recorded.Changed && engine.Store.Project.Stage.Scans.Find(outcome.Id) is not null && recorded.Effect != label + ": nothing to change")
            throw new InvalidOperationException($"{recorded.Effect} — the solve is in the scan's folder, but its record on the stage was not changed");
    }

    /// <summary>The edit a scan's record takes when it is recorded again: what a lock on the scan, on the mapping area or the armed
    /// show would refuse.</summary>
    internal static Func<Project, Project> ScanRecordTouched(string scanId) =>
        p => p.WithStage(s => s with { Scans = s.Scans.Update(scanId, sc => sc with { Notes = sc.Notes + " " }) });

    /// <summary>A scan this node holds in memory, else read back from its folder (and held).</summary>
    private ScanOutcome? Load(EngineCore engine, ScanRecord record)
    {
        if (Find(record.Id) is { } kept)
            return kept;
        if (engine.ProjectFolderForFiles is not { } folder || ScanArchive.Load(folder, record.DataPath) is not { } loaded)
            return null;
        Keep(loaded);
        return loaded;
    }

    /// <summary>SCAN SOLVE: the scan's views solved together again — fresh, with <paramref name="hold"/>'s projectors at their stage
    /// poses. Refused when nothing would change (every view solved together already and nothing held).</summary>
    public Task<ScanOutcome> SolveAsync(string scanId, IReadOnlyCollection<string> hold, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(scanId);
        ArgumentNullException.ThrowIfNull(hold);
        return RunWorkAsync(scanId, async (engine, scan, token) =>
        {
            var active = scan.Captured.Where(v => v.Solvable).Select(v => v.Id).ToList();
            // A scan whose views were placed in a site frame the stage has moved from since changes when solved again: it is carried.
            var moved = MultiViewScan.FrameMoved(scan.Captured, engine.Store.Project);
            if (hold.Count == 0)
            {
                if (!moved && scan.Multi is { } m && m.Held.IsDefaultOrEmpty && Given(m).SetEquals(active))
                    throw new InvalidOperationException($"nothing would change: every view of {scan.Name} was solved together already{LeftOutWords(m)} — HOLD <projector> re-solves holding one at its stage pose");
                if (scan.Multi is null && active.Count == 1)
                    throw new InvalidOperationException(moved
                        ? $"{scan.Name} has one view, placed before SITE ORIGIN moved the stage — SCAN START <camera> INTO {scan.Id} adds another and carries both into the new frame"
                        : $"nothing would change: {scan.Name} has one view, solved when it was scanned — SCAN START <camera> INTO {scan.Id} adds another");
            }
            else if (!moved && scan.Multi is { } held && held.Held.ToHashSet(StringComparer.Ordinal).SetEquals(hold) && Given(held).SetEquals(active))
            {
                throw new InvalidOperationException($"nothing would change: {scan.Name} was solved holding {string.Join(", ", hold)} already, with the same views{LeftOutWords(held)}");
            }
            var known = scan.Captured.Where(v => v.Solvable).SelectMany(v => v.ProjectorIds).ToHashSet(StringComparer.Ordinal);
            if (hold.FirstOrDefault(h => !known.Contains(h)) is { } stranger)
                throw new InvalidOperationException($"{stranger} is not a projector of {scan.Name}: HOLD names projectors its views saw");
            var project = engine.Store.Project;
            var (multi, placed) = await Task.Run(() => MultiViewScan.Solve(scan.Captured, project, hold, scan.Solution.Metric, null, token), token).ConfigureAwait(false);
            var metric = hold.Count > 0 ? multi.Result.Metric : scan.Solution.Metric || multi.Result.Metric;
            return Combined(scan, multi, placed, project, metric);
        }, ct);
    }

    /// <summary>The views a solve was given: those it used and those it left out — the same solve of the same views leaves them out again.</summary>
    private static HashSet<string> Given(MultiViewOutcome m) =>
        m.ViewIds.Concat(m.Result.LeftOut.Select(l => l.ViewId)).ToHashSet(StringComparer.Ordinal);

    /// <summary>The views a solve left out and why, as a refusal names them ("" when it used every view).</summary>
    private static string LeftOutWords(MultiViewOutcome m) =>
        m.Result.LeftOut.IsDefaultOrEmpty ? "" : " (" + string.Join("; ", m.Result.LeftOut.Select(l => l.Words)) + " — left out again by the same solve)";

    /// <summary>SCAN DROP: the scan without one view, solved again warm from its last solve (the frame and its scale kept). Refused
    /// for a view the scan does not have, one already dropped, one that did not read from the folder, and the scan's last view.</summary>
    public Task<ScanOutcome> DropAsync(string scanId, string viewId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(scanId);
        ArgumentNullException.ThrowIfNull(viewId);
        return RunWorkAsync(scanId, async (engine, scan, token) =>
        {
            var view = scan.Captured.FirstOrDefault(v => v.Id.Equals(viewId, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"{scan.Name} has no view {viewId} — SCAN VIEWS {scan.Id} lists them");
            if (view.State == "dropped")
                throw new InvalidOperationException($"{view.Id} of {scan.Name} is dropped already");
            if (view.State == "unreadable")
                throw new InvalidOperationException($"{view.Id} of {scan.Name} did not read from the scan's folder: it takes no part already ({view.Words})");
            if (scan.Captured.Count(v => v.Solvable) < 2)
                throw new InvalidOperationException($"{view.Id} is {scan.Name}'s only view: there would be nothing left to solve");
            var project = engine.Store.Project;
            var warm = scan.Multi is { } m && m.ViewIds.Contains(view.Id) ? (m, view.Id) : ((MultiViewOutcome, string)?)null;
            var held = scan.Multi?.Held ?? [];
            var views = warm is null ? [.. scan.Captured.Select(v => v.Id == view.Id ? v with { State = "dropped", Words = "dropped by SCAN DROP" } : v)] : scan.Captured;
            var (multi, placed) = await Task.Run(() => MultiViewScan.Solve(views, project, held, scan.Solution.Metric, warm, token), token).ConfigureAwait(false);
            return Combined(scan, multi, placed, project, scan.Solution.Metric);
        }, ct);
    }

    /// <summary>One solve of a scan off the lane: refused once the runner stops, while that scan takes a view, is being solved or has its
    /// suggestions made, and for a scan this node does not hold; the result is kept, written and recorded, and the scan shown in STATE.</summary>
    private async Task<ScanOutcome> RunWorkAsync(string scanId, Func<EngineCore, ScanOutcome, CancellationToken, Task<ScanOutcome>> work, CancellationToken ct)
    {
        EngineCore engine;
        CancellationTokenSource cts;
        var done = new TaskCompletionSource();
        lock (_gate)
        {
            if (_stopped)
                throw new InvalidOperationException(StoppingWords);
            engine = _engine ?? throw new InvalidOperationException("the scan runner is not bound to an engine");
            if (_running is not null && _runningScanId == scanId)
                throw new InvalidOperationException($"{scanId} is taking a view now — solve it once the view is done");
            // Suggestions name regions of the solve they are made from: one that lands meanwhile would leave them naming a solve that is gone.
            if (_suggesting.Contains(scanId))
                throw new InvalidOperationException($"{engine.Store.Project.Stage.Scans.Find(scanId)?.Name ?? scanId}'s suggestions are being made — solve it once they are queued");
            if (!_solving.Add(scanId))
                throw new InvalidOperationException($"{scanId} is being solved already");
            cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            // Registered with the stop check: a stop that begins after this sees the solve and waits for it.
            _work[done.Task] = cts;
        }
        try
        {
            var record = engine.Store.Project.Stage.Scans.Find(scanId) ?? throw new InvalidOperationException($"no scan {scanId}");
            // A lock that would refuse the scan's record is said before any work (and asked again before the record is written).
            if (engine.WouldRefuse(ScanRecordTouched(scanId)) is { } locked)
                throw new InvalidOperationException(locked);
            var scan = Load(engine, record) ?? throw new InvalidOperationException($"{record.Name}'s data is not on this node");
            if (scan.Captured.IsDefaultOrEmpty)
                throw new InvalidOperationException($"{record.Name} keeps no views (it was made before scans kept them) — scan again");
            if (!scan.Captured.Any(v => v.Solvable))
                throw new InvalidOperationException($"none of {record.Name}'s views can be solved: {MultiViewScan.UnreadWords(scan.Captured) ?? "every one is dropped"} — scan again");
            var carried = MultiViewScan.FrameMoved(scan.Captured, engine.Store.Project);
            var outcome = await work(engine, scan, cts.Token).ConfigureAwait(false);
            var notes = new List<string>();
            if (carried)
                notes.Add(MultiViewScan.CarriedWords + ".");
            if (MultiViewScan.UnreadWords(outcome.Captured) is { } unread)
                notes.Add(unread + ".");
            if (outcome.Simulated)
                notes.Add(TruthWords(outcome));
            await CommitAsync(engine, outcome, notes, cts.Token).ConfigureAwait(false);
            Show(outcome, MultiViewScan.Words(outcome.Multi!));
            return outcome;
        }
        finally
        {
            lock (_gate)
            {
                _solving.Remove(scanId);
                _work.Remove(done.Task);
            }
            done.SetResult();
            cts.Dispose();
        }
    }

    /// <summary>Cancels every solve under way and waits for each to end.</summary>
    private async Task StopWorkAsync()
    {
        KeyValuePair<Task, CancellationTokenSource>[] work;
        lock (_gate)
            work = [.. _work];
        foreach (var (_, cts) in work)
        {
            try
            {
                await cts.CancelAsync().ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                // That solve ended between the read and the cancel.
            }
        }
        await Task.WhenAll(work.Select(w => w.Key)).ConfigureAwait(false);
    }

    /// <summary>The scan in STATE.scan as it now stands (its views, coverage, regions and suggestions), unless a scan is running.</summary>
    internal void Show(ScanOutcome outcome, string message)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        // A scan that runs owns STATE.scan from the moment it is claimed (before its first step is published).
        lock (_gate)
        {
            if (_running is not null)
                return;
        }
        if (_view is { IsRunning: true })
            return;
        var solved = outcome.Solution.Projectors;
        var worst = solved.Where(p => p.Solved).Select(p => p.Quality).DefaultIfEmpty(0).Min();
        var last = outcome.Captured.LastOrDefault(v => v.Solvable) ?? outcome.Captured.LastOrDefault();
        Publish(new ScanView(outcome.Id, "done", "done", 0, 0, 1, Math.Round(worst / 100.0, 3), outcome.Solution.Words, message,
            [.. solved.Select(p => new ScanProjectorState(p.ProjectorId, p.Name, 1, p.Solved ? "done" : "failed"))], [.. outcome.Regions], null)
        {
            ViewId = last?.Id ?? "v1",
            Views = outcome.Captured,
            Coverage = CoverageOf(outcome),
        });
    }

    private static ImmutableArray<CoverageState> CoverageOf(ScanOutcome outcome) =>
        outcome.Multi is { } m ? [.. m.Coverage.Select(c => new CoverageState(c.ProjectorId, Math.Round(c.Seen, 4), Math.Round(c.Twice, 4)))] : [];

    /// <summary>A scan's suggestions withdrawn: their proposals still waiting are rejected with <paramref name="why"/>.</summary>
    internal void WithdrawSuggestions(EngineCore engine, string scanId, string why)
    {
        ImmutableArray<ScanSuggestion> old;
        lock (_gate)
        {
            old = _suggestions.GetValueOrDefault(scanId, []);
            _suggestions.Remove(scanId);
        }
        if (engine.Services.Assistant?.Queue is not { } queue)
            return;
        foreach (var id in old.SelectMany(x => x.Proposals))
        {
            if (queue.Find(id) is { State: ProposalState.Pending })
                queue.Reject(id, "scan suggestions", why);
        }
    }

    /// <summary>Scans whose suggestions are being made now (under the gate): one SCAN SUGGEST of a scan at a time.</summary>
    private readonly HashSet<string> _suggesting = new(StringComparer.Ordinal);

    /// <summary>One SCAN SUGGEST under way: the token it runs on (cancelled when the runner stops, which waits for it to end), released
    /// once its suggestions are made.</summary>
    internal sealed class SuggestWork : IDisposable
    {
        private readonly ScanRunner _runner;
        private readonly string _scanId;
        private readonly CancellationTokenSource _cts;
        private readonly TaskCompletionSource _done = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal SuggestWork(ScanRunner runner, string scanId, CancellationToken ct)
        {
            _runner = runner;
            _scanId = scanId;
            _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        }

        public CancellationToken Token => _cts.Token;

        internal Task Done => _done.Task;

        internal CancellationTokenSource Cancellation => _cts;

        public void Dispose()
        {
            lock (_runner._gate)
            {
                _runner._suggesting.Remove(_scanId);
                _runner._work.Remove(_done.Task);
            }
            _done.TrySetResult();
            _cts.Dispose();
        }
    }

    /// <summary>Claims a scan for one SCAN SUGGEST; null, with <paramref name="why"/>, while the runner is stopping, while the scan takes a
    /// view or is being solved (the suggestions would name regions of a solve about to be replaced) and while another SCAN SUGGEST of it
    /// runs. A solve or a view of the scan is refused while it holds the claim, so every suggestion queued names the scan's solve as it
    /// stands. The stop cancels it and waits for it, so no proposal is queued once the runner has stopped.</summary>
    internal SuggestWork? BeginSuggest(string scanId, string scanName, CancellationToken ct, out string why)
    {
        lock (_gate)
        {
            why = _stopped ? StoppingWords
                : _running is not null && _runningScanId == scanId ? $"{scanName} is taking a view now — SCAN SUGGEST once it is solved"
                : _solving.Contains(scanId) ? $"{scanName} is being solved — SCAN SUGGEST once it is done"
                : _suggesting.Contains(scanId) ? $"{scanName}'s suggestions are being made already"
                : "";
            if (why.Length > 0)
                return null;
            _suggesting.Add(scanId);
            var work = new SuggestWork(this, scanId, ct);
            _work[work.Done] = work.Cancellation;
            return work;
        }
    }

    /// <summary>The suggestions SCAN SUGGEST made for a scan, replacing any it made before.</summary>
    internal void Suggested(string scanId, ImmutableArray<ScanSuggestion> suggestions)
    {
        lock (_gate)
            _suggestions[scanId] = suggestions;
        Volatile.Read(ref _engine)?.FactsChanged();
    }

    /// <summary>The suggestions made for a scan (empty before SCAN SUGGEST).</summary>
    public ImmutableArray<ScanSuggestion> SuggestionsFor(string scanId)
    {
        lock (_gate)
            return _suggestions.GetValueOrDefault(scanId, []);
    }

    /// <summary>The suggestions waiting for a person, every scan's, with the state of their proposals now.</summary>
    public IReadOnlyList<(ScanSuggestion Suggestion, string State)> Waiting()
    {
        List<ScanSuggestion> all;
        lock (_gate)
            all = [.. _suggestions.Values.SelectMany(s => s)];
        return [.. all.Select(s => (s, StateOf(s))).Where(x => x.Item2 == "pending")];
    }

    /// <summary>A suggestion's state from its proposals: "pending" while one waits, "approved" once one ran, "rejected" when every one
    /// was, "expired", "failed"; "not measured" for a name with no region (no proposal).</summary>
    public string StateOf(ScanSuggestion suggestion)
    {
        ArgumentNullException.ThrowIfNull(suggestion);
        if (suggestion.Proposals.IsDefaultOrEmpty)
            return "not measured";
        var queue = Volatile.Read(ref _engine)?.Services.Assistant?.Queue;
        var states = suggestion.Proposals.Select(id => queue?.Find(id)?.State).ToList();
        if (states.Any(s => s is ProposalState.Done))
            return "approved";
        if (states.Any(s => s is ProposalState.Pending or ProposalState.Forwarded or ProposalState.Running))
            return "pending";
        if (states.Any(s => s is ProposalState.Failed))
            return "failed";
        if (states.All(s => s is ProposalState.Rejected))
            return "rejected";
        return "expired";
    }

    /// <summary>STATE.scan's multi-view keys: the view, the views, coverage per projector and the scan's suggestions.</summary>
    private void WriteViews(Utf8JsonWriter w, ScanView v)
    {
        w.WriteString("view", v.ViewId);
        w.WriteStartArray("views");
        foreach (var x in v.Views)
            WriteView(w, x, v.Id);
        w.WriteEndArray();
        w.WriteStartArray("coverage");
        foreach (var c in v.Coverage)
        {
            w.WriteStartObject();
            w.WriteString("scan", v.Id);
            w.WriteString("projector", c.ProjectorId);
            w.WriteNumber("seen", c.Seen);
            w.WriteNumber("twice", c.Twice);
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteStartArray("suggestions");
        foreach (var s in SuggestionsFor(v.Id))
            WriteSuggestion(w, s, StateOf(s), v.Id);
        w.WriteEndArray();
    }

    /// <summary>A view as STATE.scan.views and SCAN VIEWS show it (with its scan, for the lines a row's actions fill).</summary>
    public static void WriteView(Utf8JsonWriter w, ScanViewData x, string scanId)
    {
        ArgumentNullException.ThrowIfNull(w);
        ArgumentNullException.ThrowIfNull(x);
        w.WriteStartObject();
        w.WriteString("id", x.Id);
        w.WriteString("scan", scanId);
        w.WriteString("camera", x.CameraId);
        w.WriteStartArray("projectors");
        foreach (var p in x.ProjectorIds)
            w.WriteStringValue(p);
        w.WriteEndArray();
        w.WriteString("state", x.State);
        w.WriteNumber("rmsPx", Math.Round(x.RmsPx, 3));
        w.WriteNumber("tracks", x.Tracks);
        w.WriteString("pairing", x.Pairing);
        w.WriteString("exposure", x.Exposure);
        w.WriteStartArray("latencies");
        foreach (var l in x.Latencies)
        {
            w.WriteStartObject();
            w.WriteString("projector", l.ProjectorId);
            w.WriteNumber("ms", Math.Round(l.Latency.LatencyMs, 1));
            // A latency no flash could measure is the runner's fallback.
            w.WriteBoolean("measured", l.Latency.Transitions > 0);
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteString("words", x.Words);
        w.WriteEndObject();
    }

    /// <summary>A suggestion as STATE.scan.suggestions and SCAN SUGGEST show it: its scan and its first proposal (the SCAN SURFACE line)
    /// beside the full list, for the lines a row's actions fill.</summary>
    public static void WriteSuggestion(Utf8JsonWriter w, ScanSuggestion s, string state, string scanId)
    {
        ArgumentNullException.ThrowIfNull(w);
        ArgumentNullException.ThrowIfNull(s);
        w.WriteStartObject();
        w.WriteString("id", s.Id);
        w.WriteString("scan", scanId);
        if (s.Proposals.IsDefaultOrEmpty)
            w.WriteNull("proposal");
        else
            w.WriteString("proposal", s.Proposals[0]);
        w.WriteString("region", s.RegionId);
        w.WriteString("label", s.Label);
        w.WriteString("kind", s.Kind);
        w.WriteNumber("confidence", s.Confidence);
        w.WriteStartArray("reasons");
        foreach (var r in s.Reasons)
            w.WriteStringValue(r);
        w.WriteEndArray();
        w.WriteBoolean("measured", s.Measured);
        w.WriteStartArray("projectors");
        foreach (var p in s.Projectors)
            w.WriteStringValue(p);
        w.WriteEndArray();
        w.WriteStartArray("proposals");
        foreach (var p in s.Proposals)
            w.WriteStringValue(p);
        w.WriteEndArray();
        w.WriteString("state", state);
        w.WriteEndObject();
    }
}
