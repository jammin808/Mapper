using System.Runtime.Versioning;

namespace PlayMap.Render.D3D12.Descriptors;

/// <summary>Descriptors/ holds the descriptor heaps and their free lists (docs/round3/design/d3d12.md §0). It holds nothing yet: no Direct3D 12 object is made
/// here.</summary>
[SupportedOSPlatform("windows")]
internal static class Folder
{
    // wp3-d3d12-core fills this.
    internal const string Holds = "the descriptor heaps and their free lists";
}
