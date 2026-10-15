using System.Text.Json;
using PlayMap.Core.Bus;
using PlayMap.Core.Media;
using PlayMap.Core.Model;
using PlayMap.Core.Preflight;
using PlayMap.Core.Wire;
using PlayMap.Engine.VideoIo;

namespace PlayMap.Engine.Tests.VideoIo;

/// <summary>The video I/O spine on the engine: a live input is a library item received once by a warm receiver whatever reads it;
/// its state follows the age of its newest frame; sends make their senders and answer the full name the protocol announces; every
/// refusal says why; STATE, INDEX, the context and preflight say what is true.</summary>
public sealed class VideoIoVerbTests
{
    [Fact]
    public async Task AnInputIsAddedWarmAndReceivedByOneReceiverHoweverManyReadIt()
    {
        await using var rig = new VideoIoRig();
        var r = await rig.Ok("INPUT ADD \"omt:STUDIO (Cam 1)\"");
        var id = VideoIoRig.IdOf(r);
        Assert.Contains("added Cam 1 (omt:STUDIO (Cam 1)) — connecting", r.Effect, StringComparison.Ordinal);
        var item = rig.Engine.Store.Project.FindMedia(id)!;
        Assert.Equal(MediaKind.Stream, item.Kind);
        Assert.Equal("omt:STUDIO (Cam 1)", item.Location);
        var source = Assert.Single(rig.Omt.Opened);
        Assert.Equal("STUDIO (Cam 1)", source.Address);
        Assert.Equal("connecting", rig.Input(id).GetProperty("state").GetString());

        // Three consumers (an output, a preview, a scan camera) read it: still one receiver, and letting go keeps it.
        var handles = Enumerable.Range(0, 3).Select(_ => rig.Engine.VideoIo.Open(item)!).ToList();
        Assert.Single(rig.Omt.Opened);
        source.Push(64, 36, 180);
        foreach (var h in handles)
        {
            using var lease = h.Acquire(0);
            Assert.NotNull(lease);
            Assert.Equal(PixelLayout.Uyvy, lease!.Layout);
            Assert.Equal(64, lease.Width);
        }
        foreach (var h in handles)
            h.Dispose();
        Assert.False(source.Closed);

        var row = rig.Input(id);
        Assert.Equal("ok", row.GetProperty("state").GetString());
        Assert.Equal(64, row.GetProperty("w").GetInt32());
        Assert.Equal(36, row.GetProperty("h").GetInt32());
        Assert.Equal("uyvy", row.GetProperty("format").GetString());
        Assert.Equal(1, row.GetProperty("received").GetInt64());
        Assert.Equal("omt", row.GetProperty("proto").GetString());
        Assert.True(row.GetProperty("warm").GetBoolean());

        // INPUT LIST is STATE's list.
        var list = await rig.Ok("INPUT LIST");
        Assert.Equal(rig.State().GetProperty("inputs").GetRawText(), JsonDocument.Parse(list.Payload!).RootElement.GetRawText());

        // Removed from the library: the receiver closes.
        await rig.Ok($"REMOVE {id}");
        rig.Engine.VideoIo.Tick();
        Assert.True(source.Closed);
        Assert.Equal(0, rig.State().GetProperty("inputs").GetArrayLength());
    }

    [Fact]
    public async Task AColdInputConnectsAtItsFirstConsumerAndAProxyAsksForThePreview()
    {
        await using var rig = new VideoIoRig();
        var id = VideoIoRig.IdOf(await rig.Ok("INPUT ADD ndi://10.0.0.5:5961 Camera PROXY COLD"));
        var item = rig.Engine.Store.Project.FindMedia(id)!;
        Assert.Equal("Camera", item.Name);
        Assert.False(item.Live!.Warm);
        Assert.True(item.Live.Proxy);
        Assert.Empty(rig.Ndi.Opened);
        Assert.Equal("connecting", rig.Input(id).GetProperty("state").GetString());
        using var handle = rig.Engine.VideoIo.Open(item)!;
        Assert.Equal("10.0.0.5:5961", Assert.Single(rig.Ndi.Opened).Address);
        Assert.True(rig.Ndi.Opened[0].Settings.Proxy);
    }

