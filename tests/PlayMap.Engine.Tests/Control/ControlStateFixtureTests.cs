using System.Text.Json;
using System.Text.Json.Nodes;
using PlayMap.Core.Bus;
using PlayMap.Core.Preflight;
using PlayMap.Core.Time;
using PlayMap.Core.Wire;
using PlayMap.Devices.Midi;
using PlayMap.Engine.Tests.Spine;

namespace PlayMap.Engine.Tests.Control;

/// <summary>tests/fixtures/state/control.json is STATE's control and timecode keys (and each playlist's chase) from a real scenario —
/// a MIDI surface on its port, an Art-Net surface while Art-Net input is off, three bindings, one input heard, an internal source
/// rolling a chased playlist, an LTC source on a node without audio inputs — and control.cards.json the cards the desk draws from it.
/// PLAYMAP_WRITE_FIXTURES=1 writes both; otherwise the committed files must be what the engine writes now.</summary>
public sealed class ControlStateFixtureTests
{
    private static string FixturePath(string name) => Path.Combine(NodeExe.RepoRoot(), "tests", "fixtures", "state", name);

    internal static async Task<ControlRig> Scenario(ManualClock clock)
    {
        var midi = new FakeMidiBackend();
        midi.Plug("APC40 mkII");
        var rig = await ControlRig.StartAsync(Tc.TimecodeEngineTests.Show() with { Stage = BindingEngineTests.Show().Stage }, service: true, midi: midi, clock: clock);
        foreach (var line in new[]
        {
            "CONTROL SURFACE ADD APC40 MIDI \"APC40*\"", "CONTROL SURFACE ADD Desk ARTNET",
            "BIND ADD MIDI APC40 CC 1 48 TO SET sf_lobby opacity *", "BIND ADD ARTNET Desk 0 1 TO SET sf_stage opacity *", "BIND ADD OSC /go TO PLAYLIST GO pl_other",
            "TIMECODE SOURCE ADD Rehearsal INTERNAL FORMAT 25", "TIMECODE SOURCE ADD FOH LTC", "TIMECODE CHASE Show Rehearsal,FOH",
            "TIMECODE INTERNAL Rehearsal LOCATE 01:00:01:00", "TIMECODE INTERNAL Rehearsal PLAY",
        })
            await rig.Ok(line);
        await ControlRig.Until(() => rig.Service!.PortOf(rig.Engine.Store.Project.Control.Surfaces[0]) is not null, "APC40 on its port");
        await ControlRig.Until(() => rig.State().GetProperty("timecode").GetProperty("sources")[1].GetProperty("state").GetString() == "unavailable", "FOH unavailable");
        clock.AdvanceMs(2000);
        await rig.Engine.ControlInput.InjectAsync(Core.Control.InputAddress.Midi(Core.Control.MidiFamily.Cc, 1, 48, "APC40"), 1, CancellationToken.None);
        await rig.Engine.StepChasesAsync();
        return rig;
    }

    /// <summary>The keys with what moves from run to run (the OSC socket's ephemeral port) set to 0.</summary>
    private static string FixtureOf(string stateJson)
    {
        var root = JsonNode.Parse(stateJson)!.AsObject();
        var control = root["control"]!.DeepClone().AsObject();
        control["osc"]!["port"] = 0;
        var playlists = new JsonArray(root["playlists"]!.AsArray().Select(p => (JsonNode)new JsonObject { ["id"] = p!["id"]!.GetValue<string>(), ["chase"] = p["chase"]?.DeepClone() }).ToArray());
        var fixture = new JsonObject { ["control"] = control, ["timecode"] = root["timecode"]!.DeepClone(), ["playlists"] = playlists };
        var text = fixture.ToJsonString(FixtureJson.Indented) + "\n";
        // Fresh ids differ from run to run: each becomes its kind and its order of first appearance (ct00000001, bd00000002…).
        var seen = new Dictionary<string, string>(StringComparer.Ordinal);
        return System.Text.RegularExpressions.Regex.Replace(text, "\"(ct|bd|tc)[0-9a-f]{8}\"", m =>
        {
            if (!seen.TryGetValue(m.Value, out var stable))
            {
                stable = $"\"{m.Groups[1].Value}{(seen.Count + 1).ToString("00000000", System.Globalization.CultureInfo.InvariantCulture)}\"";
                seen[m.Value] = stable;
            }
            return stable;
        });
    }

