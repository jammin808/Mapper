using System.Collections.Concurrent;
using System.Globalization;
using PlayMap.Core.Content;
using PlayMap.Core.Model;
using PlayMap.Core.Time;
using PlayMap.Render.Engine;
using PlayMap.Render.Gpu;
using PlayMap.Render.Platform;
using PlayMap.Render.Timing;

namespace PlayMap.Engine.Rendering;

/// <summary>How the render host runs.</summary>
public sealed record RenderHostOptions
{
    public static readonly RenderHostOptions Default = new();

    /// <summary>Open a window for every output assigned to a display; false is a headless node (every output
    /// still renders offscreen, so previews, health and the scan's present times work).</summary>
    public bool Windows { get; init; } = true;

    /// <summary>The GPU device's checks (null: the build's default — error checks in debug builds only).</summary>
    public GlDeviceOptions? Device { get; init; }

    public RenderEngineOptions Engine { get; init; } = new();

    public StageResourceOptions Stage { get; init; } = StageResourceOptions.Default;

    /// <summary>The displays' measured latency, added to the predicted vertical blank: the leader instant a
    /// frame is drawn for is when its light leaves the lens.</summary>
    public long DisplayLatencyNs { get; init; }

    /// <summary>The loop's rate while no window paces it (0 = the project's frame rate).</summary>
    public double HeadlessRateHz { get; init; }

    /// <summary>The sting channel the generators read (null: no stings).</summary>
    public EffectImpulseChannel? Impulses { get; init; }

    /// <summary>How long <see cref="RenderHost.TryCreate"/> waits for the context.</summary>
    public TimeSpan StartTimeout { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>The least time between two pictures of one output view (10 Hz).</summary>
    public TimeSpan OutputPreviewInterval { get; init; } = TimeSpan.FromMilliseconds(100);

    /// <summary>The least time between two pictures of an editor or projector view (30 Hz).</summary>
    public TimeSpan StagePreviewInterval { get; init; } = TimeSpan.FromMilliseconds(33);

    /// <summary>How often the displays are listed again while windows are on (a hot-plugged projector).</summary>
    public TimeSpan DisplayPoll { get; init; } = TimeSpan.FromSeconds(2);
}

/// <summary>The render loop's own numbers: frames drawn, the CPU time of the frame path (mean and 95th
/// percentile over the last 120 frames), the loop's rate, the bytes the frame path has allocated in all
/// (flat once warm), the windows open and the picture sources open.</summary>
public readonly record struct RenderHostStats(long Frames, double FrameMs, double FrameMsP95, double Fps, long FramePathAllocatedBytes, int Windows, int OpenSources);

/// <summary>The render side of the node (<see cref="IRenderHost"/>): one OpenGL context on a thread of its
/// own, the <see cref="RenderEngine"/> on it, the output windows, the engine's pictures
/// (<see cref="EngineSources"/>), the stage's maps (<see cref="StageResources"/>), the scan's frames and the
/// previews.
///
/// <para><b>Lifecycle.</b> <see cref="TryCreate"/> starts the render thread, which makes the context and the
/// renderer and compiles every shader; it answers null with the reason when OpenGL cannot start (no display,
/// no driver). The host is then the engine's output backend: build the engine's services with it, build the
/// engine, call <see cref="Start"/> with the engine, and dispose the host (it disposes everything GL on the
/// thread that made it) before the engine:
/// <code>
/// var render = RenderHost.TryCreate(RenderHostOptions.Default with { Windows = !headless }, out var why);
/// // null: log why and run with NullBackends — the node plays and answers, it draws nothing.
/// var services = new EngineServices(settings, mono, wall, clock, media, (IOutputBackend?)render ?? NullBackends.Instance, scan, net, control, journal, folder);
/// var engine = new EngineCore(services, project);
/// render?.Start(engine);
/// …
/// if (render is not null) await render.DisposeAsync();
/// await engine.DisposeAsync();
/// </code></para>
///
/// <para><b>The frame.</b> Each frame reads <see cref="EngineCore.Snapshot"/> once (project, playback, air) and
/// draws every enabled output for the leader instant of its light: <see cref="EngineCore.LeaderNowNs"/> plus the
/// time to the pacing window's next vertical blank (none headless) plus
/// <see cref="RenderHostOptions.DisplayLatencyNs"/>. Blackout and freeze come from the air; a test pattern
/// covers the outputs the air names; the identification cards stay up until the air's instant; a scan frame
/// replaces one output's raster. The frame path allocates nothing once warm
/// (<see cref="RenderHostStats.FramePathAllocatedBytes"/> stays flat); work from other threads
/// (<see cref="ReadOutputAsync"/>, previews being set up) runs between frames.</para>
///
/// <para><b>Pacing.</b> The first output window attached paces the loop: its swap waits for the vertical
/// blank, the others swap at once. With no window (or a swap that does not wait, as on a virtual display) the
/// loop waits on a timer for the project's frame rate.</para>
///
/// <para><b>Previews</b> (the desk's thumbnails, its projector view and its 3D view) come through the VIEW verb;
/// the host attaches itself to <see cref="EngineCore.Previews"/> on <see cref="Start"/> (see
/// RenderHost.Previews.cs).</para></summary>
public sealed partial class RenderHost : IRenderHost
{
    private const string PatternGeneratorId = "gn_air_pattern";
    private const int FrameRing = 120;
    private static readonly SourceRef PatternSource = SourceRef.Generator(PatternGeneratorId);

