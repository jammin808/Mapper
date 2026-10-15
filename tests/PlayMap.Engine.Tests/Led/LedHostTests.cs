using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using PlayMap.Core.Bus;
using PlayMap.Core.Dmx;
using PlayMap.Core.Led;
using PlayMap.Core.Model;
using PlayMap.Core.Numerics;
using PlayMap.Core.Preflight;
using PlayMap.Core.Time;
using PlayMap.Core.Wire;
using PlayMap.Engine.Led;
using PlayMap.Engine.Rendering;
using PlayMap.Engine.Tests.Spine;
using PlayMap.Engine.Tests.Rendering;
using PlayMap.Net.Control;
using PlayMap.Net.Led;

namespace PlayMap.Engine.Tests.Led;

/// <summary>An engine with a render host (headless) and the LED sender on a hand-moved clock, pumped by the test.</summary>
internal sealed class LedHostRig : IAsyncDisposable
{
    private LedHostRig(ManualClock clock, EngineCore engine, RenderHost host, LedSender sender, ArtNetEndpoint? art)
    {
        Clock = clock;
        Engine = engine;
        Host = host;
        Sender = sender;
        Art = art;
    }

    public ManualClock Clock { get; }
    public EngineCore Engine { get; }
    public RenderHost Host { get; }
    public LedSender Sender { get; }
    public ArtNetEndpoint? Art { get; }

    public static string Loopback => Environment.GetEnvironmentVariable("PLAYMAP_TEST_LOOPBACK") is { Length: > 0 } a ? a : "127.0.0.1";

    public static string Address(int last)
    {
        var parts = Loopback.Split('.');
        return $"{parts[0]}.{parts[1]}.{parts[2]}.{last.ToString(CultureInfo.InvariantCulture)}";
    }

    public static LedHostRig Start(Project project, string? artNetAt = null, bool render = true)
    {
        var clock = new ManualClock(5_000_000_000);
        var art = artNetAt is null ? null : new ArtNetEndpoint(artNetAt);
        var sender = new LedSender(new LedSenderOptions { ArtNet = art, Bind = IPAddress.Parse(Loopback), Clock = clock });
        var services = EngineServices.ForTests(clock);
        services = services with
        {
            Settings = services.Settings with { NodeId = Guid.NewGuid().ToString("N")[..8], Led = LedSettings.Default with { Output = !render } },
            LedOutput = sender,
            ArtNet = art,
            PreflightChecks = [new LedPreflight()],
        };
        RenderHost? host = null;
        if (render)
        {
            host = RenderHost.TryCreate(RenderHostOptions.Default with { Windows = false }, out var why) ?? throw new InvalidOperationException("no render host: " + why);
            services = services with { Outputs = host };
        }
        var engine = new EngineCore(services, project);
        host?.Start(engine);
        return new LedHostRig(clock, engine, host!, sender, art);
    }

    /// <summary>Moves the show clock on, lets the render thread draw and read back a few frames, and pumps the sender.</summary>
    public async Task Step(long ns = 100_000_000)
    {
        Clock.Advance(ns);
        await Host.FramesAsync(4).WaitAsync(HostRig.Patience);
        Sender.Pump(Clock.NowNs);
    }

    /// <summary>The bytes last sent to a node's universe (LED MONITOR) and their frame's leader instant, ms.</summary>
    public async Task<(byte[] Slots, long LeaderMs)> Monitor(string node, int universe)
    {
        var json = JsonDocument.Parse(await LedVerbTests.Ok(Engine, $"LED MONITOR {node} {universe}")).RootElement;
        return (Convert.FromHexString(json.GetProperty("slots").GetString()!), json.GetProperty("leaderMs").GetInt64());
    }

    public async ValueTask DisposeAsync()
    {
        if (Host is not null)
            await Host.DisposeAsync();
        await Engine.DisposeAsync();
        await Sender.DisposeAsync();
        if (Art is not null)
            await Art.DisposeAsync();
    }
}

