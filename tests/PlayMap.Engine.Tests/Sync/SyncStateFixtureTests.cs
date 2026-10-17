using System.Text.Json;
using System.Text.Json.Nodes;
using PlayMap.Core.Time;
using PlayMap.Core.Wire;
using PlayMap.Engine.Tests.Host;
using PlayMap.Engine.Tests.Spine;

namespace PlayMap.Engine.Tests.Sync;

/// <summary>tests/fixtures/state/sync.json is SYNC STATUS on a group's leader from a real scenario — two nodes over loopback on one
/// manual clock, each with one synthetic output (60 Hz on the leader, 59.999 on the member, 3.21 µs after it), the member's clock
/// stepped once — and
/// sync.cards.json the cards the desk draws from it. This test writes both (PLAYMAP_WRITE_FIXTURES=1) and otherwise checks the
/// committed files are what the engine writes now; every card's path resolves in the fixture and every action is a catalogue line.</summary>
public sealed class SyncStateFixtureTests
{
    private const long Second = SyncRig.Second;
    private const long Ahead = 7_300_000_000;
    private static readonly JsonSerializerOptions Indented = FixtureJson.Indented;

    private static string FixturePath(string name) => Path.Combine(NodeExe.RepoRoot(), "tests", "fixtures", "state", name);

    private static JsonArray Cards() =>
    [
        new JsonObject
        {
            ["title"] = "Sync",
            ["path"] = "sync",
            // The node's own light for the group (read with its verdict); a node that publishes none is red while it free-wheels.
            ["light"] = new JsonArray(new JsonObject { ["from"] = "light" }, Rule("if", "freewheelS", "red"), new JsonObject { ["light"] = "grey" }),
            ["words"] = "{role} · {group} · {verdict} — {words}",
            ["fields"] = new JsonArray(Field("Role", "role"), Field("Group", "group"), Field("Leader", "leader"), Field("Feed", "feed"),
                Field("Verdict", "verdict"), Field("Steps", "steps"), Field("Group air latency (ms)", "groupLatencyMs")),
            // A card's line takes its values from the card's STATE: this node takes the lead of the group it is in. Which leader to
            // follow is the operator's choice, not a value of this block.
            ["actions"] = new JsonArray("SYNC STATUS", "SYNC LEAD $(group)", "SYNC LATENCY AUTO"),
        },
        new JsonObject
        {
            ["title"] = "Clock",
            ["path"] = "sync",
            ["light"] = new JsonArray(Rule("if", "slewing", "amber"), new JsonObject { ["light"] = "green" }),
            ["words"] = "± {offsetMs} ms · {steps} steps",
            ["fields"] = new JsonArray(Field("Uncertainty (ms)", "offsetMs"), Field("Round trip (ms)", "rttMs"), Field("Drift (ppm)", "driftPpm"),
                Field("Slewing", "slewing"), Field("Steps", "steps"), Field("Free-wheel (s)", "freewheelS")),
            ["actions"] = new JsonArray("SYNC STATUS"),
        },
        new JsonObject
        {
            ["title"] = "Vblanks",
            ["path"] = "sync.outputs",
            ["light"] = new JsonArray(new JsonObject { ["light"] = "grey" }),
            ["words"] = "{count} outputs",
            ["fields"] = new JsonArray(Field("Node", "0.node"), Field("Output", "0.output"), Field("Refresh (Hz)", "0.refreshHz"),
                Field("Phase (µs)", "0.phaseUs"), Field("± (µs)", "0.uncertaintyUs"), Field("Missed (60 s)", "0.missed60s")),
            ["actions"] = new JsonArray("SYNC STATUS", "OUTPUT STATS"),
        },
        new JsonObject
        {
            ["title"] = "Pairs",
            ["path"] = "sync.pairs",
            ["light"] = new JsonArray(Rule("if", "0.mixedRate", "amber"), new JsonObject { ["light"] = "green" }),
            ["words"] = "{count} pairs",
            ["fields"] = new JsonArray(Field("A", "0.a"), Field("B", "0.b"), Field("B on", "0.bNode"), Field("Phase (µs)", "0.phaseUs"), Field("± (µs)", "0.uncertaintyUs"),
                Field("Next slip (s)", "0.nextSlipS")),
            ["actions"] = new JsonArray("SYNC STATUS"),
        },
    ];

