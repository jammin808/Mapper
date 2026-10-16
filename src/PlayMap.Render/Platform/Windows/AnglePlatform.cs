using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using PlayMap.Core.Media;
using PlayMap.Render.Gpu;

namespace PlayMap.Render.Platform.Windows;

/// <summary>ANGLE's EGL display and ES 3.0 context on our Direct3D 11 device, current with no surface: what the renderer draws in.
/// An output window makes its own back-buffer surface current to present, then this context's surfaceless state again.</summary>
[SupportedOSPlatform("windows")]
internal sealed unsafe class AngleContext : ISurfacelessContext
{
    private readonly AngleEgl _egl;
    private nint _eglDevice;
    private nint _display;
    private nint _context;

    private AngleContext(AngleEgl egl) => _egl = egl;

    public AngleEgl Egl => _egl;

    public nint Display => _display;

    public nint Context => _context;

    public nint Config { get; private set; }

    /// <summary>Route A (an EGLImage of the back buffer) is offered; route B (a pbuffer on it) is always there.</summary>
    public bool ImageRoute { get; private set; }

    /// <summary>The three extension lists checked in order, the display verified to sit on <paramref name="d3dDevice"/>, the context
    /// made current. Throws <see cref="GpuPlatformStartException"/> at Extensions or Context with words.</summary>
    public static AngleContext Create(AngleEgl egl, nint d3dDevice, bool debug)
    {
        var c = new AngleContext(egl);
        try
        {
            c.Open(d3dDevice, debug);
            return c;
        }
        catch
        {
            c.Dispose();
            throw;
        }
    }

    private void Open(nint d3dDevice, bool debug)
    {
        if (AngleRequirements.Missing("client extensions", AngleRequirements.Client, _egl.Query(0, AngleEgl.Extensions)) is { } client)
            throw new GpuPlatformStartException(DxgiStartStage.Extensions, client);
        if (_egl.CreateDeviceAngle is null || _egl.GetPlatformDisplayExt is null || _egl.QueryDisplayAttribExt is null || _egl.QueryDeviceAttribExt is null)
            throw new GpuPlatformStartException(DxgiStartStage.Extensions, "ANGLE lists the device extensions but answers no entry point for them");
        _eglDevice = _egl.CreateDeviceAngle(AngleEgl.D3D11DeviceAngle, d3dDevice, null);
        if (_eglDevice == 0)
            throw new GpuPlatformStartException(DxgiStartStage.Context, $"ANGLE refused our Direct3D 11 device ({_egl.ErrorWords()})");
        if (AngleRequirements.Missing("device", AngleRequirements.Device, _egl.DeviceString(_eglDevice, AngleEgl.Extensions)) is { } device)
            throw new GpuPlatformStartException(DxgiStartStage.Extensions, device);
        _display = _egl.GetPlatformDisplayExt(AngleEgl.PlatformDeviceExt, _eglDevice, null);
        int major = 0, minor = 0;
        if (_display == 0 || _egl.Initialize(_display, &major, &minor) == 0)
        {
            _display = 0;
            throw new GpuPlatformStartException(DxgiStartStage.Context, $"ANGLE made no EGL display on our device ({_egl.ErrorWords()})");
        }
        var displayExtensions = _egl.Query(_display, AngleEgl.Extensions);
        if (AngleRequirements.Missing("display", AngleRequirements.Display, displayExtensions) is { } display)
            throw new GpuPlatformStartException(DxgiStartStage.Extensions, display);
        ImageRoute = AngleRequirements.HasImageRoute(displayExtensions);
        nint queried = 0, d3d = 0;
        if (_egl.QueryDisplayAttribExt(_display, AngleEgl.DeviceExt, &queried) == 0
            || _egl.QueryDeviceAttribExt(queried, AngleEgl.D3D11DeviceAngle, &d3d) == 0 || d3d != d3dDevice)
            throw new GpuPlatformStartException(DxgiStartStage.Context, "ANGLE made its own device: the display is not on ours");
        if (_egl.BindApi(AngleEgl.OpenGlEsApi) == 0)
            throw new GpuPlatformStartException(DxgiStartStage.Context, $"ANGLE binds no OpenGL ES ({_egl.ErrorWords()})");
        int* attribs = stackalloc int[]
        {
            AngleEgl.RenderableType, AngleEgl.OpenGlEs3Bit, AngleEgl.SurfaceType, AngleEgl.PbufferBit,
            AngleEgl.RedSize, 8, AngleEgl.GreenSize, 8, AngleEgl.BlueSize, 8, AngleEgl.AlphaSize, 8, AngleEgl.None,
        };
        nint config = 0;
        int count = 0;
        if (_egl.ChooseConfig(_display, attribs, &config, 1, &count) == 0 || count < 1)
            throw new GpuPlatformStartException(DxgiStartStage.Context, $"ANGLE offers no 8-bit ES 3 pbuffer config ({_egl.ErrorWords()})");
        Config = config;
        int* context = stackalloc int[] { AngleEgl.ContextMajorVersion, 3, AngleEgl.ContextMinorVersion, 0, 0x31B0, debug ? 1 : 0, AngleEgl.None };
        _context = _egl.CreateContext(_display, config, 0, context);
        if (_context == 0)
            throw new GpuPlatformStartException(DxgiStartStage.Context, $"ANGLE made no ES 3.0 context ({_egl.ErrorWords()})");
        if (_egl.MakeCurrent(_display, 0, 0, _context) == 0)
            throw new GpuPlatformStartException(DxgiStartStage.Context, $"ANGLE could not make its context current with no surface ({_egl.ErrorWords()})");
    }

