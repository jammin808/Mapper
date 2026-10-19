namespace PlayMap.Engine.Host;

/// <summary>wp3-shaders' entry point beside "engine": the shader worker's own process, where user effects compile.</summary>
public static partial class EngineEntry
{
    static partial void DispatchShaderWorker(string[] args, ref int? exit)
    {
        // wp3-shaders fills this.
    }
}
