using PlayMap.Engine.Decode;

namespace PlayMap.Engine.Host;

/// <summary>wp2-decode's parts in the exe's composition (EngineParts.Standard), added in the spine's order: the HAP layers
/// preflight check, the decode-path check, and decode.policy written back when DECODE POLICY changes it.</summary>
public sealed partial record EngineParts
{
    static partial void AddDecodeParts(List<EngineExtensionFactory> extensions)
    {
        extensions.Add(DecodeFactory);
    }

    /// <summary>The decode parts' factory the exe's composition holds (one instance, so a test can compose a node with it).</summary>
    internal static readonly EngineExtensionFactory DecodeFactory = setup => new DecodeExtension(setup);

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

        /// <summary>Keeps decode.policy through the network host, the settings file's one writer (the file read fresh under its lock,
        /// so nothing else in it is lost, nor this change by another part's); null, or the words of a failure (nothing changed).</summary>
        private string? Persist(PlayMap.Core.Media.Decode.DecodePolicy policy)
        {
            var word = PlayMap.Core.Media.Decode.DecodeLadder.Word(policy);
            try
            {
                setup.Network.KeepSettings(s => s with { Decode = s.Decode with { Policy = word } });
                return null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
            {
                return ex.Message;
            }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
