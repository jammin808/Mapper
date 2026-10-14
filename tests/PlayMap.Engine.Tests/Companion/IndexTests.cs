using System.Text.Json;
using PlayMap.Core.Model;

namespace PlayMap.Engine.Tests.Companion;

/// <summary>INDEX is the show's structure for a deck: its fields, every package's section with its preset hint as data, a structure
/// signature and a labels signature (a rename moves only the labels), STATE's project.index, and SUBSCRIBE INDEX pushed when a
/// playlist is added — latest wins, asserted on the push.</summary>
public sealed class IndexTests
{
    private sealed class FakeSection : IIndexContributor
    {
        public string Section => "companion-test";

        public IndexPresetHint Hint { get; } = new("Test rows", "testrow", [new IndexKey("Toggle", "TEST TOGGLE $(id)", "test.on", "instant")]);

        public void WriteRows(Project project, EngineSnapshot snapshot, Utf8JsonWriter w)
        {
            w.WriteStartArray();
            foreach (var list in project.Playlists)
            {
                w.WriteStartObject();
                w.WriteString("id", "tr-" + list.Id);
                w.WriteString("kind", "testrow");
                w.WriteString("label", "Row of " + list.Name);
                w.WriteString("parent", list.Id);
                w.WriteString("light", snapshot.Air.Blackout ? "amber" : "green");
                w.WriteString("state", "on");
                w.WriteEndObject();
            }
            w.WriteEndArray();
        }
    }

    [Fact]
    public async Task IndexListsTheShowAndEverySectionWithItsHint()
    {
        await using var rig = new CompanionRig();
        rig.Engine.Routes.Index(new FakeSection());
        var list = await rig.Ok("PLAYLIST NEW Show");
        var gen = await rig.Ok("GENERATOR NEW flat White");
        var item = await rig.Ok($"PLAYLIST ADD {list} generator:{gen} Intro");
        await rig.Ok($"PLAYLIST ADD {list} color:#FF0000 Red");

        var index = CompanionRig.Json(await rig.Ok("INDEX"));
        Assert.Equal(1, index.GetProperty("v").GetInt32());
        Assert.Equal(rig.Engine.Store.Current.Revision, index.GetProperty("revision").GetInt64());
        Assert.Matches("^[0-9a-f]{16}$", index.GetProperty("sig").GetString()!);
        Assert.Matches("^[0-9a-f]{16}$", index.GetProperty("labels").GetString()!);

        var pl = Assert.Single(index.GetProperty("playlists").EnumerateArray());
        Assert.Equal(list, pl.GetProperty("id").GetString());
        Assert.Equal("Show", pl.GetProperty("name").GetString());
        var items = pl.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(2, items.Count);
        Assert.Equal(item, items[0].GetProperty("id").GetString());
        Assert.Equal("Intro", items[0].GetProperty("label").GetString());
        Assert.Equal("generator:" + gen, items[0].GetProperty("source").GetString());
        Assert.Equal("generator", items[0].GetProperty("kind").GetString());
        Assert.Equal("color", items[1].GetProperty("kind").GetString());

        var g = index.GetProperty("generators").EnumerateArray().Single(x => x.GetProperty("id").GetString() == gen);
        Assert.Equal("pattern.flat", g.GetProperty("kind").GetString());
        var level = g.GetProperty("params").EnumerateArray().Single(p => p.GetProperty("name").GetString() == "level");
        Assert.Equal("number", level.GetProperty("kind").GetString());
        Assert.Equal(0, level.GetProperty("min").GetDouble());
        Assert.Equal(100, level.GetProperty("max").GetDouble());
        Assert.Equal(0, index.GetProperty("media").GetProperty("count").GetInt32());
        Assert.Equal(JsonValueKind.Array, index.GetProperty("surfaces").ValueKind);
        Assert.Equal(JsonValueKind.Array, index.GetProperty("projectors").ValueKind);
        Assert.Equal(JsonValueKind.Array, index.GetProperty("outputs").ValueKind);
        Assert.Equal(JsonValueKind.Array, index.GetProperty("devices").ValueKind);
        Assert.Equal(JsonValueKind.Array, index.GetProperty("canvases").ValueKind);

        var section = index.GetProperty("sections").EnumerateArray().Single(s => s.GetProperty("section").GetString() == "companion-test");
        var hint = section.GetProperty("hint");
        Assert.Equal("Test rows", hint.GetProperty("title").GetString());
        Assert.Equal("testrow", hint.GetProperty("rowKind").GetString());
        var key = Assert.Single(hint.GetProperty("keys").EnumerateArray());
        Assert.Equal("TEST TOGGLE $(id)", key.GetProperty("line").GetString());
        Assert.Equal("instant", key.GetProperty("press").GetString());
        var row = Assert.Single(section.GetProperty("rows").EnumerateArray());
        Assert.Equal("tr-" + list, row.GetProperty("id").GetString());
        Assert.Equal(list, row.GetProperty("parent").GetString());

        // STATE carries the structure signature, so a module without SUBSCRIBE INDEX knows when to ask.
        Assert.Equal(index.GetProperty("sig").GetString(), rig.State().GetProperty("project").GetProperty("index").GetString());
        Assert.False((await rig.Run("INDEX ALL")).Ok);
    }

