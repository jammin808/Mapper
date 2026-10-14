namespace PlayMap.Engine.Host;

/// <summary>wp2-optimise's entry points beside "engine" (its worker process).</summary>
public static partial class EngineEntry
{
    static partial void DispatchOptimise(string[] args, ref int? exit)
    {
        // wp2-optimise fills this.
    }
}
