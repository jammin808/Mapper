using System.Text.Json;

namespace PlayMap.Engine;

/// <summary>wp3-desk-map's hooks into the engine (EngineCore.Modules.cs): its verbs and the overlay's handle latency. The package
/// fills each body.</summary>
public sealed partial class EngineCore
{
    partial void RouteHandles(VerbRoutes routes)
    {
        // wp3-desk-map fills this.
    }

    partial void OverlayHandles(Utf8JsonWriter w)
    {
        // wp3-desk-map fills this.
    }
}
