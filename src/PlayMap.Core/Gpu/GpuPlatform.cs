using PlayMap.Core.Media;
using PlayMap.Core.Presentation;

namespace PlayMap.Core.Gpu;

/// <summary>Where a presenter goes: a monitor (null = a free-floating window at <see cref="X"/>, <see cref="Y"/>), borderless over
/// the whole monitor or a movable window of the given size, and whether its presents wait for the vertical blank.</summary>
public sealed record OutputPresenterOptions(string Title, MonitorInfo? Monitor, bool Borderless, int Width, int Height, int X = 64,
    int Y = 64, bool VSync = true);

/// <summary>A display: its platform index, name, desktop bounds and refresh rate (0 = unknown).</summary>
public sealed record MonitorInfo(int Index, string Name, int X, int Y, int Width, int Height, int RefreshHz);

/// <summary>The render platform a node starts on: the device the renderer draws on, the displays, the presenters outputs go to and
/// the device decoders may share. The GL platform (GLFW windows over GLX, or EGL for ES) and the DXGI flip-model platform on
/// Windows (Direct3D 11 through ANGLE) stand behind it; a Direct3D 12 platform implements it without referencing the renderer.
///
/// Threading: made, used and disposed on the render thread.</summary>
public interface IGpuPlatform : IDisposable
{
    /// <summary>"gl" (GLFW windows sharing one context), "gl-surfaceless" (EGL with no window system: offscreen only), "dxgi" (the
    /// flip model on a Direct3D 11 device through ANGLE).</summary>
    string Name { get; }

    /// <summary>The windowing API under the device: "glx", "egl", "egl-surfaceless", "wgl", "cocoa", "angle-d3d11".</summary>
    string Api { get; }

    /// <summary>Why the platform is not what was asked ("ES through GLX: EGL refused …"); "" when it is.</summary>
    string Words { get; }

    /// <summary>The device the renderer draws on.</summary>
    IGpuDevice Device { get; }

    /// <summary>The path this platform's presenters take.</summary>
    PresentPath Path { get; }

    IReadOnlyList<MonitorInfo> Monitors();

    /// <summary>A presenter on a display; throws with words when the platform cannot open one there.</summary>
    IOutputPresenter CreatePresenter(OutputPresenterOptions options);

    void PumpEvents();

    /// <summary>The renderer's device as decoders may share it.</summary>
    IGpuShare Share { get; }

    /// <summary>The lock of a device other threads issue work on while the renderer draws on it (the D3D11 device decoders share on
    /// the DXGI path): the render loop holds it around each frame, bounded (<see cref="GpuDeviceLock.FrameBoundMs"/>), and lets go of
    /// it while it waits on a display. Null when nothing else draws on the renderer's device (a GL context is the render thread's
    /// alone).</summary>
    GpuDeviceLock? DeviceLock => null;

    /// <summary>The adapter the platform renders on, in words ("Direct3D 11 + ANGLE · NVIDIA RTX A4000"); "" when the device's own
    /// renderer string says it.</summary>
    string Adapter => "";
}
