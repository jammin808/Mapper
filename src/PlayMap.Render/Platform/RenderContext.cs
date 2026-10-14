using PlayMap.Render.Gpu;
using Silk.NET.GLFW;
using Silk.NET.Maths;
using Silk.NET.OpenGL;
using Silk.NET.Windowing;

namespace PlayMap.Render.Platform;

/// <summary>The renderer's primary GL context: a hidden window whose context does every offscreen pass (sources, canvases, the
/// projector pass, the output stage, previews) and that output windows share objects with — or, with no window system, an EGL
/// context with no surface at all (it renders the same, and opens no windows). Headless engines and tests use it alone.
///
/// The context speaks one dialect (<see cref="Dialect"/>): desktop 3.3 core, or ES 3.0 — through GLFW on EGL where the platform
/// has it, else through GLX (the words say which, <see cref="Api"/>).
///
/// Threading: make it on the thread that will render (the engine's render thread, which on macOS must be the process's main
/// thread); every call afterwards — rendering, output windows, disposal — happens there.</summary>
public sealed class RenderContext : IDisposable
{
    /// <summary>GLFW's window hints are process-wide: a context made on EGL sets the creation API, makes its window and puts the
    /// native API back, all under this gate, so a window made at the same time on another thread is never made on the wrong one.</summary>
    internal static readonly Lock HintGate = new();

    private readonly IWindow? _window;
    private readonly IDisposable? _surfaceless;
    private bool _disposed;

    private RenderContext(IWindow? window, IDisposable? surfaceless, GL gl, GlDevice device, string api, bool eglWindows, string words)
    {
        _window = window;
        _surfaceless = surfaceless;
        Gl = gl;
        Device = device;
        Api = api;
        EglWindows = eglWindows;
        Words = words;
    }

    /// <summary>The window behind the context (hidden), for monitor queries and context sharing; null for a surfaceless context.</summary>
    internal IWindow? Window => _window;

    /// <summary>Output windows of this context are made on EGL too (shared contexts must come from one API).</summary>
    internal bool EglWindows { get; }

    internal GL Gl { get; }

    public GlDevice Device { get; }

    /// <summary>The dialect the context speaks (the device read it from GL_VERSION).</summary>
    public GlDialect Dialect => Device.Dialect;

    /// <summary>How the context was made: "glx", "egl" (a GLFW window on EGL), "egl-surfaceless", "wgl", "cocoa".</summary>
    public string Api { get; }

    /// <summary>Why the context is what it is when that is not what was asked (an EGL request that came up on GLX); "" otherwise.</summary>
    public string Words { get; }

    /// <summary>Whether output windows can be made on this context (a surfaceless context has no window system).</summary>
    public bool HasWindows => _window is not null;

    /// <summary>Makes a hidden context of <see cref="GlDeviceOptions.Dialect"/> through the platform's windowing: OpenGL 3.3 core
    /// (forward-compatible; debug-flagged when debug output is asked), or OpenGL ES 3.0 — on EGL when <paramref name="preferEgl"/>
    /// and the platform has it, else the native API (GLX), with the words that say so.</summary>
    public static RenderContext CreateHidden(GlDeviceOptions? options = null) => CreateHidden(options, preferEgl: true);

