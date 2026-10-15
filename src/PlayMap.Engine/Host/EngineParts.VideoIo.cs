using PlayMap.Core.Bus;
using PlayMap.Core.Media;
using PlayMap.Engine.VideoIo;
using PlayMap.Media.Video.Omt;

namespace PlayMap.Engine.Host;

/// <summary>wp2-videoio's parts in the exe's composition (EngineParts.Standard), added in the spine's order.</summary>
public sealed partial record EngineParts
{
    static partial void AddVideoIoParts(List<EngineExtensionFactory> extensions)
    {
        extensions.Add(VideoIoExtension.Factory);
    }
}

/// <summary>The node's network video: the providers this machine has (OMT always; NDI when the user's runtime loads) added to the
/// services' lists — a package after this one (Spout) adds its own the same way — the preflight section, and the hub started on the
/// engine with this machine's settings kept by it. Disposed after the transports close: the hub closes every receiver and sender,
/// then the providers go.</summary>
internal sealed class VideoIoExtension(ExtensionSetup setup) : IEngineExtension
{
    /// <summary>The part as the exe's composition adds it.</summary>
    public static readonly EngineExtensionFactory Factory = s => new VideoIoExtension(s);

    private readonly List<IDisposable> _providers = [];
    private VideoIoHub? _hub;

    public string Name => "videoio";

    public EngineServices Configure(EngineServices services)
    {
        ArgumentNullException.ThrowIfNull(services);
        var inputs = new List<ILiveInputProvider>(services.LiveInputs);
        var sends = new List<ISendProvider>(services.SendProviders);
        foreach (var provider in VideoIoProviders.Make(services, setup))
        {
            _providers.Add(provider);
            if (provider is ILiveInputProvider input)
                inputs.Add(input);
            if (provider is ISendProvider send)
                sends.Add(send);
        }
        return services with
        {
            LiveInputs = [.. inputs],
            SendProviders = [.. sends],
            PreflightChecks = [.. services.PreflightChecks, new VideoIoPreflight()],
        };
    }

    public Task StartAsync(EngineCore engine, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(engine);
        _hub = engine.VideoIo;
        _hub.Save = SaveNode;
        _hub.Start();
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _hub?.Dispose();
        foreach (var provider in _providers)
            provider.Dispose();
        _providers.Clear();
        return ValueTask.CompletedTask;
    }

    private void SaveNode(VideoIoSettings videoIo)
    {
        var current = setup.Home.LoadSettings(setup.Settings.Name);
        setup.Home.SaveSettings(current with { VideoIo = videoIo });
    }
}

/// <summary>The providers this machine makes, in the order STATE lists them.</summary>
internal static class VideoIoProviders
{
    public static IReadOnlyList<IDisposable> Make(EngineServices services, ExtensionSetup? setup)
    {
        ArgumentNullException.ThrowIfNull(services);
        var made = new List<IDisposable>();
        var settings = services.Settings.VideoIo;
        var home = setup?.Home.Folder ?? Path.Combine(Path.GetTempPath(), "playmap-" + services.Settings.NodeId);
        if (settings.OmtEnabled)
            made.Add(new OmtProvider(new OmtOptions(Path.Combine(home, "omt"), settings.OmtDiscovery, settings.OmtPortStart, settings.OmtPortEnd), services.Mono));
        return made;
    }
}
