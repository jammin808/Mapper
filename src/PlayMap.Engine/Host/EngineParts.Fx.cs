namespace PlayMap.Engine.Host;

/// <summary>wp2-fx's parts in the exe's composition (EngineParts.Standard), added in the spine's order.</summary>
public sealed partial record EngineParts
{
    static partial void AddFxParts(List<EngineExtensionFactory> extensions)
    {
        // wp2-fx fills this.
    }
}
