using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PlayMap.Core.Bus;
using PlayMap.Core.Media;
using PlayMap.Core.Media.Decode;
using PlayMap.Core.Model;
using PlayMap.Core.Preflight;
using PlayMap.Core.Wire;
using PlayMap.Engine.Decode;
using PlayMap.Engine.Host;
using PlayMap.Engine.Tests.Rendering;
using PlayMap.Media.Hw;
using PlayMap.Media.Sources;
using FfmpegFactAttribute = PlayMap.Engine.Tests.Host.FfmpegFactAttribute;

namespace PlayMap.Engine.Tests.Decode;

/// <summary>DECODE STATUS and DECODE POLICY on a node with no render context (fields, refusals with words, a no-op refused, the
/// journal); the decode-path preflight row with its fault injected (a 4K clip in the show on a node without zero-copy: amber, the
/// path it would take, the fix line); a step down the ladder making one journal row.</summary>
public sealed class DecodeVerbTests
{
    private static readonly SessionInfo Desk = SessionInfo.Desk("t");

    private static MediaItem Clip(string id, int w, int h, string codec = "h264") =>
        new(id, id, MediaKind.Video, "/nowhere/" + id + ".mp4", "", 0,
            MediaInfo.Unknown with { Width = w, Height = h, FrameRate = "60", VideoCodec = codec, DurationNs = 10_000_000_000 }, []);

    private static Project Show(params MediaItem[] items) => HostRig.WithOutputs(
        [.. items.Select((m, i) => HostRig.Screen("op_" + (i + 1), SourceRef.Media(m.Id), 1920, 1080))]) with { Media = [.. items] };

    private static async Task<CommandReply> Run(EngineCore engine, string line)
    {
        Assert.True(WireGrammar.TryParse(line, out var parsed, out var error), error?.Reason);
        return await engine.HandleAsync(parsed, Desk, CancellationToken.None);
    }

    [Fact]
    public async Task StatusAnswersTheDecodeObjectStateCarries()
    {
        await using var engine = new EngineCore(EngineServices.ForTests(), Show());
        var reply = await Run(engine, "DECODE STATUS");
        Assert.True(reply.Ok, reply.Payload);
        using var status = JsonDocument.Parse(reply.Payload!);
        using var state = JsonDocument.Parse(engine.State().Json);
        Assert.Equal(state.RootElement.GetProperty("decode").GetRawText(), status.RootElement.GetRawText());
        var platform = status.RootElement.GetProperty("platform");
        Assert.Equal(("none", "unavailable", "n/a", "none"), (platform.GetProperty("api").GetString(), platform.GetProperty("zeroCopy").GetString(),
            platform.GetProperty("direct").GetString(), platform.GetProperty("gl").GetString()));
        Assert.Equal("no render context", platform.GetProperty("why").GetString());
        Assert.Equal(("auto", false), (status.RootElement.GetProperty("policy").GetString(), status.RootElement.GetProperty("forced").GetBoolean()));
        Assert.Equal("none", platform.GetProperty("blocks").GetProperty("staging").GetString());
        Assert.False((await Run(engine, "DECODE STATUS ALL")).Ok);
    }

