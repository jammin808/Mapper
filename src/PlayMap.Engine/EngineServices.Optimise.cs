namespace PlayMap.Engine;

/// <summary>wp2-optimise's services: set by its extension's Configure, read by the packages named beside each.</summary>
public sealed partial record EngineServices
{
    /// <summary>Posters by content hash (THUMB reads them first).</summary>
    public IPosterStore? Posters { get; init; }

    /// <summary>Optimise on import: the job queue, its worker and its governor (MEDIA OPTIMISE, MEDIA JOBS, STATE.media.optimise).
    /// Null on a bare core: those verbs refuse with the words why.</summary>
    public Optimise.OptimiseService? Optimise { get; init; }
}
