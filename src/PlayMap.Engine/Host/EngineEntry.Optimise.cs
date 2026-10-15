using PlayMap.Engine.Optimise;

namespace PlayMap.Engine.Host;

/// <summary>wp2-optimise's entry points beside "engine" (its worker process).</summary>
public static partial class EngineEntry
{
    /// <summary>"optimise-worker": the encoder's own process, spoken to in JSON lines on its standard input and output
    /// (<see cref="OptimiseWorker"/>); it ends when its input closes.</summary>
    static partial void DispatchOptimise(string[] args, ref int? exit)
    {
        if (exit is null && args.Length > 0 && string.Equals(args[0], OptimiseWorker.Word, StringComparison.OrdinalIgnoreCase))
            exit = OptimiseWorker.Run(args);
    }
}