    [Fact]
    public async Task PolicyAppliesForDecodersOpenedFromNowOnAndRefusesWhatChangesNothing()
    {
        await using var engine = new EngineCore(EngineServices.ForTests(), Show());
        var persisted = new List<DecodePolicy>();
        engine.DecodeFacts.Persist = p =>
        {
            persisted.Add(p);
            return null;
        };
        var reply = await Run(engine, "DECODE POLICY SOFTWARE");
        Assert.True(reply.Ok, reply.Payload);
        using (var r = JsonDocument.Parse(reply.Payload!))
        {
            Assert.Equal(("applied", "software", false, 0), (r.RootElement.GetProperty("stage").GetString(), r.RootElement.GetProperty("policy").GetString(),
                r.RootElement.GetProperty("forced").GetBoolean(), r.RootElement.GetProperty("open").GetInt32()));
            Assert.StartsWith("decoders opened from now on; 0 open keep their path", r.RootElement.GetProperty("effect").GetString(), StringComparison.Ordinal);
        }
        Assert.Equal([DecodePolicy.Software], persisted);
        Assert.Equal((DecodePolicy.Software, false), engine.DecodeFacts.Policy);
        using (var state = JsonDocument.Parse(engine.State().Json))
            Assert.Equal("software", state.RootElement.GetProperty("decode").GetProperty("policy").GetString());
        Assert.Contains(engine.Journal.Recent(20), j => j.Kind == "decode" && j.Text.StartsWith("decode policy software", StringComparison.Ordinal));

        var again = await Run(engine, "DECODE POLICY software");
        Assert.False(again.Ok);
        Assert.Equal("the decode policy is already software", again.Payload);
        Assert.Single(persisted);

        var forceAuto = await Run(engine, "DECODE POLICY AUTO FORCE");
        Assert.False(forceAuto.Ok);
        Assert.StartsWith("FORCE goes with DIRECT only", forceAuto.Payload, StringComparison.Ordinal);
        Assert.False((await Run(engine, "DECODE POLICY FAST")).Ok);
        Assert.False((await Run(engine, "DECODE POLICY")).Ok);

        // DIRECT needs no proof here (no D3D11 renderer): accepted, with the words that zero-copy is not ready on this node.
        var direct = await Run(engine, "DECODE POLICY DIRECT");
        Assert.True(direct.Ok, direct.Payload);
        using (var r = JsonDocument.Parse(direct.Payload!))
            Assert.Contains("zero-copy is not ready here: no render context", r.RootElement.GetProperty("effect").GetString(), StringComparison.Ordinal);
        var forced = await Run(engine, "DECODE POLICY DIRECT FORCE");
        Assert.False(forced.Ok);
        Assert.StartsWith("FORCE changes nothing here", forced.Payload, StringComparison.Ordinal);
        Assert.Equal((DecodePolicy.Direct, false), engine.DecodeFacts.Policy);
    }

    [Fact]
    public async Task DirectOnAnUnprovenD3d11RendererIsRefusedUnlessForced()
    {
        await using var engine = new EngineCore(EngineServices.ForTests(), Show());
        engine.DecodeFacts.UseZeroCopy(new ZeroCopyFacts("d3d11", "WARP", true, false, "", null, "no arena"));
        var refused = await Run(engine, "DECODE POLICY DIRECT");
        Assert.False(refused.Ok);
        Assert.StartsWith("direct sampling is not proven on this driver", refused.Payload, StringComparison.Ordinal);
        Assert.Equal((DecodePolicy.Auto, false), engine.DecodeFacts.Policy);
        Assert.True((await Run(engine, "DECODE POLICY DIRECT FORCE")).Ok);
        using var state = JsonDocument.Parse(engine.State().Json);
        var decode = state.RootElement.GetProperty("decode");
        Assert.Equal(("direct", true), (decode.GetProperty("policy").GetString(), decode.GetProperty("forced").GetBoolean()));
    }

    [Fact]
    public async Task APolicyThatCannotBeWrittenIsRefusedAndNothingChanges()
    {
        await using var engine = new EngineCore(EngineServices.ForTests(), Show());
        engine.DecodeFacts.Persist = _ => "disk full";
        var reply = await Run(engine, "DECODE POLICY COPY-BACK");
        Assert.False(reply.Ok);
        Assert.Equal("decode.policy could not be written: disk full", reply.Payload);
        Assert.Equal((DecodePolicy.Auto, false), engine.DecodeFacts.Policy);
    }

    private static Task<IReadOnlyList<PreflightRow>> Check(EngineCore engine) =>
        new DecodePathCheck().RunAsync(new PreflightContext(engine, PreflightDepth.Quick, false), CancellationToken.None);