    private readonly RenderHostOptions _options;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new(false);
    private readonly AutoResetEvent _wake = new(false);
    private readonly TaskCompletionSource _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ConcurrentQueue<Action> _work = new();
#pragma warning disable CA2213 // Everything GL (and what the frame path holds) is disposed by TearDown on the render thread that made it, never from DisposeAsync's thread.
    private readonly ScanFrames _scan;
#pragma warning restore CA2213
    private readonly Lock _healthGate = new();
    private readonly Lock _waiterGate = new();
    private readonly List<(long Frame, TaskCompletionSource Done)> _frameWaiters = [];
    private readonly double[] _frameMs = new double[FrameRing];
    private readonly double[] _frameMsSorted = new double[FrameRing];
    private readonly Lock _frameMsGate = new();
    private readonly FramePacer _loopPacer = new(60);
    private readonly FramePacer _vblank = new(60);
    private readonly Func<string, long> _presentOf;

    private volatile bool _stopping;
    private volatile string? _startFailure;
    private EngineCore? _engine;
    private int _started;
    private StageResources? _stage;
#pragma warning disable CA2213 // As above: disposed by TearDown on the render thread.
    private EngineSources? _sources;
    private TimerView? _timer;

    // Render thread.
    private RenderContext? _context;
    private RenderEngine? _renderer;
    private OutputWindows? _windows;
#pragma warning restore CA2213
    private Project? _lastProject;
    private Project? _patternBase;
    private string? _patternKind;
    private Project? _patternProject;
    private IReadOnlyList<MonitorInfo> _monitors = [];
    private long _nextDisplayPollNs;
    private long _deadlineNs;
    private long _finishNs;
    private HealthSlot[] _health = [];

    // Published.
    private volatile IReadOnlyList<DisplayInfo> _displays = [];
    private volatile string _rendererName = "OpenGL";
    private long _frames;
    private long _framePathBytes;
    private int _frameWaiterCount;
    private volatile int _openWindows;

    private sealed class HealthSlot(string outputId, string display)
    {
        public string OutputId { get; } = outputId;
        public string Display = display;
        public bool Open;
        public double Fps;
        public long Dropped;
        public double FrameMsP95;
    }

