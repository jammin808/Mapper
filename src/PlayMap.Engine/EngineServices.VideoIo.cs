using System.Collections.Immutable;
using PlayMap.Core.Media;

namespace PlayMap.Engine;

/// <summary>wp2-videoio's services: set by its extension's Configure, read by the packages named beside each.</summary>
public sealed partial record EngineServices
{
    /// <summary>Live input providers (NDI, OMT; Spout adds its own).</summary>
    public ImmutableArray<ILiveInputProvider> LiveInputs { get; init; } = [];

    /// <summary>Network send providers.</summary>
    public ImmutableArray<ISendProvider> SendProviders { get; init; } = [];

    /// <summary>GPU send providers (texture sharing).</summary>
    public ImmutableArray<ISendSinkProvider> GpuSendProviders { get; init; } = [];
}
