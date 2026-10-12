using System.Globalization;
using PlayMap.Core.Geometry;
using PlayMap.Core.Model;
using PlayMap.Core.Scan;
using PlayMap.Core.Time;
using PlayMap.Engine.Devices;

namespace PlayMap.Engine.Scanning;

/// <summary>How the scan runner paces a real capture.</summary>
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

    /// <summary>Opens a real camera for a rig (null: Media's capture device). Tests hand in their own.</summary>
    public Func<CameraRig, IScanCamera?>? OpenCamera { get; init; }

    /// <summary>The settle detector's thresholds.</summary>
    public SettleOptions Settle { get; init; } = new();
}

/// <summary>
/// The scan runner behind SCAN START (<see cref="IScanBackend"/>). For each projector: its device link (when it has
/// one) readies it — shutter open, its own geometry off — and takes a shutter-closed black frame; then the plan's
/// patterns go up on the projector's output one by one through <see cref="IScanDisplay"/>, and for each the first
/// camera frame that arrived at or after the present plus the camera's measured latency and that the settle
/// detector accepts is kept; the frames are decoded, scored and turned into the smart scan. Then every projector
/// is solved together (<see cref="ScanSolver"/>), the result is kept in memory and in the scan's folder beside the
/// project, and the engine adds the <see cref="ScanRecord"/> on its lane. A simulated camera ("sim", or a rig of
/// kind <see cref="CameraSourceKind.Simulated"/>) renders its frames with the scan simulator instead. One scan at a
/// time; <see cref="Cancel"/> stops it and gives the outputs back. Progress is <see cref="Current"/>, for STATE.
/// </summary>
public sealed class ScanRunner : IScanBackend, IAsyncDisposable
{
    private const int KeptOutcomes = 8;

    private readonly IScanDisplay? _display;
    private readonly ScanRunnerOptions _options;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, ScanOutcome> _outcomes = new(StringComparer.Ordinal);
    private readonly Queue<string> _order = new();
    private EngineCore? _engine;
    private CancellationTokenSource? _running;
    private Task? _task;
    private volatile ScanProgress? _current;

    public ScanRunner(IScanDisplay? display, ScanRunnerOptions? options = null)
    {
        _display = display;
        _options = options ?? new ScanRunnerOptions();
    }

    public ScanProgress? Current => _current;

    /// <summary>Raised (on the scan's thread) whenever <see cref="Current"/> moves.</summary>
    public event Action<ScanProgress>? Progressed;

    /// <summary>Gives the runner the node it scans for: the project it reads, the lane it stores the record on.</summary>
    public void Bind(EngineCore engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        lock (_gate)
            _engine = engine;
    }

    /// <summary>A finished scan this node still holds in memory (the most recent few), or null.</summary>
    public ScanOutcome? Find(string scanId)
    {
        lock (_gate)
            return _outcomes.GetValueOrDefault(scanId);
    }