    internal static JsonArray Cards() =>
    [
        new JsonObject
        {
            ["title"] = "Control",
            ["path"] = "control",
            ["light"] = new JsonObject { ["path"] = "control.surfaces[].light", ["red"] = new JsonArray("red"), ["amber"] = new JsonArray("amber"), ["green"] = new JsonArray("green") },
            ["words"] = "control.osc.words",
            ["fields"] = new JsonArray(Field("Input on", "control.enabled"), Field("OSC open", "control.osc.open"), Field("OSC port", "control.osc.port"),
                Field("MIDI", "control.midi.backend"), Field("Art-Net input", "control.dmx.artnet.enabled"), Field("Bindings", "control.bindings.count"),
                Field("Waiting", "control.bindings.waiting"), Field("Unplugged", "control.bindings.unplugged"), Field("Last input", "control.last.input")),
            ["actions"] = new JsonArray(Action("Input off", "CONTROL ENABLE OFF"), Action("Input on", "CONTROL ENABLE ON"), Action("Stop learning", "BIND LEARN OFF"),
                Action("Monitor", "CONTROL MONITOR 16"), Action("Ports", "CONTROL PORTS")),
        },
        new JsonObject
        {
            ["title"] = "Control surface",
            ["path"] = "control.surfaces[]",
            ["light"] = new JsonObject { ["path"] = "light", ["red"] = new JsonArray("red"), ["amber"] = new JsonArray("amber"), ["green"] = new JsonArray("green") },
            ["words"] = "name",
            ["fields"] = new JsonArray(Field("Kind", "kind"), Field("Port", "port"), Field("Connected", "connected"), Field("Bindings", "bindings")),
            ["actions"] = new JsonArray(Action("Bindings off", "BIND ENABLE $(id) OFF"), Action("Bindings on", "BIND ENABLE $(id) ON"), Action("List", "BIND LIST $(id)")),
        },
        new JsonObject
        {
            ["title"] = "Timecode source",
            ["path"] = "timecode.sources[]",
            ["light"] = new JsonObject
            {
                ["path"] = "state", ["red"] = new JsonArray("unavailable", "lost", "silent"), ["amber"] = new JsonArray("acquiring", "dropout", "reverse"),
                ["green"] = new JsonArray("rolling", "parked"),
            },
            ["words"] = "words",
            ["fields"] = new JsonArray(Field("Name", "name"), Field("Kind", "kind"), Field("Code", "text"), Field("Format", "detected"), Field("Speed (ppm)", "speedPpm"),
                Field("Discontinuities", "errors.discontinuity")),
            ["actions"] = new JsonArray(Action("Reset counts", "TIMECODE SOURCE RESET $(id)")),
        },
        new JsonObject
        {
            ["title"] = "Chase",
            ["path"] = "timecode.chases[]",
            ["light"] = new JsonObject
            {
                ["path"] = "state", ["red"] = new JsonArray("held", "stopped"), ["amber"] = new JsonArray("freewheel", "free", "outside", "acquiring", "reverse"),
                ["green"] = new JsonArray("locked", "parked"),
            },
            ["words"] = "words",
            ["fields"] = new JsonArray(Field("Code", "text"), Field("Item", "item"), Field("Engaged", "engaged"), Field("Delay (ms)", "delayMs"), Field("On loss", "onLoss")),
            ["actions"] = new JsonArray(Action("Engage", "TIMECODE ENGAGE $(playlist) ON"), Action("Disengage", "TIMECODE ENGAGE $(playlist) OFF")),
        },
    ];

    private static JsonObject Field(string label, string path) => new() { ["label"] = label, ["path"] = path };

    private static JsonObject Action(string label, string line) => new() { ["label"] = label, ["line"] = line };

    /// <summary>The values a card path names: "a.b[].c" fans out over the array b.</summary>
    private static IEnumerable<JsonNode?> Resolve(JsonNode? node, string path)
    {
        // A key that is there with null is there (a surface with no port yet).
        if (path.Length == 0)
        {
            yield return node;
            yield break;
        }
        if (node is null)
            yield break;
        var dot = path.IndexOf('.', StringComparison.Ordinal);
        var head = dot < 0 ? path : path[..dot];
        var rest = dot < 0 ? "" : path[(dot + 1)..];
        if (head.EndsWith("[]", StringComparison.Ordinal))
        {
            if (node[head[..^2]] is JsonArray array)
                foreach (var item in array)
                    foreach (var r in Resolve(item, rest))
                        yield return r;
            yield break;
        }
        if (node is JsonObject obj && obj.ContainsKey(head))
            foreach (var r in Resolve(obj[head], rest))
                yield return r;
    }