    private RenderHost(RenderHostOptions options)
    {
        _options = options;
        _presentOf = PresentOf;
        _scan = new ScanFrames(() => Volatile.Read(ref _engine)?.Store.Project);
        _thread = new Thread(Main) { IsBackground = true, Name = "render", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    /// <summary>Starts the render thread and its OpenGL context; null (with the reason) when OpenGL cannot
    /// start here — no display, no driver, a context that never came up.</summary>
    public static RenderHost? TryCreate(RenderHostOptions? options, out string why)
    {
        options ??= RenderHostOptions.Default;
        if (OperatingSystem.IsLinux()
            && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY"))
            && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")))
        {
            why = "no display: DISPLAY and WAYLAND_DISPLAY are unset, and OpenGL needs one — a headless node runs a virtual display (Xvfb)";
            return null;
        }
        var host = new RenderHost(options);
        if (!host._ready.Wait(options.StartTimeout))
        {
            host._stopping = true;
            host._wake.Set();
            why = string.Create(CultureInfo.InvariantCulture, $"the OpenGL context did not come up within {options.StartTimeout.TotalSeconds:0} s");
            return null;
        }
        if (host._startFailure is { } failure)
        {
            // The render thread has torn down what it made and ended; its two signals go with the object.
            why = failure;
            return null;
        }
        why = "";
        return host;
    }

    // ---- IRenderHost ----------------------------------------------------------------------------------

    /// <summary>Starts drawing the engine's frames. Once.</summary>
    public void Start(EngineCore engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        if (Interlocked.Exchange(ref _started, 1) != 0)
            throw new InvalidOperationException("the render host has started already");
        ObjectDisposedException.ThrowIf(_stopping, this);
        _stage = new StageResources(engine.Meshes, _options.Stage);
        _timer = new TimerView(engine.Services.Network.StagewatchTimerJson);
        _sources = new EngineSources(engine.Services.Media, _scan, _timer);
        engine.BlendSolved += OnBlendSolved;
        if (engine.LastBlendSolve is { } solved)
            _stage.Offer(solved);
        Volatile.Write(ref _engine, engine);
        engine.Previews.AttachRenderer(this);
        _scan.SetOpen(null);
        _wake.Set();
    }

    public string RendererName => _rendererName;

    public IReadOnlyList<DisplayInfo> Displays() => _displays;

    public IReadOnlyList<OutputHealth> Health()
    {
        lock (_healthGate)
        {
            var list = new List<OutputHealth>(_health.Length);
            foreach (var h in _health)
                list.Add(new OutputHealth(h.OutputId, h.Open, h.Fps, h.Dropped, h.FrameMsP95, h.Display));
            return list;
        }
    }

    /// <summary>Shows an 8-bit grey frame raw on one output (every other output keeps the show) and completes
    /// with the monotonic instant it was presented: the window's swap, or the frame's completion on the GPU
    /// for an output with no window. Faults with the reason for an output that is missing, off, of another
    /// raster, or blacked out.</summary>
    public Task<long> ShowAsync(string outputId, ReadOnlyMemory<byte> gray8, int width, int height, CancellationToken ct) =>
        _scan.ShowAsync(outputId, gray8, width, height, ct);

    public void Release() => _scan.Release();

    // ---- the host's own API -----------------------------------------------------------------------------

    /// <summary>The engine's stage maps (tests and diagnostics).</summary>
    public StageResources? Stage => _stage;

    public RenderHostStats Stats
    {
        get
        {
            var frames = Interlocked.Read(ref _frames);
            var n = (int)Math.Min(frames, FrameRing);
            double mean = 0, p95 = 0;
            if (n > 0)
            {
                lock (_frameMsGate)
                {
                    Array.Copy(_frameMs, _frameMsSorted, n);
                    Array.Sort(_frameMsSorted, 0, n);
                    for (var i = 0; i < n; i++)
                        mean += _frameMsSorted[i];
                    mean /= n;
                    p95 = _frameMsSorted[Math.Clamp((int)Math.Ceiling(0.95 * n) - 1, 0, n - 1)];
                }
            }
            return new RenderHostStats(frames, mean, p95, _loopPacer.Fps, Interlocked.Read(ref _framePathBytes), _openWindows, _sources?.OpenSources ?? 0);
        }
    }

    /// <summary>Completes once the loop has drawn this many more frames.</summary>
    public Task FramesAsync(int count = 1)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_waiterGate)
        {
            _frameWaiters.Add((Interlocked.Read(ref _frames) + Math.Max(1, count), done));
            Volatile.Write(ref _frameWaiterCount, _frameWaiters.Count);
        }
        if (_stopping)
            done.TrySetCanceled(CancellationToken.None);
        return done.Task;
    }

    /// <summary>An output's finished picture after the next frame, as BGRA bytes top row first (a snapshot,
    /// a test); null when the output has drawn nothing.</summary>
    public Task<byte[]?> ReadOutputAsync(string outputId) => InvokeAsync(renderer =>
    {
        var output = _lastProject?.FindOutput(outputId);
        if (output is null || output.Width < 1 || output.Height < 1)
            return null;
        var bgra = new byte[output.Width * output.Height * 4];
        return renderer.ReadOutput(outputId, bgra) ? bgra : null;
    });

    /// <summary>Runs a body on the render thread between two frames and answers its result.</summary>
    internal Task<T> InvokeAsync<T>(Func<RenderEngine, T> body)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (_stopping)
        {
            done.TrySetException(new ObjectDisposedException(nameof(RenderHost)));
            return done.Task;
        }
        _work.Enqueue(() =>
        {
            try
            {
                done.TrySetResult(body(_renderer!));
            }
            catch (Exception ex)
            {
                done.TrySetException(ex);
            }
        });
        return done.Task;
    }

