using PlayMap.Engine.Fleet;

namespace PlayMap.Engine.Host;

/// <summary>wp2-fleet's parts in the exe's composition (EngineParts.Standard), added in the spine's order.</summary>
public sealed partial record EngineParts
{
    static partial void AddFleetParts(List<EngineExtensionFactory> extensions)
    {
        extensions.Add(FleetExtension.Factory);
    }
}