/// <summary>LED output through a real render host on llvmpipe: blackout reaches the next frame sent (colour and dimmer slots 0,
/// constants kept); two engines at the same leader instants send byte-identical payloads; STATE.led is the committed fixture and
/// its cards read it; preflight names a silent Art-Net node with the line that takes it off.</summary>
[Collection("render host")]
public sealed class LedHostTests
{
    private const string Ddp = "ln00000001";
    private const string Off = "ln00000002";

    private static Project Show()
    {
        var p = Project.CreateDefault("LED");
        var mode = new LedFixtureType("lt00000001", "Mode RGB", [new LedChannelSlot(LedChannel.Constant, 8, 42)],
            [new LedChannelSlot(LedChannel.R, 8, 0), new LedChannelSlot(LedChannel.G, 8, 0), new LedChannelSlot(LedChannel.B, 8, 0)], [], 0, "", "");
        LedFixture Strip(string id, string type, int n, int y, LedPatch? patch) =>
            new(id, "Strip " + id[^1], type, LedSpace.Canvas, "cv00000001", Transform3.Identity with { Position = new Vec3d(0, y, 0) },
                new LedLayout(LedShape.Line, n, 1, new Vec3d(63, 0, 0), LedStart.TopLeft, false, false, []), patch, LedSampling.Point, 0,
                LedColor.Neutral, true, false, false, "#FF8800", []);
        return p with
        {
            // The top half red over a #C08040 background: a strip at y = 4 must read red, one at y = 20 the background.
            Canvases = [new Canvas("cv00000001", "Wall", 64, 32, "#C08040",
                [new Layer("ly00000001", "Top", SourceRef.Solid("#FF0000"), new RectD(0, 0, 64, 16), 1, LayerBlend.Normal, true)])],
            Leds = new LedRig
            {
                Nodes =
                [
                    new LedNode(Ddp, "Bar", LedProtocol.Ddp, LedHostRig.Address(70), 0, true, 0, 0, LedSync.Latch, 510, "#FFFFFF"),
                    new LedNode(Off, "Spare", LedProtocol.ArtNet, LedHostRig.Address(71), 0, false, 0, 0, LedSync.Latch, 510, "#FFFFFF"),
                ],
                Types = [mode],
                Fixtures =
                [
                    Strip("lf00000001", mode.Id, 16, 4, new LedPatch(Ddp, 0, 1)),
                    Strip("lf00000002", "builtin:grb", 8, 20, new LedPatch(Ddp, 0, 50)),
                ],
            },
        };
    }

    [Fact]
    public async Task BlackoutReachesTheNextFrameSentAndKeepsConstants()
    {
        await using var rig = LedHostRig.Start(Show());
        await LedVerbTests.Ok(rig.Engine, "LED OUTPUT ON");
        await rig.Step();
        var (show, _) = await rig.Monitor(Ddp, 0);
        // The first strip (y = 4) reads the red top half after its mode channel; the second (y = 20, GRB from byte 50) the background.
        Assert.Equal([42, 0xFF, 0x00, 0x00, 0xFF, 0x00, 0x00], show[..7]);
        Assert.Equal([0x80, 0xC0, 0x40], show[49..52]);
        await LedVerbTests.Ok(rig.Engine, "BLACKOUT ON");
        await rig.Step();
        var (black, _) = await rig.Monitor(Ddp, 0);
        Assert.Equal(42, black[0]);
        Assert.All(black[1..], b => Assert.Equal(0, b));
        await LedVerbTests.Ok(rig.Engine, "BLACKOUT OFF");
        await LedVerbTests.Ok(rig.Engine, "LED LEVEL 0");
        await rig.Step();
        Assert.All((await rig.Monitor(Ddp, 0)).Slots[1..], b => Assert.Equal(0, b));
    }

