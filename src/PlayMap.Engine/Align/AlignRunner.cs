using System.Collections.Immutable;
using System.Globalization;
using PlayMap.Core.Align;
using PlayMap.Core.Calibration.Scoring;
using PlayMap.Core.Model;
using PlayMap.Core.Scan;
using PlayMap.Engine.Scanning;

namespace PlayMap.Engine.Align;

/// <summary>One output of a run: its measured correction (what ACCEPT applies) and what was measured — its γ, the share of its footprint
/// decoded, its warp's residual on the pairs held out, and its residual to the flat fit (output px).</summary>
public sealed record AlignRunOutput(string OutputId, string Name, MeasuredCorrection Measured, double Gamma, double DecodedShare, double HeldOutRmsPx,
    double FitRmsPx);

/// <summary>A finished run as its folder keeps it (align/&lt;run&gt;/run.json): staged, failed or cancelled; the camera, the fit, the
/// words, each output's result, the score, and what the score is made of.</summary>
public sealed record AlignRunRecord(string Id, string GroupId, string State, long StartedUnixMs, long FinishedUnixMs, string CameraId, string Fit,
    string Words, ImmutableArray<AlignRunOutput> Outputs, QualityScore Score, double SeamPx, double CoverageShare, double WhiteP5OverP95, double PlanarRmsPx);

/// <summary>An output's part of a run as STATE shows it.</summary>
public sealed record AlignOutputView(string Id, string Name, string Step, double Progress, string Light, string Words);

/// <summary>STATE.align.run: the running or last run — "running", "staged", "accepted", "failed" or "cancelled" — its step, progress (0–1),
/// the time left, words, each output, and the score once staged.</summary>
public sealed record AlignRunView(string Id, string GroupId, string State, string Step, double Progress, double EtaS, string Words,
    ImmutableArray<AlignOutputView> Outputs, QualityScore? Score)
{
    public bool IsRunning => State == "running";
}

/// <summary>What a run is given: the group, its outputs in order with their projectors (or null), the camera (null: the bare simulator),
/// where the canvas goes, the folder its files go under, and its id.</summary>
public sealed record AlignRunSetup(string RunId, AlignGroup Group, IReadOnlyList<Output> Outputs, IReadOnlyList<Projector?> Projectors, CameraRig? Rig,
    CanvasFit Fit, double Aspect, string ProjectFolder);

/// <summary>The alignment runner as the engine's verbs see it: one run at a time, off the control lane.</summary>
public interface IAlignBackend
{
    /// <summary>The running or last run (null before the first).</summary>
    AlignRunView? View { get; }

    bool IsRunning { get; }

    /// <summary>Starts a run; throws <see cref="InvalidOperationException"/> with the words why it cannot.</summary>
    void Start(AlignRunSetup setup);

    bool Cancel();

    /// <summary>A run this runner finished (null when it does not hold it: read its folder).</summary>
    AlignRunRecord? Finished(string runId);

    /// <summary>The run was applied: STATE says so.</summary>
    void MarkAccepted(string runId);

    /// <summary>Raised (on the run's thread) whenever the run moves on.</summary>
    event Action<AlignRunView>? Progressed;
}

/// <summary>The backend of a node composed without the align part: no run, and ALIGN RUN refused with the words why.</summary>
internal sealed class NoAlignBackend : IAlignBackend
{
    public static readonly NoAlignBackend Instance = new();

    public AlignRunView? View => null;

    public bool IsRunning => false;

    public void Start(AlignRunSetup setup) =>
        throw new InvalidOperationException("this node runs without the align part — its composition has no alignment runner");

    public bool Cancel() => false;

    public AlignRunRecord? Finished(string runId) => null;

    public void MarkAccepted(string runId)
    {
        // No run of this backend can be staged, so none is ever accepted.
    }

    public event Action<AlignRunView>? Progressed
    {
        add
        {
            // Nothing runs here, so nothing ever progresses: a handler is not kept.
        }
        remove
        {
            // No handler was kept.
        }
    }
}

/// <summary>How a real capture is paced and its camera opened (tests hand in their own camera and short holds).</summary>
public sealed record AlignRunnerOptions
{
    public Func<CameraRig, IScanCamera?>? OpenCamera { get; init; }

    public int SimulatedCameraWidth { get; init; } = 800;

    public int SimulatedCameraHeight { get; init; } = 450;

    public int LatencyFlashes { get; init; } = 3;

    public TimeSpan FlashHold { get; init; } = TimeSpan.FromMilliseconds(500);

    public double FallbackLatencyMs { get; init; } = 150;

    public TimeSpan FrameTimeout { get; init; } = TimeSpan.FromSeconds(5);

