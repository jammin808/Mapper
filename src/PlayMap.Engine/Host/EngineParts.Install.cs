namespace PlayMap.Engine.Host;

/// <summary>wp3-installer's parts in the exe's composition (EngineParts.Standard), added in the spine's order: preflight's install
/// section, set in its extension's Configure.</summary>
public sealed partial record EngineParts
{
    static partial void AddInstallParts(List<EngineExtensionFactory> extensions)
    {
        extensions.Add(Install.InstallParts.Factory);
    }
}
