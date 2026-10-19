using System.Runtime.Versioning;

namespace PlayMap.Render.D3D12.Shaders;

/// <summary>Shaders/ holds the HLSL library (one file per family, Common.hlsli's conventions), its program table (programs.json) and
/// layout table (programs.glsl.layout.json), and the build step (Shaders.targets) that writes each program's header and, on Windows,
/// embeds its DXIL (docs/round3/design/shaders.md). No Direct3D 12 object is made here: the device that reads the DXIL is
/// wp3-d3d12-core's.</summary>
[SupportedOSPlatform("windows")]
internal static class Folder
{
    internal const string Holds = "the HLSL, programs.json, the generated slot headers and the embedded DXIL";
}
