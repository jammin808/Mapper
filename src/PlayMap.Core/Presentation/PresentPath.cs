namespace PlayMap.Core.Presentation;

/// <summary>How an output's picture reaches its display: offscreen (no window: previews, health and scans only), a GL window
/// (a shared context that draws the finished picture and swaps), a DXGI flip-model swap chain fed through D3D11 (Windows), or a
/// Direct3D 12 flip-model swap chain (Windows). Both flip paths share DXGI's presentation model, so on the wire a D3D12 flip output
/// reports the path a DXGI flip output does (PMVB path 2, "dxgi-flip"); STATE names each by its own word.</summary>
public enum PresentPath
{
    Offscreen,
    GlWindow,
    DxgiFlip,
    D3D12Flip,
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
    /// <summary>The platform's own number for this present (DXGI's present count, which wraps through 0; unused where the platform numbers none), which its
    /// display statistics name when the present is shown.</summary>
    public uint PresentCount { get; init; }

    public static PresentResult Done(long returnedNs) => new(true, returnedNs, PresentProblem.None);
}