    public nint Proc(string name) => _egl.Proc(name);

    public void MakeCurrent()
    {
        if (_egl.MakeCurrent(_display, 0, 0, _context) == 0)
            throw new InvalidOperationException($"the ANGLE context could not be made current ({_egl.ErrorWords()})");
    }

    public void Dispose()
    {
        // Teardown: each call's failure leaves nothing more to undo, so the results are not acted on.
        if (_display != 0)
        {
            _ = _egl.MakeCurrent(_display, 0, 0, 0);
            if (_context != 0)
                _ = _egl.DestroyContext(_display, _context);
            _ = _egl.Terminate(_display);
        }
        if (_eglDevice != 0 && _egl.ReleaseDeviceAngle is not null)
            _ = _egl.ReleaseDeviceAngle(_eglDevice);
        _ = _egl.ReleaseThread is null ? 0 : _egl.ReleaseThread();
        _context = _display = _eglDevice = 0;
        _egl.Dispose();
    }
}

/// <summary>The DXGI flip-model platform (Windows): a Direct3D 11 device on the adapter that drives the primary display (or WARP),
/// ANGLE's ES 3.0 context on that device for the renderer, and one waitable FLIP_DISCARD swap chain per display output
/// (<see cref="DxgiOutputWindow"/>). The device is handed to decoders and Spout as <see cref="IGpuShare"/> (<see cref="GpuApi.D3D11"/>).
/// Experimental this round: it starts only on an explicit PRESENT PATH DXGI. Made, used and disposed on the render thread.</summary>
[SupportedOSPlatform("windows")]
public sealed class AnglePlatform : IGpuPlatform
{
    private readonly D3D11Adapter _adapter;
    private readonly AngleContext _angle;
    private readonly D3D11Share _share;
    private readonly GpuDeviceLock _deviceLock = new();
    private readonly DwmNudge _nudge = new();
    private bool _disposed;

    private AnglePlatform(D3D11Adapter adapter, AngleContext angle, RenderContext context)
    {
        _adapter = adapter;
        _angle = angle;
        Context = context;
        _share = new D3D11Share(adapter, angle, context, _deviceLock);
    }

    /// <summary>Brings the platform up stage by stage (<see cref="DxgiStartStage"/>); throws <see cref="GpuPlatformStartException"/>
    /// naming the stage. <paramref name="warp"/> renders on WARP (the CI runner, which has no GPU).</summary>
    public static AnglePlatform Create(GlDeviceOptions options, bool warp = false)
    {
        ArgumentNullException.ThrowIfNull(options);
        var monitors = DisplayList.Monitors();
        var primary = monitors.Count > 0 ? monitors[0].Name : null;
        var adapter = D3D11Adapter.Create(primary, warp);
        AngleEgl? egl = null;
        AngleContext? angle = null;
        try
        {
            egl = AngleEgl.Load();
            angle = AngleContext.Create(egl, adapter.Device, options.DebugOutput);
            egl = null;
            RenderContext? context = null;
            try
            {
                context = RenderContext.Adopt(angle, angle.Proc, options with { Dialect = GlDialect.Es300 }, "angle-d3d11");
                // Every program translated here, inside the start: a shader ANGLE refuses is this stage's words and a GL start,
                // never a render host that fails later.
                foreach (var name in Shaders.ShaderLibrary.Names)
                    context.Device.Program(name);
            }
            catch (Exception ex) when (ex is GlException or InvalidOperationException or NotSupportedException or ArgumentException)
            {
                if (context is not null)
                {
                    context.Dispose();
                    angle = null;
                }
                throw new GpuPlatformStartException(DxgiStartStage.Programs, $"ANGLE's context gave no usable GL device: {ex.Message}");
            }
            return new AnglePlatform(adapter, angle, context);
        }
        catch
        {
            angle?.Dispose();
            egl?.Dispose();
            adapter.Dispose();
            throw;
        }
    }

