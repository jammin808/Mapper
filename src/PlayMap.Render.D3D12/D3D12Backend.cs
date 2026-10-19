using System.Runtime.Versioning;

namespace PlayMap.Render.D3D12;

/// <summary>The Direct3D 12 backend's composition: the device, its queues and memory, the presenters and the rest of the folders
/// docs/round3/design/d3d12.md §0 lists, put together behind the RHI. Nothing is composed yet: no node renders through Direct3D 12,
/// and every output stays on the path it has today.</summary>
[SupportedOSPlatform("windows")]
public static class D3D12Backend
{
    // wp3-d3d12-core fills this.

    /// <summary>Why nothing renders through this backend on this node.</summary>
    public static string Words => "the Direct3D 12 backend is not built yet: nothing renders through it";
}