    public int MaxUnsettledFrames { get; init; } = 12;

    public SettleOptions Settle { get; init; } = new();

    /// <summary>Wall-clock milliseconds for a run's stamps.</summary>
    public Func<long>? UnixMs { get; init; }
}

/// <summary>
/// ALIGN RUN behind the verbs: identify (every output black, then each white alone and its grey ramp — footprints and γ), scan (each
/// output's structured-light plan alone, decoded), solve (the flat fit, the canvas, each output's measured warp and blend), stage (the
/// files and run.json written under align/&lt;run&gt;/, never over an earlier run's). A simulated camera — "sim", or a rig of kind
/// Simulated whose source names a layout — is its own display; a real camera goes through the scan display and the camera seam with
/// its latency measured first. One run at a time, on a pool task; nothing reaches the project until ALIGN ACCEPT.
/// </summary>
public sealed class AlignRunner : IAlignBackend, IAsyncDisposable
{
    private const int KeptRuns = 8;

    private readonly IScanDisplay? _display;
    private readonly AlignRunnerOptions _options;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, AlignRunRecord> _finished = new(StringComparer.Ordinal);
    private readonly Queue<string> _order = new();
    private CancellationTokenSource? _running;
    private Task? _task;
    private volatile AlignRunView? _view;

    public AlignRunner(IScanDisplay? display, AlignRunnerOptions? options = null)
    {
        _display = display;
        _options = options ?? new AlignRunnerOptions();
    }

    public AlignRunView? View => _view;

    public bool IsRunning => _view is { IsRunning: true };

    public event Action<AlignRunView>? Progressed;

    /// <summary>The run's task (tests wait on it; null before the first).</summary>
    public Task? Running => _task;

    private long Now => _options.UnixMs?.Invoke() ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public void Start(AlignRunSetup setup)
    {
        ArgumentNullException.ThrowIfNull(setup);
        lock (_gate)
        {
            if (_view is { IsRunning: true } v)
                throw new InvalidOperationException($"{v.Id} is running — ALIGN CANCEL first");
            if (!IsSimulated(setup.Rig) && _display is null)
                throw new InvalidOperationException("this node has no outputs to show the patterns on");
            _running?.Dispose();
            _running = new CancellationTokenSource();
            var outputs = setup.Outputs.Select(o => new AlignOutputView(o.Id, o.Name, "waiting", 0, "grey", "")).ToImmutableArray();
            Publish(new AlignRunView(setup.RunId, setup.Group.Id, "running", "identify", 0, -1, "starting", outputs, null));
            var token = _running.Token;
            _task = Task.Run(() => RunAsync(setup, token), CancellationToken.None);
        }
    }

    public bool Cancel()
    {
        lock (_gate)
        {
            if (_view is not { IsRunning: true } || _running is null)
                return false;
            _running.Cancel();
            return true;
        }
    }

    public AlignRunRecord? Finished(string runId)
    {
        lock (_gate)
            return _finished.GetValueOrDefault(runId);
    }

    public void MarkAccepted(string runId)
    {
        lock (_gate)
        {
            if (_finished.TryGetValue(runId, out var r))
                _finished[runId] = r with { State = "accepted" };
            if (_view is { } v && v.Id == runId && v.State == "staged")
                Publish(v with { State = "accepted", Words = "accepted: " + v.Words });
        }
    }

    /// <summary>A simulated camera: the bare "sim", or a rig of kind Simulated.</summary>
    public static bool IsSimulated(CameraRig? rig) => rig is null or { SourceKind: CameraSourceKind.Simulated };

    private void Publish(AlignRunView view)
    {
        _view = view;
        Progressed?.Invoke(view);
    }

