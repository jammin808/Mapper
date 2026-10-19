using System.Runtime.Versioning;

namespace PlayMap.Render.D3D12.Platform;

/// <summary>Platform/ holds the Direct3D 12 platform: offscreen first, then the display list and the outputs (docs/round3/design/d3d12.md §0). It holds nothing yet: no Direct3D 12 object is made
/// here.</summary>
[SupportedOSPlatform("windows")]
internal static class Folder
{
    // wp3-d3d12-core fills this.
    internal const string Holds = "the Direct3D 12 platform: offscreen first, then the display list and the outputs";
}
