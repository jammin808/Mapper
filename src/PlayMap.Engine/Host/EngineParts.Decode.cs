using PlayMap.Engine.Decode;

namespace PlayMap.Engine.Host;

/// <summary>wp2-decode's parts in the exe's composition (EngineParts.Standard), added in the spine's order: the HAP layers
/// preflight check.</summary>
public sealed partial record EngineParts
{
    static partial void AddDecodeParts(List<EngineExtensionFactory> extensions)
    {
        extensions.Add(_ => new DecodeExtension());
    }

    private sealed class DecodeExtension : IEngineExtension
    {
        public string Name => "decode";

        public EngineServices Configure(EngineServices services) =>
            services with { PreflightChecks = [.. services.PreflightChecks, new HapLayersCheck()] };

        public Task StartAsync(EngineCore engine, CancellationToken ct) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
