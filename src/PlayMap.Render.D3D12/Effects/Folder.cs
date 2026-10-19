using System.Runtime.Versioning;

namespace PlayMap.Render.D3D12.Effects;

/// <summary>Effects/ holds user effects' root signature, descriptor copies and pipelines (docs/round3/design/d3d12.md §0). It holds nothing yet: no Direct3D 12 object is made
/// here.</summary>
[SupportedOSPlatform("windows")]
internal static class Folder
{
    // wp3-shaders-2 fills this.
    internal const string Holds = "user effects' root signature, descriptor copies and pipelines";
}