    [Fact]
    public async Task A4kClipOnANodeWithoutZeroCopyIsAmberWithTheFixAndTheWordsWhy()
    {
        // The fault: 4K H.264 in the show, on a node whose renderer offers no zero-copy (here: no render context at all).
        await using var engine = new EngineCore(EngineServices.ForTests(), Show(Clip("md_4k", 3840, 2160), Clip("md_hd", 1920, 1080), Clip("md_h", 3840, 2160, "hap_q")));
        var row = Assert.Single(await Check(engine));
        Assert.Equal(("decode.cpu-4k:md_4k", "decode", Light.Amber, "hw-copy-back"), (row.Id, row.Section, row.Light, row.Value));
        Assert.Equal("MEDIA OPTIMISE md_4k TO hap-q", row.Fix);
        Assert.Equal("md_4k would copy every frame back through memory at 3840×2160 — " + ZeroCopyPlatform.None.Words, row.Note);

        // The policy is the path's ceiling: software says so.
        engine.DecodeFacts.SetPolicy(DecodePolicy.Software, false);
        var software = Assert.Single(await Check(engine));
        Assert.Equal(("software", "md_4k would decode in software at 3840×2160 — decode.policy is software"), (software.Value, software.Note));
    }

    [Fact]
    public async Task AShowWithNo4kClipOnTheCpuIsOneGreenRow()
    {
        await using var engine = new EngineCore(EngineServices.ForTests(), Show(Clip("md_hd", 1920, 1080), Clip("md_h", 4096, 2160, "hap")));
        var row = Assert.Single(await Check(engine));
        Assert.Equal(("decode.cpu-4k", Light.Green, (string?)null), (row.Id, row.Light, row.Fix));
    }

