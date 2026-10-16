using System.Text.Json;
using System.Text.Json.Nodes;
using PlayMap.Core.Bus;
using PlayMap.Core.Media;
using PlayMap.Core.Wire;
using PlayMap.Engine.VideoIo;
using PlayMap.Engine.Tests.Spine;

namespace PlayMap.Engine.Tests.VideoIo;

/// <summary>STATE.videoio from a real scenario is the committed fixture, with the card descriptor that reads it: an OMT camera on air,
/// an NDI program feed still connecting, a send watched by two receivers, two sources discovered.</summary>
public sealed class VideoIoFixtureTests
{
    private static async Task<VideoIoRig> Scenario()
    {
        // An interface no machine has and a set limit: the fixture says nothing of the machine it was written on.
        var rig = new VideoIoRig(settings: VideoIoSettings.Default with { Interface = "pm-fixture0", MaxSendMbps = 700 });
        rig.Omt.Announce(new LiveEndpoint(LiveProtocol.Omt, "omt:STUDIO (Cam 1)", "STUDIO (Cam 1)", "STUDIO", "Cam 1", "10.0.0.5:6400", 0));
        rig.Ndi.Announce(new LiveEndpoint(LiveProtocol.Ndi, "ndi:FOH (Program)", "FOH (Program)", "FOH", "Program", "10.0.0.9:5961", 0));
        await rig.Ok("INPUT ADD \"omt:STUDIO (Cam 1)\"");
        await rig.Ok("INPUT ADD \"ndi:FOH (Program)\"");
        await rig.Ok("SEND ADD OMT \"Main out\" output:op_1");
        var cam = rig.Engine.Store.Project.Media.Single(m => m.Name == "Cam 1");
        var source = rig.Omt.Opened.Single();
        for (var i = 0; i < 30; i++)
        {
            source.Push(1920, 1080, 128);
            rig.AdvanceMs(20);
        }
        using var handle = (LiveHandle)rig.Engine.VideoIo.Open(cam)!;
        handle.ForAir = true;
        using (handle.Acquire(0))
            rig.Engine.VideoIo.Tick();
        rig.Omt.Sinks.Single().Watch(2, program: true);
        return rig;
    }

    /// <summary>The cards the desk draws from STATE.videoio: what each reads and the lines its actions send ({id} is the row's).</summary>
    internal static JsonArray Cards() =>
    [
        new JsonObject
        {
            ["id"] = "videoio.runtimes",
            ["title"] = "Video I/O",
            ["path"] = "videoio",
            ["light"] = new JsonObject
            {
                ["path"] = "videoio.bandwidth.light",
                ["red"] = new JsonArray("red"),
                ["amber"] = new JsonArray("amber"),
                ["green"] = new JsonArray("green"),
            },
            ["words"] = "videoio.ndi.words",
            ["fields"] = new JsonArray(
                Field("NDI", "videoio.ndi.state"),
                Field("NDI runtime", "videoio.ndi.version"),
                Field("NDI discovery", "videoio.ndi.discovery"),
                Field("OMT", "videoio.omt.state"),
                Field("VMX", "videoio.omt.vmx"),
                Field("mDNS", "videoio.omt.mdns"),
                Field("Spout", "videoio.spout.state"),
                Field("Discovered", "videoio.discoveredCount"),
                Field("In (Mb/s)", "videoio.bandwidth.inMbps"),
                Field("Out (Mb/s)", "videoio.bandwidth.outMbps"),
                Field("Limit (Mb/s)", "videoio.bandwidth.limitMbps")),
            ["actions"] = new JsonArray(Action("Status", "VIDEOIO STATUS"), Action("Check", "VIDEOIO CHECK"), Action("Discover", "INPUT DISCOVER ALL")),
        },
        new JsonObject
        {
            ["id"] = "videoio.input",
            ["title"] = "Live input",
            ["path"] = "videoio.inputs[]",
            ["key"] = "id",
            ["light"] = new JsonObject
            {
                ["path"] = "state",
                ["red"] = new JsonArray("lost", "failed"),
                ["amber"] = new JsonArray("stale"),
                ["green"] = new JsonArray("ok"),
            },
            ["words"] = "words",
            ["fields"] = new JsonArray(
                Field("Source", "uri"),
                Field("Width", "w"),
                Field("Height", "h"),
                Field("Rate (fps)", "fps"),
                Field("Age (ms)", "ageMs"),
                Field("Format", "format"),
                Field("Tally", "tally"),
                Field("Received", "received"),
                Field("Dropped", "dropped")),
            ["actions"] = new JsonArray(Action("Reconnect", "INPUT RECONNECT {id}")),
        },
        new JsonObject
        {
            ["id"] = "videoio.send",
            ["title"] = "Send",
            ["path"] = "videoio.sends[]",
            ["key"] = "id",
            ["light"] = new JsonObject
            {
                ["path"] = "state",
                ["red"] = new JsonArray("failed"),
                ["amber"] = new JsonArray("starting"),
                ["green"] = new JsonArray("sending", "idle"),
            },
            ["words"] = "words",
            ["fields"] = new JsonArray(
                Field("Announced as", "fullName"),
                Field("Tap", "tap"),
                Field("Width", "w"),
                Field("Height", "h"),
                Field("Rate (fps)", "fps"),
                Field("Format", "format"),
                Field("Receivers", "connections"),
                Field("On program", "tally.program"),
                Field("Sent", "sent"),
                Field("Dropped", "dropped")),
            ["actions"] = new JsonArray(Action("Start", "SEND ENABLE {id} ON"), Action("Stop", "SEND ENABLE {id} OFF")),
        },
    ];

