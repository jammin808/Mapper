using System.Text.Json;
using System.Text.Json.Nodes;
using PlayMap.Core.Machine;
using PlayMap.Core.Wire;
using PlayMap.Engine.Tests.Spine;

namespace PlayMap.Engine.Tests.Machine;

/// <summary>tests/fixtures/state/machine.json is the machine block's round-3 keys (identity, capabilities, platform, locked) from a
/// scenario on a fake Windows node — IoT Enterprise LTSC 24H2, an RTX PRO, the runtimes filled by their owners, a render thread's claims,
/// one claim refused, the driver set locked and then a driver changed — and machine.cards.json the cards the desk draws from them. This
/// test writes both (PLAYMAP_WRITE_FIXTURES=1) and otherwise checks the committed files are what the engine writes now; every card's
/// path and fields resolve in the fixture and every action is a line of the catalogue.</summary>
public sealed class MachineStateFixtureTest
{
    private static readonly JsonSerializerOptions Indented = FixtureJson.Indented;

    private static string FixturePath(string name) => Path.Combine(NodeExe.RepoRoot(), "tests", "fixtures", "state", name);

    private static JsonObject Field(string label, string path) => new() { ["label"] = label, ["path"] = path };

    private static JsonArray Cards() =>
    [
        new JsonObject
        {
            ["title"] = "Machine platform",
            ["path"] = "machine.platform",
            ["light"] = "machine.platform.light",
            ["words"] = "{name}: {words}",
            ["fields"] = new JsonArray(Field("QoS", "settings.qos"), Field("Process", "settings.process"), Field("MMCSS", "settings.mmcss"),
                Field("Timer", "timer.mode"), Field("Timer p99 (µs)", "timer.lateP99Us"), Field("Awake", "awake"), Field("CPU sets", "settings.cpuSets"),
                Field("GPU priority", "settings.gpuPriority"), Field("Keep-awake", "settings.awake"), Field("Waits timed", "timer.waits"),
                Field("GC while armed", "gc.setting"), Field("GC held", "gc.held")),
            ["actions"] = new JsonArray("MACHINE STATUS", "PREFLIGHT QUICK", "MACHINE SET QOS HIGH", "MACHINE SET AWAKE ON"),
        },
        new JsonObject
        {
            ["title"] = "Claim",
            ["path"] = "machine.platform.claims",
            ["each"] = true,
            ["label"] = "what",
            ["light"] = "light",
            ["words"] = "{applied?{asked} → {readBack}:{asked} refused: {words}}",
            ["fields"] = new JsonArray(Field("Asked", "asked"), Field("Applied", "applied"), Field("Read back", "readBack"), Field("Words", "words")),
            ["actions"] = new JsonArray("MACHINE STATUS"),
        },
        new JsonObject
        {
            ["title"] = "This machine",
            ["path"] = "machine.identity",
            ["light"] = new JsonArray(new JsonObject { ["from"] = "os.tier", ["green"] = new JsonArray("ltsc", "managed", "home"), ["red"] = "unsupported" }, new JsonObject { ["fixed"] = "grey" }),
            ["words"] = "{os.name} {os.version} · build {os.build}.{os.revision} · {build.informational}",
            ["fields"] = new JsonArray(Field("Tier", "os.tier"), Field("Edition", "os.edition"), Field("CPU", "cpu.model"), Field("Logical processors", "cpu.logical"),
                Field("Vector bits", "cpu.vectorBits"), Field("GPUs", "adapters"), Field("Agility", "runtimes.agility"), Field("FFmpeg", "runtimes.ffmpeg"),
                Field("Sync firmware", "runtimes.syncFirmware")),
            ["actions"] = new JsonArray("MACHINE STATUS", "MACHINE LOCK"),
        },
        new JsonObject
        {
            ["title"] = "GPU",
            ["path"] = "machine.identity.adapters",
            ["each"] = true,
            ["label"] = "name",
            ["key"] = "luid",
            ["light"] = new JsonArray(new JsonObject { ["from"] = "support", ["green"] = new JsonArray("tier1", "validatedPerDriver"), ["amber"] = "bestEffort", ["red"] = "unsupported" }),
            ["words"] = "{tier} · driver {driver} · {support}",
            ["fields"] = new JsonArray(Field("Driver", "driver"), Field("Branch", "branch"), Field("Tier", "tier"), Field("Support", "support"),
                Field("Drives outputs", "drivesOutputs"), Field("Memory (MB)", "dedicatedMb"), Field("LUID", "luid")),
            ["actions"] = new JsonArray("MACHINE STATUS"),
        },
        new JsonObject
        {
            ["title"] = "Driver lock",
            ["path"] = "machine.locked",
            ["light"] = new JsonArray(new JsonObject { ["if"] = "drift", ["light"] = "amber" }, new JsonObject { ["fixed"] = "green" }),
            ["words"] = "{drift?{count} moved since the lock — MACHINE LOCK takes the new set:the drivers are the locked set}",
            ["fields"] = new JsonArray(Field("Windows", "set.os"), Field("PlayMap", "set.playmap"), Field("Agility", "set.agility"), Field("FFmpeg", "set.ffmpeg"),
                Field("Drift", "drift")),
            ["actions"] = new JsonArray("MACHINE LOCK", "MACHINE LOCK CLEAR"),
        },
        new JsonObject
        {
            ["title"] = "Capability",
            ["path"] = "machine.capabilities",
            ["each"] = true,
            ["label"] = "feature",
            ["key"] = "feature",
            ["light"] = new JsonArray(new JsonObject { ["if"] = "supported", ["light"] = "green" }, new JsonObject { ["fixed"] = "grey" }),
            ["words"] = "{supported?supported:{words}}",
            ["fields"] = new JsonArray(Field("Supported", "supported"), Field("Words", "words"), Field("Driver", "driver")),
            ["actions"] = new JsonArray("MACHINE STATUS"),
        },
    ];

