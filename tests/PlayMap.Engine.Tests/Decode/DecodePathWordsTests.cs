using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PlayMap.Core.Model;
using PlayMap.Core.Wire;
using PlayMap.Engine.Host;
using PlayMap.Engine.Tests.Rendering;
using FfmpegFactAttribute = PlayMap.Engine.Tests.Host.FfmpegFactAttribute;

namespace PlayMap.Engine.Tests.Decode;

/// <summary>An H.264 clip on a node, on this machine as it is: the output shows the clip's frame for the leader's time, and
/// STATE.decode.sources[] says the decoder's path and why it is not on a better one. On a machine without libva (CI, a bare
/// Ubuntu; tools/without-libs.sh makes one) VA-API is skipped with those words instead of aborting the node.</summary>
[Collection("render host")]
public sealed class DecodePathWordsTests
{
    [Fact]
    public async Task AClipPlaysAndStateSaysWhyItsDecoderIsNotOnABetterPath()
    {
        var dir = Directory.CreateTempSubdirectory("playmap-decode-words-");
        try
        {
            FfmpegFactAttribute.UseDefaultLibraries();
            var clip = Clips.Counted(3);
            await using var media = new MediaBackend(new NodeHome(Path.Combine(dir.FullName, "node")), NullLogger.Instance);
            var project = HostRig.WithOutputs(HostRig.Screen("op_1", SourceRef.None, 64, 48));
            await using var rig = HostRig.Start(project, media);
            media.Changed += rig.Engine.FactsChanged;

            var import = await rig.Run("MEDIA IMPORT " + WireGrammar.Quote(clip) + " Counted");
            Assert.True(import.Ok, import.Payload);
            string id;
            using (var accepted = JsonDocument.Parse(import.Payload!))
                id = accepted.RootElement.GetProperty("id").GetString()!;
            var waited = Stopwatch.StartNew();
            while (rig.Engine.Store.Project.FindMedia(id)?.Info.VideoCodec != "h264")
            {
                Assert.True(waited.Elapsed < HostRig.Patience, "the import never finished");
                await Task.Delay(20);
            }

            await rig.Ok($"OUTPUT SOURCE op_1 media:{id}");
            rig.Clock.NowNs = 6_000_000_000 + (long)(37.5 * 40_000_000);
            var px = await rig.ReadUntil("op_1", p => Clips.IndexIn(p, 64) == 37);
            Assert.Equal(37, Clips.IndexIn(px, 64));

            rig.Engine.FactsChanged();
            using var state = JsonDocument.Parse(rig.Engine.State().Json);
            var source = Assert.Single(state.RootElement.GetProperty("decode").GetProperty("sources").EnumerateArray());
            Assert.Equal(id, source.GetProperty("media").GetString());
            Assert.Equal("green", source.GetProperty("light").GetString());
            var path = source.GetProperty("path").GetString();
            var why = source.GetProperty("why").GetString()!;
            if (!OperatingSystem.IsLinux())
                return;
            var missing = new[] { "libva.so.2", "libva-drm.so.2" }.FirstOrDefault(l => !NativeLibrary.TryLoad(l, out _));
            if (missing is not null)
            {
                Assert.StartsWith($"vaapi skipped: {missing} is not on this machine", why, StringComparison.Ordinal);
                // The next rung: CUDA either decodes (and says nothing) or says why not, and the clip is in software.
                Assert.Equal(why.Contains("cuda", StringComparison.Ordinal) ? "software" : "hw-copy-back", path);
            }
            else
            {
                Assert.DoesNotContain("skipped", why, StringComparison.Ordinal);
            }
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}
