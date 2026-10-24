using System.Runtime.Versioning;

namespace PlayMap.Render.D3D12.Video;

/// <summary>Video/ holds the D3D12VA handoff and the copy-queue arena (docs/round3/design/d3d12.md §0). It holds nothing yet: no Direct3D 12 object is made
/// here.</summary>
[SupportedOSPlatform("windows")]
internal static class Folder
{
    // wp3-video fills this.
    internal const string Holds = "the D3D12VA handoff and the copy-queue arena";
}
