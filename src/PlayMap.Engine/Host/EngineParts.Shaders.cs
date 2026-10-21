namespace PlayMap.Engine.Host;

/// <summary>wp3-shaders' parts in the exe's composition (EngineParts.Standard), added in the spine's order: the quarantine kept in the
/// node's home, the shader worker (this exe in its "shader-worker" role) as FX CHECK's stage check, and the quarantine's preflight
/// rows.</summary>
public sealed partial record EngineParts
{
    static partial void AddShadersParts(List<EngineExtensionFactory> extensions)
    {
        extensions.Add(ShaderWorker.ShadersExtension.Factory);
    }
}
