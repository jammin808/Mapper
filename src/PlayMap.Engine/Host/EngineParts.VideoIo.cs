namespace PlayMap.Engine.Host;

/// <summary>wp2-videoio's parts in the exe's composition (EngineParts.Standard), added in the spine's order.</summary>
public sealed partial record EngineParts
{
    static partial void AddVideoIoParts(List<EngineExtensionFactory> extensions)
    {
        // wp2-videoio fills this.
    }
}
