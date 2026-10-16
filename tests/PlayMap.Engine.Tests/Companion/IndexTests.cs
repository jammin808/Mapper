using System.Text.Json;
using PlayMap.Core.Model;
using PlayMap.Core.Wire;

namespace PlayMap.Engine.Tests.Companion;

/// <summary>INDEX is the show's structure for a deck: its fields, every package's section with its preset hint as data, a structure
/// signature and a labels signature (a rename moves only the labels), STATE's project.index, and SUBSCRIBE INDEX pushed when a
/// playlist is added — latest wins, asserted on the push — when a section row's light moves with the show, and when anything else it
/// lists moves within its kind (an item's source, a playlist's loop).</summary>
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
    public async Task EverySectionKeyIsPressedAtLeastAsHardAsTheLineItSends()
    {
        // Every package's section, the fleet's and the sync group's too, is listed on every node; a key whose line is its own (not a
        // row's id) says a press no softer than that line's (design §3.7: held unless it is one of the eight instant presses, pressed
        // twice where its row is), so a deck that follows the hint alone never fires a held line at once.
        await using var engine = new EngineCore(EngineServices.ForTests());
        var sections = engine.Routes.IndexSections.ToList();
        Assert.Superset(new HashSet<string>(["fleet", "sync", "suggestions", "scan", "eye", "show", "led", "calibration", "timecode"], StringComparer.Ordinal),
            sections.Select(s => s.Section).ToHashSet(StringComparer.Ordinal));
        string[] order = [PressShapes.Instant, PressShapes.Hold, PressShapes.Double];
        var soft = new List<string>();
        foreach (var section in sections)
        {
            foreach (var key in section.Hint.Keys)
            {
                Assert.Contains(key.Press, order);
                if (key.DownLine.Trim() == "$(id)")
                    continue;
                var text = key.DownLine.Replace("$(id)", "x0a1b2c3d", StringComparison.Ordinal);
                Assert.True(WireGrammar.TryParse(text, out var line, out var error), $"{section.Section}: {text}: {error?.Reason}");
                var press = PressOf(line);
                if (Array.IndexOf(order, key.Press) < Array.IndexOf(order, press))
                    soft.Add($"{section.Section} '{key.DownLine}' says {key.Press}, its line is pressed {press}");
            }
        }
        Assert.True(soft.Count == 0, string.Join("\n", soft));
    }

    /// <summary>A line's press as a key sends it: its row's (the eight instant rows, a row pressed twice), else held when the line is
    /// high-risk with its words (a row's per-argument risk) or a review row that puts a picture over the air.</summary>
    private static string PressOf(WireLine line)
    {
        var spec = VerbCatalog.Match(line)?.Spec;
        Assert.NotNull(spec);
        var row = VerbParams.Press(spec);
        if (row == PressShapes.Double || (row == PressShapes.Instant && spec.Risk == VerbRisk.High))
            return row;
        return VerbCatalog.RiskOf(line) == VerbRisk.High || (row == PressShapes.Hold && spec.Risk != VerbRisk.High) ? PressShapes.Hold : PressShapes.Instant;
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
    public async Task AProjectHoldingAnIdTwiceStillWritesIndexAndState()
    {
        var media = new MediaItem("md00000001", "One", MediaKind.Video, "/nowhere/one.mp4", "sha256:" + new string('a', 64), 1000,
            MediaInfo.Unknown with { DurationNs = 4_000_000_000 }, []);
        await using var rig = new CompanionRig(project: Project.CreateDefault("Twice") with { Media = [media, media with { Name = "Again" }] });
        var index = CompanionRig.Json(await rig.Ok("INDEX"));
        Assert.Equal(2, index.GetProperty("media").GetProperty("count").GetInt32());
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

    [Fact]
    public async Task ASectionRowsLightMovingWithTheShowPushesIndexAgainWithItsStructureAndLabelsKept()
    {
        await using var rig = new CompanionRig();
        rig.Engine.Routes.Index(new FakeSection());
        var list = await rig.Ok("PLAYLIST NEW Show");
        var sink = new RecordingSink();
        using var sub = rig.Open("INDEX", sink);
        await sink.Heard("\"tr-" + list + "\"");
        var opened = CompanionRig.Json(sink.Latests["index"]["INDEX ".Length..]);
        Assert.Equal("green", RowLight(opened, list));

        // Blackout moves no structure and no label, only the row's light. Every INDEX carries every section's rows, and another
        // section's may be amber from the first: the wait is for this row lit amber.
        await rig.Ok("BLACKOUT ON", OpsRig.Desk);
        await sink.Heard(l => RowLight(CompanionRig.Json(l["INDEX ".Length..]), list) == "amber");
        var pushed = CompanionRig.Json(sink.Latests["index"]["INDEX ".Length..]);
        Assert.Equal("amber", RowLight(pushed, list));
        Assert.Equal(opened.GetProperty("sig").GetString(), pushed.GetProperty("sig").GetString());
        Assert.Equal(opened.GetProperty("labels").GetString(), pushed.GetProperty("labels").GetString());
        Assert.Equal(2, sink.Lines.Count);

        // STATE moving again with the row's light where it was pushes nothing new.
        await rig.Ok("FREEZE ON", OpsRig.Desk);
        await rig.Ok("PLAYLIST NEW Encore");
        await sink.Heard("Encore");
        Assert.Equal(3, sink.Lines.Count);
    }

    [Fact]
    public async Task ASourceChangeWithinItsKindPushesIndexWithTheNewSourceAndItsSignaturesKept()
    {
        await using var rig = new CompanionRig();
        var list = await rig.Ok("PLAYLIST NEW Show");
        var item = await rig.Ok($"PLAYLIST ADD {list} color:#FF0000 Wash");
        var sink = new RecordingSink();
        using var sub = rig.Open("INDEX", sink);
        await sink.Heard("color:#FF0000");
        var opened = CompanionRig.Json(sink.Latests["index"]["INDEX ".Length..]);

        // A colour for a colour: no id, kind or name moves, yet a key's picture does.
        await rig.Ok($"SET {item} source {{\"kind\":\"color\",\"id\":\"#00FF00\"}}");
        await sink.Heard("color:#00FF00");
        var pushed = CompanionRig.Json(sink.Latests["index"]["INDEX ".Length..]);
        Assert.Equal("color:#00FF00", pushed.GetProperty("playlists")[0].GetProperty("items")[0].GetProperty("source").GetString());
        Assert.Equal(opened.GetProperty("sig").GetString(), pushed.GetProperty("sig").GetString());
        Assert.Equal(opened.GetProperty("labels").GetString(), pushed.GetProperty("labels").GetString());
        Assert.Equal(2, sink.Lines.Count);

        // A playlist's loop is listed too: it pushes as well.
        await rig.Ok($"SET {list} loop true");
        await sink.Heard("\"loop\":true");
        Assert.True(CompanionRig.Json(sink.Latests["index"]["INDEX ".Length..]).GetProperty("playlists")[0].GetProperty("loop").GetBoolean());
        Assert.Equal(3, sink.Lines.Count);
    }

    private static string? RowLight(JsonElement index, string list) =>
        index.GetProperty("sections").EnumerateArray().Single(s => s.GetProperty("section").GetString() == "companion-test")
            .GetProperty("rows").EnumerateArray().Single(r => r.GetProperty("id").GetString() == "tr-" + list).GetProperty("light").GetString();
}
