using PlayMap.Core.Presentation;

namespace PlayMap.Core.Gpu;

/// <summary>Where one output's finished picture goes: a window or swap chain on a display that presents on its vertical blank. The
/// renderer draws every pass offscreen, records the output's final picture into the presenter's next buffer in the frame's list,
/// submits the list, then presents.
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

    /// <summary>Records the picture into the presenter's next buffer in the frame's list (GL: the window's default framebuffer, the
    /// only flip; Direct3D 12: the swap chain's current back buffer).</summary>
    void Record(ICommandList list, GpuTexture picture);

    /// <summary>Records black into the presenter's next buffer (an output with nothing drawn yet).</summary>
    void RecordBlack(ICommandList list);

    /// <summary>After the frame's list is submitted: queues the recorded buffer for display. Never blocks longer than its wait for
    /// the vertical blank when it is the pacing output. A present with nothing recorded since the last is refused.</summary>
    PresentResult Present();

    /// <summary>Reads the display's own statistics into the output's clock and ledger, after every output presented this frame.
    /// A GL window has none beyond the swap's return, which the renderer has already given the clock.</summary>
    void Observe(PresentClock clock)
    {
    }

    /// <summary>The frame-latency waitable where the platform has one (DXGI), else null: the loop waits on it before sampling time.</summary>
    WaitHandle? FrameLatencyWaitable => null;
}
