namespace PlayMap.Engine.Host;

/// <summary>wp3-shaders' parts in the exe's composition (EngineParts.Standard), added in the spine's order.</summary>
public sealed partial record EngineParts
{
    static partial void AddShadersParts(List<EngineExtensionFactory> extensions)
    {
        // wp3-shaders fills this.
    }
}
