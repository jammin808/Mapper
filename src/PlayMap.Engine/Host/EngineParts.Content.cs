namespace PlayMap.Engine.Host;

/// <summary>wp2-content's parts in the exe's composition (EngineParts.Standard), added in the spine's order.</summary>
public sealed partial record EngineParts
{
    static partial void AddContentParts(List<EngineExtensionFactory> extensions)
    {
        extensions.Add(Content.ContentExtension.Factory);
    }
}
