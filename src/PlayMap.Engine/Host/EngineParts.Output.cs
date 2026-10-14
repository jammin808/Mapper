using PlayMap.Engine.Presentation;

namespace PlayMap.Engine.Host;

/// <summary>wp2-output's parts in the exe's composition (EngineParts.Standard), added in the spine's order: its preflight check.</summary>
public sealed partial record EngineParts
{
    static partial void AddOutputParts(List<EngineExtensionFactory> extensions) => extensions.Add(OutputParts.Factory);
}

/// <summary>Adds the presentation and lock check to the node's preflight; attaches nothing else (the render host is the output edge).</summary>
internal sealed class OutputParts : IEngineExtension
{
    /// <summary>The factory the exe's composition holds (one instance, so a test can find it there).</summary>
    public static readonly EngineExtensionFactory Factory = _ => new OutputParts();

    public string Name => "output";

    public EngineServices Configure(EngineServices services) =>
        services with { PreflightChecks = services.PreflightChecks.Add(new OutputPresentCheck()) };

    public Task StartAsync(EngineCore engine, CancellationToken ct) => Task.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
