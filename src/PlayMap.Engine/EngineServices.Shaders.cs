using PlayMap.Core.Effects;
using PlayMap.Engine.ShaderWorker;

namespace PlayMap.Engine;

/// <summary>wp3-shaders' services: set by its extension's Configure, read by the packages named beside each.</summary>
public sealed partial record EngineServices
{
    /// <summary>The kinds this node will not draw, kept in the node's home by wp3-shaders' extension (a node composed without it
    /// holds one in memory, empty until something quarantines a kind); filled by the shader worker's stops, wp3-shaders-2's GPU-time
    /// guard and wp3-stability-2's removal breadcrumbs; lifted by FX RELEASE.</summary>
    public ShaderQuarantine Quarantine { get; init; } = new();

    /// <summary>The shader worker user effects compile in (wp3-shaders sets it; wp3-shaders-2 hands its DXIL to the device); null: no
    /// worker on this node.</summary>
    public ShaderWorkerHost? ShaderWorker { get; init; }
}