    private static string Fixture(JsonElement state)
    {
        var machine = state.GetProperty("machine");
        var root = new JsonObject
        {
            ["machine"] = new JsonObject
            {
                ["identity"] = JsonNode.Parse(machine.GetProperty("identity").GetRawText()),
                ["capabilities"] = JsonNode.Parse(machine.GetProperty("capabilities").GetRawText()),
                ["platform"] = JsonNode.Parse(machine.GetProperty("platform").GetRawText()),
                ["locked"] = JsonNode.Parse(machine.GetProperty("locked").GetRawText()),
            },
        };
        return root.ToJsonString(Indented) + "\n";
    }

    private static JsonNode? At(JsonNode? node, string path)
    {
        foreach (var part in path.Split('.'))
        {
            node = node switch
            {
                JsonObject o => o[part],
                JsonArray a when int.TryParse(part, out var i) && i < a.Count => a[i],
                _ => null,
            };
        }
        return node;
    }

    [Fact]
    public async Task TheMachinesStateIsTheCommittedFixtureAndItsCardsReadIt()
    {
        await using var rig = new MachineRig();
        rig.Node.FillRuntimes(agility: "1.619.2 (app)", ffmpeg: "n9.0.2");
        rig.Node.FillRuntimes(syncFirmware: "3.06");
        rig.Platform.Refuse.Add("thread.render.priority");
        using (rig.Recorded.ClaimThread(ThreadRole.Render))
        using (rig.Recorded.ClaimThread(ThreadRole.Decode))
        {
            await rig.Ok("MACHINE LOCK");
            rig.Platform.Ident = MachineRig.Identity("591.44");
        }

        var state = rig.State();
        var machine = state.GetProperty("machine");
        Assert.Equal("ltsc", machine.GetProperty("identity").GetProperty("os").GetProperty("tier").GetString());
        Assert.Equal("1.619.2 (app)", machine.GetProperty("identity").GetProperty("runtimes").GetProperty("agility").GetString());
        Assert.Equal("amber", machine.GetProperty("platform").GetProperty("light").GetString());
        Assert.Single(machine.GetProperty("locked").GetProperty("drift").EnumerateArray());

        var fixture = Fixture(state);
        var cards = Cards().ToJsonString(Indented) + "\n";
        if (Environment.GetEnvironmentVariable("PLAYMAP_WRITE_FIXTURES") == "1")
        {
            PlayMap.Core.Storage.DurableFile.WriteAllText(FixturePath("machine.json"), fixture);
            PlayMap.Core.Storage.DurableFile.WriteAllText(FixturePath("machine.cards.json"), cards);
        }
        Assert.Equal(PlayMap.Core.Storage.DurableFile.ReadAllText(FixturePath("machine.json")).ReplaceLineEndings("\n"), fixture);
        Assert.Equal(PlayMap.Core.Storage.DurableFile.ReadAllText(FixturePath("machine.cards.json")).ReplaceLineEndings("\n"), cards);

        var root = JsonNode.Parse(fixture);
        foreach (var card in Cards())
        {
            var at = At(root, card!["path"]!.GetValue<string>());
            Assert.NotNull(at);
            JsonNode[] blocks = card["each"]?.GetValue<bool>() == true ? [.. at.AsArray().Select(e => e!)] : [at];
            Assert.NotEmpty(blocks);
            foreach (var block in blocks)
            {
                foreach (var field in card["fields"]!.AsArray())
                    Assert.True(At(block, field!["path"]!.GetValue<string>()) is not null, $"{card["title"]}: {field["path"]}");
            }
            foreach (var action in card["actions"]!.AsArray())
            {
                Assert.True(WireGrammar.TryParse(action!.GetValue<string>(), out var line, out _));
                Assert.NotNull(VerbCatalog.Match(line!));
            }
        }
    }
}
