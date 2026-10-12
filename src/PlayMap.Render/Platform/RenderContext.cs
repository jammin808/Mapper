using PlayMap.Render.Gpu;
using Silk.NET.Maths;
using Silk.NET.OpenGL;
using Silk.NET.Windowing;

namespace PlayMap.Render.Platform;

/// <summary>The renderer's primary GL context: a hidden window whose context does every offscreen pass
/// (sources, canvases, the projector pass, the output stage, previews) and that output windows share
/// objects with. Headless engines and tests use it alone.
///
/// Threading: make it on the thread that will render (the engine's render thread, which on macOS must be
/// the process's main thread); every call afterwards — rendering, output windows, disposal — happens there.</summary>
public sealed class RenderContext : IDisposable
{
    private readonly IWindow _window;
    private bool _disposed;

    private RenderContext(IWindow window, GL gl, GlDevice device)
    {
        _window = window;
        Gl = gl;
        Device = device;
    }

    /// <summary>The window behind the context (hidden), for monitor queries and context sharing.</summary>
    internal IWindow Window => _window;

    internal GL Gl { get; }

    public GlDevice Device { get; }

    /// <summary>Makes a hidden OpenGL 3.3 core context (forward-compatible; debug-flagged in debug builds).</summary>
    public static RenderContext CreateHidden(GlDeviceOptions? options = null)
    {
        options ??= GlDeviceOptions.Default;
        var flags = ContextFlags.ForwardCompatible | (options.DebugOutput ? ContextFlags.Debug : ContextFlags.Default);
        var wo = WindowOptions.Default with
        {
            IsVisible = false,
            Size = new Vector2D<int>(16, 16),
            Title = "PlayMap render",
            API = new GraphicsAPI(ContextAPI.OpenGL, ContextProfile.Core, flags, new APIVersion(3, 3)),
            ShouldSwapAutomatically = false,
            VSync = false,
            PreferredDepthBufferBits = 0,
            PreferredStencilBufferBits = 0,
        };
        var window = Silk.NET.Windowing.Window.Create(wo);
        window.Initialize();
        window.MakeCurrent();
        var gl = GL.GetApi(window);
        try
        {
            var device = new GlDevice(gl, options);
            return new RenderContext(window, gl, device);
        }
        catch
        {
            gl.Dispose();
            window.Dispose();
            throw;
        }
    }

    /// <summary>Makes this context current on the calling thread (after an output window's present).</summary>
    public void MakeCurrent() => _window.MakeCurrent();

    /// <summary>The displays the platform reports: index, name, bounds in desktop pixels and refresh rate.</summary>
    public IReadOnlyList<MonitorInfo> Monitors()
    {
        var list = new List<MonitorInfo>();
        foreach (var m in Silk.NET.Windowing.Monitor.GetMonitors(_window))
        {
            var b = m.Bounds;
            list.Add(new MonitorInfo(m.Index, m.Name, b.Origin.X, b.Origin.Y, b.Size.X, b.Size.Y, m.VideoMode.RefreshRate ?? 0));
        }
        return list;
    }

    /// <summary>Pumps the platform's window events (all windows share one event queue).</summary>
    public void PumpEvents() => _window.DoEvents();

    /// <summary>Deletes the device's objects and closes the context; on its own thread.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _window.MakeCurrent();
        Device.Dispose();
        Gl.Dispose();
        _window.Dispose();
        _disposed = true;
    }
}

/// <summary>A display: its platform index, name, desktop bounds and refresh rate (0 = unknown).</summary>
public sealed record MonitorInfo(int Index, string Name, int X, int Y, int Width, int Height, int RefreshHz);