    [Fact]
    public async Task TwoEnginesAtTheSameLeaderInstantsSendTheSamePayloads()
    {
        await using var a = LedHostRig.Start(Show());
        await using var b = LedHostRig.Start(Show());
        foreach (var rig in new[] { a, b })
        {
            await LedVerbTests.Ok(rig.Engine, "LED OUTPUT ON");
            await LedVerbTests.Ok(rig.Engine, "LED TEST CHASE ON lf00000002");
        }
        for (var i = 0; i < 5; i++)
        {
            await Task.WhenAll(a.Step(), b.Step());
            var (sa, la) = await a.Monitor(Ddp, 0);
            var (sb, lb) = await b.Monitor(Ddp, 0);
            Assert.Equal(la, lb);
            Assert.Equal(sa, sb);
        }
        // The chase moves with show time: the next frame differs from this one, the same on both.
        var (s1, _) = await a.Monitor(Ddp, 0);
        await Task.WhenAll(a.Step(), b.Step());
        var (s2, _) = await a.Monitor(Ddp, 0);
        Assert.NotEqual(s1[49..73], s2[49..73]);
        Assert.Equal(s2, (await b.Monitor(Ddp, 0)).Slots);
    }

    [Fact]
    public async Task TodaysLedStateIsTheCommittedFixtureAndItsCardsReadIt()
    {
        var show = Show();
        show = show with
        {
            Leds = show.Leds with
            {
                Fixtures = show.Leds.Fixtures.Add(show.Leds.Fixtures[1] with { Id = "lf00000003", Name = "Strip 3", Patch = new LedPatch(Ddp, 0, 60) })
                    .Add(show.Leds.Fixtures[1] with { Id = "lf00000004", Name = "Halo", Space = LedSpace.World, SpaceId = "", Patch = null }),
            },
        };
        await using var rig = LedHostRig.Start(show);
        await LedVerbTests.Ok(rig.Engine, "LED OUTPUT ON");
        await LedVerbTests.Ok(rig.Engine, "LED TEST WALK ON lf00000002");
        for (var i = 0; i < 3; i++)
            await rig.Step();
        var fixture = FixtureOf(rig.Engine.State().Json);
        using (var doc = JsonDocument.Parse(fixture))
        {
            var led = doc.RootElement.GetProperty("led");
            Assert.True(led.GetProperty("output").GetBoolean());
            Assert.Equal(("sending", "disabled"), (led.GetProperty("nodes")[0].GetProperty("state").GetString(), led.GetProperty("nodes")[1].GetProperty("state").GetString()));
            Assert.Equal(1, led.GetProperty("conflicts").GetArrayLength());
            Assert.Contains("world sampling is next round", led.GetProperty("warnings")[0].GetString(), StringComparison.Ordinal);
        }
        var cards = Cards().ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n";
        var statePath = Path.Combine(NodeExe.RepoRoot(), "tests", "fixtures", "state", "led.json");
        var cardsPath = Path.Combine(NodeExe.RepoRoot(), "tests", "fixtures", "state", "led.cards.json");
        if (Environment.GetEnvironmentVariable("PLAYMAP_WRITE_FIXTURES") == "1")
        {
            await File.WriteAllTextAsync(statePath, fixture);
            await File.WriteAllTextAsync(cardsPath, cards);
        }
        Assert.Equal((await File.ReadAllTextAsync(statePath)).ReplaceLineEndings("\n"), fixture);
        Assert.Equal((await File.ReadAllTextAsync(cardsPath)).ReplaceLineEndings("\n"), cards);
        var state = JsonNode.Parse(fixture)!;
        foreach (var card in Cards().Select(c => c!.AsObject()))
        {
            var bases = Resolve(state, card["path"]!.GetValue<string>()).Where(x => x is not null).ToList();
            Assert.NotEmpty(bases);
            foreach (var field in card["fields"]!.AsArray())
                Assert.All(bases, b => Assert.Contains(Resolve(card["path"]!.GetValue<string>() == "led" ? state : b, field!["path"]!.GetValue<string>()), x => x is not null));
            foreach (var action in card["actions"]!.AsArray())
            {
                var line = action!["line"]!.GetValue<string>().Replace("$(id)", Ddp, StringComparison.Ordinal);
                Assert.True(WireGrammar.TryParse(line, out var parsed, out _) && VerbCatalog.Match(parsed) is not null, line);
            }
        }
    }

