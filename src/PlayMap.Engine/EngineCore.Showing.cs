using System.Text.Json;
using PlayMap.Core.Model;

namespace PlayMap.Engine;

/// <summary>wp3-desk-map's hooks into the engine (EngineCore.Modules.cs): what each screen shows: the outputs' showing key, the
/// media's shownOn key and the words the context's output items carry. The package fills each body.</summary>
public sealed partial class EngineCore
{
    partial void OutputShowing(Utf8JsonWriter w, Output o)
    {
        // wp3-desk-map fills this.
    }

    partial void MediaShowing(Utf8JsonWriter w)
    {
        // wp3-desk-map fills this.
    }

    internal partial string ShowingWords(Output output) =>
        ""; // wp3-desk-map fills this.
}