    /// <inheritdoc cref="CreateHidden(GlDeviceOptions?)"/>
    public static RenderContext CreateHidden(GlDeviceOptions? options, bool preferEgl)
    {
        options ??= GlDeviceOptions.Default;
        var es = options.Dialect == GlDialect.Es300;
        var flags = es
            ? (options.DebugOutput ? ContextFlags.Debug : ContextFlags.Default)
            : ContextFlags.ForwardCompatible | (options.DebugOutput ? ContextFlags.Debug : ContextFlags.Default);
        var wo = WindowOptions.Default with
        {
            IsVisible = false,
            Size = new Vector2D<int>(16, 16),
            Title = "PlayMap render",
            API = es
                ? new GraphicsAPI(ContextAPI.OpenGLES, ContextProfile.Core, flags, new APIVersion(3, 0))
                : new GraphicsAPI(ContextAPI.OpenGL, ContextProfile.Core, flags, new APIVersion(3, 3)),
            ShouldSwapAutomatically = false,
            VSync = false,
            PreferredDepthBufferBits = 0,
            PreferredStencilBufferBits = 0,
        };
        var words = "";
        IWindow window;
        var egl = false;
        if (es && preferEgl && OperatingSystem.IsLinux())
        {
            try
            {
                window = MakeWindow(wo, egl: true);
                egl = true;
            }
            catch (Exception ex) when (ex is GlfwException or InvalidOperationException or PlatformNotSupportedException)
            {
                words = $"ES through GLX: EGL refused the window ({ex.Message})";
                window = MakeWindow(wo, egl: false);
            }
        }
        else
        {
            window = MakeWindow(wo, egl: false);
        }
        window.MakeCurrent();
        var gl = es ? GL.GetApi(AliasLoader.Wrap(n => window.GLContext!.GetProcAddress(n))) : GL.GetApi(window);
        try
        {
            var device = new GlDevice(gl, options);
            var api = egl ? "egl" : OperatingSystem.IsWindows() ? "wgl" : OperatingSystem.IsMacOS() ? "cocoa" : "glx";
            if (device.Dialect != options.Dialect)
                words = $"asked for {GlDialects.Word(options.Dialect)}, the driver gave {GlDialects.Word(device.Dialect)}";
            return new RenderContext(window, null, gl, device, api, egl, words);
        }
        catch
        {
            gl.Dispose();
            window.Dispose();
            throw;
        }
    }

    /// <summary>A context with no window and no surface (Linux, EGL's surfaceless platform; Mesa offers it): renders every
    /// offscreen pass, opens no output windows. Throws <see cref="PlatformNotSupportedException"/> with words where there is none.</summary>
    public static RenderContext CreateSurfaceless(GlDeviceOptions? options = null)
    {
        options ??= GlDeviceOptions.Default;
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("a surfaceless context is made through EGL on Linux only");
        var egl = EglSurfacelessContext.Create(options.Dialect, options.DebugOutput);
        var gl = GL.GetApi(AliasLoader.Wrap(EglSurfacelessContext.ProcAddress));
        try
        {
            var device = new GlDevice(gl, options);
            return new RenderContext(null, egl, gl, device, "egl-surfaceless", eglWindows: false, "");
        }
        catch
        {
            gl.Dispose();
            egl.Dispose();
            throw;
        }
    }

    /// <summary>Makes a window under the hint gate; on EGL the creation API is set for this window only and put back after.</summary>
    internal static IWindow MakeWindow(WindowOptions options, bool egl)
    {
        lock (HintGate)
        {
            var glfw = egl ? GlfwProvider.GLFW.Value : null;
            glfw?.WindowHint(WindowHintContextApi.ContextCreationApi, ContextApi.EglContextApi);
            var window = Silk.NET.Windowing.Window.Create(options);
            try
            {
                window.Initialize();
            }
            catch
            {
                window.Dispose();
                throw;
            }
            finally
            {
                glfw?.WindowHint(WindowHintContextApi.ContextCreationApi, ContextApi.NativeContextApi);
            }
            return window;
        }
    }

    /// <summary>Makes this context current on the calling thread (after an output window's present).</summary>
    public void MakeCurrent()
    {
        if (_window is not null)
            _window.MakeCurrent();
        else if (OperatingSystem.IsLinux() && _surfaceless is EglSurfacelessContext egl)
            egl.MakeCurrent();
    }

    /// <summary>The displays the platform reports: index, name, bounds in desktop pixels and refresh rate (none surfaceless).</summary>
    public IReadOnlyList<MonitorInfo> Monitors()
    {
        var list = new List<MonitorInfo>();
        if (_window is null)
            return list;
        foreach (var m in Silk.NET.Windowing.Monitor.GetMonitors(_window))
        {
            var b = m.Bounds;
            list.Add(new MonitorInfo(m.Index, m.Name, b.Origin.X, b.Origin.Y, b.Size.X, b.Size.Y, m.VideoMode.RefreshRate ?? 0));
        }
        return list;
    }

    /// <summary>Pumps the platform's window events (all windows share one event queue).</summary>
    public void PumpEvents() => _window?.DoEvents();

    /// <summary>Deletes the device's objects and closes the context; on its own thread.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        MakeCurrent();
        Device.Dispose();
        Gl.Dispose();
        _window?.Dispose();
        _surfaceless?.Dispose();
        _disposed = true;
    }
}

/// <summary>A display: its platform index, name, desktop bounds and refresh rate (0 = unknown).</summary>
public sealed record MonitorInfo(int Index, string Name, int X, int Y, int Width, int Height, int RefreshHz);