    private static JsonObject Field(string label, string path) => new() { ["label"] = label, ["path"] = path };

    private static JsonObject Action(string label, string line) => new() { ["label"] = label, ["line"] = line };

    private static string FixtureOf(string stateJson)
    {
        var root = JsonNode.Parse(stateJson)!.AsObject();
        var videoio = root["videoio"]!.AsObject();
        // Ids are made fresh per run: rows name them in order instead.
        var ids = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var list in new[] { "inputs", "sends" })
            foreach (var row in videoio[list]!.AsArray())
                ids[row!["id"]!.GetValue<string>()] = $"{list[..^1]}_{ids.Count + 1}";
        var text = videoio.ToJsonString();
        foreach (var (id, stable) in ids)
            text = text.Replace(id, stable, StringComparison.Ordinal);
        var fixture = new JsonObject { ["videoio"] = JsonNode.Parse(text) };
        return fixture.ToJsonString(FixtureJson.Indented) + "\n";
    }

    [Fact]
    public async Task TodaysVideoIoStateIsTheCommittedFixtureAndItsCardsReadIt()
    {
        await using var rig = await Scenario();
        var fixture = FixtureOf(StateNow(rig));
        using (var doc = JsonDocument.Parse(fixture))
        {
            var v = doc.RootElement.GetProperty("videoio");
            var inputs = v.GetProperty("inputs").EnumerateArray().ToList();
            Assert.Equal(["ok", "connecting"], inputs.Select(i => i.GetProperty("state").GetString()));
            Assert.Equal("program", inputs[0].GetProperty("tally").GetString());
            Assert.Equal(1920, inputs[0].GetProperty("w").GetInt32());
            Assert.Equal(50, inputs[0].GetProperty("fps").GetDouble(), 1);
            var send = Assert.Single(v.GetProperty("sends").EnumerateArray());
            Assert.Equal("sending", send.GetProperty("state").GetString());
            Assert.Equal(2, send.GetProperty("connections").GetInt32());
            Assert.Equal(2, v.GetProperty("discoveredCount").GetInt32());
            Assert.Equal("input_1", v.GetProperty("discovered")[1].GetProperty("added").GetString());
            Assert.Equal(700, v.GetProperty("bandwidth").GetProperty("limitMbps").GetDouble());
        }
        var cards = Cards().ToJsonString(FixtureJson.Indented) + "\n";
        var statePath = Path.Combine(NodeExe.RepoRoot(), "tests", "fixtures", "state", "videoio.json");
        var cardsPath = Path.Combine(NodeExe.RepoRoot(), "tests", "fixtures", "state", "videoio.cards.json");
        if (Environment.GetEnvironmentVariable("PLAYMAP_WRITE_FIXTURES") == "1")
        {
            await File.WriteAllTextAsync(statePath, fixture);
            await File.WriteAllTextAsync(cardsPath, cards);
        }
        Assert.Equal((await File.ReadAllTextAsync(statePath)).ReplaceLineEndings("\n"), fixture);
        Assert.Equal((await File.ReadAllTextAsync(cardsPath)).ReplaceLineEndings("\n"), cards);

        // Every card path reads a value in the fixture (a list card's paths read its first row), and every action, its {id} the first
        // row's real id, is a line of the catalogue this engine routes.
        var raw = JsonNode.Parse(StateNow(rig))!;
        foreach (var card in Cards().Select(c => c!.AsObject()))
        {
            var path = card["path"]!.GetValue<string>();
            var rows = Resolve(raw, path).ToList();
            Assert.NotEmpty(rows);
            var row = rows[0];
            var relative = path.EndsWith("[]", StringComparison.Ordinal);
            Assert.NotEmpty(Resolve(relative ? row : raw, card["light"]!["path"]!.GetValue<string>()));
            Assert.NotEmpty(Resolve(relative ? row : raw, card["words"]!.GetValue<string>()));
            foreach (var field in card["fields"]!.AsArray())
                Assert.NotEmpty(Resolve(relative ? row : raw, field!["path"]!.GetValue<string>()));
            foreach (var action in card["actions"]!.AsArray())
            {
                var line = action!["line"]!.GetValue<string>();
                if (relative)
                    line = line.Replace("{id}", row!["id"]!.GetValue<string>(), StringComparison.Ordinal);
                Assert.True(WireGrammar.TryParse(line, out var parsed, out _) && VerbCatalog.Match(parsed) is { } m && rig.Engine.IsAvailable(m.Spec), line);
            }
        }
    }

    private static string StateNow(VideoIoRig rig)
    {
        rig.Engine.FactsChanged();
        return rig.Engine.State().Json;
    }

    [Fact]
    public async Task TheFixtureIsTheSameOnASecondRun()
    {
        await using var a = await Scenario();
        await using var b = await Scenario();
        Assert.Equal(FixtureOf(StateNow(a)), FixtureOf(StateNow(b)));
    }

    private static IEnumerable<JsonNode?> Resolve(JsonNode? node, string path)
    {
        IEnumerable<JsonNode?> current = [node];
        foreach (var part in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            var list = part.EndsWith("[]", StringComparison.Ordinal);
            var name = list ? part[..^2] : part;
            current = current.SelectMany(n => n is JsonObject o && o.ContainsKey(name) ? [o[name]] : Enumerable.Empty<JsonNode?>()).ToList();
            if (list)
                current = current.SelectMany(n => n is JsonArray a ? a : Enumerable.Empty<JsonNode?>()).ToList();
        }
        return current;
    }
}