    private void OnBlendSolved(BlendSolveResult result) => _stage?.Offer(result);

    // ---- the render thread ------------------------------------------------------------------------------

    private void Main()
    {
        try
        {
            _context = RenderContext.CreateHidden(_options.Device ?? GlDeviceOptions.Default);
            _renderer = new RenderEngine(_context, _options.Engine);
            _renderer.WarmUp();
            var d = _context.Device;
            _rendererName = string.Create(CultureInfo.InvariantCulture, $"OpenGL {d.GlVersion / 10}.{d.GlVersion % 10} {d.Renderer}");
            _windows = new OutputWindows(_context, _renderer);
            RefreshDisplays();
        }
        // The thread's boundary: whatever stopped the context from coming up is the reason TryCreate gives.
        catch (Exception ex)
        {
            _startFailure = $"OpenGL could not start: {ex.Message}";
            TearDown();
            _ready.Set();
            _exited.TrySetResult();
            return;
        }
        _ready.Set();
        try
        {
            while (!_stopping && Volatile.Read(ref _engine) is null)
                _wake.WaitOne(TimeSpan.FromMilliseconds(250));
            if (!_stopping && Volatile.Read(ref _engine) is { } engine)
                Loop(engine);
        }
        // A fault in the loop stops the drawing, not the node: the engine plays on, STATE shows the outputs closed.
        catch (Exception ex)
        {
            Volatile.Read(ref _engine)?.Journal.Add("fault", $"render: {ex.GetType().Name}: {ex.Message}", ok: false);
        }
        finally
        {
            TearDown();
            _exited.TrySetResult();
        }
    }

    private void Loop(EngineCore engine)
    {
        _deadlineNs = MonoClock.Instance.NowNs;
        while (!_stopping)
        {
            var bytesBefore = GC.GetAllocatedBytesForCurrentThread();
            var start = MonoClock.Instance.NowNs;
            Frame(engine);
            var end = MonoClock.Instance.NowNs;
            Interlocked.Add(ref _framePathBytes, GC.GetAllocatedBytesForCurrentThread() - bytesBefore);
            var frames = Interlocked.Increment(ref _frames);
            lock (_frameMsGate)
                _frameMs[(int)((frames - 1) % FrameRing)] = (end - start) / 1e6;
            if (Volatile.Read(ref _frameWaiterCount) > 0)
                ReleaseFrameWaiters(frames);
            while (_work.TryDequeue(out var work))
                work();
            Pace(engine, start);
        }
    }

