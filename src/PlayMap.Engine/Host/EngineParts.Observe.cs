namespace PlayMap.Engine.Host;

/// <summary>wp3-stability's parts in the exe's composition (EngineParts.Standard), added in the spine's order.</summary>
public sealed partial record EngineParts
{
    static partial void AddObserveParts(List<EngineExtensionFactory> extensions)
    {
        // wp3-stability fills this.
    }
}