    public string Name => "dxgi";

    public string Api => "angle-d3d11";

    public string Words => "";

    public RenderContext Context { get; }

    public PresentPath Path => PresentPath.DxgiFlip;

    public IGpuShare Share => _share;

    /// <summary>The device's lock: decoders (FFmpeg's lock callbacks) and texture sharers take it around their immediate-context work;
    /// the render loop holds it around each frame and every window lets go of it while it waits on its swap chain.</summary>
    public GpuDeviceLock DeviceLock => _deviceLock;

    /// <summary>"Direct3D 11 + ANGLE · NVIDIA RTX A4000".</summary>
    public string Adapter => "Direct3D 11 + ANGLE · " + _adapter.Name;

    public bool Warp => _adapter.Warp;

    public IReadOnlyList<MonitorInfo> Monitors() => DisplayList.Monitors();

    public IOutputPresenter CreatePresenter(OutputPresenterOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return DxgiOutputWindow.Create(_adapter, _angle, Context, options, _nudge, _deviceLock);
    }

    /// <summary>Messages are pumped by each window's frame wait and here, between frames; never blocks.</summary>
    public void PumpEvents() => DxgiOutputWindow.PumpMessages();

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _nudge.Dispose();
        // The GL device goes first (its context current), then ANGLE's context and display, then the D3D11 device.
        Context.Dispose();
        _adapter.Dispose();
    }

    /// <summary>The device as decoders see it: Direct3D 11 with its LUID, the device ANGLE's display was made on (so the importer's
    /// plane images of a decoder's texture are legal), and its lock. ANGLE records the renderer's work onto the device's immediate
    /// context; a sharer — FFmpeg's D3D11VA decoder through its lock callbacks, a texture sharer on this device — issues
    /// immediate-context work only inside Enter/Exit, which the render loop holds around each frame (<see cref="GpuDeviceLock"/>).
    /// Multithread protection is the floor under it: each call is ordered even in a frame drawn past the lock's bound. Decoders
    /// go on it only when it was made with video support and that protection (<see cref="ZeroCopyWords"/>). No proof of direct
    /// sampling runs this round, so <see cref="DirectProven"/> is false: the direct rung is only ever forced.</summary>
    private sealed class D3D11Share(D3D11Adapter adapter, AngleContext angle, RenderContext context, GpuDeviceLock deviceLock) : IGpuShare, IAngleDisplay
    {
        private static long _epochs;
        private readonly Dictionary<string, string> _failures = new(StringComparer.Ordinal);
        private readonly Lock _gate = new();

        public GpuApi Api => GpuApi.D3D11;

        public string Describe { get; } = string.Create(CultureInfo.InvariantCulture,
            $"Direct3D 11 + ANGLE {context.Device.Renderer} on {adapter.Name}");

        public nint D3D11Device => adapter.Device;

        public long AdapterLuid => adapter.Luid;

        public string? DrmRenderNode => null;

        public long DeviceEpoch { get; } = Interlocked.Increment(ref _epochs);

        public bool DirectProven => false;

        public bool CanImport(GpuSurfaceFormat format, uint fourcc, ulong modifier) => false;

        public string ZeroCopyWords { get; } =
            !adapter.VideoSupport ? $"the renderer's Direct3D 11 device on {adapter.Name} has no video support: decoders copy frames back"
            : !adapter.MultithreadProtected ? "the renderer's Direct3D 11 device is not multithread-protected: decoders copy frames back"
            : "";

        public AngleEgl Egl => angle.Egl;

        public nint Display => angle.Display;

        public void Enter() => deviceLock.Enter();

        public void Exit() => deviceLock.Exit();

        public void ReportImportFailure(string sourceKey, string words)
        {
            lock (_gate)
                _failures[sourceKey] = words;
        }

        public bool ImportFailed(string sourceKey, out string words)
        {
            lock (_gate)
            {
                if (_failures.TryGetValue(sourceKey, out var w))
                {
                    words = w;
                    return true;
                }
            }
            words = "";
            return false;
        }
    }
}

/// <summary>This machine's displays as Windows lists them (EnumDisplayMonitors), with each one's GDI name ("\\.\DISPLAY2") and its
/// active refresh from the display configuration's path (the rational refreshRate, rounded for the list).</summary>
[SupportedOSPlatform("windows")]
internal static unsafe class DisplayList
{
    [ThreadStatic]
    private static List<MonitorInfo>? _collect;

    public static IReadOnlyList<MonitorInfo> Monitors()
    {
        var list = new List<MonitorInfo>();
        _collect = list;
        try
        {
            _ = DxgiNative.EnumDisplayMonitors(0, 0, &OnMonitor, 0);
        }
        finally
        {
            _collect = null;
        }
        for (var i = 0; i < list.Count; i++)
            list[i] = list[i] with { Index = i };
        return list;
    }