    private void Frame(EngineCore engine)
    {
        var renderer = _renderer!;
        var windows = _windows!;
        var snap = engine.Snapshot;
        var project = snap.Project.Project;
        var air = snap.Air;
        _stage!.Observe(snap.Project);
        var monoNow = MonoClock.Instance.NowNs;
        if (!ReferenceEquals(project, _lastProject))
        {
            _lastProject = project;
            ProjectChanged(engine, project);
        }
        else if (_options.Windows && monoNow >= _nextDisplayPollNs)
        {
            PollDisplays(engine, project, monoNow);
        }
        if (windows.Pump())
            WindowsChanged(engine);

        _sources!.BeginFrame(snap, renderer.Frame + 1);
        _scan.BeginFrame(project, air.EffectiveBlackout);
        var lead = windows.Pacer is not null ? Math.Max(0, _vblank.PredictNextVblank(monoNow) - monoNow) : 0;
        var leaderNs = engine.LeaderNowNs + lead + _options.DisplayLatencyNs;
        var inputs = new FrameInputs
        {
            Project = air.PatternKind is { } kind ? PatternProject(project, kind) : project,
            LeaderNs = leaderNs,
            Sources = _sources,
            Stage = _stage,
            Blackout = air.EffectiveBlackout,
            Freeze = air.Frozen,
            PatternOverride = air.PatternKind is null ? null : PatternSource,
            PatternOutputs = air.PatternOutputs,
            Raw = _scan,
            Identify = air.IdentifyUntilLeaderNs != 0 && leaderNs < air.IdentifyUntilLeaderNs,
            Impulse = _options.Impulses?.Current ?? EffectImpulse.None,
        };
        renderer.Render(in inputs);
        if (windows.Pacer is { } pacer && windows.WindowOf(pacer) is { } paced)
            _vblank.OnPresented(paced.LastSwapNs);
        _loopPacer.OnPresented(MonoClock.Instance.NowNs);
        RenderPreviews(renderer, project, monoNow);
        if (_scan.AnyHanded)
        {
            _finishNs = -1;
            _scan.AfterFrame(_presentOf);
        }
        UpdateHealth(renderer, windows);
    }

    /// <summary>When a scan frame was presented on an output: its window's swap, else (no window) the instant
    /// the GPU finished the frame.</summary>
    private long PresentOf(string outputId)
    {
        if (_windows!.WindowOf(outputId) is { } window)
            return window.LastSwapNs;
        if (_finishNs < 0)
        {
            _context!.Device.Finish();
            _finishNs = MonoClock.Instance.NowNs;
        }
        return _finishNs;
    }

    /// <summary>The project with the air's test pattern as one more generator (made once per project and kind).</summary>
    private Project PatternProject(Project project, string kind)
    {
        if (!ReferenceEquals(project, _patternBase) || !string.Equals(kind, _patternKind, StringComparison.Ordinal))
        {
            _patternBase = project;
            _patternKind = kind;
            _patternProject = project with { Generators = project.Generators.Add(new Generator(PatternGeneratorId, kind, kind, ParamSet.Empty)) };
        }
        return _patternProject!;
    }

    private void ProjectChanged(EngineCore engine, Project project)
    {
        if (_options.Windows)
        {
            RefreshDisplays();
            if (_windows!.Sync(project, _monitors))
                WindowsChanged(engine);
        }
        var slots = new List<HealthSlot>();
        foreach (var o in project.Outputs)
            if (o.Enabled)
                slots.Add(new HealthSlot(o.Id, o.DisplayKey));
        lock (_healthGate)
            _health = [.. slots];
        _nextDisplayPollNs = MonoClock.Instance.NowNs + (long)_options.DisplayPoll.TotalNanoseconds;
    }

    private void PollDisplays(EngineCore engine, Project project, long monoNow)
    {
        _nextDisplayPollNs = monoNow + (long)_options.DisplayPoll.TotalNanoseconds;
        var before = _monitors;
        RefreshDisplays();
        if (before.SequenceEqual(_monitors))
            return;
        _windows!.Sync(project, _monitors);
        WindowsChanged(engine);
    }

    private void RefreshDisplays()
    {
        var monitors = _context!.Monitors();
        _monitors = monitors;
        var list = new List<DisplayInfo>(monitors.Count);
        foreach (var m in monitors)
            list.Add(new DisplayInfo(DisplayMatch.KeyOf(m, monitors), m.Name, m.X, m.Y, m.Width, m.Height, m.RefreshHz, m.Index == 0));
        _displays = list;
    }

