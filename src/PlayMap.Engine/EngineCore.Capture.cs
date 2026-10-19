using System.Text.Json;
using PlayMap.Core.Model;
using PlayMap.Engine.VideoIo;

namespace PlayMap.Engine;

/// <summary>wp3-capture's hooks into the engine (EngineCore.Modules.cs): its verbs, the outputs' passThrough key, the live inputs'
/// capture and latency keys and its checks. The package fills each body.</summary>
public sealed partial class EngineCore
{
    partial void RouteCapture(VerbRoutes routes)
    {
        // wp3-capture fills this.
    }

    partial void OutputCapture(Utf8JsonWriter w, Output o)
    {
        // wp3-capture fills this.
    }

    partial void InputCapture(Utf8JsonWriter w, LiveInput input)
    {
        // wp3-capture fills this.
    }

    partial void ValidateCapture(Project p, object entity, ref string? why)
    {
        // wp3-capture fills this.
    }
}
