using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Extensions.Logging;
using PlayMap.Core.Content;
using PlayMap.Core.Model;
using PlayMap.Core.Time;
using PlayMap.Engine.Host;
using PlayMap.Engine.Presentation;
using PlayMap.Render.Engine;
using PlayMap.Render.Gpu;
using PlayMap.Render.Platform;
using PlayMap.Render.Sync;
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

    /// <summary>The platform asked for: the present path and the GL dialect (NodeSettings.Present), or no window system at all.</summary>
    public PlatformRequest Platform { get; init; } = PlatformRequest.Default;

    /// <summary>What a "dxgi" path is made by (null: this machine's — the flip-model platform on Windows, none elsewhere); tests hand
    /// in a platform of their own.</summary>
    public Func<GlDeviceOptions, IGpuPlatform>? Dxgi { get; init; }

    /// <summary>The present fuse this start armed (the DXGI path, explicit); the host deletes it once every display output reached its
    /// first settled frames (null: no fuse — a GL start, or a test).</summary>
    public Presentation.PresentFuse? Fuse { get; init; }

    /// <summary>The framelock tier (null: this machine's — NVAPI on Windows, none elsewhere). The host owns and disposes it.</summary>
    public Func<IFrameLockDriver>? FrameLock { get; init; }

    /// <summary>Outputs that fell out of the lock join again by themselves at an idle boundary (NodeSettings.Present.FrameLock "auto");
    /// false joins only on SYNC APPLY and SYNC REJOIN (which a node set "off" refuses: it never joins).</summary>
    public bool FrameLockAuto { get; init; } = true;

    /// <summary>The swap group and barrier SYNC APPLY joins (NodeSettings.Present).</summary>
    public uint SwapGroup { get; init; } = 1;

    public uint SwapBarrier { get; init; } = 1;

    public RenderEngineOptions Engine { get; init; } = new();

    public StageResourceOptions Stage { get; init; } = StageResourceOptions.Default;

    /// <summary>The displays' latency when no output names its own (<c>Output.LatencyMs</c> all 0), added to the predicted vertical
    /// blank: the leader instant a frame is drawn for is when its light leaves the lens. Outputs that name theirs give the node's air
    /// latency instead (the largest), and the faster ones wait whole frames for it (RenderHost.Output.cs).</summary>
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

    /// <summary>Frames in a row that may fault before the loop gives up (a lost context faults every frame).</summary>
    private const int FaultLimit = 30;
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
    private FramePacer _loopPacer = new(60);
    private readonly HeadlessPacing _headless = new();
    private readonly Func<string, long> _presentOf;
    private readonly double[] _presentMs = new double[FrameRing];
    private readonly double[] _presentMsSorted = new double[FrameRing];
#pragma warning disable CA2213 // Disposed by TearDown on the render thread (it leaves every joined output there).
    private readonly FrameLockHost _lock;
#pragma warning restore CA2213

    private volatile bool _stopping;
    private volatile string? _startFailure;
    private EngineCore? _engine;
    private FactsSignal? _facts;
    private int _started;
    private StageResources? _stage;
