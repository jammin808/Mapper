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
/// flip-model platform on Windows stands behind the same seam next.
///
/// Threading: made, used and disposed on the render thread.</summary>
public interface IGpuPlatform : IDisposable
{
    /// <summary>"gl" (GLFW windows sharing one context), "gl-surfaceless" (EGL with no window system: offscreen only).</summary>
    string Name { get; }

    /// <summary>The windowing API under the context: "glx", "egl", "egl-surfaceless", "wgl", "cocoa".</summary>
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
}

/// <summary>What a node asks of its platform: the present path ("auto" | "gl" | "dxgi") and the GL dialect ("auto" | "core330" |
/// "es300"), from <c>NodeSettings.Present</c>; and whether the node runs with no window system (offscreen only).</summary>
public sealed record PlatformRequest(string Path = "auto", string Dialect = "auto", bool Surfaceless = false)
{
    public static readonly PlatformRequest Default = new();
}

/// <summary>Makes the platform a node asks for.</summary>
public static class GpuPlatforms
{
    /// <summary>The GL platform of the asked dialect (auto is desktop 3.3 core). ES on Linux comes up on EGL, else GLX with words
    /// (<see cref="IGpuPlatform.Words"/>); "dxgi" is not a path this build presents on, so it is GL with words. Throws with
    /// words when no context comes up at all.</summary>
    public static IGpuPlatform Create(PlatformRequest request, GlDeviceOptions device)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(device);
        var words = new List<string>(2);
        if (request.Path.Trim().Equals("dxgi", StringComparison.OrdinalIgnoreCase))
            words.Add("the DXGI path is not in this build: outputs present through GL");
        var dialect = GlDialects.Parse(request.Dialect) ?? GlDialect.Core330;
        var options = device with { Dialect = dialect };
        var context = request.Surfaceless ? RenderContext.CreateSurfaceless(options) : RenderContext.CreateHidden(options);
        if (context.Words.Length > 0)
            words.Add(context.Words);
        return new GlPlatform(context, string.Join("; ", words));
    }
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