    private async Task RunAsync(AlignRunSetup setup, CancellationToken ct)
    {
        var started = Now;
        var outputs = setup.Outputs;
        IScanCamera? camera = null;
        var stepEta = Stopwatch();
        try
        {
            IAlignShooter shooter;
            if (IsSimulated(setup.Rig))
            {
                var rasters = outputs.Select(o => (o.Width, o.Height)).ToList();
                var (cw, ch) = setup.Rig is { Intrinsics: { Width: > 0, Height: > 0 } k } ? (k.Width, k.Height) : (_options.SimulatedCameraWidth, _options.SimulatedCameraHeight);
                var sim = await Task.Run(() => MultiProjectorSim.FromSource(setup.Rig?.Source ?? "", rasters, cw, ch), ct).ConfigureAwait(false);
                shooter = new SimShooter(sim);
            }
            else
            {
                var rig = setup.Rig!;
                camera = (_options.OpenCamera ?? DefaultCamera)(rig)
                    ?? throw new InvalidOperationException($"{rig.Name} is a {rig.SourceKind.ToString().ToLowerInvariant()} camera, which this node cannot align through yet");
                if (!await camera.OpenAsync(ct).ConfigureAwait(false))
                    throw new InvalidOperationException($"{rig.Name} did not open: {camera.Problem}");
                shooter = new DisplayShooter(_display!, camera, outputs, _options);
            }
            var specs = outputs.Select((o, j) => new AlignOutputSpec(o.Id, o.Name, o.Width, o.Height, PriorFor(o, setup.Projectors[j]))).ToList();
            var total = AlignSequence.Shots(specs);
            var (black, captures) = await AlignSequence.CaptureAsync(shooter, specs, p =>
            {
                var eta = p.Progress > 0.02 ? stepEta() * (1 - p.Progress) / p.Progress : -1;
                Step(setup, p.Step, p.Output, 0.9 * p.Progress, eta, p.Words, total);
            }, ct).ConfigureAwait(false);
            Step(setup, "solve", -1, 0.9, -1, "fitting the screen, the canvas, each output's warp and blend", total);
            var group = setup.Group;
            var corners = group.Target.CameraCorners.IsDefaultOrEmpty ? [] : group.Target.CameraCorners.ToList();
            var input = new AlignSolveInput(black.Width, black.Height, black.Gray, captures, setup.Fit, setup.Aspect, corners, group.Target.MarginPct, group.Options);
            var result = await Task.Run(() => AlignSolve.Solve(input, ct), ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            Finish(setup, started, result, captures);
        }
        catch (OperationCanceledException)
        {
            Fail(setup, started, "cancelled", "cancelled — the outputs are the show's again");
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            Fail(setup, started, "failed", ex.Message);
        }
        finally
        {
            _display?.Release();
            camera?.Dispose();
        }
    }

    private static Func<double> Stopwatch()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        return () => sw.Elapsed.TotalSeconds;
    }

    private static IScanCamera? DefaultCamera(CameraRig rig) =>
        CaptureCamera.OptionsFor(rig) is { } options ? new CaptureCamera(options) : null;

    /// <summary>What the rectification may assume about an output's projector: its optics at the output's raster when the output shows
    /// a projector, ±5 %; otherwise a centred lens of throw 1.5, ±30 %.</summary>
    internal static ProjectorPrior PriorFor(Output o, Projector? pj)
    {
        if (pj is not { Intrinsics: { Width: > 0, Height: > 0, Fx: > 0 } k })
            return ProjectorPrior.Unknown(o.Width, o.Height);
        var sx = (double)o.Width / k.Width;
        var sy = (double)o.Height / k.Height;
        return new ProjectorPrior(o.Width, o.Height, k.Cx * sx, k.Cy * sy, k.Fx * sx, 0.05);
    }

    private void Step(AlignRunSetup setup, string step, int output, double progress, double eta, string words, int total)
    {
        if (_view is not { IsRunning: true } v || v.Id != setup.RunId)
            return;
        var outputs = v.Outputs;
        if (output >= 0 && output < outputs.Length)
        {
            for (var j = 0; j < outputs.Length; j++)
            {
                outputs = outputs.SetItem(j, j == output
                    ? outputs[j] with { Step = step, Progress = Math.Min(1, outputs[j].Progress + 1.0 / Math.Max(1, total / outputs.Length)), Light = "green", Words = words }
                    : outputs[j]);
            }
        }
        Publish(v with { Step = step, Progress = Math.Round(progress, 3), EtaS = eta < 0 ? -1 : Math.Round(eta, 1), Words = words, Outputs = outputs });
    }

    private void Fail(AlignRunSetup setup, long started, string state, string words)
    {
        var record = new AlignRunRecord(setup.RunId, setup.Group.Id, state, started, Now, setup.Rig?.Id ?? "sim", setup.Fit.ToString().ToLowerInvariant(),
            words, [], QualityScore.None with { Grade = "F", Unit = "points", Source = "align", Words = words }, -1, 0, double.NaN, double.NaN);
        Keep(record);
        if (state == "failed")
            TryArchive(setup, record, []);
        var outputs = _view?.Outputs ?? [];
        Publish(new AlignRunView(setup.RunId, setup.Group.Id, state, _view?.Step ?? "", _view?.Progress ?? 0, -1, words,
            [.. outputs.Select(o => o with { Light = state == "failed" ? "red" : "grey" })], null));
    }