    private static JsonArray Cards() =>
    [
        new JsonObject
        {
            ["title"] = "LED",
            ["path"] = "led",
            ["light"] = new JsonObject { ["path"] = "led.nodes[].state", ["red"] = new JsonArray("unreachable", "refused"), ["amber"] = new JsonArray("late"), ["green"] = new JsonArray("sending") },
            ["words"] = "led.warnings",
            ["fields"] = new JsonArray(Field("Output", "led.output"), Field("Level", "led.level"), Field("Fixtures", "led.fixtures"), Field("Pixels", "led.pixels"),
                Field("Universes", "led.universes"), Field("Mb/s", "led.mbps"), Field("Test", "led.test"), Field("Overlaps", "led.conflicts.length")),
            ["actions"] = new JsonArray(Action("Output on", "LED OUTPUT ON"), Action("Output off", "LED OUTPUT OFF"), Action("Test off", "LED TEST OFF"),
                Action("Level +", "LED LEVEL BY 5"), Action("Level −", "LED LEVEL BY -5"), Action("Re-patch", "LED AUTOPATCH ALL")),
        },
        new JsonObject
        {
            ["title"] = "LED node",
            ["path"] = "led.nodes[]",
            ["light"] = new JsonObject { ["path"] = "state", ["red"] = new JsonArray("unreachable", "refused"), ["amber"] = new JsonArray("late"), ["green"] = new JsonArray("sending") },
            ["words"] = "why",
            ["fields"] = new JsonArray(Field("Name", "name"), Field("Protocol", "protocol"), Field("Address", "host"), Field("State", "state"), Field("Frames/s", "fps"),
                Field("Universes", "universes"), Field("Late (ms)", "lateMs")),
            ["actions"] = new JsonArray(Action("On", "LED OUTPUT ON $(id)"), Action("Off", "LED OUTPUT OFF $(id)"), Action("Chase", "LED TEST CHASE ON $(id)"),
                Action("Monitor", "LED MONITOR $(id) 0")),
        },
    ];

    private static JsonObject Field(string label, string path) => new() { ["label"] = label, ["path"] = path };

    private static JsonObject Action(string label, string line) => new() { ["label"] = label, ["line"] = line };

    /// <summary>STATE's led key with what moves between runs (frames a second, late read-backs) set to 0 and the slot's addresses as 127.0.0.x.</summary>
    private static string FixtureOf(string stateJson)
    {
        var led = JsonNode.Parse(stateJson)!["led"]!.DeepClone().AsObject();
        // Read-back timing on the GPU is the driver's; the slot's loopback address is the machine's.
        led["lateCollects"] = 0;
        foreach (var n in led["nodes"]!.AsArray())
        {
            n!["fps"] = 0;
            n["host"] = "127.0.0." + n["host"]!.GetValue<string>().Split('.')[^1];
        }
        return new JsonObject { ["led"] = led }.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n";
    }

    /// <summary>The values at a dotted path ("led.nodes[].state", "led.conflicts.length").</summary>
    private static IEnumerable<JsonNode?> Resolve(JsonNode? node, string path)
    {
        IEnumerable<JsonNode?> current = [node];
        foreach (var part in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            var each = part.EndsWith("[]", StringComparison.Ordinal);
            var name = each ? part[..^2] : part;
            current = current.SelectMany(n =>
            {
                if (name == "length" && n is JsonArray arr)
                    return [JsonValue.Create(arr.Count)];
                var v = n is JsonObject o && o.TryGetPropertyValue(name, out var x) ? x : null;
                if (n is JsonObject o2 && !o2.ContainsKey(name))
                    return Array.Empty<JsonNode?>();
                return each && v is JsonArray a ? a.ToArray() : [v ?? JsonValue.Create("null")];
            }).ToList();
        }
        return current;
    }

