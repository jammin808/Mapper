using PlayMap.Core.Media;
using PlayMap.Engine.Presentation;
using PlayMap.Media.Video.Spout;

namespace PlayMap.Engine.Host;

/// <summary>wp2-output's parts in the exe's composition (EngineParts.Standard), added in the spine's order: its preflight check.</summary>
public sealed partial record EngineParts
{
    static partial void AddOutputParts(List<EngineExtensionFactory> extensions)
    {
        extensions.Add(OutputParts.Factory);
        extensions.Add(SpoutParts.Factory);
    }
}

/// <summary>Adds the presentation and lock check to the node's preflight and this machine's present settings and fuse (kept in the
/// node home, saved by PRESENT PATH through the network host); attaches nothing else (the render host is the output edge).</summary>
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
        // The node home's present settings as the file holds them (read only: the network host is the file's one writer), else this run's.
        var node = new PresentNode((setup.Home.ReadSettings() ?? setup.Settings).Present, SavePresent,
            new PresentFuse(setup.Home.Folder, () => setup.Wall.UnixMs));
        return services with { PreflightChecks = checks, PresentNode = node };
    }

    /// <summary>Keeps the present settings through the network host, the settings file's one writer (the file read fresh under its
    /// lock, so nothing else in it is lost, nor this change by another part's); a write that fails throws, and PRESENT PATH refuses with
    /// its words.</summary>
    private void SavePresent(PlayMap.Core.Bus.PresentSettings present) => setup!.Network.KeepSettings(s => s with { Present = present });

    public Task StartAsync(EngineCore engine, CancellationToken ct) => Task.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>Spout on this node (wp2-d3d11): its provider added to videoio's lists — live inputs, senders fed by the send stage, GPU
/// senders — when the node's video I/O settings have Spout on. Off Windows it is registered unavailable, so every Spout line is refused
/// "Spout is Windows only". Disposed after videoio's hub (the parts go in reverse order), which closes every Spout input and send first.</summary>
internal sealed class SpoutParts(ExtensionSetup? setup) : IEngineExtension
{
    public static readonly EngineExtensionFactory Factory = s => new SpoutParts(s);

    private SpoutProvider? _provider;

    public string Name => "spout";

    public EngineServices Configure(EngineServices services)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (!services.Settings.VideoIo.SpoutEnabled)
            return services;
        var options = new SpoutOptions(services.Settings.Name, Math.Max(100, services.Settings.VideoIo.SpoutDiscoveryMs));
        if (setup is not null)
            options = options with { UnixMs = () => setup.Wall.UnixMs };
        _provider = SpoutProvider.ForThisMachine(options, services.Mono);
        return services with
        {
            LiveInputs = [.. services.LiveInputs, _provider],
            SendProviders = [.. services.SendProviders, _provider],
            GpuSendProviders = [.. services.GpuSendProviders, (ISendSinkProvider)_provider],
        };
    }

    public Task StartAsync(EngineCore engine, CancellationToken ct) => Task.CompletedTask;

    public ValueTask DisposeAsync()
    {
        _provider?.Dispose();
        _provider = null;
        return ValueTask.CompletedTask;
    }
}
