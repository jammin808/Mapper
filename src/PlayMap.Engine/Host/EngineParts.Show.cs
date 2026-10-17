using PlayMap.Media.Audio;

namespace PlayMap.Engine.Host;

/// <summary>wp2-show's parts in the exe's composition (EngineParts.Standard), added in the spine's order.</summary>
public sealed partial record EngineParts
{
    static partial void AddShowParts(List<EngineExtensionFactory> extensions)
    {
        // The show lives in the engine, and the services' gate (EngineServices.ShowGate) is bound to it at construction. Its one
        // part is the air's sound (wp2-blind): the machine's output, or none when the node's settings say off.
        extensions.Add(EngineExtension.OnStart("sound", (engine, _) =>
        {
            var device = engine.Services.Settings.Show.AirSound;
            if (device.Equals("off", StringComparison.OrdinalIgnoreCase))
                return Task.FromResult<IAsyncDisposable?>(null);
            var output = AudioOut.OpenDevice(device.Length == 0 || device.Equals("default", StringComparison.OrdinalIgnoreCase) ? null : device,
                engine.Services.Clock.Local);
            return Task.FromResult<IAsyncDisposable?>(engine.StartAirAudio(output));
        }));
    }
}
