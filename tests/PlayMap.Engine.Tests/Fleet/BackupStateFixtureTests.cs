using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PlayMap.Core.Time;
using PlayMap.Core.Wire;
using PlayMap.Engine.Tests.Host;
using PlayMap.Engine.Tests.Spine;
using PlayMap.Engine.Tests.Sync;

namespace PlayMap.Engine.Tests.Fleet;

/// <summary>STATE.fleet of a hot-backup pair as the desk reads it, written to tests/fixtures/state/backup.json from two real nodes: the
/// backup mirroring (its mirror, the route it has by the mirror), the primary with its backup listed, and the backup after FAILOVER (its
/// last takeover); and backup.cards.json, the cards the desk draws from those keys, each field and verb checked against the fixture.
/// What moves between runs (times, ids, milliseconds, the other machines' health) is set to fixed values.</summary>
public sealed partial class BackupStateFixtureTests
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static string FixturePath(string name) => Path.Combine(NodeExe.RepoRoot(), "tests", "fixtures", "state", name);

    private static JsonObject Field(string label, string path) => new() { ["label"] = label, ["path"] = path };

    /// <summary>The cards, each with the fixture key it is read from (the desk reads them at fleet.… on whichever node shows them).</summary>
    private static readonly (string On, JsonObject Card)[] Cards =
    [
        ("backup", new JsonObject
        {
            ["title"] = "Hot backup",
            ["path"] = "fleet.mirror",
            ["light"] = new JsonArray(new JsonObject { ["from"] = "state", ["green"] = "identical", ["red"] = "lost" }),
            ["words"] = "{words}",
            ["fields"] = new JsonArray(Field("Of", "name"), Field("State", "state"), Field("Revision", "rev"), Field("Behind", "behindRev")),
            ["actions"] = new JsonArray("FAILOVER", "FLEET SOLO", "PREFLIGHT QUICK"),
        }),
        ("primary", new JsonObject
        {
            ["title"] = "Backups",
            ["path"] = "fleet.backups",
            ["light"] = new JsonArray(new JsonObject { ["from"] = "0.light" }),
            ["words"] = "{count} hot backups",
            ["fields"] = new JsonArray(Field("Name", "0.name"), Field("State", "0.state"), Field("Words", "0.words")),
            ["actions"] = new JsonArray("FLEET FOCUS $(node)", "FLEET ROUTE TEST"),
        }),
        ("takenOver", new JsonObject
        {
            ["title"] = "Last takeover",
            ["path"] = "fleet.last",
            ["light"] = new JsonArray(new JsonObject { ["fixed"] = "green" }),
            ["words"] = "{words}",
            ["fields"] = new JsonArray(Field("Kind", "kind"), Field("Id", "id"), Field("At", "at")),
            ["actions"] = new JsonArray("FAILBACK CONFIRM"),
        }),
        ("backup", new JsonObject
        {
            ["title"] = "Routes",
            ["path"] = "fleet.routes",
            ["light"] = new JsonArray(new JsonObject { ["fixed"] = "grey" }),
            ["words"] = "{count} routes",
            ["fields"] = new JsonArray(Field("Output", "0.output"), Field("Device", "0.device"), Field("Primary input", "0.input"),
                Field("Backup input", "0.backupInput")),
            ["actions"] = new JsonArray("FLEET ROUTE TEST"),
        }),
    ];

    [GeneratedRegex("tk[0-9a-f]{8}")]
    private static partial Regex TakeoverId();

    [GeneratedRegex(@"[0-9]+(\.[0-9]+)? ms")]
    private static partial Regex Millis();

    [GeneratedRegex("dv[0-9a-f]{8}")]
    private static partial Regex DeviceId();

    private static JsonNode Steady(JsonElement fleet)
    {
        var o = JsonNode.Parse(DeviceId().Replace(Millis().Replace(TakeoverId().Replace(fleet.GetRawText(), "tk00000000"), "0 ms"), "dv00000001"))!.AsObject();
        o.Remove("nodes");
        o["light"] = "green";
        o["headline"] = "2 machines";
        if (o["last"] is JsonObject last)
            last["at"] = 1_760_000_000_000;
        return o;
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
    public async Task TheBackupPairsStateIsTheCommittedFixtureAndItsCardsReadIt()
    {
        var clock = new ManualClock(1_000 * SyncRig.Second);
        var time = new ManualTime();
        await using var primary = await BackupRig.StartAsync("FOH-A", BackupRig.PrimaryId, clock, time);
        await using var backup = await BackupRig.StartAsync("FOH-B", BackupRig.BackupId, clock, time);
        await SyncRig.OpenShowAsync(primary);
        var dv = await primary.OkAsync("DEVICE ADD P1 pjlink 127.0.0.1:9");
        await primary.OkAsync($"FLEET ROUTE op_1 DEVICE {dv} 31 32");
        await primary.OkAsync("SYNC LEAD show");
        await BackupRig.MeetAsync(primary, backup);
        await BackupRig.BackUpAsync(primary, backup);
        Assert.True(await Eventually.TrueAsync(() => BackupRig.Fleet(primary).GetProperty("backups").EnumerateArray()
            .Any(b => b.GetProperty("state").GetString() == "identical")), BackupRig.Fleet(primary).ToString());

        var root = new JsonObject
        {
            ["backup"] = new JsonObject { ["fleet"] = Steady(BackupRig.Fleet(backup)) },
            ["primary"] = new JsonObject { ["fleet"] = Steady(BackupRig.Fleet(primary)) },
        };
        Assert.True((await backup.RunAsync("FAILOVER")).Ok);
        root["takenOver"] = new JsonObject { ["fleet"] = Steady(BackupRig.Fleet(backup)) };

        var fixture = root.ToJsonString(Indented) + "\n";
        var cards = new JsonArray([.. Cards.Select(c => c.Card.DeepClone())]).ToJsonString(Indented) + "\n";
        if (Environment.GetEnvironmentVariable("PLAYMAP_WRITE_FIXTURES") == "1")
        {
            await File.WriteAllTextAsync(FixturePath("backup.json"), fixture);
            await File.WriteAllTextAsync(FixturePath("backup.cards.json"), cards);
        }
        Assert.Equal((await File.ReadAllTextAsync(FixturePath("backup.json"))).ReplaceLineEndings("\n"), fixture);
        Assert.Equal((await File.ReadAllTextAsync(FixturePath("backup.cards.json"))).ReplaceLineEndings("\n"), cards);

        var parsed = JsonNode.Parse(fixture)!;
        foreach (var (on, card) in Cards)
        {
            var at = At(parsed[on], card["path"]!.GetValue<string>());
            Assert.NotNull(at);
            foreach (var field in card["fields"]!.AsArray())
                Assert.True(At(at, field!["path"]!.GetValue<string>()) is not null, $"{card["title"]}: {field["path"]}");
            foreach (var action in card["actions"]!.AsArray())
            {
                Assert.True(WireGrammar.TryParse(action!.GetValue<string>(), out var line, out _));
                Assert.NotNull(VerbCatalog.Match(line));
            }
        }
    }
}
