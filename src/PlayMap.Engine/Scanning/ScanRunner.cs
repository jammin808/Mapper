using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using PlayMap.Core.Geometry;
using PlayMap.Core.Model;
using PlayMap.Core.Scan;
using PlayMap.Core.Vision;
using PlayMap.Engine.Devices;
using PlayMap.Vision.Segmentation;

namespace PlayMap.Engine.Scanning;

/// <summary>How the scan runner paces a real capture and opens its cameras.</summary>
public sealed record ScanRunnerOptions
{
    /// <summary>White flashes (each followed by black) that measure the camera's latency.</summary>
    public int LatencyFlashes { get; init; } = 3;

    /// <summary>How long each flash (and each black between) stays on the glass.</summary>
    public TimeSpan FlashHold { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>The latency taken when the flashes could not be measured (a dark room, a tiny projection).</summary>
    public double FallbackLatencyMs { get; init; } = 150;

    /// <summary>The longest wait for one camera frame before the scan fails.</summary>
    public TimeSpan FrameTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>After this many late-enough frames without the settle detector accepting one, the last is taken (a
    /// pattern the camera cannot tell from the previous one never "changes").</summary>
    public int MaxUnsettledFrames { get; init; } = 12;

    /// <summary>The bare "sim" camera's raster.</summary>
    public int SimulatedCameraWidth { get; init; } = 320;

    public int SimulatedCameraHeight { get; init; } = 240;

    /// <summary>Opens a real camera for a rig, for a scan and for its camera views (null: Media's capture device).
    /// Tests hand in their own.</summary>
    public Func<CameraRig, IScanCamera?>? OpenCamera { get; init; }

    /// <summary>The settle detector's thresholds.</summary>
    public SettleOptions Settle { get; init; } = new();

    /// <summary>The segmenter click-to-surface uses (null: Vision's router with the classical segmenter, which needs
    /// no model).</summary>
    public ISegmenter? Segmenter { get; init; }
}

/// <summary>One projector's part of a scan as STATE.scan shows it.</summary>
public sealed record ScanProjectorState(string Id, string Name, double Progress, string State);

/// <summary>STATE.scan: the running (or last) scan — its id from the start, "starting", "running", "done", "failed" or
/// "cancelled", the step it is on, patterns shown of all, progress and quality (0–1), the quality in words, a
/// message, each projector's progress, and the regions it suggests as surfaces (on the smart-scan picture, 0–1).</summary>
public sealed record ScanView(
    string Id,
    string State,
    string Stage,
    int Pattern,
    int Patterns,
    double Progress,
    double Quality,
    string QualityWords,
    string Message,
    ImmutableArray<ScanProjectorState> Projectors,
    ImmutableArray<ScanRegion> Regions,
    string? ProjectorId)
{
    public bool IsRunning => State is "starting" or "running";
}

/// <summary>
/// The scan runner behind SCAN START (<see cref="IScanBackend"/>). For each projector: its device link (when it has
/// one) readies it — shutter open, its own geometry off — and takes a shutter-closed black frame; then the plan's
/// patterns go up on the projector's output one by one through <see cref="IScanDisplay"/>, and for each the first
/// camera frame that arrived at or after the present plus the camera's measured latency and that the settle
/// detector accepts is kept; the frames are decoded, scored and turned into the smart scan. Then every projector
/// is solved together (<see cref="ScanSolver"/>), the surfaces it suggests are found, the result is kept in memory
/// and in the scan's folder beside the project, and the engine adds the <see cref="ScanRecord"/> on its lane. A
/// simulated camera ("sim", or a rig of kind <see cref="CameraSourceKind.Simulated"/>) renders its frames with the
/// scan simulator instead. One scan at a time; <see cref="Cancel"/> stops it and gives the outputs back. Progress is
/// <see cref="View"/> (STATE.scan) and <see cref="Current"/> (the seam's summary).
/// </summary>
public sealed class ScanRunner : IScanBackend, IAsyncDisposable
{
    private const int KeptOutcomes = 8;

    /// <summary>The share of the progress bar the captures take; solving and meshing take the rest.</summary>
    private const double CaptureShare = 0.85;

    private readonly IScanDisplay? _display;
    private readonly ScanRunnerOptions _options;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, ScanOutcome> _outcomes = new(StringComparer.Ordinal);
    private readonly Queue<string> _order = new();
    private readonly ISegmenter _segmenter;
    private EngineCore? _engine;
    private ScanViews? _views;
    private CancellationTokenSource? _running;
    private Task? _task;
    private volatile ScanView? _view;
    private volatile ScanProgress? _current;

    public ScanRunner(IScanDisplay? display, ScanRunnerOptions? options = null)
    {
        _display = display;
        _options = options ?? new ScanRunnerOptions();
        _segmenter = _options.Segmenter ?? new SegmenterRouter([], new ClassicalSegmenter(), null);
    }

    public ScanProgress? Current => _current;

    /// <summary>STATE.scan's row: the running or last scan (null before the first).</summary>
    public ScanView? View => _view;

    /// <summary>The id of the scan running now (null when none is).</summary>
    public string? RunningId => _view is { IsRunning: true } v ? v.Id : null;

    /// <summary>The segmenter click-to-surface uses.</summary>
    public ISegmenter Segmenter => _segmenter;

    /// <summary>Raised (on the scan's thread) whenever the scan moves on.</summary>
    public event Action<ScanView>? Progressed;

    /// <summary>Gives the runner the node it scans for: the project it reads, the lane it stores the record on, and
    /// the node's preview views, where it feeds the camera and scan views (<see cref="ScanViews"/>). Once.</summary>
    public void Bind(EngineCore engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ScanViews views;
        lock (_gate)
        {
            if (_engine is not null)
                throw new InvalidOperationException("the scan runner is bound to an engine already");
            _engine = engine;
            views = new ScanViews(this, engine, _options.OpenCamera ?? DefaultCamera, _options.OpenCamera is not null,
                _options.SimulatedCameraWidth, _options.SimulatedCameraHeight);
            _views = views;
        }
        engine.Previews.Register("camera", views.Cameras);
        engine.Previews.Register("scan", views.Scans);
    }

    /// <summary>A finished scan this node still holds in memory (the most recent few), or null.</summary>
    public ScanOutcome? Find(string scanId)
    {
        lock (_gate)
            return _outcomes.GetValueOrDefault(scanId);
    }

    /// <summary>Keeps a scan read back from its folder, so the next click on it does not read it again.</summary>
    public void Remember(ScanOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        Keep(outcome);
    }

    public Task<string> StartAsync(string cameraId, IReadOnlyList<string> projectorIds, string mode, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(cameraId);
        ArgumentNullException.ThrowIfNull(projectorIds);
        EngineCore engine;
        CancellationTokenSource cts;
        Setup setup;
        lock (_gate)
        {
            if (_engine is null)
                return Task.FromException<string>(new InvalidOperationException("the scan runner is not bound to an engine"));
            if (_running is not null)
                return Task.FromException<string>(new InvalidOperationException("a scan is already running — SCAN CANCEL stops it"));
            engine = _engine;
            try
            {
                setup = ReadSetup(engine, cameraId, projectorIds, mode);
            }
            catch (InvalidOperationException ex)
            {
                return Task.FromException<string>(ex);
            }
            // The scan outlives the line that started it (an HTTP request ends at once): only SCAN CANCEL stops it.
            cts = new CancellationTokenSource();
            _running = cts;
        }
        var track = new Track(this, setup);
        track.Step("starting", "starting", "getting ready");
        var run = Task.Run(() => RunAsync(engine, setup, track, cts.Token), CancellationToken.None);
        lock (_gate)
            _task = run;
        return Finish(run, track, cts);
    }

    public bool Cancel()
    {
        lock (_gate)
        {
            if (_running is null || _running.IsCancellationRequested)
                return false;
            _running.Cancel();
            return true;
        }
    }

    private async Task<string> Finish(Task<string> run, Track track, CancellationTokenSource cts)
    {
        try
        {
            return await run.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            track.End("cancelled", "Cancelled — the outputs are back on the show");
            throw;
        }
        catch (Exception ex)
        {
            track.End("failed", ex.Message);
            throw;
        }
        finally
        {
            lock (_gate)
            {
                _running = null;
                _task = null;
            }
            cts.Dispose();
        }
    }

    private void Publish(ScanView view)
    {
        _view = view;
        _current = new ScanProgress(view.State, view.ProjectorId, view.Pattern, view.Patterns, Math.Round(view.Quality * 100, 1),
            view.Message.Length > 0 ? view.Message : view.Stage);
        Progressed?.Invoke(view);
        EngineCore? engine;
        lock (_gate)
            engine = _engine;
        engine?.FactsChanged();
    }

    /// <summary>One projector of a scan: the model's projector, the output it is scanned through (none when
    /// simulated) and the plan at that raster.</summary>
    private sealed record Part(Projector Projector, Output? Output, ScanPlan Plan)
    {
        public int Width => Plan.ProjectorWidth;

        public int Height => Plan.ProjectorHeight;
    }

    /// <summary>What a scan reads from the project when it starts.</summary>
    private sealed record Setup(string Id, CameraRig? Rig, ScanCameraView Camera, List<Part> Parts, Project Project, string? Folder, bool Simulated)
    {
        public string Names => string.Join(", ", Parts.Select(p => p.Projector.Name));

        /// <summary>The camera's id as its views name it ("sim" for the bare simulator).</summary>
        public string CameraId => Rig?.Id ?? SimulatedRig.CameraId;
    }

    /// <summary>The scan's progress as it moves: each step published as a new <see cref="ScanView"/>.</summary>
    private sealed class Track(ScanRunner runner, Setup setup)
    {
        private readonly Lock _gate = new();
        private readonly double[] _progress = new double[setup.Parts.Count];
        private readonly string[] _states = Enumerable.Repeat("waiting", setup.Parts.Count).ToArray();
        private readonly int _patterns = setup.Parts.Sum(p => p.Plan.Count);
        private int _shown;
        private double _overall;
        private double _quality;
        private string _qualityWords = "";
        private string _stage = "starting";
        private string _message = "";
        private string? _projector;
        private ImmutableArray<ScanRegion> _regions = [];

        public void Step(string state, string stage, string message, int? part = null, string? partState = null, double? overall = null)
        {
            lock (_gate)
            {
                _stage = stage;
                _message = message;
                if (part is { } j)
                {
                    _projector = setup.Parts[j].Projector.Id;
                    if (partState is not null)
                        _states[j] = partState;
                }
                if (overall is { } o)
                    _overall = Math.Max(_overall, o);
            }
            Publish(state);
        }

        public void Pattern(int part, int index, string message)
        {
            lock (_gate)
            {
                _shown++;
                _projector = setup.Parts[part].Projector.Id;
                _states[part] = "scanning";
                _progress[part] = (index + 1) / (double)setup.Parts[part].Plan.Count;
                _overall = CaptureShare * _shown / Math.Max(1, _patterns);
                _stage = "scanning";
                _message = message;
            }
            Publish("running");
        }

        /// <summary>A projector decoded: its part is done and the quality is the worst decode so far.</summary>
        public void Decoded(int part, ScanQualityReport quality)
        {
            lock (_gate)
            {
                _progress[part] = 1;
                _states[part] = "done";
                var q = quality.Score / 100.0;
                if (_qualityWords.Length == 0 || q < _quality)
                {
                    _quality = q;
                    _qualityWords = quality.Words;
                }
            }
            Publish("running");
        }

        public void Quality(double quality, string words)
        {
            lock (_gate)
            {
                _quality = quality;
                _qualityWords = words;
            }
        }

        public void Regions(IReadOnlyList<ScanRegion> regions)
        {
            lock (_gate)
                _regions = [.. regions];
        }

        public void End(string state, string message)
        {
            lock (_gate)
            {
                _stage = state;
                _message = message;
                if (state == "done")
                {
                    _overall = 1;
                    for (var j = 0; j < _states.Length; j++)
                        _states[j] = "done";
                }
                else
                {
                    for (var j = 0; j < _states.Length; j++)
                    {
                        if (_states[j] != "done")
                            _states[j] = state;
                    }
                }
                _projector = null;
            }
            Publish(state);
        }

        private void Publish(string state)
        {
            ScanView view;
            lock (_gate)
            {
                view = new ScanView(setup.Id, state, _stage, _shown, _patterns, Math.Round(_overall, 3), Math.Round(_quality, 3), _qualityWords, _message,
                    [.. setup.Parts.Select((p, j) => new ScanProjectorState(p.Projector.Id, p.Projector.Name, Math.Round(_progress[j], 3), _states[j]))],
                    _regions, _projector);
            }
            runner.Publish(view);
        }
    }

    private async Task<string> RunAsync(EngineCore engine, Setup setup, Track track, CancellationToken ct)
    {
        var views = new List<(string, ProjectorView)>();
        var notes = new List<string>();
        List<ProjectorCapture> captures;
        SimulatedRig? rig = null;
        Func<Core.Numerics.Vec2d, Core.Numerics.Vec2d, double?>? known = null;
        if (setup.Simulated)
        {
            rig = new SimulatedRig(SimulatedRig.SceneOf(setup.Rig), setup.Camera.Intrinsics.Width, setup.Camera.Intrinsics.Height,
                setup.Parts.Select(p => p.Projector.Intrinsics).ToList());
            await PrepareDevicesAsync(engine, setup, track, null, notes, ct).ConfigureAwait(false);
            SimTruth truth;
            await HoldCameraAsync(setup).ConfigureAwait(false);
            try
            {
                captures = CaptureSimulated(rig, setup, track, views, out truth, ct);
            }
            finally
            {
                ReleaseCamera(setup);
            }
            known = rig.KnownDistance(truth);
        }
        else
        {
            captures = await CaptureRealAsync(engine, setup, track, views, notes, ct).ConfigureAwait(false);
        }

        track.Step("running", "calibrating", $"solving {setup.Names}", overall: 0.88);
        var options = new ScanSolveOptions { KnownDistance = known, Model = StageModel(setup) };
        var solution = await Task.Run(() => ScanSolver.Solve(setup.Camera, captures, options, ct), ct).ConfigureAwait(false);
        if (!solution.Projectors.Any(p => p.Solved))
            throw new InvalidOperationException("no projector could be solved — " + string.Join("; ", solution.Projectors.Select(p => p.Name + ": " + p.Words)));

        track.Step("running", "finding surfaces", "looking for the surfaces worth making", overall: 0.95);
        var name = "Scan of " + setup.Names;
        var truthDevices = rig is null ? (IReadOnlyList<SimulatedDevice>)[]
            : [new SimulatedDevice("camera", rig.Camera.Intrinsics, rig.Camera.Pose), .. setup.Parts.Select((p, j) => new SimulatedDevice(p.Projector.Id, rig.Projector(j).Intrinsics, rig.Projector(j).Pose))];
        var outcome = new ScanOutcome(setup.Id, name, setup.Rig?.Id ?? SimulatedRig.CameraId, setup.Simulated, rig?.SceneName ?? "", solution, views, truthDevices);
        var geometry = ScanArchive.GeometryOf(outcome, ScanSurfaces.CameraImage(captures[0].Map));
        var regions = geometry is null ? [] : await Task.Run(() => ScanSurfaces.Regions(geometry), ct).ConfigureAwait(false);
        outcome = outcome with { Geometry = geometry, Regions = regions };
        track.Regions(regions);
        if (rig is not null)
            notes.Add(TruthWords(outcome));
        var dataPath = "";
        if (setup.Folder is { } folder)
        {
            try
            {
                dataPath = ScanArchive.Save(folder, outcome);
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
        var record = new ScanRecord(setup.Id, name, ScanKind.StructuredLight, engine.Services.Wall.UnixMs, setup.Rig?.Id ?? SimulatedRig.CameraId,
            [.. setup.Parts.Select(p => p.Projector.Id)], dataPath, null, outcome.WorstResidualPx, solution.Points,
            string.Join(" ", new[] { solution.Words }.Concat(notes)));
        await engine.Lane.RunAsync(() => engine.Store.Apply($"scan {name}", p => p.WithStage(s => s with { Scans = s.Scans.Upsert(record) }))).ConfigureAwait(false);
        var worst = captures.MinBy(c => c.Quality.Score)!.Quality;
        track.Quality(worst.Score / 100.0, string.Create(CultureInfo.InvariantCulture,
            $"{worst.Grade} — {worst.DecodedFraction * 100:0} % of the lit picture decoded, {regions.Count} region{(regions.Count == 1 ? "" : "s")} found"));
        track.End("done", string.Create(CultureInfo.InvariantCulture, $"{setup.Id} ready — SCAN APPLY {setup.Id} puts it on the stage. {solution.Words}"));
        return setup.Id;
    }

    private Setup ReadSetup(EngineCore engine, string cameraId, IReadOnlyList<string> projectorIds, string mode)
    {
        var p = engine.Store.Project;
        var rig = p.Stage.Cameras.Find(cameraId);
        if (rig is null && !cameraId.Equals(SimulatedRig.CameraId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"no camera {cameraId}");
        var simulated = rig is null || rig.SourceKind == CameraSourceKind.Simulated;
        var planOptions = new ScanPlanOptions
        {
            Mode = mode.ToLowerInvariant() switch
            {
                "fast" => ScanMode.Fast,
                "difficult" => ScanMode.Difficult,
                _ => ScanMode.Robust,
            },
        };
        var parts = new List<Part>();
        foreach (var id in projectorIds)
        {
            var pj = p.FindProjector(id) ?? throw new InvalidOperationException($"no projector {id}");
            if (simulated)
            {
                parts.Add(new Part(pj, null, ScanPlan.Create(pj.Intrinsics.Width, pj.Intrinsics.Height, planOptions)));
                continue;
            }
            var output = p.Outputs.FirstOrDefault(o => o.ProjectorId == pj.Id && o.Enabled)
                ?? throw new InvalidOperationException($"no output drives {pj.Name} — set an output's projector to it first");
            parts.Add(new Part(pj, output, ScanPlan.Create(output.Width, output.Height, planOptions)));
        }
        if (parts.Count == 0)
            throw new InvalidOperationException("no projector to scan");
        ScanCameraView camera;
        if (rig is not null)
        {
            camera = new ScanCameraView(rig.Id, rig.Name, rig.Intrinsics, rig.Lens, rig.Pose, rig.Calibrated);
        }
        else
        {
            // The bare simulator: a calibrated webcam (its optics are the scene camera's) whose place in the world the
            // demo knows. The projectors are what the scan finds.
            var truth = new SimulatedRig(SimulatedRig.DefaultScene, _options.SimulatedCameraWidth, _options.SimulatedCameraHeight,
                parts.Select(x => x.Projector.Intrinsics).ToList()).Camera;
            camera = new ScanCameraView(SimulatedRig.CameraId, "Simulated camera", truth.Intrinsics, truth.Lens, truth.Pose, true);
        }
        return new Setup(p.NewId(Ids.Scan), rig, camera, parts, p, engine.ProjectFolderForFiles, simulated);
    }

    /// <summary>The stage's first imported mesh that loads from the project folder, for a bridged registration.</summary>
    private static MeshRaycaster? StageModel(Setup setup)
    {
        if (setup.Folder is null || !setup.Camera.Calibrated || setup.Simulated)
            return null;
        foreach (var m in setup.Project.Stage.Meshes.Where(m => m.Path.Length > 0 && m.Origin == MeshOrigin.Imported))
        {
            try
            {
                if (MeshIO.Load(Path.Combine(setup.Folder, m.Path)).Mesh is { TriangleCount: > 0 } mesh)
                    return new MeshRaycaster(mesh);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or NotSupportedException or InvalidDataException)
            {
                // A model that does not load is not bridged into; the scan self-calibrates instead.
                continue;
            }
        }
        return null;
    }

    private List<ProjectorCapture> CaptureSimulated(SimulatedRig rig, Setup setup, Track track, List<(string, ProjectorView)> views, out SimTruth truth,
        CancellationToken ct)
    {
        var captures = new List<ProjectorCapture>();
        SimTruth? first = null;
        for (var j = 0; j < setup.Parts.Count; j++)
        {
            var part = setup.Parts[j];
            var sim = rig.Simulator(j, part.Plan);
            first ??= sim.Truth;
            var frames = new byte[part.Plan.Count][];
            for (var i = 0; i < part.Plan.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                frames[i] = sim.Render(i);
                ViewsOf()?.CameraFrame(setup.CameraId, frames[i], sim.CameraWidth, sim.CameraHeight, Core.Time.MonoClock.Instance.NowNs);
                track.Pattern(j, i, string.Create(CultureInfo.InvariantCulture,
                    $"{part.Projector.Name}: pattern {i + 1} of {part.Plan.Count} ({part.Plan.Frames[i].Name}), simulated {rig.SceneName}"));
            }
            captures.Add(Decode(j, part, sim.CameraWidth, sim.CameraHeight, frames, sim.RenderWhiteColour(), views, track));
        }
        truth = first!;
        return captures;
    }

    private static ProjectorCapture Decode(int j, Part part, int cameraWidth, int cameraHeight, byte[][] frames, int[]? colour,
        List<(string, ProjectorView)> views, Track track)
    {
        track.Step("running", "decoding", $"{part.Projector.Name}: decoding {part.Plan.Count} frames", j, "decoding");
        var map = new StructuredLightDecoder(part.Plan).Decode(cameraWidth, cameraHeight, frames, colour);
        var quality = ScanQuality.Assess(map);
        if (map.Axes == ScanAxes.Both && map.Stats.Valid > 0)
            views.Add((part.Projector.Id, ProjectorView.Build(map)));
        track.Decoded(j, quality);
        return new ProjectorCapture(part.Projector, part.Width, part.Height, map, quality);
    }

    private ScanViews? ViewsOf()
    {
        lock (_gate)
            return _views;
    }

    /// <summary>The scan takes its camera from the camera's views (they show the scan's frames meanwhile).</summary>
    private Task HoldCameraAsync(Setup setup) => ViewsOf()?.HoldAsync(setup.CameraId) ?? Task.CompletedTask;

    /// <summary>The scan has closed its camera: the views show the camera again.</summary>
    private void ReleaseCamera(Setup setup) => ViewsOf()?.Release(setup.CameraId);

    /// <summary>Each projector with a device link is readied for the scan (shutter open, its own geometry off, lens
    /// recorded) and gives a shutter-closed black frame; what its protocol cannot do is said in the notes.</summary>
    private async Task PrepareDevicesAsync(EngineCore engine, Setup setup, Track track, IScanCamera? camera, List<string> notes, CancellationToken ct)
    {
        if (engine.Services.Devices is not { } devices)
            return;
        for (var j = 0; j < setup.Parts.Count; j++)
        {
            var pj = setup.Parts[j].Projector;
            var link = setup.Project.Stage.Devices.FirstOrDefault(d => d.ProjectorId == pj.Id);
            if (link is null)
                continue;
            track.Step("running", "preparing", $"{pj.Name}: readying {link.Name} for the scan", j, "preparing");
            var prepared = await devices.PrepareForScanAsync(link.Id, ct).ConfigureAwait(false);
            notes.Add($"{link.Name}: {DeviceService.Summary(prepared)}.");
            var level = double.NaN;
            var black = await devices.BlackFrameAsync(link.Id, async c =>
            {
                if (camera is not null && await camera.NextAsync(0, _options.FrameTimeout, c).ConfigureAwait(false) is { } f)
                    level = LatencyEstimator.MeanBrightness(f.Gray);
            }, ct).ConfigureAwait(false);
            notes.Add(double.IsNaN(level)
                ? $"{link.Name} black frame: {DeviceService.Summary(black)}."
                : string.Create(CultureInfo.InvariantCulture, $"{link.Name} black frame: the room reads {level:0.0} with the shutter closed."));
        }
    }

    private async Task<List<ProjectorCapture>> CaptureRealAsync(EngineCore engine, Setup setup, Track track, List<(string, ProjectorView)> views,
        List<string> notes, CancellationToken ct)
    {
        var display = _display ?? throw new InvalidOperationException("this node has no outputs to show the patterns on");
        var rig = setup.Rig!;
        await HoldCameraAsync(setup).ConfigureAwait(false);
        IScanCamera? camera = null;
        try
        {
            camera = (_options.OpenCamera ?? DefaultCamera)(rig)
                ?? throw new InvalidOperationException($"{rig.Name} is a {rig.SourceKind.ToString().ToLowerInvariant()} camera, which this node cannot scan through yet");
            if (!await camera.OpenAsync(ct).ConfigureAwait(false))
                throw new InvalidOperationException($"{rig.Name} did not open: {camera.Problem}");
            await PrepareDevicesAsync(engine, setup, track, camera, notes, ct).ConfigureAwait(false);
            var captures = new List<ProjectorCapture>();
            for (var j = 0; j < setup.Parts.Count; j++)
            {
                var part = setup.Parts[j];
                track.Step("running", "latency", $"{part.Projector.Name}: measuring the camera's latency with white flashes", j, "latency");
                var latency = await MeasureLatencyAsync(display, camera, setup.CameraId, part.Output!, ct).ConfigureAwait(false);
                notes.Add($"{part.Projector.Name}: {latency.Words}.");
                var (frames, w, h, unsettled) = await ShowAndCaptureAsync(display, camera, setup.CameraId, j, part, latency.LatencyMs, track, ct).ConfigureAwait(false);
                if (unsettled > 0)
                    notes.Add(string.Create(CultureInfo.InvariantCulture, $"{part.Projector.Name}: {unsettled} of {part.Plan.Count} patterns were taken without the camera settling."));
                captures.Add(Decode(j, part, w, h, frames, null, views, track));
            }
            return captures;
        }
        finally
        {
            display.Release();
            camera?.Dispose();
            ReleaseCamera(setup);
        }
    }

    private static IScanCamera? DefaultCamera(CameraRig rig) =>
        CaptureCamera.OptionsFor(rig) is { } options ? new CaptureCamera(options) : null;

    /// <summary>The camera's latency: black, then white and black a few times, each change stamped at its present;
    /// every frame's mean brightness collected meanwhile.</summary>
    private async Task<LatencyEstimate> MeasureLatencyAsync(IScanDisplay display, IScanCamera camera, string cameraId, Output output, CancellationToken ct)
    {
        var views = ViewsOf();
        var estimator = new LatencyEstimator();
        var gate = new Lock();
        var white = new byte[output.Width * output.Height];
        Array.Fill(white, (byte)255);
        var black = new byte[output.Width * output.Height];
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var collect = Task.Run(async () =>
        {
            var generation = 0L;
            while (!stop.IsCancellationRequested)
            {
                CameraFrame? f;
                try
                {
                    f = await camera.NextAsync(generation, TimeSpan.FromMilliseconds(200), stop.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                if (f is null)
                    continue;
                generation = f.Generation;
                views?.CameraFrame(cameraId, f.Gray, f.Width, f.Height, f.ArrivalNs);
                var mean = LatencyEstimator.MeanBrightness(f.Gray);
                lock (gate)
                    estimator.AddFrame(f.ArrivalNs / 1e6, mean);
            }
        }, CancellationToken.None);
        try
        {
            await display.ShowAsync(output.Id, black, output.Width, output.Height, ct).ConfigureAwait(false);
            await Task.Delay(_options.FlashHold, ct).ConfigureAwait(false);
            for (var k = 0; k < 2 * Math.Max(1, _options.LatencyFlashes); k++)
            {
                var at = await display.ShowAsync(output.Id, k % 2 == 0 ? white : black, output.Width, output.Height, ct).ConfigureAwait(false);
                lock (gate)
                    estimator.AddChange(at / 1e6);
                await Task.Delay(_options.FlashHold, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            await stop.CancelAsync().ConfigureAwait(false);
            await collect.ConfigureAwait(false);
        }
        LatencyEstimate? estimate;
        lock (gate)
            estimate = estimator.Estimate();
        return estimate ?? new LatencyEstimate(_options.FallbackLatencyMs, double.NaN, 0, double.NaN);
    }

    /// <summary>Every pattern on the glass in turn; for each, the first frame that arrived at or after the present
    /// plus the latency and that the settle detector accepts (or, after enough late frames, the last one).</summary>
    private async Task<(byte[][] Frames, int Width, int Height, int Unsettled)> ShowAndCaptureAsync(IScanDisplay display, IScanCamera camera,
        string cameraId, int j, Part part, double latencyMs, Track track, CancellationToken ct)
    {
        var views = ViewsOf();
        var plan = part.Plan;
        var output = part.Output!;
        var frames = new byte[plan.Count][];
        var pattern = new byte[output.Width * output.Height];
        SettleDetector? settle = null;
        int w = 0, h = 0, unsettled = 0;
        var generation = 0L;
        var latencyNs = (long)(latencyMs * 1e6);
        for (var i = 0; i < plan.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            plan.Render(i, pattern);
            var present = await display.ShowAsync(output.Id, pattern, output.Width, output.Height, ct).ConfigureAwait(false);
            var earliest = present + latencyNs;
            CameraFrame? last = null;
            var offered = 0;
            while (true)
            {
                var f = await camera.NextAsync(generation, _options.FrameTimeout, ct).ConfigureAwait(false)
                    ?? throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture,
                        $"the camera sent no picture for {_options.FrameTimeout.TotalSeconds:0.#} s ({camera.Problem})"));
                generation = f.Generation;
                views?.CameraFrame(cameraId, f.Gray, f.Width, f.Height, f.ArrivalNs);
                if (f.ArrivalNs < earliest)
                    continue;
                if (settle is null)
                {
                    (w, h) = (f.Width, f.Height);
                    settle = new SettleDetector(w, h, _options.Settle);
                }
                if (f.Width != w || f.Height != h)
                    throw new InvalidOperationException("the camera changed its picture size during the scan");
                last = f;
                offered++;
                if (settle.Offer(f.Gray) == SettleState.Settled)
                    break;
                if (offered >= _options.MaxUnsettledFrames)
                {
                    unsettled++;
                    break;
                }
            }
            frames[i] = last.Gray;
            track.Pattern(j, i, string.Create(CultureInfo.InvariantCulture, $"{part.Projector.Name}: pattern {i + 1} of {plan.Count} ({plan.Frames[i].Name})"));
        }
        return (frames, w, h, unsettled);
    }

    private void Keep(ScanOutcome outcome)
    {
        lock (_gate)
        {
            if (!_outcomes.ContainsKey(outcome.Id))
                _order.Enqueue(outcome.Id);
            _outcomes[outcome.Id] = outcome;
            while (_order.Count > KeptOutcomes)
                _outcomes.Remove(_order.Dequeue());
        }
    }

    /// <summary>STATE.scan: this runner's view (null before the first scan).</summary>
    public void WriteState(Utf8JsonWriter w)
    {
        ArgumentNullException.ThrowIfNull(w);
        if (_view is not { } v)
        {
            w.WriteNullValue();
            return;
        }
        w.WriteStartObject();
        w.WriteString("id", v.Id);
        w.WriteString("state", v.State);
        w.WriteString("stage", v.Stage);
        w.WriteNumber("pattern", v.Pattern);
        w.WriteNumber("patterns", v.Patterns);
        w.WriteNumber("progress", v.Progress);
        w.WriteNumber("quality", v.Quality);
        w.WriteString("qualityWords", v.QualityWords);
        w.WriteString("message", v.Message);
        w.WriteStartArray("projectors");
        foreach (var p in v.Projectors)
        {
            w.WriteStartObject();
            w.WriteString("id", p.Id);
            w.WriteString("name", p.Name);
            w.WriteNumber("progress", p.Progress);
            w.WriteString("state", p.State);
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteStartArray("regions");
        foreach (var r in v.Regions)
        {
            w.WriteStartObject();
            w.WriteString("id", r.Id);
            w.WriteString("label", r.Label);
            w.WriteStartArray("rect");
            w.WriteNumberValue(r.X);
            w.WriteNumberValue(r.Y);
            w.WriteNumberValue(r.W);
            w.WriteNumberValue(r.H);
            w.WriteEndArray();
            w.WriteNumber("confidence", r.Confidence);
            w.WriteEndObject();
        }
        w.WriteEndArray();
        if (v.ProjectorId is null)
            w.WriteNull("projector");
        else
            w.WriteString("projector", v.ProjectorId);
        w.WriteEndObject();
    }

    /// <summary>How close a simulated scan came to the simulator's truth, in words.</summary>
    public static string TruthWords(ScanOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        var parts = new List<string>();
        foreach (var p in outcome.Solution.Projectors.Where(p => p.Solved))
        {
            if (outcome.Truth.FirstOrDefault(t => t.Id == p.ProjectorId) is not { } t)
                continue;
            var (position, degrees) = PoseError(p.Pose, t.Pose);
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"{p.Name} {position * 1000:0.#} mm, {degrees:0.###}°"));
        }
        return parts.Count == 0 ? "" : "Against the simulator's truth: " + string.Join("; ", parts) + ".";
    }

    /// <summary>The distance between two poses' centres and the angle between their orientations (degrees).</summary>
    public static (double Position, double Degrees) PoseError(Pose a, Pose b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        var r = a.CameraToWorld.Transposed() * b.CameraToWorld;
        var angle = Math.Acos(Math.Clamp((r.Trace - 1) / 2, -1, 1)) * 180 / Math.PI;
        return (Core.Numerics.Vec3d.Distance(a.Position, b.Position), angle);
    }

    /// <summary>Stops the scan running now, if any, and waits until it has ended (its outputs are back on the show and
    /// its words published), then takes its feeds off the node's views and lets every camera go. The node calls this
    /// before the parts a scan uses go (the projectors' links, the engine).</summary>
    public async Task StopAsync()
    {
        Task? task;
        CancellationTokenSource? running;
        lock (_gate)
        {
            running = _running;
            task = _task;
        }
        if (running is not null)
        {
            try
            {
                await running.CancelAsync().ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                // The scan finished between the read and the cancel: nothing is left to stop.
            }
        }
        if (task is not null)
        {
            try
            {
                await task.ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // A scan stopped by the node's end ends here, whatever stopped it; Finish published its words.
            }
        }
        if (ViewsOf() is { } views)
            await views.StopAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        lock (_gate)
        {
            _running?.Dispose();
        }
    }
}