    [Fact]
    public async Task EveryInputRefusalSaysWhy()
    {
        await using var rig = new VideoIoRig(ndi: false);
        Assert.Contains("ndi:HOST (Name)", await rig.Refused("INPUT ADD nothing-here"), StringComparison.Ordinal);
        Assert.Contains("ndi:HOST (Name)", await rig.Refused("INPUT ADD ndi:"), StringComparison.Ordinal);
        // No NDI runtime on this node: refused with the words that say where to get one and the variable that points at it.
        var ndi = await rig.Refused("INPUT ADD \"ndi:STUDIO (Cam 1)\"");
        Assert.Contains("PLAYMAP_NDI_DIR", ndi, StringComparison.Ordinal);
        Assert.Contains("https://ndi.video", ndi, StringComparison.Ordinal);
        if (!OperatingSystem.IsWindows())
            Assert.Equal("Spout is Windows only", await rig.Refused("INPUT ADD spout:Resolume"));
        var id = VideoIoRig.IdOf(await rig.Ok("INPUT ADD omt://10.0.0.7:6400 Program"));
        Assert.Equal($"omt://10.0.0.7:6400 is already in the library as {id} (Program)", await rig.Refused("INPUT ADD omt://10.0.0.7:6400"));
        // The journal and the project saw only the one that landed.
        Assert.Single(rig.Engine.Store.Project.Media, m => m.Kind == MediaKind.Stream);
    }

    [Fact]
    public async Task AnInputGoesStaleAfterThreePeriodsAndLostAfterLostMsAndReconnectIsRefusedWhileItDelivers()
    {
        await using var rig = new VideoIoRig();
        var id = VideoIoRig.IdOf(await rig.Ok("INPUT ADD \"omt:STUDIO (Cam 1)\""));
        var source = rig.Omt.Opened.Single();
        for (var i = 0; i < 10; i++)
        {
            source.Push();
            rig.AdvanceMs(20);
        }
        // 50 fps: a period of 20 ms, stale past 60 ms.
        Assert.Equal("ok", rig.Input(id).GetProperty("state").GetString());
        Assert.Equal(50, rig.Input(id).GetProperty("fps").GetDouble(), 1);
        var healthy = await rig.Refused($"INPUT RECONNECT {id}");
        Assert.Matches("^Cam 1 delivers 50 fps \\(\\d+ ms old\\) — nothing to reconnect$", healthy);
        rig.AdvanceMs(60);
        Assert.Equal("stale", rig.Input(id).GetProperty("state").GetString());
        rig.AdvanceMs(1_000);
        var lost = rig.Input(id);
        Assert.Equal("lost", lost.GetProperty("state").GetString());
        Assert.Equal("no frame for 1.1 s", lost.GetProperty("words").GetString());

        // Lost: reconnect closes the receiver and opens another; answered accepted until a frame lands.
        var r = await rig.Ok($"INPUT RECONNECT {id}");
        Assert.False(r.Applied);
        Assert.True(source.Closed);
        Assert.Equal(2, rig.Omt.Opened.Count);
        Assert.Equal("connecting", rig.Input(id).GetProperty("state").GetString());
        Assert.Equal("no live input md_nothing", await rig.Refused("INPUT RECONNECT md_nothing"));
    }

