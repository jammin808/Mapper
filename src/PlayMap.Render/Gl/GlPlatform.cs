using System.Globalization;
using PlayMap.Core.Gpu;
using PlayMap.Core.Media;
using PlayMap.Core.Presentation;

namespace PlayMap.Render.Gl;

/// <summary>A GL platform (GLFW windows over GLX or WGL, EGL, or ANGLE's ES on Direct3D 11): its primary context, made current on
/// the render thread, which the renderer draws in through its <see cref="RenderContext.Device"/>.</summary>
public interface IGlPlatform : IGpuPlatform
{
    RenderContext Context { get; }
}

/// <summary>The GL context of a platform, for the frame code that draws through <see cref="GlDevice"/>.</summary>
public static class GlPlatformContext
{
    extension(IGpuPlatform platform)
    {
        /// <summary>The platform's GL context; a platform that is not a GL one has none, and is refused.</summary>
        public RenderContext Context =>
            platform is IGlPlatform gl ? gl.Context : throw new NotSupportedException($"the {platform.Name} platform has no GL context");
    }
}

/// <summary>Today's platform: one hidden GL context (GLFW over GLX, or EGL) and an <see cref="OutputWindow"/> per output sharing
/// its objects; or an EGL context with no window system, which renders every output offscreen and opens no windows.</summary>
public sealed class GlPlatform : IGlPlatform
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

    /// <summary>The renderer draws through <see cref="Context"/>'s GL device on this platform; it offers no
    /// <see cref="IGpuDevice"/>.</summary>
    public IGpuDevice Device => throw new NotSupportedException("the GL platform offers no IGpuDevice: the renderer draws through its GL context");

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
    /// renderer's textures); nothing but the render thread draws on the context, so there is no lock to take.</summary>
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

        public void Forget(string sourceKey)
        {
            lock (_gate)
                _failures.Remove(sourceKey);
        }
    }
}
