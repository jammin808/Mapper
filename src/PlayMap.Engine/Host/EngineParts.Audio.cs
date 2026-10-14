namespace PlayMap.Engine.Host;

/// <summary>wp2-audio's parts in the exe's composition (EngineParts.Standard), added in the spine's order.</summary>
public sealed partial record EngineParts
{
    static partial void AddAudioParts(List<EngineExtensionFactory> extensions)
    {
        // wp2-audio fills this.
    }
}