    private sealed class FaultySection : IIndexContributor
    {
        public string Section => "companion-faulty";

        public IndexPresetHint Hint { get; } = new("Faulty", "faulty", []);

        public void WriteRows(Project project, EngineSnapshot snapshot, Utf8JsonWriter w) =>
            throw new NullReferenceException("a package's own bug");
    }

    [Fact]
    public async Task ASectionThatFaultsSaysSoAndNeverTakesIndexOrStateDown()
    {
        await using var rig = new CompanionRig();
        rig.Engine.Routes.Index(new FaultySection());
        var index = CompanionRig.Json(await rig.Ok("INDEX"));
        var section = index.GetProperty("sections").EnumerateArray().Single(s => s.GetProperty("section").GetString() == "companion-faulty");
        Assert.Equal(0, section.GetProperty("rows").GetArrayLength());
        Assert.Contains("a package's own bug", section.GetProperty("fault").GetString(), StringComparison.Ordinal);
        Assert.Equal(index.GetProperty("sig").GetString(), rig.State().GetProperty("project").GetProperty("index").GetString());
    }

    [Fact]
    public async Task ARenameMovesOnlyTheLabels()
    {
        await using var rig = new CompanionRig();
        var list = await rig.Ok("PLAYLIST NEW Show");
        await rig.Ok($"PLAYLIST ADD {list} color:#00FF00 Green");
        var before = CompanionRig.Json(await rig.Ok("INDEX"));

        await rig.Ok($"SET {list} name \"Encore\"");
        var renamed = CompanionRig.Json(await rig.Ok("INDEX"));
        Assert.Equal("Encore", renamed.GetProperty("playlists")[0].GetProperty("name").GetString());
        Assert.Equal(before.GetProperty("sig").GetString(), renamed.GetProperty("sig").GetString());
        Assert.NotEqual(before.GetProperty("labels").GetString(), renamed.GetProperty("labels").GetString());

        await rig.Ok($"PLAYLIST ADD {list} color:#0000FF Blue");
        var grown = CompanionRig.Json(await rig.Ok("INDEX"));
        Assert.NotEqual(renamed.GetProperty("sig").GetString(), grown.GetProperty("sig").GetString());
    }

    [Fact]
    public async Task SubscribeIndexPushesOnAPlaylistAddLatestWins()
    {
        await using var rig = new CompanionRig();
        var first = await rig.Ok("PLAYLIST NEW Show");
        var sink = new RecordingSink();
        using var sub = rig.Open("INDEX", sink);
        // At once: the INDEX the session asked for.
        await sink.Heard("\"" + first + "\"");
        var opened = sink.Latests["index"];

        var second = await rig.Ok("PLAYLIST NEW Encore");
        await sink.Heard("\"" + second + "\"");
        var pushed = sink.Latests["index"];
        Assert.StartsWith("INDEX {", pushed, StringComparison.Ordinal);
        var doc = CompanionRig.Json(pushed["INDEX ".Length..]);
        Assert.Equal(2, doc.GetProperty("playlists").GetArrayLength());
        Assert.NotEqual(CompanionRig.Json(opened["INDEX ".Length..]).GetProperty("sig").GetString(), doc.GetProperty("sig").GetString());
        // One key: a newer INDEX replaces an older one not yet written.
        Assert.Equal(["index"], sink.Latests.Keys);

        // A change INDEX does not show (a playlist's loop flag is shown; the air is not) pushes nothing new.
        var lines = sink.Lines.Count;
        await rig.Ok("BLACKOUT ON", OpsRig.Desk);
        await rig.Ok("PLAYLIST NEW Third");
        await sink.Heard("Third");
        Assert.Equal(lines + 1, sink.Lines.Count);

        sub.Dispose();
        await rig.Ok("PLAYLIST NEW Fourth");
        await rig.Ok("INDEX");
        Assert.DoesNotContain(sink.Lines, l => l.Contains("Fourth", StringComparison.Ordinal));
    }
}