    [Fact]
    public async Task AStepDownTheLadderIsOneJournalRowAndMovesState()
    {
        var dir = Directory.CreateTempSubdirectory("playmap-decode-step-");
        try
        {
            await using var media = new MediaBackend(new NodeHome(Path.Combine(dir.FullName, "node")), NullLogger.Instance);
            await using var engine = new EngineCore(EngineServices.ForTests() with { Media = media }, Show());
            media.Attach(engine);
            var changed = 0;
            media.Changed += () => Interlocked.Increment(ref changed);
            var before = engine.Journal.Recent(100).Count(j => j.Kind == "decode");
            media.OnPathStep(Clip("md_a", 3840, 2160) with { Name = "Intro" },
                new PathStep("md_a", DecodePath.HwDirect, DecodePath.HwCopyBack, DemotionTrigger.ImportFailed, "the renderer could not import a frame: EGL_BAD_MATCH"));
            var rows = engine.Journal.Recent(100).Where(j => j.Kind == "decode").ToList();
            Assert.Equal(before + 1, rows.Count);
            Assert.Equal("Intro: hw-direct → hw-copy-back — the renderer could not import a frame: EGL_BAD_MATCH", rows[^1].Text);
            Assert.True(changed >= 1);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}

/// <summary>A real clip on a render host: the path STATE says is the path the source's frames came by (its health), DECODE STATUS
/// says the same, and the preflight row for a 4K clip reads the open decoder (no "would").</summary>
[Collection("render host")]
public sealed class DecodePathStateTests
{
    private static string Clip4K(string folder)
    {
        // mpeg4 (LGPL-compatible) at 3840×2160, two seconds at 25 fps: the size, not the codec, is what the row is about.
        var path = Path.Combine(folder, "uhd.mp4");
        var psi = new ProcessStartInfo(Clips.Ffmpeg()) { RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in new[] { "-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", "testsrc2=s=3840x2160:r=25:d=2",
                     "-c:v", "mpeg4", "-q:v", "8", "-pix_fmt", "yuv420p", path })
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0 && File.Exists(path), "ffmpeg: " + err);
        return path;
    }

    [FfmpegFact]
    public async Task TheStatePathIsThePathTheFramesCameBy()
    {
        var dir = Directory.CreateTempSubdirectory("playmap-decode-path-");
        try
        {
            FfmpegFactAttribute.UseDefaultLibraries();
            var clip = Clip4K(dir.FullName);
            await using var media = new MediaBackend(new NodeHome(Path.Combine(dir.FullName, "node")), NullLogger.Instance);
            var project = HostRig.WithOutputs(HostRig.Screen("op_1", SourceRef.None, 64, 36));
            await using var rig = HostRig.Start(project, media);
            media.Changed += rig.Engine.FactsChanged;
            var import = await rig.Run("MEDIA IMPORT " + WireGrammar.Quote(clip) + " UHD");
            Assert.True(import.Ok, import.Payload);
            string id;
            using (var accepted = JsonDocument.Parse(import.Payload!))
                id = accepted.RootElement.GetProperty("id").GetString()!;
            var waited = Stopwatch.StartNew();
            while (rig.Engine.Store.Project.FindMedia(id)?.Info.Width != 3840)
            {
                Assert.True(waited.Elapsed < HostRig.Patience, "the import never finished");
                await Task.Delay(20);
            }
            await rig.Ok($"OUTPUT SOURCE op_1 media:{id}");
            rig.Clock.NowNs = 6_000_000_000;
            await rig.Read("op_1");
            MediaBackend.OpenDecoder? open = null;
            waited.Restart();
            // Until a frame has been published: the path a source reports is its latest frame's.
            while ((open = media.OpenDecoders().FirstOrDefault(d => d.ItemId == id)) is null || open.Source.Health.BusBytesPerFrame == 0)
            {
                Assert.True(waited.Elapsed < HostRig.Patience, "no frame was published");
                await rig.Read("op_1");
            }
            var used = DecodeLadder.Word(open.Source.Health.Path);
            Assert.Contains(used, new[] { "software", "hw-copy-back" });

            rig.Engine.FactsChanged();
            using var state = JsonDocument.Parse(rig.Engine.State().Json);
            var decode = state.RootElement.GetProperty("decode");
            var source = decode.GetProperty("sources").EnumerateArray().Single(s => s.GetProperty("media").GetString() == id);
            Assert.Equal(used, source.GetProperty("path").GetString());
            Assert.False(source.GetProperty("zeroCopy").GetBoolean());
            Assert.Equal((3840, 2160), (source.GetProperty("width").GetInt32(), source.GetProperty("height").GetInt32()));
            Assert.Equal(0, source.GetProperty("surfaces").GetInt32());
            Assert.True(source.GetProperty("busMbPerFrame").GetDouble() > 12, "a 4K 4:2:0 frame through memory is over 12 MB, not " + source.GetRawText());
            // This rig's context is GLX on llvmpipe: no zero-copy, and the words say why.
            var platform = decode.GetProperty("platform");
            Assert.Equal("unavailable", platform.GetProperty("zeroCopy").GetString());
            var platformWhy = platform.GetProperty("why").GetString()!;
            Assert.False(string.IsNullOrWhiteSpace(platformWhy));
            // The clip says why it opened below the top of the ladder: the renderer's words.
            Assert.Contains(platformWhy, source.GetProperty("why").GetString(), StringComparison.Ordinal);
            Assert.Equal("amber", platform.GetProperty("light").GetString());
            Assert.StartsWith("1 clip at 4K decodes on the CPU: UHD (" + used + ")", platform.GetProperty("words").GetString(), StringComparison.Ordinal);

            var status = await rig.Run("DECODE STATUS");
            using (var s = JsonDocument.Parse(status.Payload!))
                Assert.Equal(used, s.RootElement.GetProperty("sources").EnumerateArray().Single(x => x.GetProperty("media").GetString() == id).GetProperty("path").GetString());

            var rows = await new DecodePathCheck().RunAsync(new PreflightContext(rig.Engine, PreflightDepth.Quick, false), CancellationToken.None);
            var row = Assert.Single(rows);
            Assert.Equal(("decode.cpu-4k:" + id, Light.Amber, used, $"MEDIA OPTIMISE {id} TO hap-q"), (row.Id, row.Light, row.Value, row.Fix));
            Assert.DoesNotContain("would", row.Note, StringComparison.Ordinal);
            Assert.StartsWith(string.Create(CultureInfo.InvariantCulture, $"UHD {(used == "software" ? "decodes in software" : "decodes on the GPU but copies every frame back through memory")} at 3840×2160"),
                row.Note, StringComparison.Ordinal);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}