    public Task<string> StartAsync(string cameraId, IReadOnlyList<string> projectorIds, string mode, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(cameraId);
        ArgumentNullException.ThrowIfNull(projectorIds);
        EngineCore engine;
        CancellationTokenSource cts;
        lock (_gate)
        {
            if (_engine is null)
                return Task.FromException<string>(new InvalidOperationException("the scan runner is not bound to an engine"));
            if (_running is not null)
                return Task.FromException<string>(new InvalidOperationException("a scan is already running — SCAN CANCEL stops it"));
            engine = _engine;
            // The scan outlives the line that started it (an HTTP request ends at once): only SCAN CANCEL stops it.
            cts = new CancellationTokenSource();
            _running = cts;
        }
        var scanMode = mode.ToLowerInvariant() switch
        {
            "fast" => ScanMode.Fast,
            "difficult" => ScanMode.Difficult,
            _ => ScanMode.Robust,
        };
        Publish(new ScanProgress("starting", null, 0, 0, 0, "getting ready"));
        var run = Task.Run(() => RunAsync(engine, cameraId, projectorIds, scanMode, cts.Token), CancellationToken.None);
        lock (_gate)
            _task = run;
        return Finish(run, cts);
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

    private async Task<string> Finish(Task<string> run, CancellationTokenSource cts)
    {
        try
        {
            return await run.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Publish(new ScanProgress("cancelled", null, 0, 0, 0, "the scan was cancelled; the outputs are back on the show"));
            throw;
        }
        catch (Exception ex)
        {
            Publish(new ScanProgress("failed", _current?.ProjectorId, _current?.Pattern ?? 0, _current?.Of ?? 0, 0, ex.Message));
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

    private void Publish(ScanProgress progress)
    {
        _current = progress;
        Progressed?.Invoke(progress);
        EngineCore? engine;
        lock (_gate)
            engine = _engine;
        engine?.FactsChanged();
    }

    /// <summary>What a scan reads from the project when it starts.</summary>
    private sealed record Setup(CameraRig? Rig, ScanCameraView Camera, List<Projector> Projectors, Project Project, string? Folder);

    private async Task<string> RunAsync(EngineCore engine, string cameraId, IReadOnlyList<string> projectorIds, ScanMode mode, CancellationToken ct)
    {
        var setup = ReadSetup(engine, cameraId, projectorIds);
        var simulated = setup.Rig is null || setup.Rig.SourceKind == CameraSourceKind.Simulated;
        var names = string.Join(", ", setup.Projectors.Select(p => p.Name));
        List<ProjectorCapture> captures;
        var views = new List<(string, ProjectorView)>();
        var notes = new List<string>();
        SimulatedRig? rig = null;
        Func<Core.Numerics.Vec2d, Core.Numerics.Vec2d, double?>? known = null;
        if (simulated)
        {
            rig = new SimulatedRig(SimulatedRig.SceneOf(setup.Rig), setup.Camera.Intrinsics.Width, setup.Camera.Intrinsics.Height,
                setup.Projectors.Select(p => p.Intrinsics).ToList());
            await PrepareDevicesAsync(engine, setup, null, notes, ct).ConfigureAwait(false);
            captures = CaptureSimulated(rig, setup, mode, views, out var truth, ct);
            known = rig.KnownDistance(truth);
        }
        else
        {
            captures = await CaptureRealAsync(engine, setup, mode, views, notes, ct).ConfigureAwait(false);
        }

        Publish(new ScanProgress("calibrating", null, 0, 0, captures.Min(c => c.Quality.Score), $"solving {names}"));
        var options = new ScanSolveOptions { KnownDistance = known, Model = StageModel(setup) };
        var solution = await Task.Run(() => ScanSolver.Solve(setup.Camera, captures, options, ct), ct).ConfigureAwait(false);
        if (!solution.Projectors.Any(p => p.Solved))
            throw new InvalidOperationException("no projector could be solved — " + string.Join("; ", solution.Projectors.Select(p => p.Name + ": " + p.Words)));

        var id = await engine.Lane.RunAsync(() => engine.Store.Project.NewId(Ids.Scan)).ConfigureAwait(false);
        var name = "Scan of " + names;
        var truthDevices = rig is null ? (IReadOnlyList<SimulatedDevice>)[]
            : [new SimulatedDevice("camera", rig.Camera.Intrinsics, rig.Camera.Pose), .. setup.Projectors.Select((p, j) => new SimulatedDevice(p.Id, rig.Projector(j).Intrinsics, rig.Projector(j).Pose))];
        var outcome = new ScanOutcome(id, name, setup.Rig?.Id ?? SimulatedRig.CameraId, simulated, rig?.SceneName ?? "", solution, views, truthDevices);
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
        var record = new ScanRecord(id, name, ScanKind.StructuredLight, engine.Services.Wall.UnixMs, setup.Rig?.Id ?? SimulatedRig.CameraId,
            [.. setup.Projectors.Select(p => p.Id)], dataPath, null, outcome.WorstResidualPx, solution.Points,
            string.Join(" ", new[] { solution.Words }.Concat(notes)));
        await engine.Lane.RunAsync(() => engine.Store.Apply($"scan {name}", p => p.WithStage(s => s with { Scans = s.Scans.Upsert(record) }))).ConfigureAwait(false);
        var quality = captures.Min(c => c.Quality.Score);
        Publish(new ScanProgress("done", null, captures.Count, captures.Count, quality,
            string.Create(CultureInfo.InvariantCulture, $"{id} ready — SCAN APPLY {id} puts it on the stage. {solution.Words}")));
        return id;
    }

    private Setup ReadSetup(EngineCore engine, string cameraId, IReadOnlyList<string> projectorIds)
    {
        var p = engine.Store.Project;
        var rig = p.Stage.Cameras.Find(cameraId);
        if (rig is null && !cameraId.Equals(SimulatedRig.CameraId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"no camera {cameraId}");
        var projectors = new List<Projector>();
        foreach (var id in projectorIds)
            projectors.Add(p.FindProjector(id) ?? throw new InvalidOperationException($"no projector {id}"));
        if (projectors.Count == 0)
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
                projectors.Select(x => x.Intrinsics).ToList()).Camera;
            camera = new ScanCameraView(SimulatedRig.CameraId, "Simulated camera", truth.Intrinsics, truth.Lens, truth.Pose, true);
        }
        return new Setup(rig, camera, projectors, p, engine.ProjectFolderForFiles);
    }

    /// <summary>The stage's first mesh that loads from the project folder, for a bridged registration.</summary>
    private static MeshRaycaster? StageModel(Setup setup)
    {
        if (setup.Folder is null || !setup.Camera.Calibrated)
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

    private List<ProjectorCapture> CaptureSimulated(SimulatedRig rig, Setup setup, ScanMode mode, List<(string, ProjectorView)> views, out SimTruth truth, CancellationToken ct)
    {
        var captures = new List<ProjectorCapture>();
        SimTruth? first = null;
        for (var j = 0; j < setup.Projectors.Count; j++)
        {
            var pj = setup.Projectors[j];
            var w = pj.Intrinsics.Width;
            var h = pj.Intrinsics.Height;
            var plan = ScanPlan.Create(w, h, new ScanPlanOptions { Mode = mode });
            var sim = rig.Simulator(j, plan);
            first ??= sim.Truth;
            var frames = new byte[plan.Count][];
            for (var i = 0; i < plan.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                frames[i] = sim.Render(i);
                Publish(new ScanProgress("scanning", pj.Id, i + 1, plan.Count, captures.Count > 0 ? captures[^1].Quality.Score : 0,
                    string.Create(CultureInfo.InvariantCulture, $"{pj.Name}: pattern {i + 1} of {plan.Count} ({plan.Frames[i].Name}), simulated {rig.SceneName}")));
            }
            captures.Add(Decode(pj, w, h, plan, sim.CameraWidth, sim.CameraHeight, frames, sim.RenderWhiteColour(), views));
        }
        truth = first!;
        return captures;
    }

    private ProjectorCapture Decode(Projector pj, int w, int h, ScanPlan plan, int cameraWidth, int cameraHeight, byte[][] frames, int[]? colour,
        List<(string, ProjectorView)> views)
    {
        Publish(new ScanProgress("decoding", pj.Id, plan.Count, plan.Count, 0, $"{pj.Name}: decoding {plan.Count} frames"));
        var map = new StructuredLightDecoder(plan).Decode(cameraWidth, cameraHeight, frames, colour);
        var quality = ScanQuality.Assess(map);
        if (map.Axes == ScanAxes.Both && map.Stats.Valid > 0)
            views.Add((pj.Id, ProjectorView.Build(map)));
        Publish(new ScanProgress("decoding", pj.Id, plan.Count, plan.Count, quality.Score, $"{pj.Name}: {quality.Words}"));
        return new ProjectorCapture(pj, w, h, map, quality);
    }

    /// <summary>Each projector with a device link is readied for the scan (shutter open, its own geometry off, lens
    /// recorded) and gives a shutter-closed black frame; what its protocol cannot do is said in the notes.</summary>
    private async Task PrepareDevicesAsync(EngineCore engine, Setup setup, IScanCamera? camera, List<string> notes, CancellationToken ct)
    {
        if (engine.Services.Devices is not { } devices)
            return;
        foreach (var pj in setup.Projectors)
        {
            var link = setup.Project.Stage.Devices.FirstOrDefault(d => d.ProjectorId == pj.Id);
            if (link is null)
                continue;
            Publish(new ScanProgress("preparing", pj.Id, 0, 0, 0, $"{pj.Name}: readying {link.Name} for the scan"));
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

    private async Task<List<ProjectorCapture>> CaptureRealAsync(EngineCore engine, Setup setup, ScanMode mode, List<(string, ProjectorView)> views,
        List<string> notes, CancellationToken ct)
    {
        var display = _display ?? throw new InvalidOperationException("this node has no outputs to show the patterns on");
        var rig = setup.Rig!;
        var camera = (_options.OpenCamera ?? DefaultCamera)(rig)
            ?? throw new InvalidOperationException($"{rig.Name} is a {rig.SourceKind.ToString().ToLowerInvariant()} camera, which this node cannot scan through yet");
        try
        {
            if (!await camera.OpenAsync(ct).ConfigureAwait(false))
                throw new InvalidOperationException($"{rig.Name} did not open: {camera.Problem}");
            await PrepareDevicesAsync(engine, setup, camera, notes, ct).ConfigureAwait(false);
            var captures = new List<ProjectorCapture>();
            foreach (var pj in setup.Projectors)
            {
                var output = setup.Project.Outputs.FirstOrDefault(o => o.ProjectorId == pj.Id && o.Enabled)
                    ?? throw new InvalidOperationException($"no output drives {pj.Name} — set an output's projector to it first");
                var latency = await MeasureLatencyAsync(display, camera, pj, output, ct).ConfigureAwait(false);
                notes.Add($"{pj.Name}: {latency.Words}.");
                var plan = ScanPlan.Create(output.Width, output.Height, new ScanPlanOptions { Mode = mode });
                var (frames, w, h, unsettled) = await ShowAndCaptureAsync(display, camera, pj, output, plan, latency.LatencyMs, captures, ct).ConfigureAwait(false);
                if (unsettled > 0)
                    notes.Add(string.Create(CultureInfo.InvariantCulture, $"{pj.Name}: {unsettled} of {plan.Count} patterns were taken without the camera settling."));
                captures.Add(Decode(pj, output.Width, output.Height, plan, w, h, frames, null, views));
            }
            return captures;
        }
        finally
        {
            display.Release();
            camera.Dispose();
        }
    }

    private static IScanCamera? DefaultCamera(CameraRig rig) =>
        CaptureCamera.OptionsFor(rig) is { } options ? new CaptureCamera(options) : null;

    /// <summary>The camera's latency: black, then white and black a few times, each change stamped at its present;
    /// every frame's mean brightness collected meanwhile.</summary>
    private async Task<LatencyEstimate> MeasureLatencyAsync(IScanDisplay display, IScanCamera camera, Projector pj, Output output, CancellationToken ct)
    {
        Publish(new ScanProgress("latency", pj.Id, 0, 0, 0, $"{pj.Name}: measuring the camera's latency with white flashes"));
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
    private async Task<(byte[][] Frames, int Width, int Height, int Unsettled)> ShowAndCaptureAsync(IScanDisplay display, IScanCamera camera, Projector pj,
        Output output, ScanPlan plan, double latencyMs, List<ProjectorCapture> done, CancellationToken ct)
    {
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
            Publish(new ScanProgress("scanning", pj.Id, i + 1, plan.Count, done.Count > 0 ? done[^1].Quality.Score : 0,
                string.Create(CultureInfo.InvariantCulture, $"{pj.Name}: pattern {i + 1} of {plan.Count} ({plan.Frames[i].Name})")));
        }
        return (frames, w, h, unsettled);
    }

    private void Keep(ScanOutcome outcome)
    {
        lock (_gate)
        {
            _outcomes[outcome.Id] = outcome;
            _order.Enqueue(outcome.Id);
            while (_order.Count > KeptOutcomes)
                _outcomes.Remove(_order.Dequeue());
        }
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

    public async ValueTask DisposeAsync()
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
        lock (_gate)
            _running?.Dispose();
    }
}
