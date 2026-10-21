namespace PlayMap.Engine.Host;

/// <summary>wp3-installer's parts in the exe's composition (EngineParts.Standard), added in the spine's order.</summary>
public sealed partial record EngineParts
{
    static partial void AddInstallParts(List<EngineExtensionFactory> extensions)
    {
        // wp3-installer fills this.
    }
}
