namespace PlayMap.Engine.Host;

/// <summary>wp3-platform's parts in the exe's composition (EngineParts.Standard), added in the spine's order: the machine platform it composes (EngineServices.MachinePlatform) and its arm hook, set in its
/// extension's Configure.</summary>
public sealed partial record EngineParts
{
    static partial void AddMachineParts(List<EngineExtensionFactory> extensions)
    {
        // wp3-platform fills this.
    }
}
