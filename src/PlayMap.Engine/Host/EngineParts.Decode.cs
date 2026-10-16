using PlayMap.Engine.Decode;

namespace PlayMap.Engine.Host;

/// <summary>wp2-decode's parts in the exe's composition (EngineParts.Standard), added in the spine's order: the HAP layers
/// preflight check, the decode-path check, and decode.policy written back when DECODE POLICY changes it.</summary>
public sealed partial record EngineParts
{
    static partial void AddDecodeParts(List<EngineExtensionFactory> extensions)
    {
        extensions.Add(setup => new DecodeExtension(setup));
    }

    private sealed class DecodeExtension(ExtensionSetup setup) : IEngineExtension
    {
        public string Name => "decode";

        public EngineServices Configure(EngineServices services) =>
            services with { PreflightChecks = [.. services.PreflightChecks, new HapLayersCheck(), new DecodePathCheck()] };

        public Task StartAsync(EngineCore engine, CancellationToken ct)
        {
            engine.DecodeFacts.Persist = Persist;
            return Task.CompletedTask;
        }

        /// <summary>Writes decode.policy into the node's settings file (read fresh, so nothing else in it is lost).</summary>
        private string? Persist(PlayMap.Core.Media.Decode.DecodePolicy policy)
        {
            try
            {
                var current = setup.Home.LoadSettings(setup.Settings.Name);
                setup.Home.SaveSettings(current with { Decode = current.Decode with { Policy = PlayMap.Core.Media.Decode.DecodeLadder.Word(policy) } });
                return null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return ex.Message;
            }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
