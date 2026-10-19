using System.Runtime.Versioning;

namespace PlayMap.Render.D3D12.Share;

/// <summary>Share/ holds the exporter: shared textures, shared fences and their NT handles (docs/round3/design/d3d12.md §0). It holds nothing yet: no Direct3D 12 object is made
/// here.</summary>
[SupportedOSPlatform("windows")]
internal static class Folder
{
    // wp3-d3d12-core fills this.
    internal const string Holds = "the exporter: shared textures, shared fences and their NT handles";
}