    [Fact]
    public async Task PreflightNamesASilentArtNetNodeAndAnAnsweringOneIsGreen()
    {
        // No renderer: the machine master is on from the node's settings; the sender polls the nodes it runs.
        var p = Project.CreateDefault("LED") with { Canvases = [new Canvas("cv00000001", "Wall", 64, 32, "#000000", [])] };
        LedNode Art(string id, string name, int last) => new(id, name, LedProtocol.ArtNet, LedHostRig.Address(last), 0, true, 0, 0, LedSync.Latch, 510, "#FFFFFF");
        LedFixture At(string id, string node) => new(id, id, "builtin:rgb", LedSpace.Canvas, "cv00000001", Transform3.Identity,
            new LedLayout(LedShape.Line, 4, 1, new Vec3d(3, 0, 0), LedStart.TopLeft, false, false, []), new LedPatch(node, 0, 1), LedSampling.Point, 0,
            LedColor.Neutral, true, false, false, "#FF8800", []);
        p = p with { Leds = new LedRig { Nodes = [Art("ln00000001", "Silent", 81), Art("ln00000002", "Answers", 82)], Fixtures = [At("lf00000001", "ln00000001"), At("lf00000002", "ln00000002")] } };
        using var fake = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        fake.Bind(new IPEndPoint(IPAddress.Parse(LedHostRig.Address(82)), ArtNet.Port));
        await using var rig = LedHostRig.Start(p, artNetAt: LedHostRig.Address(80), render: false);
        rig.Sender.Pump(rig.Clock.NowNs);
        // The answering node hears the poll and replies to the port it came from (the one Art-Net socket).
        var buf = new byte[600];
        EndPoint from = new IPEndPoint(IPAddress.Any, 0);
        Assert.True(fake.Poll(2_000_000, SelectMode.SelectRead));
        var n = fake.ReceiveFrom(buf, ref from);
        Assert.True(ArtNet.IsPoll(buf.AsSpan(0, n)));
        Assert.Equal(new IPEndPoint(IPAddress.Parse(LedHostRig.Address(80)), ArtNet.Port), from);
        var reply = new byte[ArtNet.PollReplyLength];
        ArtNet.WritePollReply(reply, new ArtPollReplyInfo(IPAddress.Parse(LedHostRig.Address(82)).GetAddressBytes(), "PixLite16", "PixLite16 MkII", "", [0, 1], 0));
        fake.SendTo(reply, from);
        var waited = System.Diagnostics.Stopwatch.StartNew();
        while (rig.Sender.SeenAt(LedHostRig.Address(82)) is null)
        {
            Assert.True(waited.Elapsed < TimeSpan.FromSeconds(10), "the reply was never heard");
            await Task.Delay(5);
        }
        var rows = await new LedPreflight().RunAsync(new PreflightContext(rig.Engine, PreflightDepth.Quick, false), CancellationToken.None);
        var silent = rows.Single(r => r.Id == "led:ln00000001");
        Assert.Equal((Light.Red, "LED OUTPUT OFF ln00000001"), (silent.Light, silent.Fix));
        Assert.Contains("has not answered ArtPoll", silent.Note, StringComparison.Ordinal);
        var answers = rows.Single(r => r.Id == "led:ln00000002");
        Assert.Equal((Light.Green, null), (answers.Light, answers.Fix));
        Assert.Equal("answered ArtPoll as PixLite16 (44 Hz)", answers.Note);
        // The fix line takes the silent node off, and its row goes.
        await LedVerbTests.Ok(rig.Engine, silent.Fix!);
        rows = await new LedPreflight().RunAsync(new PreflightContext(rig.Engine, PreflightDepth.Quick, false), CancellationToken.None);
        Assert.DoesNotContain(rows, r => r.Id == "led:ln00000001");
        var seen = LedVerbTests.Led(rig.Engine).GetProperty("nodes")[1].GetProperty("seen");
        Assert.Equal(("PixLite16", 44), (seen.GetProperty("name").GetString(), seen.GetProperty("refreshHz").GetInt32()));
    }
}
