using System.Text.Json;

namespace PlayMap.Engine;

/// <summary>wp3-desk-gpu's hooks into the engine (EngineCore.Modules.cs): STATE's previews block. The package fills each
/// body.</summary>
public sealed partial class EngineCore
{
    partial void StatePreview(Utf8JsonWriter w)
    {
        // wp3-desk-gpu fills this.
    }
}
