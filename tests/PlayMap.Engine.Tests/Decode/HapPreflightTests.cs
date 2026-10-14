using System.Text.Json;
using PlayMap.Core.Bus;
using PlayMap.Core.Media;
using PlayMap.Core.Model;
using PlayMap.Core.Preflight;
using PlayMap.Core.Show;
using PlayMap.Core.Wire;
using PlayMap.Engine.Decode;
using PlayMap.Engine.Tests.Rendering;

namespace PlayMap.Engine.Tests.Decode;

/// <summary>The HAP layers preflight check with faults injected (a node that sustains fewer layers than the show plays, a HAP R
/// item on a GPU without BPTC), MEDIA INSPECT's refusals, STATE.decode on a bare node, and the colour override's check.</summary>
public sealed class HapPreflightTests
{
    private static readonly SessionInfo Desk = SessionInfo.Desk("t");

    private static MediaItem Hap(string id, string codec, int w = 3840, int h = 2160, string rate = "60") =>
        new(id, id, MediaKind.Video, "/nowhere/" + id + ".mov", "", 0,
            MediaInfo.Unknown with { Width = w, Height = h, FrameRate = rate, VideoCodec = codec, DurationNs = 10_000_000_000 }, []);

    private static Project Show(params MediaItem[] items) => HostRig.WithOutputs(
        [.. items.Select((m, i) => HostRig.Screen("op_" + (i + 1), SourceRef.Media(m.Id), 1920, 1080))]) with { Media = [.. items] };

    private static Task<IReadOnlyList<PreflightRow>> Check(EngineCore engine) =>
        new HapLayersCheck().RunAsync(new PreflightContext(engine, PreflightDepth.Quick, false), CancellationToken.None);

    [Fact]
    public async Task MoreLayersThanTheNodeSustainsIsAmberWithTheLineThatShowsWhy()
    {
        await using var engine = new EngineCore(EngineServices.ForTests(), Show(Hap("md_a", "hap_q"), Hap("md_b", "hap_q"), Hap("md_c", "hap", 1920, 1080, "30")));
        // The fault: this node's cores carry one 4K60 HAP Q layer.
        engine.DecodeFacts.Record(DecodeFacts.Measure(1, 9.0, 1_760_000_000_000));
        var row = Assert.Single(await Check(engine));
        Assert.Equal(("decode", Light.Amber, "3 of 1"), (row.Section, row.Light, row.Value));
        Assert.StartsWith("The show needs 3 HAP layers at 4K60; this node sustains 1 — ", row.Note, StringComparison.Ordinal);
        Assert.Equal("MEDIA INSPECT md_a", row.Fix);
        Assert.Equal("0badc0de", row.Node);

        // The same show on a node that carries many: green, no fix.
        engine.DecodeFacts.Record(DecodeFacts.Measure(16, 2.0, 1_760_000_000_000));
        var green = Assert.Single(await Check(engine));
        Assert.Equal((Light.Green, "3 of 133", (string?)null), (green.Light, green.Value, green.Fix));
    }

    [Fact]
    public async Task HapROnAGpuWithoutBptcIsRedWithTheLineThatMakesAVersionItPlays()
    {
        await using var engine = new EngineCore(EngineServices.ForTests(), Show(Hap("md_r", "hap_r"), Hap("md_q", "hap_q")));
        engine.DecodeFacts.Record(DecodeFacts.Measure(16, 2.0, 1_760_000_000_000));
        engine.DecodeFacts.Renderer(BlockFormats.Bc1 | BlockFormats.Bc3 | BlockFormats.Bc4);
        var rows = await Check(engine);
        Assert.Equal(2, rows.Count);
        var red = rows.Single(r => r.Light == Light.Red);
        Assert.Equal("decode.hap-gpu:md_r", red.Id);
        Assert.Equal("HAP R needs BPTC textures, which this GPU's GL does not offer — optimise to HAP Q for this node", red.Note);
        Assert.Equal("MEDIA OPTIMISE md_r TO hap-q", red.Fix);

        using var state = JsonDocument.Parse(engine.State().Json);
        var platform = state.RootElement.GetProperty("decode").GetProperty("platform");
        Assert.Equal("red", platform.GetProperty("light").GetString());
        Assert.False(platform.GetProperty("blocks").GetProperty("bc7").GetBoolean());
        Assert.Equal(16, platform.GetProperty("hapLayers4k60").GetProperty("cores").GetInt32());
        var item = Assert.Single(JsonDocument.Parse(engine.Context()).RootElement.GetProperty("items").EnumerateArray(),
            i => i.GetProperty("id").GetString() == "decode");
        Assert.Equal("red", item.GetProperty("light").GetString());

        // With BPTC the item plays and the check has nothing against it.
        engine.DecodeFacts.Renderer(BlockFormats.Bc1 | BlockFormats.Bc3 | BlockFormats.Bc4 | BlockFormats.Bc7 | BlockFormats.Bc6h);
        Assert.DoesNotContain(await Check(engine), r => r.Light == Light.Red);
    }

