namespace PlayMap.Engine.Host;

/// <summary>wp2-companion's parts in the exe's composition (EngineParts.Standard), added in the spine's order.</summary>
public sealed partial record EngineParts
{
    static partial void AddCompanionParts(List<EngineExtensionFactory> extensions)
    {
        extensions.Add(setup => new Companion.CompanionExtension(setup));
    }
}
