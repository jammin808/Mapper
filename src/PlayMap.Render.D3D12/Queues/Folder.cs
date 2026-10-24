using System.Runtime.Versioning;

namespace PlayMap.Render.D3D12.Queues;

/// <summary>Queues/ holds the queues, their timeline fences and the frame resources (docs/round3/design/d3d12.md §0). It holds nothing yet: no Direct3D 12 object is made
/// here.</summary>
[SupportedOSPlatform("windows")]
internal static class Folder
{
    // wp3-d3d12-core fills this.
    internal const string Holds = "the queues, their timeline fences and the frame resources";
}
