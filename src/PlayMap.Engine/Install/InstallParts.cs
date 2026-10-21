using PlayMap.Engine.Host;

namespace PlayMap.Engine.Install;

/// <summary>wp3-installer's part in the exe's composition (Host/EngineParts.Install.cs): preflight's <c>install</c> section
/// (<see cref="InstallPreflight"/>), read from the same facts STATE's <c>machine.install</c> publishes.</summary>
public sealed class InstallParts : IEngineExtension
{
    /// <summary>The exe's install part.</summary>
    public static readonly EngineExtensionFactory Factory = _ => new InstallParts();

    public string Name => "install";

    public EngineServices Configure(EngineServices services)
    {
        ArgumentNullException.ThrowIfNull(services);
        return services with { PreflightChecks = services.PreflightChecks.Add(new InstallPreflight()) };
    }

    public Task StartAsync(EngineCore engine, CancellationToken ct) => Task.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