    [Fact]
    public async Task TodaysControlStateIsTheCommittedFixtureAndItsCardsReadIt()
    {
        await using var rig = await Scenario(new ManualClock(5_000_000_000));
        var fixture = FixtureOf(rig.Engine.State().Json);
        using (var doc = JsonDocument.Parse(fixture))
        {
            var control = doc.RootElement.GetProperty("control");
            Assert.Equal(("green", "amber"), (control.GetProperty("surfaces")[0].GetProperty("light").GetString(), control.GetProperty("surfaces")[1].GetProperty("light").GetString()));
            Assert.Equal("MIDI APC40 CC 1 48", control.GetProperty("last").GetProperty("input").GetString());
            var timecode = doc.RootElement.GetProperty("timecode");
            Assert.Equal(("rolling", "01:00:03:00"), (timecode.GetProperty("sources")[0].GetProperty("state").GetString(), timecode.GetProperty("sources")[0].GetProperty("text").GetString()));
            Assert.Equal("unavailable", timecode.GetProperty("sources")[1].GetProperty("state").GetString());
            Assert.Equal(("locked", "it_a"), (timecode.GetProperty("chases")[0].GetProperty("state").GetString(), timecode.GetProperty("chases")[0].GetProperty("item").GetString()));
        }
        var cards = Cards().ToJsonString(FixtureJson.Indented) + "\n";
        if (Environment.GetEnvironmentVariable("PLAYMAP_WRITE_FIXTURES") == "1")
        {
            PlayMap.Core.Storage.DurableFile.WriteAllText(FixturePath("control.json"), fixture);
            PlayMap.Core.Storage.DurableFile.WriteAllText(FixturePath("control.cards.json"), cards);
        }
        Assert.Equal((PlayMap.Core.Storage.DurableFile.ReadAllText(FixturePath("control.json"))).ReplaceLineEndings("\n"), fixture);
        Assert.Equal((PlayMap.Core.Storage.DurableFile.ReadAllText(FixturePath("control.cards.json"))).ReplaceLineEndings("\n"), cards);

        var state = JsonNode.Parse(fixture)!;
        foreach (var card in Cards().Select(c => c!.AsObject()))
        {
            var path = card["path"]!.GetValue<string>();
            var rows = Resolve(state, path).ToList();
            Assert.NotEmpty(rows);
            foreach (var field in card["fields"]!.AsArray())
            {
                var fieldPath = field!["path"]!.GetValue<string>();
                Assert.All(rows, r => Assert.NotEmpty(Resolve(path.Contains("[]", StringComparison.Ordinal) ? r : state, fieldPath)));
            }
            foreach (var action in card["actions"]!.AsArray())
            {
                var line = action!["line"]!.GetValue<string>().Replace("$(id)", "ct00000001", StringComparison.Ordinal).Replace("$(playlist)", "pl_show", StringComparison.Ordinal);
                Assert.True(WireGrammar.TryParse(line, out var parsed, out _) && VerbCatalog.Match(parsed) is not null, line);
            }
        }
    }

    [Fact]
    public async Task ThePreflightRowsSayWhatIsNotReadyWithTheLineThatFixesIt()
    {
        await using var rig = await Scenario(new ManualClock(5_000_000_000));
        var checks = (Control: (Engine.IPreflightCheck)new Engine.Control.ControlPreflight(), Timecode: (Engine.IPreflightCheck)new Engine.Control.TimecodePreflight());
        var context = new PreflightContext(rig.Engine, PreflightDepth.Quick, false);
        var rows = (await checks.Control.RunAsync(context, CancellationToken.None)).ToDictionary(r => r.Id);
        // The fault: a binding reads Art-Net while Art-Net input is off.
        Assert.Equal((Light.Red, "CONTROL DMX ARTNET ON"), (rows["control:artnet"].Light, rows["control:artnet"].Fix));
        var apc = rig.Engine.Store.Project.Control.Surfaces[0];
        Assert.Equal((Light.Green, "APC40 mkII"), (rows["control:" + apc.Id].Light, rows["control:" + apc.Id].Value));
        Assert.Equal(Light.Green, rows["control:osc"].Light);
        var tc = (await checks.Timecode.RunAsync(context, CancellationToken.None)).Single();
        Assert.Equal((Light.Green, "rolling"), (tc.Light, tc.Value));
        // The source stops: the chase's row turns amber with the line that hands the playlist back.
        await rig.Ok("TIMECODE INTERNAL Rehearsal STOP");
        await rig.Ok("TIMECODE CHASE Show FOH");
        tc = (await checks.Timecode.RunAsync(context, CancellationToken.None)).Single();
        Assert.Equal((Light.Red, "unavailable", "TIMECODE ENGAGE pl_show OFF"), (tc.Light, tc.Value, tc.Fix));
        await rig.Ok("TIMECODE ENGAGE Show OFF");
        await rig.Engine.StepChasesAsync();
        tc = (await checks.Timecode.RunAsync(context, CancellationToken.None)).Single();
        Assert.Equal((Light.Amber, "TIMECODE ENGAGE pl_show ON"), (tc.Light, tc.Fix));
        await rig.Ok("CONTROL ENABLE OFF");
        rows = (await checks.Control.RunAsync(context, CancellationToken.None)).ToDictionary(r => r.Id);
        Assert.Equal((Light.Red, "CONTROL ENABLE ON"), (rows["control:off"].Light, rows["control:off"].Fix));
    }

    [Fact]
    public async Task TheExtensionPutsTheArtNetSocketTheServiceAndBothChecksInTheServices()
    {
        var setup = new Engine.Host.ExtensionSetup(null!, null!, NodeSettings.CreateDefault("t"), MonoClock.Instance, ShowWallClock.System(), null!, null!, null!,
            NullBackends.Instance, null!, NullBackends.Instance, null!);
        await using var extension = new Engine.Control.ControlExtension(setup);
        var services = extension.Configure(EngineServices.ForTests());
        Assert.Equal("control", extension.Name);
        Assert.NotNull(services.ArtNet);
        Assert.NotNull(services.Control);
        Assert.Equal(["control", "timecode"], services.PreflightChecks.Select(c => c.Section));
        // Nothing opened the socket: no consumer needed it yet.
        Assert.False(((PlayMap.Net.Control.ArtNetEndpoint)services.ArtNet).IsOpen);
    }
}
