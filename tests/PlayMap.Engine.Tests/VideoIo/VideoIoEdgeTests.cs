using System.Diagnostics;
using System.Text.Json;
using PlayMap.Core.Bus;
using PlayMap.Core.Media;
using PlayMap.Core.Model;
using PlayMap.Core.Preflight;
using PlayMap.Engine.Playback;
using PlayMap.Engine.Rendering;
using PlayMap.Engine.VideoIo;

namespace PlayMap.Engine.Tests.VideoIo;

/// <summary>The edges of live inputs and sends: a camera rig's feed follows its source; a cold input is received only while shown;
/// tally is program only for what the air draws and the feed left by a takeover goes dark; generic edits hold a send to its tap and
/// the bandwidth guard as SEND ADD does; a fallback never shows a live input, however deep, and one that went is black; a packed
/// target never outgrows a texture; a GPU send provider's protocol is not failed for having no read-back sender.</summary>
public sealed class VideoIoEdgeTests
{
    private sealed class HubMedia(VideoIoHub hub) : IMediaBackend
    {
        public Task<MediaImport> ImportAsync(string location, IProgress<double>? progress, CancellationToken ct) => throw new NotSupportedException();

        public IVideoSource? Open(MediaItem item) => hub.Open(item);

        public MediaReadiness Readiness(MediaItem item) => new(true, true, true, true, "ready");

        public string? PathForHash(string contentHash) => null;
    }

    /// <summary>A GPU send provider (Spout's kind) that is ready and is never asked to make anything here.</summary>
    private sealed class GpuProvider : ISendSinkProvider
    {
        public SendProtocol Protocol => SendProtocol.Spout;

        public ProviderStatus Status => new("ready", "fake gpu 1.0", "", "", "");

        public IGpuSendTarget Create(VideoSend send, string publishedName, IGpuShare gpu) => throw new NotSupportedException();

        public void Dispose()
        {
            // Nothing made.
        }
    }

    private static MediaItem Stream(string id, string location) => new(id, id, MediaKind.Stream, location, "", 0, MediaInfo.Unknown, []);

    [Fact]
    public async Task ACameraRigsFeedFollowsItsSourceToAnotherSender()
    {
        await using var rig = new VideoIoRig();
        // A rig's item is made by the renderer's sources, never in the library: no reconcile sees it.
        var a = rig.Engine.VideoIo.Open(Stream("cr_1", "omt:A (Cam)"))!;
        var first = rig.Omt.Opened.Single();
        var b = rig.Engine.VideoIo.Open(Stream("cr_1", "omt:B (Cam)"))!;
        Assert.True(first.Closed);
        var second = rig.Omt.Opened.Last();
        Assert.Equal("B (Cam)", second.Address);
        second.Push(luma: 77);
        using (var lease = b.Acquire(0)!)
            Assert.Equal(64, lease.Width);
        a.Dispose();
        rig.Engine.VideoIo.Tick();
        Assert.False(second.Closed);
        b.Dispose();
        rig.Engine.VideoIo.Tick();
        Assert.True(second.Closed);
    }

