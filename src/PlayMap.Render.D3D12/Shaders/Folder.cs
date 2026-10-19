using System.Runtime.Versioning;

namespace PlayMap.Render.D3D12.Shaders;

/// <summary>Shaders/ holds the HLSL, programs.json, the generated slot headers and the embedded DXIL (docs/round3/design/d3d12.md §0). It holds nothing yet: no Direct3D 12 object is made
/// here.</summary>
[SupportedOSPlatform("windows")]
internal static class Folder
{
    // wp3-shaders fills this.
    internal const string Holds = "the HLSL, programs.json, the generated slot headers and the embedded DXIL";
}