#pragma warning disable CA2213 // As above: disposed by TearDown on the render thread.
    private EngineSources? _sources;
    private TimerView? _timer;

    // Render thread.
    private IGpuPlatform? _platform;
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
        public bool HasPresent;
        public PresentSnapshot Present;
    }

    private RenderHost(RenderHostOptions options)
    {
        _options = options;
        _presentOf = PresentOf;
        _lock = new FrameLockHost(options.FrameLock?.Invoke() ?? FrameLockDrivers.ForThisMachine(), options.FrameLockAuto);
        _lock.Changed += OnLockChanged;
        _lock.FellOut += OnFellOut;
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

    /// <summary>The node's render host as the engine host asks for it (<see cref="EngineParts.RenderHost"/>): windows on
    /// the displays outputs are assigned to, none for a headless node (which still renders offscreen when it has GL).
    /// A headless node with no display has no GL by design: null, and the log says why. Any other failure throws with
    /// the reason, which the host shows as the node's "render" fault.</summary>
    public static IRenderHost? ForNode(RenderHostRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var log = request.Logs.CreateLogger("PlayMap.Render");
        // The node home's settings, not the run's: PRESENT PATH saved them for this start. Read only (the network host is the file's
        // one writer): a file that does not read now leaves this run's.
        var saved = request.Home.ReadSettings() ?? request.Settings;
        var present = saved.Present;
        var fuse = new PresentFuse(request.Home.Folder, () => request.Wall.UnixMs);
        var (path, fuseWords) = PresentChoice.Decide(present.Path, fuse.Read());
        var armed = path == "dxgi" && OperatingSystem.IsWindows() && !request.Options.Headless;
        if (armed)
            fuse.Arm("dxgi");
        var host = TryCreate(RenderHostOptions.Default with
        {
            Windows = !request.Options.Headless,
            Platform = new PlatformRequest(path, present.Dialect) { Words = fuseWords, GlApi = saved.Decode.GlApi },
            Fuse = armed ? fuse : null,
            FrameLockAuto = !string.Equals(present.FrameLock, "off", StringComparison.OrdinalIgnoreCase),
            SwapGroup = (uint)Math.Max(1, present.SwapGroup),
            SwapBarrier = (uint)Math.Max(0, present.SwapBarrier),
        }, out var why);
        if (host is not null)
        {
            log.LogInformation("The render host is up: {Renderer}, {Windows}.", host.RendererName,
                request.Options.Headless ? "headless (outputs render offscreen)" : "windows on the assigned displays");
            return host;
        }
        if (request.Options.Headless && why.StartsWith("no display", StringComparison.Ordinal))
        {
            log.LogInformation("This headless node draws nothing: {Why}.", why);
            return null;
        }
        throw new InvalidOperationException(why);
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
        _sources = new EngineSources(engine.Services.Media, _scan, _timer, said: words => engine.Journal.Add("fault", words, ok: false));
        engine.BlendSolved += OnBlendSolved;
        if (engine.LastBlendSolve is { } solved)
            _stage.Offer(solved);
        Volatile.Write(ref _facts, new FactsSignal(engine.FactsChanged));
        Volatile.Write(ref _engine, engine);
        engine.Services.Media?.UseGpu(_platform?.Share);
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

    // ---- the packages' hooks (each in its package's RenderHost.<Word>.cs) ------------------------------------

    /// <summary>What each package adds to the frame's inputs (levels, passes, measured stages, modulation), in this order, after the
    /// host's own inputs and before the frame is drawn. Render thread; allocation-free once warm.</summary>
    private void ComposeModules(EngineCore engine, EngineSnapshot snap, ref FrameInputs inputs)
    {
        ComposeShow(engine, snap, ref inputs); ComposeOutput(engine, snap, ref inputs); ComposeDecode(engine, snap, ref inputs);
        ComposeVideoIo(engine, snap, ref inputs); ComposeFx(engine, snap, ref inputs); ComposeAudio(engine, snap, ref inputs);
        ComposeContent(engine, snap, ref inputs); ComposeLed(engine, snap, ref inputs); ComposeAlign(engine, snap, ref inputs);
        ComposeSite(engine, snap, ref inputs); ComposeWall(engine, snap, ref inputs);
    }

    /// <summary>Once the context exists (render thread): each package makes its GPU objects.</summary>
    private void ContextModules(RenderEngine renderer)
    {
        OnContextShow(renderer); OnContextOutput(renderer); OnContextDecode(renderer); OnContextVideoIo(renderer); OnContextFx(renderer);
        OnContextAudio(renderer); OnContextContent(renderer); OnContextLed(renderer); OnContextAlign(renderer); OnContextSite(renderer);
        OnContextWall(renderer);
    }

    /// <summary>When the context goes (render thread): each package lets its GPU objects go.</summary>
    private void ContextLostModules()
    {
        OnContextLostShow(); OnContextLostOutput(); OnContextLostDecode(); OnContextLostVideoIo(); OnContextLostFx(); OnContextLostAudio();
        OnContextLostContent(); OnContextLostLed(); OnContextLostAlign(); OnContextLostSite(); OnContextLostWall();
    }

    partial void ComposeShow(EngineCore engine, EngineSnapshot snap, ref FrameInputs inputs);
    partial void OnContextShow(RenderEngine renderer);
    partial void OnContextLostShow();

    partial void ComposeOutput(EngineCore engine, EngineSnapshot snap, ref FrameInputs inputs);
    partial void OnContextOutput(RenderEngine renderer);
    partial void OnContextLostOutput();

    partial void ComposeDecode(EngineCore engine, EngineSnapshot snap, ref FrameInputs inputs);
    partial void OnContextDecode(RenderEngine renderer);
    partial void OnContextLostDecode();

    partial void ComposeVideoIo(EngineCore engine, EngineSnapshot snap, ref FrameInputs inputs);
    partial void OnContextVideoIo(RenderEngine renderer);
    partial void OnContextLostVideoIo();

    partial void ComposeFx(EngineCore engine, EngineSnapshot snap, ref FrameInputs inputs);
    partial void OnContextFx(RenderEngine renderer);
    partial void OnContextLostFx();

    partial void ComposeAudio(EngineCore engine, EngineSnapshot snap, ref FrameInputs inputs);
    partial void OnContextAudio(RenderEngine renderer);
    partial void OnContextLostAudio();

    partial void ComposeContent(EngineCore engine, EngineSnapshot snap, ref FrameInputs inputs);
    partial void OnContextContent(RenderEngine renderer);
    partial void OnContextLostContent();

    partial void ComposeLed(EngineCore engine, EngineSnapshot snap, ref FrameInputs inputs);
    partial void OnContextLed(RenderEngine renderer);
    partial void OnContextLostLed();

    partial void ComposeAlign(EngineCore engine, EngineSnapshot snap, ref FrameInputs inputs);
    partial void OnContextAlign(RenderEngine renderer);
    partial void OnContextLostAlign();

    partial void ComposeSite(EngineCore engine, EngineSnapshot snap, ref FrameInputs inputs);
    partial void OnContextSite(RenderEngine renderer);
    partial void OnContextLostSite();

    partial void ComposeWall(EngineCore engine, EngineSnapshot snap, ref FrameInputs inputs);
    partial void OnContextWall(RenderEngine renderer);
    partial void OnContextLostWall();

    // ---- the render thread ------------------------------------------------------------------------------

    private void Main()
    {
        try
        {
            _platform = _options.Dxgi is { } dxgi
                ? GpuPlatforms.Create(_options.Platform, _options.Device ?? GlDeviceOptions.Default, dxgi)
                : GpuPlatforms.Create(_options.Platform, _options.Device ?? GlDeviceOptions.Default);
            _dxgiUp = _platform.Path == PresentPath.DxgiFlip;
            _context = _platform.Context;
            _renderer = new RenderEngine(_context, _options.Engine with { GpuTiming = _options.Engine.GpuTiming && _context.Device.HasTimerQuery });
            _renderer.WarmUp();
            ContextModules(_renderer);
            var d = _context.Device;
            _rendererName = d.Dialect == GlDialect.Es300
                ? string.Create(CultureInfo.InvariantCulture, $"OpenGL ES {d.GlVersion / 10}.{d.GlVersion % 10} {d.Renderer}")
                : string.Create(CultureInfo.InvariantCulture, $"OpenGL {d.GlVersion / 10}.{d.GlVersion % 10} {d.Renderer}");
            _windows = new OutputWindows(_platform, _renderer);
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
        var faults = 0;
        // A device other threads share (the DXGI path's, decoders on it): held around each frame and the work between frames, taken
        // within its bound (past it the frame draws on the device's own per-call ordering, counted), let go of before the pacing wait.
        var deviceLock = _platform?.DeviceLock;
        while (!_stopping)
        {
            var bytesBefore = GC.GetAllocatedBytesForCurrentThread();
            var start = MonoClock.Instance.NowNs;
            _ = deviceLock?.TryEnter(Core.Media.GpuDeviceLock.FrameBoundMs);
            try
            {
                try
                {
                    Frame(engine);
                    faults = 0;
                }
                catch (Exception ex) when (ex is GlException or InvalidOperationException or ArgumentException or IOException or ObjectDisposedException)
                {
                    // One bad frame is journalled and the next is drawn; a fault every frame for a while (a lost
                    // context) stops the loop, and the engine plays on with its outputs closed.
                    if (faults++ == 0)
                        engine.Journal.Add("fault", $"render frame: {ex.GetType().Name}: {ex.Message}", ok: false);
                    _context!.Device.InvalidateState();
                    if (faults >= FaultLimit)
                        throw;
                }
                var end = MonoClock.Instance.NowNs;
                Interlocked.Add(ref _framePathBytes, GC.GetAllocatedBytesForCurrentThread() - bytesBefore);
                var frames = Interlocked.Increment(ref _frames);
                lock (_frameMsGate)
                    _frameMs[(int)((frames - 1) % FrameRing)] = (end - start) / 1e6;
                if (Volatile.Read(ref _frameWaiterCount) > 0)
                    ReleaseFrameWaiters(frames);
                while (_work.TryDequeue(out var work))
                    work();
            }
            finally
            {
                deviceLock?.ExitAll();
            }
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
            WindowsChanged();

        _sources!.BeginFrame(snap, renderer.Frame + 1);
        _scan.BeginFrame(project, air.EffectiveBlackout);
        // Show time is a pure function of the vblank the frame is for: under the lock, the anchor's time of the pacer's next count
        // (the same on every node); otherwise the leader's now plus the time to the pacer's next vblank.
        long leaderNs;
        if (_lock.TryNextLeaderNs(windows.Pacer, out var lockedNs))
        {
            leaderNs = lockedNs + AirLatencyNs(project);
        }
        else
        {
            var next = windows.Pacer is { } pacing ? renderer.NextVblankNs(pacing, monoNow) : null;
            var lead = next is { } n ? Math.Max(0, n - monoNow) : 0;
            leaderNs = engine.LeaderNowNs + lead + AirLatencyNs(project);
        }
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
        ComposeModules(engine, snap, ref inputs);
        renderer.Render(in inputs);
        lock (_frameMsGate)
            _presentMs[(int)(renderer.Frame % FrameRing)] = renderer.LastPresentMs;
        _lock.Step(renderer, project, windows.Pacer, MonoClock.Instance.NowNs, engine.LeaderNowNs, engine.Services.ShowGate.Armed,
            air.EffectiveBlackout, air.EffectiveBlackout || !Playing(snap.Playback));
        // The frame's present is stamped once, at the same point of every frame: its outputs drawn (a scan frame on an
        // output with no window finished on the GPU first), before the previews and before any reader runs between
        // frames — so what previews cost never moves it, and no readback of the frame can come before it.
        _finishNs = -1;
        if (_scan.AnyHanded)
            _scan.AfterFrame(_presentOf);
        var finished = _finishNs >= 0 ? _finishNs : MonoClock.Instance.NowNs;
        _loopPacer.OnPresented(finished);
        _headless.OnPresented(finished);
        RenderPreviews(renderer, project, monoNow);
        if (UpdateHealth(renderer, windows))
            WindowsChanged();
    }

    /// <summary>Whether any playlist is playing (the lock's boundary: an output that fell out rejoins only when none is, or blacked out).</summary>
    private static bool Playing(Playback.PlaybackSnapshot playback)
    {
        foreach (var (_, p) in playback.Playlists)
            if (p.Clip.State == Core.Playback.TransportState.Playing)
                return true;
        return false;
    }

    /// <summary>When a scan frame was presented on an output: its window's swap, else (no window) the instant
    /// the GPU finished the frame's outputs — taken once a frame, before its previews are drawn.</summary>
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
        _headless.Sync(project);
        PlanLatency(project);
        if (_options.Windows)
        {
            RefreshDisplays();
            if (_windows!.Sync(project, _monitors))
                WindowsChanged();
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
        WindowsChanged();
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
    private void WindowsChanged()
    {
        _openWindows = _windows!.Open;
        Volatile.Read(ref _facts)?.Raise();
    }

    /// <summary>Writes each output's numbers for STATE; true when what STATE's light is made of moved (a window
    /// opened or closed, the first dropped frame, the first measured rate) so the engine republishes — the numbers
    /// themselves ride the next push, never a push of their own.</summary>
    private bool UpdateHealth(RenderEngine renderer, OutputWindows windows)
    {
        var moved = false;
        lock (_healthGate)
        {
            foreach (var h in _health)
            {
                var before = (h.Open, h.Dropped > 0, h.Fps > 0);
                if (windows.WindowOf(h.OutputId) is not null)
                {
                    var s = renderer.Stats(h.OutputId);
                    h.Open = true;
                    h.Fps = s.Fps;
                    h.FrameMsP95 = s.FrameMsP95;
                }
                else
                {
                    // Offscreen: the loop's cadence, measured against this output's own refresh target.
                    var pacer = _headless.Of(h.OutputId) ?? _loopPacer;
                    h.Open = false;
                    h.Fps = pacer.Fps;
                    h.FrameMsP95 = pacer.FrameMsP95;
                }
                // Dropped is the display side's count against the output's own refresh: the late presents of the last 10 s, once
                // they pass the 0.5 % budget (0 within it) — what lights the output amber.
                if (renderer.Present(h.OutputId) is { } present)
                {
                    h.Present = present;
                    h.HasPresent = true;
                    h.Dropped = present.LateOverBudget;
                }
                if (windows.DisplayOf(h.OutputId) is { } display)
                    h.Display = display;
                moved |= before != (h.Open, h.Dropped > 0, h.Fps > 0);
            }
            if (!_firstFrames && _dxgiUp)
                _firstFrames = FirstFramesUp(windows.Pacer, renderer.Frame);
        }
        return moved;
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
        var project = engine.Store.Project;
        var rate = _options.HeadlessRateHz > 0 ? _options.HeadlessRateHz : HeadlessPacing.LoopRateHz(project, RenderEngine.FrameRateOf(project.Settings.FrameRate));
        if (Math.Abs(_loopPacer.NominalHz - rate) > 1e-6)
            _loopPacer = new FramePacer(rate);
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
        // The GPU objects go under the shared device's lock as frames are drawn (taken within its bound; let go of before the device).
        var deviceLock = _platform?.DeviceLock;
        _ = deviceLock?.TryEnter(Core.Media.GpuDeviceLock.FrameBoundMs);
        try
        {
            // Every output leaves the lock before its window goes.
            _lock.Dispose();
            _windows?.Dispose();
            _sources?.Dispose();
            _timer?.Dispose();
            // The packages' GPU objects go before the renderer that made the context they live in.
            if (_renderer is not null)
                ContextLostModules();
            // The renderer first: its readbacks write into the views' rings until it is gone.
            _renderer?.Dispose();
            DisposePreviews();
            deviceLock?.ExitAll();
            if (_platform is not null)
                _platform.Dispose();
            else
                _context?.Dispose();
        }
        catch (Exception ex) when (ex is GlException or InvalidOperationException or ObjectDisposedException)
        {
            _startFailure ??= $"the render context did not close cleanly: {ex.Message}";
        }
        finally
        {
            // A teardown that stopped part way still lets the sharers in (none is left holding on a thread that ends).
            deviceLock?.ExitAll();
        }
        lock (_waiterGate)
        {
            foreach (var (_, done) in _frameWaiters)
                done.TrySetCanceled(CancellationToken.None);
            _frameWaiters.Clear();
        }
        lock (_healthGate)
            _health = [];
        _openWindows = 0;
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
            engine.Services.Media?.UseGpu(null);
        }
        _wake.Set();
        await _exited.Task.ConfigureAwait(false);
        // An orderly stop of a DXGI start is no crash: the fuse it armed goes, whether or not its outputs reached their first frames.
        if (_dxgiUp && !_fuseCleared && _options.Fuse is { } fuse)
        {
            _fuseCleared = true;
            fuse.Clear();
        }
        if (_stage is not null)
            await _stage.DisposeAsync().ConfigureAwait(false);
        // The render thread has exited: nothing raises the signal any more.
        Volatile.Read(ref _facts)?.Dispose();
        _wake.Dispose();
        _ready.Dispose();
    }
}
