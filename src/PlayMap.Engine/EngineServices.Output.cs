using PlayMap.Engine.Presentation;

namespace PlayMap.Engine;

/// <summary>wp2-output's services (wp2-d3d11 continues them): set by the output part's Configure, read by PRESENT PATH, PRESENT RESET,
/// <c>machine.present</c> and the preflight's present row.</summary>
public sealed partial record EngineServices
{
    /// <summary>This machine's present settings and fuse (null: a node with no home, as in most tests — PRESENT PATH refuses).</summary>
    public PresentNode? PresentNode { get; init; }
}
