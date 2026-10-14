using PlayMap.Engine.Audio;

namespace PlayMap.Engine.Host;

/// <summary>wp2-audio's parts in the exe's composition (EngineParts.Standard), added in the spine's order.</summary>
public sealed partial record EngineParts
{
    static partial void AddAudioParts(List<EngineExtensionFactory> extensions)
    {
        extensions.Add(AudioExtension.Factory);
    }
}

/// <summary>The node's sound: the runtime with this machine's backends (FFmpeg's capture devices; WASAPI on Windows) and paced test
/// inputs, handed to the engine and to the packages after this one (the hub, the bus, the frames), its preflight check, and its
/// choices kept in the node's settings. Disposed after the transports close, which closes every open input.</summary>
internal sealed class AudioExtension(ExtensionSetup setup) : IEngineExtension
{
    /// <summary>The part as the exe's composition adds it.</summary>
    public static readonly EngineExtensionFactory Factory = s => new AudioExtension(s);

    private AudioRuntime? _runtime;

    public string Name => "audio";

    public EngineServices Configure(EngineServices services)
    {
        ArgumentNullException.ThrowIfNull(services);
        var runtime = new AudioRuntime(services.Mono, services.Clock, paced: true, services.Settings.Audio)
        {
            SaveNode = SaveNode,
        };
        AudioBackends.AddTo(runtime, services.Settings.Audio.Backend);
        _runtime = runtime;
        return services with
        {
            AudioRuntime = runtime,
            Modulation = runtime.RenderBus,
            AudioFrames = runtime.RenderBus,
            AudioInputs = runtime.Hub,
            PreflightChecks = [.. services.PreflightChecks, new AudioPreflight()],
        };
    }

    public Task StartAsync(EngineCore engine, CancellationToken ct) => Task.CompletedTask;

    public ValueTask DisposeAsync()
    {
        _runtime?.Dispose();
        return ValueTask.CompletedTask;
    }

    private void SaveNode(PlayMap.Core.Bus.AudioNodeSettings audio)
    {
        var current = setup.Home.LoadSettings(setup.Settings.Name);
        setup.Home.SaveSettings(current with { Audio = audio });
    }
}