    /// <summary>STATE's outputs moved: the engine hears it off this thread (its listeners do work).</summary>
    private void WindowsChanged(EngineCore engine)
    {
        _openWindows = _windows!.Open;
        ThreadPool.UnsafeQueueUserWorkItem(static e => e.FactsChanged(), engine, preferLocal: false);
    }

    private void UpdateHealth(RenderEngine renderer, OutputWindows windows)
    {
        lock (_healthGate)
        {
            foreach (var h in _health)
            {
                if (windows.WindowOf(h.OutputId) is not null)
                {
                    var s = renderer.Stats(h.OutputId);
                    h.Open = true;
                    h.Fps = s.Fps;
                    h.Dropped = s.Dropped;
                    h.FrameMsP95 = s.FrameMsP95;
                }
                else
                {
                    // Offscreen: the loop's own cadence is the output's.
                    h.Open = false;
                    h.Fps = _loopPacer.Fps;
                    h.Dropped = _loopPacer.Missed;
                    h.FrameMsP95 = _loopPacer.FrameMsP95;
                }
                if (windows.DisplayOf(h.OutputId) is { } display)
                    h.Display = display;
            }
        }
    }

    private void ReleaseFrameWaiters(long frames)
    {
        List<TaskCompletionSource>? due = null;
        lock (_waiterGate)
        {
            for (var i = _frameWaiters.Count - 1; i >= 0; i--)
            {
                if (_frameWaiters[i].Frame > frames)
                    continue;
                (due ??= []).Add(_frameWaiters[i].Done);
                _frameWaiters.RemoveAt(i);
            }
            Volatile.Write(ref _frameWaiterCount, _frameWaiters.Count);
        }
        if (due is not null)
            foreach (var d in due)
                d.TrySetResult();
    }

    /// <summary>Waits for the next frame's slot. A window whose swap waits for the vertical blank paces the
    /// loop by itself (the wait here is then under the margin); otherwise the deadline advances by whole
    /// periods so the mean rate is exact, and is taken up again from now after a stall.</summary>
    private void Pace(EngineCore engine, long frameStartNs)
    {
        var rate = _options.HeadlessRateHz > 0 ? _options.HeadlessRateHz : RenderEngine.FrameRateOf(engine.Store.Project.Settings.FrameRate);
        var period = (long)(1e9 / rate);
        var now = MonoClock.Instance.NowNs;
        if (_windows!.Pacer is not null)
        {
            _deadlineNs = frameStartNs + period;
        }
        else
        {
            _deadlineNs += period;
            if (now - _deadlineNs > period)
                _deadlineNs = now;
        }
        var wait = _deadlineNs - now;
        var margin = _windows.Pacer is not null ? 2_000_000 : 500_000;
        if (wait > margin)
            _wake.WaitOne(TimeSpan.FromTicks(wait / 100));
    }

    /// <summary>Deletes everything GL on this thread (windows, renderer, context) and fails what still waits.</summary>
    private void TearDown()
    {
        _scan.Dispose();
        try
        {
            _windows?.Dispose();
            _sources?.Dispose();
            _timer?.Dispose();
            // The renderer first: its readbacks write into the views' rings until it is gone.
            _renderer?.Dispose();
            DisposePreviews();
            _context?.Dispose();
        }
        catch (Exception ex) when (ex is GlException or InvalidOperationException or ObjectDisposedException)
        {
            _startFailure ??= $"the render context did not close cleanly: {ex.Message}";
        }
        lock (_waiterGate)
        {
            foreach (var (_, done) in _frameWaiters)
                done.TrySetCanceled(CancellationToken.None);
            _frameWaiters.Clear();
        }
        while (_work.TryDequeue(out var work))
            work();
    }

    /// <summary>Stops the loop and waits for the render thread to delete everything it made.</summary>
    public async ValueTask DisposeAsync()
    {
        _stopping = true;
        if (Volatile.Read(ref _engine) is { } engine)
        {
            engine.Previews.AttachRenderer(null);
            engine.BlendSolved -= OnBlendSolved;
        }
        _wake.Set();
        await _exited.Task.ConfigureAwait(false);
        if (_stage is not null)
            await _stage.DisposeAsync().ConfigureAwait(false);
        _wake.Dispose();
        _ready.Dispose();
    }
}