    [Fact]
    public async Task AHapRItemNothingShowsIsNotTheShowsProblemUntilAnOutputShowsIt()
    {
        var shown = Hap("md_q", "hap_q");
        var library = Hap("md_r", "hap_r");
        await using var engine = new EngineCore(EngineServices.ForTests(), Show(shown) with { Media = [shown, library] });
        engine.DecodeFacts.Record(DecodeFacts.Measure(16, 2.0, 1_760_000_000_000));
        engine.DecodeFacts.Renderer(BlockFormats.Bc1 | BlockFormats.Bc3 | BlockFormats.Bc4);

        var row = Assert.Single(await Check(engine));
        Assert.Equal(("decode.hap-layers", Light.Green), (row.Id, row.Light));
        string StateLight()
        {
            using var state = JsonDocument.Parse(engine.State().Json);
            return state.RootElement.GetProperty("decode").GetProperty("platform").GetProperty("light").GetString()!;
        }
        string ContextLight()
        {
            using var context = JsonDocument.Parse(engine.Context());
            return context.RootElement.GetProperty("items").EnumerateArray()
                .Single(i => i.GetProperty("id").GetString() == "decode").GetProperty("light").GetString()!;
        }
        Assert.Equal("green", StateLight());
        Assert.Equal("green", ContextLight());

        // An output showing it makes it the show's: red in preflight, STATE and the context, with the line that fixes it.
        Assert.True(WireGrammar.TryParse("OUTPUT SOURCE op_1 media:md_r", out var line, out var error), error?.Reason);
        var routed = await engine.HandleAsync(line, Desk, CancellationToken.None);
        Assert.True(routed.Ok, routed.Payload);
        var red = Assert.Single(await Check(engine), r => r.Light == Light.Red);
        Assert.Equal(("decode.hap-gpu:md_r", "MEDIA OPTIMISE md_r TO hap-q"), (red.Id, red.Fix));
        Assert.Equal("red", StateLight());
        Assert.Equal("red", ContextLight());
    }

    [Fact]
    public async Task AShowWithoutHapIsOneGreenRowAndABareNodeWritesTheDecodeBlock()
    {
        await using var engine = new EngineCore(EngineServices.ForTests());
        var row = Assert.Single(await Check(engine));
        Assert.Equal((Light.Green, "none", (string?)null), (row.Light, row.Value, row.Fix));
        using var state = JsonDocument.Parse(engine.State().Json);
        var decode = state.RootElement.GetProperty("decode");
        Assert.Equal("none", decode.GetProperty("platform").GetProperty("blocks").GetProperty("staging").GetString());
        Assert.Equal(JsonValueKind.Null, decode.GetProperty("platform").GetProperty("hapLayers4k60").ValueKind);
        Assert.Equal(0, decode.GetProperty("sources").GetArrayLength());
        Assert.False(state.RootElement.GetProperty("machine").TryGetProperty("gl", out _));
    }

    [Fact]
    public async Task MediaInspectRefusesWithWords()
    {
        var services = EngineServices.ForTests() with { ShowGate = new ArmedGate() };
        await using var engine = new EngineCore(services, Show(Hap("md_a", "hap_q")));
        async Task<CommandReply> Run(string text)
        {
            Assert.True(WireGrammar.TryParse(text, out var line, out var error), error?.Reason);
            return await engine.HandleAsync(line, Desk, CancellationToken.None);
        }
        var armed = await Run("MEDIA INSPECT md_a");
        Assert.False(armed.Ok);
        Assert.StartsWith("the show is armed: MEDIA INSPECT reads the whole file from disk", armed.Payload, StringComparison.Ordinal);
        var none = await Run("MEDIA INSPECT md_zz");
        Assert.Equal((false, "no media md_zz"), (none.Ok, none.Payload));
        var onPeer = await Run("MEDIA INSPECT md_a ON 1a2b3c4d");
        Assert.Equal((false, "MEDIA INSPECT takes one media, not 'ON 1a2b3c4d' — MEDIA INSPECT <media> (ask the node itself for its budget)"),
            (onPeer.Ok, onPeer.Payload));

        await using var open = new EngineCore(EngineServices.ForTests(), Show(Hap("md_a", "hap_q")));
        Assert.True(WireGrammar.TryParse("MEDIA INSPECT md_a", out var l, out _));
        var missing = await open.HandleAsync(l, Desk, CancellationToken.None);
        Assert.Equal((false, "md_a is not on this node"), (missing.Ok, missing.Payload));
    }

    [Fact]
    public async Task TheColourOverrideIsCheckedBeforeItIsKept()
    {
        await using var engine = new EngineCore(EngineServices.ForTests(), Show(Hap("md_h", "hap_hdr")));
        async Task<CommandReply> Run(string text)
        {
            Assert.True(WireGrammar.TryParse(text, out var line, out var error), error?.Reason);
            return await engine.HandleAsync(line, Desk, CancellationToken.None);
        }
        var bad = await Run("SET md_h color.transfer \"gamma\"");
        Assert.False(bad.Ok);
        Assert.Contains("color.transfer is auto, sdr, pq, hlg or linear, not \"gamma\"", bad.Payload, StringComparison.Ordinal);
        Assert.Equal("auto", engine.Store.Project.FindMedia("md_h")!.Color.Transfer);
        var good = await Run("SET md_h color.whiteNits 100");
        Assert.True(good.Ok, good.Payload);
        Assert.Equal(100, engine.Store.Project.FindMedia("md_h")!.Color.WhiteNits);
        var nits = await Run("SET md_h color.whiteNits 20000");
        Assert.False(nits.Ok);
        Assert.Contains("color.whiteNits is 0 (the rule's) or 1–10000 nits", nits.Payload, StringComparison.Ordinal);
    }

    private sealed class ArmedGate : IShowGate
    {
        public bool Armed => true;
        public string ArmedBy => "Desk";
        public string? BlindOwner => null;
        public int MinGoMs => 0;
        public ShowRole RoleOf(SessionInfo session) => ShowRole.Operator;
        public ShowDecision Decide(VerbSpec spec, WireLine line, SessionInfo session) => new(ShowVerdict.Allow, null);
        public IReadOnlyCollection<string>? Allowed(SessionInfo session) => null;
    }
}