    [Fact]
    public async Task ASendMakesItsSenderAndIsIdleUntilWatched()
    {
        await using var rig = new VideoIoRig();
        var r = await rig.Ok("SEND ADD OMT \"Main out\" output:op_1");
        var id = VideoIoRig.IdOf(r);
        using (var doc = JsonDocument.Parse(r.Payload!))
        {
            Assert.Equal("FAKEHOST (Main out)", doc.RootElement.GetProperty("fullName").GetString());
            Assert.Equal(1280, doc.RootElement.GetProperty("w").GetInt32());
            Assert.Equal(720, doc.RootElement.GetProperty("h").GetInt32());
            // OMT medium ≈ 1.6 bits a pixel: 1280 × 720 × 60 × 1.6 ≈ 88 Mb/s.
            Assert.Equal(88, doc.RootElement.GetProperty("estMbps").GetInt32());
        }
        Assert.Equal("sending FAKEHOST (Main out) from output:op_1", r.Effect);
        var sink = Assert.Single(rig.Omt.Sinks);
        Assert.Equal(new SendSpec(id, "Main out", 1280, 720, 60, 1, SendFormat.Uyvy, "default", "", "", false), sink.Spec);
        var entry = Assert.Single(rig.Engine.VideoIo.Plan);
        Assert.Same(sink, entry.Sink);
        Assert.Equal("op_1", entry.Target);

        var row = rig.Send(id);
        Assert.Equal("idle", row.GetProperty("state").GetString());
        Assert.Equal("FAKEHOST (Main out)", row.GetProperty("fullName").GetString());
        Assert.Equal("uyvy", row.GetProperty("format").GetString());
        Assert.Equal(60, row.GetProperty("fps").GetDouble());
        sink.Watch(2, program: true);
        row = rig.Send(id);
        Assert.Equal("sending", row.GetProperty("state").GetString());
        Assert.Equal(2, row.GetProperty("connections").GetInt32());
        Assert.True(row.GetProperty("tally").GetProperty("program").GetBoolean());

        // Off, with the receivers it costs named; off again is refused; on again makes a new sender.
        Assert.Equal("Main out stopped — 2 receivers lost this picture", (await rig.Ok($"SEND ENABLE {id} OFF")).Effect);
        Assert.Equal(1, sink.Closes);
        Assert.Equal("off", rig.Send(id).GetProperty("state").GetString());
        Assert.Empty(rig.Engine.VideoIo.Plan);
        Assert.Equal("Main out is already off", await rig.Refused($"SEND ENABLE {id} OFF"));
        await rig.Ok($"SEND ENABLE {id} ON");
        Assert.Equal(2, rig.Omt.Sinks.Count);
        Assert.Equal("removed Main out", (await rig.Ok($"SEND REMOVE {id}")).Effect);
        Assert.Equal(1, rig.Omt.Sinks[1].Closes);
        Assert.Empty(rig.Engine.Store.Project.Sends);
    }

    [Fact]
    public async Task SendSizesFitTheirFormatAndRatesAreRationals()
    {
        await using var rig = new VideoIoRig();
        var r = await rig.Ok("SEND ADD OMT Alpha canvas:cv_main 1918x1080 @59.94 UYVA QUALITY HIGH ALWAYS");
        Assert.Contains("(1918 → 1916 (UYVA needs a multiple of 4))", r.Effect, StringComparison.Ordinal);
        var sink = rig.Omt.Sinks.Single();
        Assert.Equal((1916, 1080, 60000, 1001, SendFormat.Uyva, "high", true), (sink.Spec.Width, sink.Spec.Height, sink.Spec.FpsN, sink.Spec.FpsD,
            sink.Spec.Format, sink.Spec.Quality, sink.Spec.Alpha));
        var send = rig.Engine.Store.Project.Sends.Single();
        Assert.False(send.OnlyWhenWatched);
        // Always: sending though nobody watches.
        Assert.Equal("sending", rig.Send(send.Id).GetProperty("state").GetString());
    }

