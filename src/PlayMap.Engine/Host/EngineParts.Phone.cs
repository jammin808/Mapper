namespace PlayMap.Engine.Host;

/// <summary>wp2-phonescan's parts in the exe's composition (EngineParts.Standard), added in the spine's order.</summary>
public sealed partial record EngineParts
{
    static partial void AddPhoneParts(List<EngineExtensionFactory> extensions)
    {
        // wp2-phonescan fills this.
    }
}