    private static JsonObject Rule(string kind, string path, string light) => new() { [kind] = path, ["light"] = light };

    private static JsonObject Field(string label, string path) => new() { ["label"] = label, ["path"] = path };

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
    public async Task TheLeadersSyncStatusIsTheCommittedFixtureAndItsCardsReadIt()
    {
        var clock = new ManualClock(1_000 * Second);
        var leaderV = new FakeVblanks(FakeVblanks.At("op_1", 60, clock.NowNs + Ahead));
        var memberV = new FakeVblanks(FakeVblanks.At("op_1", 59.999, clock.NowNs + 3_210));
        await using var leader = await SyncRig.StartAsync("FOH", SyncRig.LeaderId, new OffsetClock(clock, Ahead), SyncRig.Fast(leaderV));
        await using var member = await SyncRig.StartAsync("Stage", SyncRig.FollowerId, clock, SyncRig.Fast(memberV));
        await SyncRig.OpenShowAsync(leader);
        await SyncRig.OpenShowAsync(member);
        await SyncRig.GroupAsync(leader, member);
        Assert.True(await Eventually.TrueAsync(() => SyncRig.Sync(leader).GetProperty("outputs").GetArrayLength() == 2), SyncRig.Sync(leader).ToString());
        // AUTO is carried on the watch after the member is heard (a lead carries none before).
        Assert.True(await Eventually.TrueAsync(() => SyncRig.Sync(leader).GetProperty("groupLatencyMs").GetDouble() == 16), SyncRig.Sync(leader).ToString());

        var status = await leader.OkAsync("SYNC STATUS");
        var sync = JsonNode.Parse(status)!.AsObject();
        Assert.Equal("leader", sync["role"]!.GetValue<string>());
        Assert.Equal("free-running", sync["verdict"]!.GetValue<string>());
        // The first round's keys, then SPINE §9's sync keys in their order (the group's, the outputs' latency and lock, the fleet's
        // epoch), then the group's light, last.
        Assert.Equal(["role", "leader", "offsetMs", "rttMs", "driftPpm", "groups", "group", "feed", "slewing", "steps", "freewheelS", "outputs", "pairs",
            "verdict", "words", "groupLatencyMs", "latencyMs", "lock", "epoch", "light"], sync.Select(kv => kv.Key));
        var fixture = new JsonObject { ["sync"] = sync }.ToJsonString(Indented) + "\n";
        var cards = Cards().ToJsonString(Indented) + "\n";
        if (Environment.GetEnvironmentVariable("PLAYMAP_WRITE_FIXTURES") == "1")
        {
            await File.WriteAllTextAsync(FixturePath("sync.json"), fixture);
            await File.WriteAllTextAsync(FixturePath("sync.cards.json"), cards);
        }
        Assert.Equal((await File.ReadAllTextAsync(FixturePath("sync.json"))).ReplaceLineEndings("\n"), fixture);
        Assert.Equal((await File.ReadAllTextAsync(FixturePath("sync.cards.json"))).ReplaceLineEndings("\n"), cards);

        var root = JsonNode.Parse(fixture);
        foreach (var card in Cards())
        {
            var at = At(root, card!["path"]!.GetValue<string>());
            Assert.NotNull(at);
            foreach (var field in card["fields"]!.AsArray())
                Assert.True(At(at, field!["path"]!.GetValue<string>()) is not null || at is JsonObject o && o.ContainsKey(field["path"]!.GetValue<string>()),
                    $"{card["title"]}: {field!["path"]}");
            foreach (var action in card["actions"]!.AsArray())
            {
                Assert.True(WireGrammar.TryParse(action!.GetValue<string>().Replace("$(group)", "show", StringComparison.Ordinal), out var line, out _));
                Assert.NotNull(VerbCatalog.Match(line!));
            }
        }
    }
}
