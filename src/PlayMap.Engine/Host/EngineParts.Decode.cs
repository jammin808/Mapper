namespace PlayMap.Engine.Host;

/// <summary>wp2-decode's parts in the exe's composition (EngineParts.Standard), added in the spine's order.</summary>
public sealed partial record EngineParts
{
    static partial void AddDecodeParts(List<EngineExtensionFactory> extensions)
    {
        // wp2-decode fills this.
    }
}