    [Fact]
    public async Task AColdInputIsReceivedOnlyWhileShownAndReconnectAllLeavesItClosed()
    {
        await using var rig = new VideoIoRig();
        var r = await rig.Ok("INPUT ADD \"omt:STUDIO (Cam 3)\" COLD");
        var cold = VideoIoRig.IdOf(r);
        Assert.Equal("connecting", JsonDocument.Parse(r.Payload!).RootElement.GetProperty("state").GetString());
        var warm = VideoIoRig.IdOf(await rig.Ok("INPUT ADD \"omt:STUDIO (Cam 4)\""));
        var warmFeed = rig.Omt.Opened.Single();
        // The warm one is lost; ALL reconnects it and leaves the cold one, which nothing shows, closed.
        warmFeed.Push();
        rig.AdvanceMs(1_200);
        // Answered at the first frame from the new receiver: frames are pushed on it until the answer comes.
        var reconnect = rig.Run("INPUT RECONNECT ALL");
        while (!reconnect.IsCompleted)
        {
            if (rig.Omt.Opened.Count == 2)
                rig.Omt.Opened[1].Push();
            await Task.Delay(5);
        }
        Assert.Equal("reconnected Cam 4: frames are arriving", (await reconnect).Effect);
        Assert.Equal(2, rig.Omt.Opened.Count);
        Assert.All(rig.Omt.Opened, f => Assert.Equal("STUDIO (Cam 4)", f.Address));
        var handle = rig.Engine.VideoIo.Open(rig.Engine.Store.Project.FindMedia(cold)!)!;
        var coldFeed = rig.Omt.Opened.Single(f => f.Address == "STUDIO (Cam 3)");
        rig.Engine.VideoIo.Tick();
        Assert.False(coldFeed.Closed);
        // Its last consumer lets go: it closes a second later, without waiting for an edit (a consumer that only swaps its handle
        // within the second, as the renderer does on an edit of the item, keeps it open).
        handle.Dispose();
        rig.Engine.VideoIo.Tick();
        rig.AdvanceMs(500);
        using (rig.Engine.VideoIo.Open(rig.Engine.Store.Project.FindMedia(cold)!)!)
            rig.Engine.VideoIo.Tick();
        rig.AdvanceMs(900);
        rig.Engine.VideoIo.Tick();
        Assert.False(coldFeed.Closed);
        rig.AdvanceMs(1_000);
        rig.Engine.VideoIo.Tick();
        Assert.True(coldFeed.Closed);
        Assert.Equal("connecting", rig.Input(cold).GetProperty("state").GetString());
        rig.Omt.Opened.Last(f => f.Address == "STUDIO (Cam 4)").Push();
        Assert.Equal("ok", rig.Input(warm).GetProperty("state").GetString());
    }

    [Fact]
    public async Task AReconnectTheProtocolRefusesIsRefusedWithItsWords()
    {
        await using var rig = new VideoIoRig();
        var id = VideoIoRig.IdOf(await rig.Ok("INPUT ADD \"omt:STUDIO (Cam 1)\""));
        rig.Omt.Opened.Single().Push();
        rig.AdvanceMs(1_200);
        rig.Omt.Status = new ProviderStatus("failed", "", "", "the codec went away", "");
        Assert.Equal("Cam 1: the codec went away", await rig.Refused($"INPUT RECONNECT {id}"));
        // The journal says what came of it: the old receiver closed, the new one refused.
        Assert.Contains(rig.Engine.Journal.Recent(50), j => j.Kind == "videoio" && !j.Ok
            && j.Text == "PlayMapDesk reconnected Cam 1: the old receivers closed, the new ones refused — Cam 1: the codec went away");
        // The protocol is back: tried again, never refused from memory; a receiver that fails at once is refused with its words.
        rig.Omt.Status = new ProviderStatus("ready", "fake 1.0", "/fake/omt", "", "");
        rig.Omt.FailOnOpen = "no route to STUDIO";
        Assert.Equal("Cam 1: no route to STUDIO", await rig.Refused($"INPUT RECONNECT {id}"));
        rig.Omt.FailOnOpen = "";
        var reconnect = rig.Run($"INPUT RECONNECT {id}");
        while (!reconnect.IsCompleted)
        {
            rig.Omt.Opened[^1].Push();
            await Task.Delay(5);
        }
        Assert.Equal("reconnected Cam 1: frames are arriving", (await reconnect).Effect);
    }

    [Fact]
    public async Task AColdInputNothingShowsIsNotReconnected()
    {
        await using var rig = new VideoIoRig();
        var cold = VideoIoRig.IdOf(await rig.Ok("INPUT ADD \"omt:STUDIO (Cam 3)\" COLD"));
        Assert.Equal("Cam 3 is cold and nothing shows it — it connects when something does", await rig.Refused($"INPUT RECONNECT {cold}"));
        Assert.Equal("every open live input delivers — nothing to reconnect", await rig.Refused("INPUT RECONNECT ALL"));
        Assert.Empty(rig.Omt.Opened);
    }