    [Fact]
    public async Task EverySendRefusalSaysWhy()
    {
        await using var rig = new VideoIoRig(settings: VideoIoSettings.Default with { MaxSendMbps = 150 });
        Assert.Equal("NDI chooses its own quality: QUALITY is OMT's", await rig.Refused("SEND ADD NDI Main output:op_1 QUALITY HIGH"));
        Assert.Equal("Main taps output op_9, which is not in the project", await rig.Refused("SEND ADD OMT Main output:op_9"));
        Assert.Equal("a tap is output:<id>, canvas:<id> or source:<ref>, not \"op_1\"", await rig.Refused("SEND ADD OMT Main op_1"));
        Assert.Contains("prewarp taps are next round", await rig.Refused("SEND ADD OMT Main prewarp:op_1"), StringComparison.Ordinal);
        Assert.Equal("SEND ADD takes NDI, OMT or SPOUT first, not 'RTMP'", await rig.Refused("SEND ADD RTMP Main output:op_1"));
        Assert.Contains("does not take 'P216'", await rig.Refused("SEND ADD OMT Main output:op_1 P216"), StringComparison.Ordinal);
        if (!OperatingSystem.IsWindows())
            Assert.Equal("Spout is Windows only", await rig.Refused("SEND ADD SPOUT Main output:op_1"));
        // The bandwidth guard: 88 Mb/s fits under 150; a second makes 176.
        await rig.Ok("SEND ADD OMT Main output:op_1");
        Assert.Equal("OMT already sends \"Main\" from this node (" + rig.Engine.Store.Project.Sends[0].Id + ")",
            await rig.Refused("SEND ADD OMT main output:op_1"));
        Assert.Equal("2 sends ≈ 177 Mb/s > 150 Mb/s (this node's MAX-SEND-MBPS) — VIDEOIO SET MAX-SEND-MBPS <n> to allow more",
            await rig.Refused("SEND ADD OMT Second output:op_1"));
        await using var noNdi = new VideoIoRig(ndi: false);
        Assert.Contains("PLAYMAP_NDI_DIR", await noNdi.Refused("SEND ADD NDI Main output:op_1"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task VideoIoSetChangesTheNodesSettingsAndSaysWhenTheyApply()
    {
        await using var rig = new VideoIoRig();
        VideoIoSettings? saved = null;
        rig.Engine.VideoIo.Save = s => saved = s;
        var r = await rig.Ok("VIDEOIO SET NDI-DISCOVERY 10.0.0.2,10.0.0.3");
        Assert.Equal("next start", JsonDocument.Parse(r.Payload!).RootElement.GetProperty("applies").GetString());
        Assert.Equal("10.0.0.2,10.0.0.3", saved!.NdiDiscovery);
        Assert.Equal("10.0.0.2,10.0.0.3", rig.State().GetProperty("ndi").GetProperty("discovery").GetString());
        Assert.Equal("video I/O already has NDI discovery server 10.0.0.2,10.0.0.3", await rig.Refused("VIDEOIO SET NDI-DISCOVERY 10.0.0.2,10.0.0.3"));
        r = await rig.Ok("VIDEOIO SET NDI-GROUPS stage,foh");
        Assert.Equal("now", JsonDocument.Parse(r.Payload!).RootElement.GetProperty("applies").GetString());
        Assert.Equal("stage,foh", rig.Engine.VideoIo.Settings.NdiGroups);
        await rig.Ok("VIDEOIO SET OMT OFF");
        Assert.Equal("off", rig.State().GetProperty("omt").GetProperty("state").GetString());
        Assert.StartsWith("OMT is off on this node", await rig.Refused("INPUT ADD omt://10.0.0.7:6400"), StringComparison.Ordinal);
        Assert.Contains("does not take", await rig.Refused("VIDEOIO SET MAX-SEND-MBPS lots"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TallyTellsTheSenderWhenItsInputIsOnAirAndBlackoutMakesItPreview()
    {
        await using var rig = new VideoIoRig();
        var id = VideoIoRig.IdOf(await rig.Ok("INPUT ADD \"omt:STUDIO (Cam 1)\""));
        var source = rig.Omt.Opened.Single();
        source.Push();
        using var handle = rig.Engine.VideoIo.Open(rig.Engine.Store.Project.FindMedia(id)!)!;
        rig.Engine.VideoIo.Tick();
        Assert.Equal((false, false), source.Tally);
        using (handle.Acquire(0))
            rig.Engine.VideoIo.Tick();
        Assert.Equal((true, false), source.Tally);
        Assert.Equal("program", rig.Input(id).GetProperty("tally").GetString());
        Assert.True(rig.Input(id).GetProperty("onAir").GetBoolean());
        await rig.Ok("BLACKOUT ON");
        using (handle.Acquire(0))
            rig.Engine.VideoIo.Tick();
        Assert.Equal((false, true), source.Tally);
        // Not drawn for longer than the window: none, sent once.
        rig.AdvanceMs(300);
        rig.Engine.VideoIo.Tick();
        rig.Engine.VideoIo.Tick();
        Assert.Equal([(true, false), (false, true), (false, false)], source.TallySent);
    }

    [Fact]
    public async Task PreflightIsRedWithAFixWhenAnInputOnAirIsLost()
    {
        await using var rig = new VideoIoRig();
        var check = new VideoIoPreflight();
        async Task<IReadOnlyList<PreflightRow>> Rows() => await check.RunAsync(new PreflightContext(rig.Engine, PreflightDepth.Quick, false), CancellationToken.None);
        var calm = Assert.Single(await Rows());
        Assert.Equal(Light.Green, calm.Light);
        Assert.Equal("videoio", calm.Section);

        var id = VideoIoRig.IdOf(await rig.Ok("INPUT ADD \"omt:STUDIO (Cam 1)\""));
        var source = rig.Omt.Opened.Single();
        source.Push();
        using var handle = rig.Engine.VideoIo.Open(rig.Engine.Store.Project.FindMedia(id)!)!;
        using (handle.Acquire(0))
            rig.Engine.VideoIo.Tick();
        var good = (await Rows()).Single(r => r.Id == "videoio.input." + id);
        Assert.Equal(Light.Green, good.Light);
        Assert.Contains("on air", good.Note, StringComparison.Ordinal);

        // The fault: frames stop while it is on air.
        rig.AdvanceMs(200);
        using (handle.Acquire(0))
            rig.Engine.VideoIo.Tick();
        rig.AdvanceMs(1_000);
        using (handle.Acquire(0))
            rig.Engine.VideoIo.Tick();
        var bad = (await Rows()).Single(r => r.Id == "videoio.input." + id);
        Assert.Equal(Light.Red, bad.Light);
        Assert.Equal("lost", bad.Value);
        Assert.Equal($"INPUT RECONNECT {id}", bad.Fix);
        Assert.True(WireGrammar.TryParse(bad.Fix!, out var fix, out _) && VerbCatalog.Match(fix) is { } m && rig.Engine.IsAvailable(m.Spec));
        // VIDEOIO CHECK answers the same rows.
        var checkRows = JsonDocument.Parse((await rig.Ok("VIDEOIO CHECK")).Payload!).RootElement;
        Assert.Contains(checkRows.EnumerateArray(), r => r.GetProperty("id").GetString() == "videoio.input." + id && r.GetProperty("light").GetString() == "red");
    }

    [Fact]
    public async Task ARuntimeTheShowUsesThatIsMissingIsRedInPreflightAndTheContext()
    {
        await using var rig = new VideoIoRig();
        await rig.Ok("INPUT ADD \"ndi:STUDIO (Cam 1)\"");
        rig.Ndi.Status = new ProviderStatus("absent", "", "", "the NDI runtime went away", "https://ndi.video");
        var rows = await new VideoIoPreflight().RunAsync(new PreflightContext(rig.Engine, PreflightDepth.Quick, false), CancellationToken.None);
        var runtime = rows.Single(r => r.Id == "videoio.ndi.runtime");
        Assert.Equal(Light.Red, runtime.Light);
        Assert.Equal("the NDI runtime went away", runtime.Note);
        Assert.NotNull(runtime.Fix);
        using var ctx = JsonDocument.Parse(rig.Engine.Context());
        var item = ctx.RootElement.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("id").GetString() == "videoio");
        Assert.Equal("red", item.GetProperty("light").GetString());
    }

    [Fact]
    public async Task TheContextNamesInputsOnAirAndSendsAndLinksThemWithEyeVerbs()
    {
        await using var rig = new VideoIoRig();
        var id = VideoIoRig.IdOf(await rig.Ok("INPUT ADD \"omt:STUDIO (Cam 1)\""));
        var send = VideoIoRig.IdOf(await rig.Ok("SEND ADD OMT Main output:op_1"));
        await rig.Ok($"OUTPUT SOURCE op_1 media:{id}");
        var source = rig.Omt.Opened.Single();
        source.Push();
        using var handle = rig.Engine.VideoIo.Open(rig.Engine.Store.Project.FindMedia(id)!)!;
        using (handle.Acquire(0))
            rig.Engine.VideoIo.Tick();
        rig.Omt.Sinks.Single().Watch(3);
        rig.Engine.VideoIo.Tick();
        using var ctx = JsonDocument.Parse(rig.Engine.Context());
        var items = ctx.RootElement.GetProperty("items").EnumerateArray().ToDictionary(i => i.GetProperty("id").GetString()!);
        Assert.Equal("green", items["videoio"].GetProperty("light").GetString());
        Assert.Equal("1 input ok, 1 send to 3 receivers", items["videoio"].GetProperty("state").GetString());
        Assert.Equal("on air", items["input:" + id].GetProperty("state").GetString());
        Assert.Equal("3 receivers", items["send:" + send].GetProperty("state").GetString());
        var links = ctx.RootElement.GetProperty("links").EnumerateArray()
            .Select(l => (l.GetProperty("from").GetString(), l.GetProperty("to").GetString(), l.GetProperty("kind").GetString())).ToList();
        Assert.Contains(("input:" + id, "output:op_1", "feeds"), links);
        Assert.Contains(("output:op_1", "send:" + send, "sends"), links);
    }

    [Fact]
    public async Task TheIndexSectionsListInputsAndSendsWithKeysTheCatalogueHolds()
    {
        await using var rig = new VideoIoRig();
        var id = VideoIoRig.IdOf(await rig.Ok("INPUT ADD \"omt:STUDIO (Cam 1)\""));
        var send = VideoIoRig.IdOf(await rig.Ok("SEND ADD OMT Main output:op_1"));
        foreach (var (name, kind, rowId) in new[] { ("video", "input", id), ("sends", "send", send) })
        {
            var section = Assert.Single(rig.Engine.Routes.IndexSections, s => s.Section == name);
            Assert.Equal(kind, section.Hint.RowKind);
            foreach (var key in section.Hint.Keys)
            {
                var line = key.DownLine.Replace("$(id)", rowId, StringComparison.Ordinal);
                Assert.True(WireGrammar.TryParse(line, out var parsed, out _) && VerbCatalog.Match(parsed) is { } m && rig.Engine.IsAvailable(m.Spec), line);
                Assert.Equal(VerbParams.Press(VerbCatalog.Match(parsed)!.Value.Spec), key.Press);
            }
            using var ms = new MemoryStream();
            using (var w = new Utf8JsonWriter(ms))
                section.WriteRows(rig.Engine.Store.Project, rig.Engine.Snapshot, w);
            var row = Assert.Single(JsonDocument.Parse(ms.ToArray()).RootElement.EnumerateArray());
            Assert.Equal(["id", "kind", "label", "parent", "light", "state"], row.EnumerateObject().Select(p => p.Name));
            Assert.Equal(rowId, row.GetProperty("id").GetString());
            Assert.Equal(kind, row.GetProperty("kind").GetString());
        }
    }

    [Fact]
    public async Task DiscoveryListsWhatTheProvidersSeeAndMarksWhatTheLibraryHolds()
    {
        await using var rig = new VideoIoRig();
        rig.Omt.Announce(new LiveEndpoint(LiveProtocol.Omt, "omt:STUDIO (Cam 1)", "STUDIO (Cam 1)", "STUDIO", "Cam 1", "10.0.0.5:6400", 0));
        rig.Ndi.Announce(new LiveEndpoint(LiveProtocol.Ndi, "ndi:FOH (Program)", "FOH (Program)", "FOH", "Program", "10.0.0.9:5961", 0));
        var id = VideoIoRig.IdOf(await rig.Ok("INPUT ADD \"omt:STUDIO (Cam 1)\""));
        var state = rig.State();
        Assert.Equal(2, state.GetProperty("discoveredCount").GetInt32());
        var rows = state.GetProperty("discovered").EnumerateArray().ToList();
        Assert.Equal(["ndi:FOH (Program)", "omt:STUDIO (Cam 1)"], rows.Select(r => r.GetProperty("uri").GetString()));
        Assert.Equal(id, rows[1].GetProperty("added").GetString());
        Assert.Equal("", rows[0].GetProperty("added").GetString());
        var before = rig.Ndi.Refreshes;
        var r = JsonDocument.Parse((await rig.Ok("INPUT DISCOVER NDI")).Payload!).RootElement;
        Assert.Equal(before + 1, rig.Ndi.Refreshes);
        Assert.Equal("ready", r.GetProperty("ndi").GetString());
        Assert.Equal("ndi:FOH (Program)", Assert.Single(r.GetProperty("sources").EnumerateArray()).GetProperty("uri").GetString());
        Assert.Contains("WAIT", await rig.Refused("INPUT DISCOVER SOMETIMES"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASendOrALiveInputWithBadFieldsIsRefusedByTheGenericEdits()
    {
        await using var rig = new VideoIoRig();
        var id = VideoIoRig.IdOf(await rig.Ok("SEND ADD OMT Main output:op_1"));
        Assert.Contains("P216 sends are next round", await rig.Refused($"SET {id} format \"p216\""), StringComparison.Ordinal);
        Assert.Contains("1–48 printable characters", await rig.Refused($"SET {id} name \"{new string('n', 49)}\""), StringComparison.Ordinal);
        Assert.Contains("fps is 0", await rig.Refused($"SET {id} fps 500"), StringComparison.Ordinal);
        var input = VideoIoRig.IdOf(await rig.Ok("INPUT ADD \"omt:STUDIO (Cam 1)\""));
        Assert.Contains("live.fallback", await rig.Refused($"SET {input} live {{\"fallback\":\"live:{input}\"}}"), StringComparison.Ordinal);
        Assert.Contains("live.lostMs", await rig.Refused($"SET {input} live {{\"lostMs\":5}}"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheExesCompositionCarriesTheProvidersAndThePreflightSection()
    {
        Assert.Contains(PlayMap.Engine.Host.VideoIoExtension.Factory, PlayMap.Engine.Host.EngineParts.Standard().Extensions);
        await using var rig = new VideoIoRig(ndi: false);
        // The part reads nothing of the host's setup until it saves this machine's choices.
        await using var part = PlayMap.Engine.Host.VideoIoExtension.Factory(null!);
        Assert.Equal("videoio", part.Name);
        var configured = part.Configure(EngineServices.ForTests() with
        {
            Settings = EngineServices.ForTests().Settings with { VideoIo = VideoIoSettings.Default with { NdiRuntimePath = "/nonexistent-ndi" } },
        });
        Assert.Single(configured.PreflightChecks, c => c is VideoIoPreflight);
        Assert.Contains(configured.LiveInputs, p => p.Protocol == LiveProtocol.Omt);
        Assert.Contains(configured.SendProviders, p => p.Protocol == LiveProtocol.Omt);
        // NDI is registered whether or not the user installed its runtime, so every refusal can say where to get it.
        var ndi = Assert.Single(configured.LiveInputs, p => p.Protocol == LiveProtocol.Ndi);
        Assert.Contains(ndi.Status.State, new[] { "ready", "absent", "failed" });
        var off = part.Configure(EngineServices.ForTests() with
        {
            Settings = EngineServices.ForTests().Settings with { VideoIo = VideoIoSettings.Default with { NdiEnabled = false, OmtEnabled = false } },
        });
        Assert.DoesNotContain(off.LiveInputs, p => p.Protocol is LiveProtocol.Ndi or LiveProtocol.Omt);
    }
}
