using PlayMap.Render.Gpu;
using PlayMap.Render.Timing;

namespace PlayMap.Render.Platform;

/// <summary>How an output's picture reaches its display: offscreen (no window: previews, health and scans only), a GL window
/// (a shared context that draws the finished picture and swaps), or a DXGI flip-model swap chain (Windows, next round).</summary>
public enum PresentPath
{
    Offscreen,
    GlWindow,
    DxgiFlip,
}

/// <summary>Why a present did not queue a new picture.</summary>
public enum PresentProblem
{
    None,
    /// <summary>The output's queue was full: the previous picture stays up one more refresh (never a block, never a tear).</summary>
    Held,
    /// <summary>The window is hidden or covered: the present was dropped by the platform.</summary>
    Occluded,
    DeviceLost,
    Failed,
}

/// <summary>What one present did: whether a new picture was queued, the monotonic instant the call returned (with vsync on GL,
/// just after the vertical blank it waited for), and the problem when it did not queue.</summary>
public readonly record struct PresentResult(bool Queued, long ReturnedNs, PresentProblem Problem)
{
    public static PresentResult Done(long returnedNs) => new(true, returnedNs, PresentProblem.None);
}

/// <summary>Where one output's finished picture goes: a window on a display that presents on its vertical blank. The renderer
/// draws every pass offscreen and hands the presenter the output's final picture once a frame.
///
/// Threading and pacing: render thread only. A presenter whose <see cref="SwapInterval"/> is 1 waits for its display's vertical
/// blank in <see cref="Present"/> (the pacing output); at 0 it presents at once. Present never blocks longer than that wait.</summary>
public interface IOutputPresenter : IDisposable
{
    int Width { get; }

    int Height { get; }

    /// <summary>The platform closed it (the operator closed the window).</summary>
    bool IsClosing { get; }

    PresentPath Path { get; }

    /// <summary>1 = the present waits for the vertical blank (the pacing output), 0 = it presents at once.</summary>
    int SwapInterval { get; }

    void SetSwapInterval(int interval);

    /// <summary>The monotonic instant the last present returned (the vblank clock's raw sample on GL).</summary>
    long LastSwapNs { get; }

    /// <summary>Shows a finished picture.</summary>
    PresentResult Present(GpuTexture picture);

    /// <summary>Shows black (an output with nothing drawn yet).</summary>
    PresentResult PresentBlack();

    /// <summary>Reads the display's own statistics into the output's clock and ledger, after every output presented this frame.
    /// A GL window has none beyond the swap's return, which the renderer has already given the clock.</summary>
    void Observe(PresentClock clock)
    {
    }
}
