namespace PlayMap.Engine.Host;

/// <summary>wp2-align's parts in the exe's composition (EngineParts.Standard), added in the spine's order.</summary>
public sealed partial record EngineParts
{
    static partial void AddAlignParts(List<EngineExtensionFactory> extensions)
    {
        // wp2-align fills this.
    }
}