    [UnmanagedCallersOnly]
    private static int OnMonitor(nint monitor, nint hdc, nint rect, nint data)
    {
        var list = _collect;
        if (list is null)
            return 0;
        var info = new MonitorInfoEx((uint)sizeof(MonitorInfoEx), 0, 0, 0, 0, 0);
        if (!DxgiNative.GetMonitorInfo(monitor, &info))
            return 1;
        var name = DxgiNative.Text(&info, MonitorInfoEx.DeviceOffset, 32);
        var (num, den) = Refresh(name);
        var hz = den > 0 ? (int)Math.Round(num / (double)den) : 0;
        var m = new MonitorInfo(list.Count, name, info.Left, info.Top, info.Right - info.Left, info.Bottom - info.Top, hz);
        // The primary display first (MONITORINFOF_PRIMARY): the adapter that drives it is the one the device goes on.
        if ((info.Flags & 1) != 0)
            list.Insert(0, m);
        else
            list.Add(m);
        return 1;
    }

    /// <summary>The active refresh of the display path whose source is <paramref name="gdiName"/>, as a rational (0/0 when none).</summary>
    public static (uint Numerator, uint Denominator) Refresh(string gdiName)
    {
        uint paths = 0, modes = 0;
        if (DxgiNative.GetDisplayConfigBufferSizes(DxgiNative.QdcOnlyActivePaths, &paths, &modes) != 0 || paths == 0)
            return (0, 0);
        var pathInfo = new DisplayConfigPathInfo[paths];
        var modeInfo = new DisplayConfigModeInfo[Math.Max(1, modes)];
        fixed (DisplayConfigPathInfo* p = pathInfo)
        fixed (DisplayConfigModeInfo* m = modeInfo)
        {
            if (DxgiNative.QueryDisplayConfig(DxgiNative.QdcOnlyActivePaths, &paths, p, &modes, m, 0) != 0)
                return (0, 0);
            for (var i = 0; i < paths; i++)
            {
                var request = new DisplayConfigSourceDeviceName(DxgiNative.DisplayConfigGetSourceName, (uint)sizeof(DisplayConfigSourceDeviceName),
                    p[i].SourceAdapterLow, p[i].SourceAdapterHigh, p[i].SourceId);
                if (DxgiNative.DisplayConfigGetDeviceInfo(&request) != 0)
                    continue;
                if (!string.Equals(DxgiNative.Text(&request, DisplayConfigSourceDeviceName.GdiNameOffset, 32), gdiName, StringComparison.OrdinalIgnoreCase))
                    continue;
                // The target mode's measured vSyncFreq when the path names one; the path's nominal refreshRate otherwise.
                var idx = p[i].TargetModeInfoIdx;
                if (idx < modes && m[idx].InfoType == DxgiNative.ModeInfoTypeTarget && m[idx].VSyncDenominator > 0)
                    return (m[idx].VSyncNumerator, m[idx].VSyncDenominator);
                return (p[i].RefreshNumerator, p[i].RefreshDenominator);
            }
        }
        return (0, 0);
    }
}

/// <summary>The compositor nudge (Microsoft notes DwmFlush may be needed before frame statistics update on systems with the
/// hardware flip queue): DwmFlush blocks its caller until the next composition, so it is called only on this helper thread, at most
/// once a second and only when a window asked, never on the render thread that drives every output.</summary>
[SupportedOSPlatform("windows")]
internal sealed class DwmNudge : IDisposable
{
    private readonly AutoResetEvent _asked = new(false);
    private readonly ManualResetEventSlim _stop = new(false);
    private readonly Thread _thread;
    private long _calls;

    public DwmNudge()
    {
        _thread = new Thread(Loop) { IsBackground = true, Name = "dwm nudge" };
        _thread.Start();
    }

    /// <summary>The thread DwmFlush runs on (never the caller of <see cref="Ask"/>).</summary>
    public int ThreadId => _thread.ManagedThreadId;

    public long Calls => Interlocked.Read(ref _calls);

    /// <summary>Any thread, never blocks: the helper flushes once at its next turn.</summary>
    public void Ask() => _asked.Set();

    private void Loop()
    {
        var handles = new WaitHandle[] { _stop.WaitHandle, _asked };
        while (WaitHandle.WaitAny(handles) == 1)
        {
            _ = DxgiNative.DwmFlush();
            Interlocked.Increment(ref _calls);
            // At most once a second, however often it is asked.
            if (_stop.Wait(1000))
                return;
        }
    }

    public void Dispose()
    {
        _stop.Set();
        // Joined to the end: the thread's handles are disposed only once it can no longer wait on them.
        _thread.Join();
        _asked.Dispose();
        _stop.Dispose();
    }
}
