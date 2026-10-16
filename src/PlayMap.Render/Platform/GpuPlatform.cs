using System.Globalization;
using PlayMap.Core.Media;
using PlayMap.Render.Gpu;

namespace PlayMap.Render.Platform;

/// <summary>Where a presenter goes: a monitor (null = a free-floating window at <see cref="X"/>, <see cref="Y"/>), borderless over
/// the whole monitor or a movable window of the given size, and whether its presents wait for the vertical blank.</summary>
public sealed record OutputPresenterOptions(string Title, MonitorInfo? Monitor, bool Borderless, int Width, int Height, int X = 64,
    int Y = 64, bool VSync = true);

/// <summary>The render platform a node starts on: the primary context the renderer draws in, the displays, the presenters outputs
/// go to and the device decoders may share. The GL platform (GLFW windows over GLX, or EGL for ES) is the one this round; the DXGI
/// flip-model platform on Windows (<c>Windows.AnglePlatform</c>, experimental) stands behind the same seam.
///
/// Threading: made, used and disposed on the render thread.</summary>
public interface IGpuPlatform : IDisposable
{
    /// <summary>"gl" (GLFW windows sharing one context), "gl-surfaceless" (EGL with no window system: offscreen only), "dxgi" (the
    /// flip model on a Direct3D 11 device through ANGLE).</summary>
    string Name { get; }

    /// <summary>The windowing API under the context: "glx", "egl", "egl-surfaceless", "wgl", "cocoa", "angle-d3d11".</summary>
    string Api { get; }

    /// <summary>Why the platform is not what was asked ("ES through GLX: EGL refused …"); "" when it is.</summary>
    string Words { get; }

    RenderContext Context { get; }

    /// <summary>The path this platform's presenters take.</summary>
    PresentPath Path { get; }

    IReadOnlyList<MonitorInfo> Monitors();

    /// <summary>A presenter on a display; throws with words when the platform cannot open one there.</summary>
    IOutputPresenter CreatePresenter(OutputPresenterOptions options);

    void PumpEvents();

    /// <summary>The renderer's device as decoders may share it.</summary>
    IGpuShare Share { get; }

    /// <summary>The adapter the platform renders on, in words ("Direct3D 11 + ANGLE · NVIDIA RTX A4000"); "" when the GL renderer
    /// string says it.</summary>
    string Adapter => "";
}

/// <summary>What a node asks of its platform: the present path ("auto" | "gl" | "dxgi") and the GL dialect ("auto" | "core330" |
/// "es300"), from <c>NodeSettings.Present</c>; whether the node runs with no window system (offscreen only); whether the DXGI path
/// renders on WARP (the software rasteriser, for CI); and words that already explain the choice (a blown fuse), which lead the
/// platform's own.</summary>
public sealed record PlatformRequest(string Path = "auto", string Dialect = "auto", bool Surfaceless = false)
{
    public static readonly PlatformRequest Default = new();

    public bool Warp { get; init; }

    public string Words { get; init; } = "";
}

/// <summary>Makes the platform a node asks for.</summary>
public static class GpuPlatforms
{
    /// <summary>The platform of the asked path and dialect: "dxgi" is the flip-model platform on Windows (experimental, explicit only),
    /// GL with words where it is not there or did not start (the words name the stage it stopped at); "auto" and "gl" are the GL
    /// platform of the asked dialect (auto is desktop 3.3 core) — AUTO is OpenGL on every platform this round. ES on Linux comes up on
    /// EGL, else GLX with words (<see cref="IGpuPlatform.Words"/>); a path or dialect word it does not know is the platform's choice,
    /// with words. Throws with words when no GL context comes up at all.</summary>
    public static IGpuPlatform Create(PlatformRequest request, GlDeviceOptions device) =>
        Create(request, device, ThisMachinesDxgi(request));

