using System.Runtime.Versioning;

namespace PlayMap.Render.D3D12.Bindless;

/// <summary>Bindless/ holds the surface records and the indirect draws (docs/round3/design/d3d12.md §0). It holds nothing yet: no Direct3D 12 object is made
/// here.</summary>
[SupportedOSPlatform("windows")]
internal static class Folder
{
    // wp3-d3d12-passes-2 fills this.
    internal const string Holds = "the surface records and the indirect draws";
}
