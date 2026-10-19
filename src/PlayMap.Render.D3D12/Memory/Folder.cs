using System.Runtime.Versioning;

namespace PlayMap.Render.D3D12.Memory;

/// <summary>Memory/ holds the allocator, the rings, transient memory and the deferred-release queue (docs/round3/design/d3d12.md §0). It holds nothing yet: no Direct3D 12 object is made
/// here.</summary>
[SupportedOSPlatform("windows")]
internal static class Folder
{
    // wp3-d3d12-core fills this.
    internal const string Holds = "the allocator, the rings, transient memory and the deferred-release queue";
}
