namespace PlayMap.Engine.Host;

/// <summary>wp2-led's parts in the exe's composition (EngineParts.Standard), added in the spine's order.</summary>
public sealed partial record EngineParts
{
    static partial void AddLedParts(List<EngineExtensionFactory> extensions)
    {
        // wp2-led fills this.
    }
}