    [Fact]
    public async Task AProtocolOffFromTheNextStartTakesNoNewWorkWhicheverVerbAsks()
    {
        await using var rig = new VideoIoRig();
        var id = VideoIoRig.IdOf(await rig.Ok("INPUT ADD \"omt:STUDIO (Cam 1)\""));
        var send = VideoIoRig.IdOf(await rig.Ok("SEND ADD OMT Main output:op_1"));
        await rig.Ok($"SEND ENABLE {send} OFF");
        await rig.Ok("VIDEOIO SET OMT OFF");
        const string off = "OMT is off from the next engine start — VIDEOIO SET OMT ON keeps it";
        Assert.Equal(off, await rig.Refused($"INPUT BACKUP {id} \"omt:STUDIO2 (Cam 1)\""));
        Assert.Contains(off, await rig.Refused($"SET {send} enabled true"), StringComparison.Ordinal);
        Assert.Contains(off, await rig.Refused("ADD send {\"name\":\"Other\",\"protocol\":\"omt\",\"tap\":\"output:op_1\",\"enabled\":true}"),
            StringComparison.Ordinal);
        Assert.Contains(off, await rig.Refused("ADD media {\"name\":\"Cam 9\",\"kind\":\"stream\",\"location\":\"omt:STUDIO (Cam 9)\"}"),
            StringComparison.Ordinal);
        // What runs is untouched: the input still has its receiver, and an edit of it that adds no new work is taken.
        await rig.Ok($"SET {id} name \"Cam A\"");
        Assert.False(rig.Omt.Opened.Single().Closed);
        // STATE says what runs and what the next start brings; preflight warns, amber, with the line that keeps it.
        var omt = rig.State().GetProperty("omt");
        Assert.Equal("ready", omt.GetProperty("state").GetString());
        Assert.Equal("off", omt.GetProperty("next").GetProperty("state").GetString());
        var row = (await new VideoIoPreflight().RunAsync(new PreflightContext(rig.Engine, PreflightDepth.Quick, false),
            CancellationToken.None)).Single(r => r.Id == "videoio.omt.runtime");
        Assert.Equal((Light.Amber, "off at next start", "VIDEOIO SET OMT ON"), (row.Light, row.Value, row.Fix));
    }

    [Fact]
    public async Task ASendTapsSomethingRealAndALargerOutputIsHeldToTheGuard()
    {
        await using var rig = new VideoIoRig(settings: VideoIoSettings.Default with { MaxSendMbps = 150 });
        Assert.Equal("Main taps media:md_typo0001, which is not in the project", await rig.Refused("SEND ADD NDI Main source:media:md_typo0001"));
        var a = VideoIoRig.IdOf(await rig.Ok("SEND ADD OMT A output:op_1"));
        await rig.Ok($"SEND ENABLE {a} OFF");
        await rig.Ok($"SET {a} tap \"output:op_9\"");
        Assert.Equal("A taps output op_9, which is not in the project", await rig.Refused($"SEND ENABLE {a} ON"));
        await rig.Ok($"SET {a} tap \"output:op_1\"");
        await rig.Ok($"SEND ENABLE {a} ON");
        Assert.Contains("taps nothing", await rig.Refused("ADD send {\"name\":\"Empty\",\"protocol\":\"omt\",\"enabled\":true}"), StringComparison.Ordinal);
        // op_1 at 1280×720@60 ≈ 88 Mb/s; 2560 wide would make the send ≈ 177 Mb/s, past the 150 limit.
        Assert.StartsWith("1 send ≈ 177 Mb/s > 150 Mb/s", await rig.Refused("SET op_1 width 2560"), StringComparison.Ordinal);
        await rig.Ok("SET op_1 width 640");
    }

    [Fact]
    public async Task ASendMovedToAnotherProtocolGetsThatProtocolsSender()
    {
        await using var rig = new VideoIoRig();
        var id = VideoIoRig.IdOf(await rig.Ok("SEND ADD NDI Main output:op_1"));
        var ndi = rig.Ndi.Sinks.Single();
        await rig.Ok($"SET {id} protocol \"omt\"");
        // A generic edit reaches the hub at its next tick.
        rig.Engine.VideoIo.Tick();
        Assert.Equal(1, ndi.Closes);
        Assert.Single(rig.Omt.Sinks);
        Assert.Equal("omt", rig.Send(id).GetProperty("proto").GetString());
        Assert.Equal("FAKEHOST (Main)", rig.Send(id).GetProperty("fullName").GetString());
    }

