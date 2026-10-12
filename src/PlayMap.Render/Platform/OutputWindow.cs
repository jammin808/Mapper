using PlayMap.Core.Time;
using PlayMap.Render.Gpu;
using Silk.NET.Maths;
using Silk.NET.Windowing;

namespace PlayMap.Render.Platform;

/// <summary>Where an output window goes: a display by index (−1 = a free-floating window at
/// <see cref="X"/>, <see cref="Y"/>), borderless over the whole display or a movable window of the given size,
/// and whether its swap waits for the display's vertical blank.</summary>
public sealed record OutputWindowOptions(
    string Title,
    int MonitorIndex = -1,
    bool Borderless = true,
    bool VSync = true,
    int Width = 1280,
    int Height = 720,
    int X = 64,
    int Y = 64);

/// <summary>A window an output presents into: borderless over a display (a projector, an LED processor) or
/// a movable confidence window. Its context shares objects with the <see cref="RenderContext"/>; every pass
/// draws offscreen in the primary context and the window only presents the output's finished picture — one
/// textured triangle and a swap — so the windows' contexts hold nothing but their own vertex array.
///
/// Threading and pacing: create, present, pump and dispose on the render thread. A swap with vsync waits
/// for that display's vertical blank, so when one thread drives several windows only one of them should
/// wait (the pacing window, <see cref="SetSwapInterval"/> 1) and the others swap at once (0); the engine
/// picks the pacer. A window never makes the primary context lose its state: the present switches to the
/// window's context and back.</summary>
public sealed class OutputWindow : IDisposable
{
    private readonly RenderContext _primary;
    private readonly IWindow _window;
    private uint _vao;
    private bool _disposed;

    private OutputWindow(RenderContext primary, IWindow window)
    {
        _primary = primary;
        _window = window;
    }

    public int Width => _window.FramebufferSize.X;
    public int Height => _window.FramebufferSize.Y;
    public bool IsClosing => _window.IsClosing;

    /// <summary>The monotonic instant the last swap returned — with vsync, just after the vertical blank it
    /// waited for (the frame pacer's raw sample).</summary>
    public long LastSwapNs { get; private set; }

    public int SwapInterval { get; private set; }

    public static OutputWindow Create(RenderContext primary, OutputWindowOptions options)
    {
        ArgumentNullException.ThrowIfNull(primary);
        ArgumentNullException.ThrowIfNull(options);
        var (x, y, w, h) = (options.X, options.Y, options.Width, options.Height);
        if (options.MonitorIndex >= 0)
        {
            foreach (var m in primary.Monitors())
            {
                if (m.Index != options.MonitorIndex)
                    continue;
                if (options.Borderless)
                    (x, y, w, h) = (m.X, m.Y, Math.Max(1, m.Width), Math.Max(1, m.Height));
                else
                    (x, y) = (m.X + options.X, m.Y + options.Y);
            }
        }
        // The present program is compiled in the primary context before the window's context exists.
        primary.Device.Program("present");
        var api = primary.Window.API;
        var wo = WindowOptions.Default with
        {
            Title = options.Title,
            Position = new Vector2D<int>(x, y),
            Size = new Vector2D<int>(w, h),
            WindowBorder = options.Borderless ? WindowBorder.Hidden : WindowBorder.Resizable,
            WindowState = WindowState.Normal,
            TopMost = options.Borderless,
            API = api,
            SharedContext = primary.Window.GLContext,
            ShouldSwapAutomatically = false,
            VSync = options.VSync,
            IsVisible = true,
            PreferredDepthBufferBits = 0,
            PreferredStencilBufferBits = 0,
        };
        var window = Silk.NET.Windowing.Window.Create(wo);
        window.Initialize();
        var output = new OutputWindow(primary, window);
        window.MakeCurrent();
        output._vao = primary.Device.CreateContextVertexArray();
        output.SetSwapIntervalCurrent(options.VSync ? 1 : 0);
        primary.MakeCurrent();
        return output;
    }

    /// <summary>1 = wait for the vertical blank (the pacing window), 0 = swap at once.</summary>
    public void SetSwapInterval(int interval)
    {
        if (interval == SwapInterval)
            return;
        _window.MakeCurrent();
        SetSwapIntervalCurrent(interval);
        _primary.MakeCurrent();
    }

    private void SetSwapIntervalCurrent(int interval)
    {
        _window.GLContext?.SwapInterval(interval);
        SwapInterval = interval;
    }

    /// <summary>Shows a finished picture: switches to the window's context, draws it over the window,
    /// swaps, and returns to the primary context.</summary>
    public void Present(GpuTexture picture)
    {
        ArgumentNullException.ThrowIfNull(picture);
        ObjectDisposedException.ThrowIf(_disposed, this);
        var device = _primary.Device;
        device.Flush();
        _window.MakeCurrent();
        device.DrawPresent(_vao, picture, Width, Height);
        _window.SwapBuffers();
        LastSwapNs = MonoClock.Instance.NowNs;
        _primary.MakeCurrent();
    }

    /// <summary>Shows black (an output with nothing drawn yet).</summary>
    public void PresentBlack()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _window.MakeCurrent();
        _primary.Device.ClearDefault(Width, Height);
        _window.SwapBuffers();
        LastSwapNs = MonoClock.Instance.NowNs;
        _primary.MakeCurrent();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _window.MakeCurrent();
        _primary.Device.DeleteContextVertexArray(_vao);
        _vao = 0;
        _window.Dispose();
        _primary.MakeCurrent();
        _disposed = true;
    }
}