    private void Finish(AlignRunSetup setup, long started, AlignSolveResult result, IReadOnlyList<AlignCapture> captures)
    {
        if (!result.Ok)
        {
            Fail(setup, started, "failed", result.Words);
            return;
        }
        var finished = Now;
        var dataPath = AlignArchive.DataPath(setup.RunId);
        var files = new List<(string Name, byte[] Bytes)>();
        var outs = ImmutableArray.CreateBuilder<AlignRunOutput>(result.Outputs.Length);
        var uniformity = double.IsFinite(result.WhiteP5OverP95) ? Math.Max(0, 1 - result.WhiteP5OverP95) : -1;
        var score = RoutableFixes(CalibrationScoring.ForAlign(new AlignScoreInputs(setup.Group.Id, result.CoverageShare, result.SeamPx, -1, uniformity, -1)),
            setup.Group);
        for (var j = 0; j < result.Outputs.Length; j++)
        {
            var o = result.Outputs[j];
            var warpName = $"warp-{o.OutputId}.pfm";
            var blendName = $"blend-{o.OutputId}.pfm";
            var warpBytes = Pfm.Write(o.Warp.Grid.ToMap());
            var blendBytes = Pfm.Write(o.Blend.ToMap());
            files.Add((warpName, warpBytes));
            files.Add((blendName, blendBytes));
            var measured = new MeasuredCorrection(
                new MeasuredWarp(o.Warp.Grid.Columns, o.Warp.Grid.Rows, dataPath + "/" + warpName, Pfm.Hash(warpBytes), Math.Round(o.Warp.HeldOutRmsPx, 3)),
                null,
                new MeasuredBlend(o.Blend.Width, o.Blend.Height, dataPath + "/" + blendName, Pfm.Hash(blendBytes)),
                new MeasuredColor(1, 1, 1, Math.Round(o.Gamma, 3), 1),
                Math.Round(o.Warp.HeldOutRmsPx, 3), "", "", setup.RunId, finished);
            outs.Add(new AlignRunOutput(o.OutputId, setup.Outputs[j].Name, measured, o.Gamma, o.DecodedShare, o.Warp.HeldOutRmsPx, o.FitRmsPx));
        }
        var words = string.Create(CultureInfo.InvariantCulture, $"staged {score.Grade} {score.Value:0}: ") + result.Words
            + string.Create(CultureInfo.InvariantCulture, $" — ALIGN ACCEPT {setup.RunId} applies it");
        var record = new AlignRunRecord(setup.RunId, setup.Group.Id, "staged", started, finished, setup.Rig?.Id ?? "sim", setup.Fit.ToString().ToLowerInvariant(),
            words, outs.ToImmutable(), score, result.SeamPx, result.CoverageShare, result.WhiteP5OverP95, result.Planar?.RmsPx ?? double.NaN);
        if (!TryArchive(setup, record, files))
        {
            Fail(setup, started, "failed", $"the run's files could not be written under {dataPath} — check the project folder");
            return;
        }
        Keep(record);
        var views = record.Outputs.Select(o => new AlignOutputView(o.OutputId, o.Name, "staged", 1, "green",
            string.Create(CultureInfo.InvariantCulture, $"γ {o.Gamma:0.00}, {o.DecodedShare * 100:0} % decoded, warp {o.HeldOutRmsPx:0.00} px"))).ToImmutableArray();
        Publish(new AlignRunView(setup.RunId, setup.Group.Id, "staged", "stage", 1, 0, words, views, score));
    }

    /// <summary>Every fix a run's score carries is a line this node carries out: the uniformity term's (colour steps are next round)
    /// becomes the group's own uniformity raised to 1 for the next run, or no line when it is 1 already.</summary>
    internal static QualityScore RoutableFixes(QualityScore score, AlignGroup group) => score with
    {
        Terms = [.. score.Terms.Select(t => t.Code == ScoreTermCodes.Uniformity
            ? t with { Fix = group.Options.Uniformity < 1 ? $"SET {group.Id} options.uniformity 1" : "" }
            : t)],
    };

    private bool TryArchive(AlignRunSetup setup, AlignRunRecord record, IReadOnlyList<(string Name, byte[] Bytes)> files)
    {
        try
        {
            AlignArchive.Save(setup.ProjectFolder, record, files);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private void Keep(AlignRunRecord record)
    {
        lock (_gate)
        {
            if (!_finished.ContainsKey(record.Id))
                _order.Enqueue(record.Id);
            _finished[record.Id] = record;
            while (_order.Count > KeptRuns)
                _finished.Remove(_order.Dequeue());
        }
    }

    public async ValueTask DisposeAsync()
    {
        CancellationTokenSource? cts;
        Task? task;
        lock (_gate)
        {
            cts = _running;
            task = _task;
        }
        if (cts is not null)
            await cts.CancelAsync().ConfigureAwait(false);
        if (task is not null)
            await task.ConfigureAwait(false);
        cts?.Dispose();
    }
}