    [Fact]
    public async Task AnUnfitFallbackNeverBlocksAnEditThatLeavesItAlone()
    {
        var project = VideoIoRig.Show() with
        {
            Media =
            [
                Stream("md_cam", "omt:STUDIO (Cam 1)") with { Live = LiveSettings.Default with { OnLoss = LiveLoss.Fallback, Fallback = "playlist:pl_cams" } },
                Stream("md_cam2", "omt:STUDIO (Cam 2)"),
            ],
            Playlists = [new Playlist("pl_cams", "Cams", [PlaylistItem.For("it_1", SourceRef.Media("md_cam2"), "Cam 2")], false, null)],
        };
        await using var rig = new VideoIoRig(project);
        await rig.Ok("SET md_cam name \"Cam A\"");
        await rig.Ok("SET md_cam live {\"onLoss\":\"fallback\",\"fallback\":\"playlist:pl_cams\",\"lostMs\":500}");
        // A fallback set or changed is checked: a live one is refused.
        Assert.Contains("never a live input", await rig.Refused("SET md_cam live {\"onLoss\":\"fallback\",\"fallback\":\"media:md_cam2\"}"),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task VideoIoSetSpoutSaysWhenNoProviderIsHereAndAGpuProvidersSendSaysWhyItSendsNothing()
    {
        // Kept for the Spout package, which reads it when it registers; a node with no Spout provider now is told so.
        await using var bare = new VideoIoRig();
        Assert.Equal("Spout off (no Spout provider on this node (Spout is Windows only)) — applies at the next engine start",
            (await bare.Ok("VIDEOIO SET SPOUT OFF")).Effect);
        await using var rig = new VideoIoRig(gpu: new GpuProvider());
        await rig.Ok("VIDEOIO SET SPOUT OFF");
        var id = VideoIoRig.IdOf(await rig.Ok("SEND ADD SPOUT Main output:op_1"));
        var row = rig.Send(id);
        Assert.Equal("failed", row.GetProperty("state").GetString());
        Assert.Equal("Spout sends are drawn by Spout's own GPU stage, which this node does not run", row.GetProperty("words").GetString());
    }

    [Fact]
    public async Task TallyIsProgramOnlyForTheAirAndTheFeedLeftByATakeoverGoesDark()
    {
        await using var rig = new VideoIoRig();
        var id = VideoIoRig.IdOf(await rig.Ok("INPUT ADD \"omt:STUDIO (Cam 1)\""));
        await rig.Ok($"INPUT BACKUP {id} \"omt:STUDIO2 (Cam 1)\"");
        var primary = rig.Omt.Opened.Single(f => f.Address == "STUDIO (Cam 1)");
        var backup = rig.Omt.Opened.Single(f => f.Address == "STUDIO2 (Cam 1)");
        primary.Push();
        backup.Push();
        using var sources = new EngineSources(new HubMedia(rig.Engine.VideoIo));
        var snap = new EngineSnapshot(new ProjectSnapshot(rig.Engine.Store.Project, 1, ""), PlaybackSnapshot.Empty, AirState.Initial);
        long frame = 0;
        var waited = Stopwatch.StartNew();
        // The air part of a frame draws it: program.
        while (true)
        {
            sources.BeginFrame(snap, ++frame);
            using var lease = sources.Acquire(SourceRef.Media(id), 0);
            if (lease is not null)
                break;
            Assert.True(waited.Elapsed < TimeSpan.FromSeconds(10), "the input never opened for the renderer");
            Thread.Yield();
        }
        rig.Engine.VideoIo.Tick();
        Assert.Equal((true, false), primary.Tally);
        // Only after the frame's air (a preview): preview.
        rig.AdvanceMs(300);
        sources.BeginFrame(snap, ++frame);
        sources.EndAir();
        using (sources.Acquire(SourceRef.Media(id), 0))
            rig.Engine.VideoIo.Tick();
        Assert.Equal((false, true), primary.Tally);
        // The backup takes over while drawn on air: it is on program, and the primary it left goes dark.
        for (var t = 0; t < 60; t++)
        {
            backup.Push();
            rig.AdvanceMs(20);
            sources.BeginFrame(snap, ++frame);
            using (sources.Acquire(SourceRef.Media(id), 0))
                rig.Engine.VideoIo.Tick();
        }
        Assert.Equal("backup", rig.Input(id).GetProperty("using").GetString());
        Assert.Equal((true, false), backup.Tally);
        Assert.Equal((false, false), primary.Tally);
    }

    [Fact]
    public async Task GenericEditsHoldASendToItsTapAndTheBandwidthGuardAsSendAddAndSendEnableDo()
    {
        await using var rig = new VideoIoRig(settings: VideoIoSettings.Default with { MaxSendMbps = 150 });
        // OMT medium 1.6 bits a pixel: 1280×720 at 60 ≈ 88 Mb/s.
        var a = VideoIoRig.IdOf(await rig.Ok("SEND ADD OMT A output:op_1"));
        Assert.Equal("A taps output op_9, which is not in the project", await rig.Refused($"SET {a} tap \"output:op_9\""));
        Assert.Equal("1 send ≈ 177 Mb/s > 150 Mb/s (this node's MAX-SEND-MBPS) — VIDEOIO SET MAX-SEND-MBPS <n> to allow more",
            await rig.Refused($"SET {a} fps 120"));
        // Lowering what a send takes is never refused, even over the limit.
        await rig.Ok($"SET {a} fps 30");
        await rig.Ok($"SEND ENABLE {a} OFF");
        await rig.Ok($"SET {a} fps 60");
        var b = VideoIoRig.IdOf(await rig.Ok("SEND ADD OMT B output:op_1"));
        Assert.Equal("2 sends ≈ 177 Mb/s > 150 Mb/s (this node's MAX-SEND-MBPS) — VIDEOIO SET MAX-SEND-MBPS <n> to allow more",
            await rig.Refused($"SEND ENABLE {a} ON"));
        Assert.False(rig.Engine.Store.Project.Sends.Find(a)!.Enabled);
        Assert.True(rig.Engine.Store.Project.Sends.Find(b)!.Enabled);
        // A packed target is a texture: a PA16 send stacks three planes, so 5462 rows would need more than 16384.
        Assert.Contains("at most 16384", await rig.Refused("SEND ADD NDI Tall output:op_1 1920x5462 PA16"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFallbackNeverShowsALiveInputHoweverDeepAndOneThatWentIsBlack()
    {
        var project = VideoIoRig.Show() with
        {
            Media =
            [
                Stream("md_cam", "omt:STUDIO (Cam 1)"),
                Stream("md_cam2", "omt:STUDIO (Cam 2)"),
                new MediaItem("md_standby", "Standby loop", MediaKind.Video, "/clips/standby.mp4", "", 0, MediaInfo.Unknown, []),
            ],
            Playlists = [new Playlist("pl_cams", "Cams", [PlaylistItem.For("it_1", SourceRef.Media("md_cam2"), "Cam 2")], false, null)],
            Canvases =
            [
                .. VideoIoRig.Show().Canvases,
                new Canvas("cv_pip", "PiP", 1920, 1080, "#000000",
                    [new Layer("ly_1", "cam", SourceRef.Playlist("pl_cams"), new RectD(0, 0, 960, 540), 1, LayerBlend.Normal, true)]),
            ],
        };
        await using var rig = new VideoIoRig(project);
        rig.Engine.VideoIo.Tick();
        Assert.Equal("playlist:pl_cams shows the live input media:md_cam2: a fallback is a picture this node always has",
            await rig.Refused("INPUT FALLBACK md_cam playlist:pl_cams"));
        Assert.Equal("canvas:cv_pip shows the live input media:md_cam2: a fallback is a picture this node always has",
            await rig.Refused("INPUT FALLBACK md_cam canvas:cv_pip"));
        Assert.Contains("shows the live input", await rig.Refused("SET md_cam live {\"onLoss\":\"fallback\",\"fallback\":\"canvas:cv_pip\"}"),
            StringComparison.Ordinal);

        // A fallback in use cannot leave the show.
        await rig.Ok("INPUT FALLBACK md_cam media:md_standby");
        Assert.StartsWith("media Standby loop is in use by md_cam", await rig.Refused("REMOVE md_standby"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFallbackThatCameToShowALiveInputIsBlackOnLoss()
    {
        // A show whose fallback (written before this node checked it, or by another) shows a live input through a playlist.
        var project = VideoIoRig.Show() with
        {
            Media =
            [
                Stream("md_cam", "omt:STUDIO (Cam 1)") with { Live = LiveSettings.Default with { OnLoss = LiveLoss.Fallback, Fallback = "playlist:pl_cams" } },
                Stream("md_cam2", "omt:STUDIO (Cam 2)"),
            ],
            Playlists = [new Playlist("pl_cams", "Cams", [PlaylistItem.For("it_1", SourceRef.Media("md_cam2"), "Cam 2")], false, null)],
        };
        await using var rig = new VideoIoRig(project);
        rig.Engine.VideoIo.Tick();
        rig.Omt.Opened.Single(f => f.Address == "STUDIO (Cam 1)").Push();
        rig.AdvanceMs(1_200);
        rig.Engine.VideoIo.Tick();
        Assert.Equal("black", rig.Input("md_cam").GetProperty("loss").GetString());
        Assert.Equal(SourceRef.None, rig.Engine.VideoIo.InputOf("md_cam")!.LossRoute);
    }
}
