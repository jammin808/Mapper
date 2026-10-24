using PlayMap.Engine.ShaderWorker;

namespace PlayMap.Engine.Host;

/// <summary>wp3-shaders' entry point beside "engine": the shader worker's own process, where user effects compile.</summary>
public static partial class EngineEntry
{
    /// <summary>"shader-worker": the compilers' own process, spoken to in JSON lines on its standard input and output
    /// (<see cref="ShaderWorkerMain"/>); it ends when its input closes.</summary>
    static partial void DispatchShaderWorker(string[] args, ref int? exit)
    {
        if (exit is null && args.Length > 0 && string.Equals(args[0], ShaderWorkerMain.Word, StringComparison.OrdinalIgnoreCase))
            exit = ShaderWorkerMain.Run(args);
    }
}
