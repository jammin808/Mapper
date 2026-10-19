namespace PlayMap.Engine.Host;

/// <summary>wp3-desk-gpu's parts in the exe's composition (EngineParts.Standard), added in the spine's order: the preview share it composes (EngineServices.PreviewShare) and its GPU lifecycle listener, set in its
/// extension's Configure.</summary>
public sealed partial record EngineParts
{
    static partial void AddPreviewParts(List<EngineExtensionFactory> extensions)
    {
        // wp3-desk-gpu fills this.
    }
}
