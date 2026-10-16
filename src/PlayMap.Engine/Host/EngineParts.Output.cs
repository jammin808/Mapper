using PlayMap.Engine.Presentation;

namespace PlayMap.Engine.Host;

/// <summary>wp2-output's parts in the exe's composition (EngineParts.Standard), added in the spine's order: its preflight check.</summary>
public sealed partial record EngineParts
{
    static partial void AddOutputParts(List<EngineExtensionFactory> extensions) => extensions.Add(OutputParts.Factory);
}

/// <summary>Adds the presentation and lock check to the node's preflight and this machine's present settings and fuse (kept in the
/// node home, saved by PRESENT PATH); attaches nothing else (the render host is the output edge).</summary>
internal sealed class OutputParts(ExtensionSetup? setup) : IEngineExtension
{
    /// <summary>The factory the exe's composition holds (one instance, so a test can find it there).</summary>
    public static readonly EngineExtensionFactory Factory = s => new OutputParts(s);

    public string Name => "output";

    public EngineServices Configure(EngineServices services)
    {
        ArgumentNullException.ThrowIfNull(services);
        var checks = services.PreflightChecks.Add(new OutputPresentCheck());
        // A composition with no node home (a test's) keeps no present settings: PRESENT PATH says so.
        if (setup is null)
            return services with { PreflightChecks = checks };
        var node = new PresentNode(setup.Home.LoadSettings(setup.Settings.Name).Present, SavePresent,
            new PresentFuse(setup.Home.Folder, () => setup.Wall.UnixMs));
        return services with { PreflightChecks = checks, PresentNode = node };
    }

    private void SavePresent(PlayMap.Core.Bus.PresentSettings present)
    {
        var current = setup!.Home.LoadSettings(setup.Settings.Name);
        setup.Home.SaveSettings(current with { Present = present });
    }

    public Task StartAsync(EngineCore engine, CancellationToken ct) => Task.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
