using System.Text.Json;
using PlayMap.Core.Wire;

namespace PlayMap.Engine;

/// <summary>wp3-desk-gpu's hooks into the engine (EngineCore.Modules.cs): STATE's previews block and VIEW … SHARED's answer. The
/// package fills each body.</summary>
public sealed partial class EngineCore
{
    partial void StatePreview(Utf8JsonWriter w)
    {
        // wp3-desk-gpu fills this.
    }

    partial void ViewShared(List<string> args, SessionInfo session, ref CommandReply? reply)
    {
        // wp3-desk-gpu fills this.
    }
}
