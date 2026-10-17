using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PlayMap.Core.Model;
using PlayMap.Core.Wire;
using PlayMap.Engine.Host;
using PlayMap.Engine.Tests.Optimise;
using FfmpegFactAttribute = PlayMap.Engine.Tests.Host.FfmpegFactAttribute;

namespace PlayMap.Engine.Tests.Rendering;

/// <summary>A stopped render host holds no clip open: once its stop returns, every decoder it opened has closed (the media backend
/// lists none) and the clip's folder can be deleted — on Windows, where an open file refuses its delete, too.</summary>
public sealed class SourceReleaseTests
{
    [FfmpegFact]
    [Trait("Category", "Windows")]
    public async Task OnceTheRenderHostHasStoppedNoDecoderHoldsItsClip()
    {
        var dir = Directory.CreateTempSubdirectory("playmap-release-");
        var clip = OptimiseClips.Mpeg4WithAudio(dir.FullName, 320, 180, 2);
        var media = new MediaBackend(new NodeHome(Path.Combine(dir.FullName, "node")), NullLogger.Instance);
        var rig = HostRig.Start(HostRig.WithOutputs(HostRig.Screen("op_1", SourceRef.None, 64, 36)), media);
        try
        {
            var import = await rig.Run("MEDIA IMPORT " + WireGrammar.Quote(clip) + " Clip");
            Assert.True(import.Ok, import.Payload);
            string id;
            using (var accepted = JsonDocument.Parse(import.Payload!))
                id = accepted.RootElement.GetProperty("id").GetString()!;
            var waited = Stopwatch.StartNew();
            while (rig.Engine.Store.Project.FindMedia(id)?.Info.Width != 320)
            {
                Assert.True(waited.Elapsed < HostRig.Patience, "the import never finished");
                await Task.Delay(20);
            }
            await rig.Ok($"OUTPUT SOURCE op_1 media:{id}");
            rig.Clock.NowNs = 1_000_000_000;
            waited.Restart();
            while (!media.OpenDecoders().Any(d => d.ItemId == id && d.Source.Health.BusBytesPerFrame > 0))
            {
                Assert.True(waited.Elapsed < HostRig.Patience, "no frame was published");
                await rig.Read("op_1");
            }
        }
        finally
        {
            await rig.DisposeAsync();
        }
        Assert.Empty(media.OpenDecoders());
        Assert.DoesNotContain(rig.Engine.Journal.Recent(50), r => r.Text.Contains("did not close", StringComparison.Ordinal));
        File.Delete(clip);
        Assert.False(File.Exists(clip));
        await media.DisposeAsync();
        dir.Delete(recursive: true);
    }
}
