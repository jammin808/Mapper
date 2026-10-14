namespace PlayMap.Engine.Host;

/// <summary>wp2-show's parts in the exe's composition (EngineParts.Standard), added in the spine's order.</summary>
public sealed partial record EngineParts
{
    static partial void AddShowParts(List<EngineExtensionFactory> extensions)
    {
        // wp2-show fills this.
    }
}
