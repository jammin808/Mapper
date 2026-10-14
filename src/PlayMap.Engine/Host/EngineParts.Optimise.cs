namespace PlayMap.Engine.Host;

/// <summary>wp2-optimise's parts in the exe's composition (EngineParts.Standard), added in the spine's order.</summary>
public sealed partial record EngineParts
{
    static partial void AddOptimiseParts(List<EngineExtensionFactory> extensions)
    {
        // wp2-optimise fills this.
    }
}
