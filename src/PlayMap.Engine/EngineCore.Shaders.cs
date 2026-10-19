using System.Text.Json;

namespace PlayMap.Engine;

/// <summary>wp3-shaders' hooks into the engine (EngineCore.Modules.cs): its verbs (FX RELEASE), the fx block's quarantine and the
/// compile lane's worker (wp3-shaders-2 continues it). FX CHECK's stages come through <see cref="EngineServices.FxStages"/>. The
/// package fills each body.</summary>
public sealed partial class EngineCore
{
    partial void RouteShaders(VerbRoutes routes)
    {
        // wp3-shaders fills this.
    }

    partial void FxCompileShaders(Utf8JsonWriter w)
    {
        // wp3-shaders fills this.
    }

    partial void FxShaders(Utf8JsonWriter w)
    {
        // wp3-shaders fills this.
    }
}
