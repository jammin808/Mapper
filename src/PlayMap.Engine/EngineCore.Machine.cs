using System.Text.Json;
using PlayMap.Core.Bus;

namespace PlayMap.Engine;

/// <summary>wp3-platform's hooks into the engine (EngineCore.Modules.cs): its verbs, the machine block's identity, capabilities,
/// platform and locked keys, and the context's machine item. The package fills each body.</summary>
public sealed partial class EngineCore
{
    partial void RouteMachine(VerbRoutes routes)
    {
        // wp3-platform fills this.
    }

    partial void MachineMachine(Utf8JsonWriter w)
    {
        // wp3-platform fills this.
    }

    partial void ContextMachine(List<ContextItem> items, List<ContextLink> links)
    {
        // wp3-platform fills this.
    }
}
