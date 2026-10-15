namespace PlayMap.Engine;

/// <summary>wp2-align's services: the runner ALIGN RUN starts (the host makes it with the render host as its display; a bare core
/// makes one that only the simulated camera can run).</summary>
public sealed partial record EngineServices
{
    public Align.IAlignBackend? Align { get; init; }
}