    /// <summary>As <see cref="Create(PlatformRequest, GlDeviceOptions)"/>, with the DXGI platform's maker given (null: this machine
    /// has none): tests hand in one that fails at a chosen stage.</summary>
    public static IGpuPlatform Create(PlatformRequest request, GlDeviceOptions device, Func<GlDeviceOptions, IGpuPlatform>? dxgi)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(device);
        var words = new List<string>(4);
        if (request.Words.Length > 0)
            words.Add(request.Words);
        var path = request.Path.Trim();
        if (path.Equals("dxgi", StringComparison.OrdinalIgnoreCase))
        {
            if (dxgi is null)
            {
                words.Add("the DXGI path is Windows only: outputs present through OpenGL");
            }
            else
            {
                try
                {
                    return dxgi(device);
                }
                catch (GpuPlatformStartException ex)
                {
                    words.Add($"the DXGI path stopped at {GpuPlatformStartException.Word(ex.Stage)}: {ex.Message}; outputs present through OpenGL");
                }
                catch (Exception ex) when (ex is InvalidOperationException or DllNotFoundException or EntryPointNotFoundException
                    or PlatformNotSupportedException or BadImageFormatException)
                {
                    words.Add($"the DXGI path did not start: {ex.Message}; outputs present through OpenGL");
                }
            }
        }
        else if (!IsAuto(path) && !path.Equals("gl", StringComparison.OrdinalIgnoreCase) && !path.Equals("dxgi", StringComparison.OrdinalIgnoreCase))
        {
            words.Add($"present path '{request.Path}' is none of auto, gl, dxgi: outputs present through GL");
        }
        var asked = GlDialects.Parse(request.Dialect);
        if (asked is null && !IsAuto(request.Dialect))
            words.Add($"GL dialect '{request.Dialect}' is none of auto, core330, es300: the context is desktop 3.3 core");
        var dialect = asked ?? GlDialect.Core330;
        var options = device with { Dialect = dialect };
        var context = request.Surfaceless ? RenderContext.CreateSurfaceless(options) : RenderContext.CreateHidden(options);
        if (context.Words.Length > 0)
            words.Add(context.Words);
        return new GlPlatform(context, string.Join("; ", words));
    }

    /// <summary>This machine's DXGI platform maker: the flip-model platform on Windows, none elsewhere.</summary>
    private static Func<GlDeviceOptions, IGpuPlatform>? ThisMachinesDxgi(PlatformRequest request)
    {
        if (!OperatingSystem.IsWindows())
            return null;
        return AngleMaker(request.Warp);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static Func<GlDeviceOptions, IGpuPlatform> AngleMaker(bool warp) => o => Windows.AnglePlatform.Create(o, warp);

    /// <summary>A settings word that leaves the choice to the platform: empty, or "auto".</summary>
    private static bool IsAuto(string? word) => string.IsNullOrWhiteSpace(word) || word.Trim().Equals("auto", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Today's platform: one hidden GL context (GLFW over GLX, or EGL) and an <see cref="OutputWindow"/> per output sharing
/// its objects; or an EGL context with no window system, which renders every output offscreen and opens no windows.</summary>
public sealed class GlPlatform : IGpuPlatform
{
    private readonly GlShare _share;
    private bool _disposed;

    public GlPlatform(RenderContext context, string words = "")
    {
        ArgumentNullException.ThrowIfNull(context);
        Context = context;
        Words = words;
        _share = new GlShare(context);
    }

    public string Name => Context.HasWindows ? "gl" : "gl-surfaceless";

    public string Api => Context.Api;

    public string Words { get; }

    public RenderContext Context { get; }

    public PresentPath Path => Context.HasWindows ? PresentPath.GlWindow : PresentPath.Offscreen;

    public IGpuShare Share => _share;

    public IReadOnlyList<MonitorInfo> Monitors() => Context.Monitors();

    public IOutputPresenter CreatePresenter(OutputPresenterOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!Context.HasWindows)
            throw new PlatformNotSupportedException("this node renders with no window system (EGL surfaceless): outputs stay offscreen");
        var o = new OutputWindowOptions(options.Title, options.Monitor?.Index ?? -1, options.Borderless, options.VSync, options.Width,
            options.Height, options.X, options.Y);
        return OutputWindow.Create(Context, o);
    }

    public void PumpEvents() => Context.PumpEvents();

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Context.Dispose();
    }

    /// <summary>The GL device as decoders see it: OpenGL, no D3D11 device, no import proven this round (decoders copy into the
    /// renderer's textures); one render thread, so the lock is the thread itself.</summary>
    private sealed class GlShare(RenderContext context) : IGpuShare
    {
        private static long _epochs;
        private readonly Dictionary<string, string> _failures = new(StringComparer.Ordinal);
        private readonly Lock _gate = new();

        public GpuApi Api => GpuApi.OpenGl;

        public string Describe { get; } = string.Create(CultureInfo.InvariantCulture,
            $"OpenGL {context.Device.GlVersion / 10}.{context.Device.GlVersion % 10} {GlDialects.Word(context.Dialect)} {context.Device.Renderer} ({context.Api})");

        public nint D3D11Device => 0;

        public long AdapterLuid => 0;

        public string? DrmRenderNode => null;

        public long DeviceEpoch { get; } = Interlocked.Increment(ref _epochs);

        public bool DirectProven => false;

        public bool CanImport(GpuSurfaceFormat format, uint fourcc, ulong modifier) => false;

        public void Enter()
        {
            // One render thread owns the context: there is nothing to enter.
        }

        public void Exit()
        {
            // As Enter.
        }

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
