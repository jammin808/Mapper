namespace PlayMap.Engine.Host;

/// <summary>wp2-wall's parts in the exe's composition (EngineParts.Standard), added in the spine's order.</summary>
public sealed partial record EngineParts
{
    static partial void AddWallParts(List<EngineExtensionFactory> extensions)
    {
        // wp2-wall fills this.
    }
}
