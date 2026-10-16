using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using PlayMap.Core.Model;
using PlayMap.Core.Wire;
using PlayMap.Engine.Host;
using FfmpegFactAttribute = PlayMap.Engine.Tests.Host.FfmpegFactAttribute;
using PlayMap.Engine.Tests.Rendering;
using PlayMap.Engine.Tests.Spine;
using Xunit.Abstractions;

namespace PlayMap.Engine.Tests.Decode;

/// <summary>HAP through a node, headless on llvmpipe: MEDIA IMPORT of a HAP Q clip FFmpeg's encoder made, OUTPUT SOURCE, and the
/// output read back shows the clip's frame for the leader's time; STATE.decode says what the renderer samples and the decoder's
/// path and HAP facts; MEDIA INSPECT reads the clip; decode.json and decode.cards.json are written here.</summary>
[Collection("render host")]
public sealed class HapEndToEndTests(ITestOutputHelper log)
{
    private static string FixturePath(string name) => Path.Combine(NodeExe.RepoRoot(), "tests", "fixtures", "state", name);

    /// <summary>A 64 × 48 HAP Q clip at 25 fps, three seconds, every frame its index painted (eight 8-pixel blocks in the top 24
    /// rows, block b white when bit b of the frame number is set).</summary>
    private static string CountedHapQ(string folder)
    {
        var path = Path.Combine(folder, "counted-hapq.mov");
        var painter = "geq=lum='if(lt(Y,24),if(mod(floor(N/pow(2,floor(X/8))),2),235,16),128)':cb=128:cr=128,format=rgba";
        var psi = new ProcessStartInfo(Clips.Ffmpeg()) { RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in new[] { "-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", "color=c=black:s=64x48:r=25:d=3", "-vf", painter,
                     "-c:v", "hap", "-format", "hap_q", path })
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0 && File.Exists(path), "ffmpeg: " + err);
        return path;
    }

    /// <summary>The decode block of STATE with what moves between runs (timings, counters, ids) set to fixed values.</summary>
    internal static string Normalise(JsonElement decode)
    {
        var node = JsonNode.Parse(decode.GetRawText())!.AsObject();
        if (node["platform"] is JsonObject platform)
        {
            // The free part of the arena moves with the moment.
            if (platform["blocks"] is JsonObject b)
                b["arenaFreeMb"] = 255;
            // The fixture is written on Linux under X: this machine's own binding words read as GLX's.
            HostRig.DecodeBindingAsFixture(platform);
        }
        if (node["platform"]?["hapLayers4k60"] is JsonObject layers)
        {
            layers["value"] = 9;
            layers["measuredAt"] = 1_760_000_000_000;
            layers["decodeMs"] = 7.4;
            layers["cores"] = 4;
            layers["words"] = "4K HAP Q second stage 7.4 ms a frame on one core; cores 4 × 16.7 ms ÷ 7.4 ms = 9; disk not measured; upload not measured";
        }
        foreach (var s in node["sources"]!.AsArray())
        {
            var o = s!.AsObject();
            o["key"] = "md_fixture";
            o["media"] = "md_fixture";
            o["decodeMsP95"] = 0;
            o["dropped"] = 0;
            o["late"] = 0;
            if (o["hap"] is JsonObject hap)
                hap["stageMsP95"] = 0;
        }
        var root = new JsonObject { ["decode"] = node };
        return root.ToJsonString(FixtureJson.Indented) + "\n";
    }

    /// <summary>What the desk's generic card template renders for STATE.decode: title, STATE path, light, words, fields, verb lines.</summary>
    internal const string Cards = """
        [
          {
            "id": "decode.platform",
            "title": "Decode",
            "path": "decode.platform",
            "light": "decode.platform.light",
            "words": "decode.platform.words",
            "fields": [
              { "label": "HAP layers at 4K60", "path": "decode.platform.hapLayers4k60.value" },
              { "label": "Limit", "path": "decode.platform.hapLayers4k60.limit" },
              { "label": "BC1 / BC3", "path": "decode.platform.blocks.bc3" },
              { "label": "BC4", "path": "decode.platform.blocks.bc4" },
              { "label": "BC7 / BC6H", "path": "decode.platform.blocks.bc7" },
              { "label": "Staging", "path": "decode.platform.blocks.staging" },
              { "label": "Upload arena MB", "path": "decode.platform.blocks.arenaMb" },
              { "label": "Zero-copy", "path": "decode.platform.zeroCopy" },
              { "label": "Why not", "path": "decode.platform.why" },
              { "label": "Direct sampling", "path": "decode.platform.direct" },
              { "label": "Policy", "path": "decode.policy" }
            ],
            "actions": [ "PREFLIGHT QUICK", "DECODE STATUS", "DECODE POLICY AUTO", "DECODE POLICY SOFTWARE" ]
          },
          {
            "id": "decode.source",
            "title": "Decoder",
            "path": "decode.sources[]",
            "key": "media",
            "light": "light",
            "words": "why",
            "fields": [
              { "label": "Codec", "path": "codec" },
              { "label": "Path", "path": "path" },
              { "label": "Device", "path": "device" },
              { "label": "Zero-copy", "path": "zeroCopy" },
              { "label": "Decode ms p95", "path": "decodeMsP95" },
              { "label": "Texture", "path": "hap.texture" },
              { "label": "Chunks", "path": "hap.chunks" },
              { "label": "Refused", "path": "hap.corrupt" }
            ],
            "actions": [ "MEDIA INSPECT {media}" ]
          }
        ]
        """;

    [Fact]
    public async Task AHapClipImportedAndRoutedShowsTheFrameOfTheLeadersTime()
    {
        var dir = Directory.CreateTempSubdirectory("playmap-hap-e2e-");
        try
        {
            FfmpegFactAttribute.UseDefaultLibraries();
            var clip = CountedHapQ(dir.FullName);
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
            while (rig.Engine.Store.Project.FindMedia(id)?.Info.VideoCodec != "hap_q")
            {
                Assert.True(waited.Elapsed < HostRig.Patience, "the import never finished");
                await Task.Delay(20);
            }
            var item = rig.Engine.Store.Project.FindMedia(id)!;
            Assert.Equal((64, 48, "ycocg-bc3"), (item.Info.Width, item.Info.Height, item.Info.PixelFormat));

            await rig.Ok($"OUTPUT SOURCE op_1 media:{id}");
            // A library item shown directly loops on the leader's clock: frame n covers [n, n + 1) × 40 ms of each 3 s.
            // The last request is not the end: a clip that reached its end says "end" (a fact the fixture would carry).
            foreach (var frame in new[] { 25, 62, 74, 13 })
            {
                rig.Clock.NowNs = 6_000_000_000 + (long)((frame + 0.5) * 40_000_000);
                var px = await rig.ReadUntil("op_1", p => Clips.IndexIn(p, 64) == frame);
                Assert.Equal(frame, Clips.IndexIn(px, 64));
            }

            rig.Engine.FactsChanged();
            using var state = JsonDocument.Parse(rig.Engine.State().Json);
            var decode = state.RootElement.GetProperty("decode");
            var blocks = decode.GetProperty("platform").GetProperty("blocks");
            Assert.True(blocks.GetProperty("bc1").GetBoolean() && blocks.GetProperty("bc3").GetBoolean() && blocks.GetProperty("bc4").GetBoolean()
                        && blocks.GetProperty("bc7").GetBoolean() && blocks.GetProperty("bc6h").GetBoolean());
            // The render context's upload arena (wp2-zerocopy): llvmpipe maps persistent buffers, so the HAP frames were written
            // into it by the decode thread and uploaded from it.
            Assert.Equal("persistent", blocks.GetProperty("staging").GetString());
            Assert.Equal(256, blocks.GetProperty("arenaMb").GetInt32());
            var source = Assert.Single(decode.GetProperty("sources").EnumerateArray());
            Assert.Equal(id, source.GetProperty("media").GetString());
            Assert.Equal("hap_q", source.GetProperty("codec").GetString());
            Assert.Equal("hap-gpu", source.GetProperty("path").GetString());
            Assert.True(source.GetProperty("zeroCopy").GetBoolean());
            Assert.Equal("green", source.GetProperty("light").GetString());
            var hap = source.GetProperty("hap");
            Assert.Equal("ycocg-bc3", hap.GetProperty("texture").GetString());
            Assert.Equal(0, hap.GetProperty("corrupt").GetInt64());
            Assert.Equal(0.003, hap.GetProperty("uploadMb").GetDouble());   // 16 × 12 blocks of 16 bytes
            Assert.Equal("persistent", hap.GetProperty("staging").GetString());
            // The binding the render context came through: GLFW on an X server (this rig's Xvfb) makes it with GLX.
            var gl = state.RootElement.GetProperty("machine").GetProperty("gl").GetString();
            if (OperatingSystem.IsLinux() && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")))
                Assert.Equal("glx", gl);
            else
                Assert.Contains(gl, new[] { "egl", "glx", "wgl", "cgl" });
            Assert.Equal("green", decode.GetProperty("platform").GetProperty("light").GetString());

            var inspect = await rig.Run("MEDIA INSPECT " + id);
            Assert.True(inspect.Ok, inspect.Payload);
            log.WriteLine(inspect.Payload);
            using (var reply = JsonDocument.Parse(inspect.Payload!))
            {
                var r = reply.RootElement;
                Assert.Equal(("hap_q", "ycocg-bc3", 64, 48, false), (r.GetProperty("codec").GetString(), r.GetProperty("texture").GetString(),
                    r.GetProperty("width").GetInt32(), r.GetProperty("height").GetInt32(), r.GetProperty("oddSize").GetBoolean()));
                Assert.Equal(75, r.GetProperty("frames").GetInt64());
                Assert.Equal(0, r.GetProperty("refused").GetInt64());
                Assert.Equal(1, r.GetProperty("chunks").GetProperty("typical").GetInt32());
                Assert.Equal(1.0, r.GetProperty("secondStage").GetProperty("snappy").GetDouble() + r.GetProperty("secondStage").GetProperty("raw").GetDouble(), 3);
                var budget = r.GetProperty("budget");
                Assert.Contains(budget.GetProperty("limit").GetString(), new[] { "disk", "cores", "upload" });
                Assert.True(budget.GetProperty("layers").GetInt32() > 0);
                Assert.Equal(JsonValueKind.Array, r.GetProperty("fixes").ValueKind);
            }

            var fixture = Normalise(decode);
            var path = FixturePath("decode.json");
            var cardsPath = FixturePath("decode.cards.json");
            var cards = JsonNode.Parse(Cards)!.ToJsonString(FixtureJson.Indented) + "\n";
            if (Environment.GetEnvironmentVariable("PLAYMAP_WRITE_FIXTURES") == "1")
            {
                await File.WriteAllTextAsync(path, fixture);
                await File.WriteAllTextAsync(cardsPath, cards);
            }
            Assert.Equal((await File.ReadAllTextAsync(path)).ReplaceLineEndings("\n"), fixture);
            Assert.Equal((await File.ReadAllTextAsync(cardsPath)).ReplaceLineEndings("\n"), cards);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}
