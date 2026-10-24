using System.Runtime.Versioning;

namespace PlayMap.Render.D3D12.Present;

/// <summary>Present/ holds the flip presenters (docs/round3/design/d3d12.md §0). It holds nothing yet: no Direct3D 12 object is made
/// here.</summary>
[SupportedOSPlatform("windows")]
internal static class Folder
{
    // wp3-present fills this.
    internal const string Holds = "the flip presenters";
}
