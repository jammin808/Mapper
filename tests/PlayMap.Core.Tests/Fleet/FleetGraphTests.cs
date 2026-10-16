using System.Text.Json;
using PlayMap.Core.Bus;
using PlayMap.Core.Fleet;

namespace PlayMap.Core.Tests.Fleet;

/// <summary>The board as one graph: a golden for three machines, the same facts giving the same ids and places in any order, the
/// PROBLEMS lens, the problems queue and what words resolve to.</summary>
public sealed class FleetGraphTests
{
    /// <summary>FOH-A (this node, primary, two outputs and a projector), FOH-B (its backup, following its clock, one output dropping
    /// frames) and FOH-C (gone quiet 7 s after its last record).</summary>
    internal static IReadOnlyList<FleetMachine> ThreeNodes()
    {
        var a = HealthRecordTests.Known();
        var b = a with
        {
            Node = "9f8e7d6c",
            Name = "FOH-B",
            Role = "backup",
            Of = "0badc0de",
            SyncRole = "follower",
            SyncLeader = "0badc0de",
            SyncUncertaintyMs = 0.4,
            Outputs = [new HealthOutput("op_1", "Left", true, 59, 4, 1, "gl-window")],
            Projectors = [],
        };
        var c = a with { Node = "c0ffee00", Name = "FOH-C", Role = "solo", Projectors = [], Outputs = [new HealthOutput("op_9", "Rear", true, 60, 0, 0, "gl-window")] };
        return
        [
            new("0badc0de", "FOH-A", FleetHeard.Live, HealthRecord.Of(a), "", Self: true),
            new("9f8e7d6c", "FOH-B", FleetHeard.Live, HealthRecord.Of(b), "subscribed over TLS, certificate pinned") { SilentS = 0.4 },
            new("c0ffee00", "FOH-C", FleetHeard.Gone, HealthRecord.Of(c), "FOH-C's health feed ended") { SilentS = 7 },
        ];
    }

    private static string GoldenPath() => Path.Combine(RepoRoot(), "tests", "PlayMap.Core.Tests", "Fleet", "Golden", "board-3.json");

    internal static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "PlayMap.sln")))
                return dir.FullName;
        }
        throw new InvalidOperationException("the repository root (PlayMap.sln) is not above the test's folder");
    }

    private static readonly JsonSerializerOptions IndentedOptions = new() { WriteIndented = true, NewLine = "\n" };

    private static string Indented(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return JsonSerializer.Serialize(doc.RootElement, IndentedOptions) + "\n";
    }

    [Fact]
    public async Task ThreeMachinesAreTheGolden()
    {
        var board = Indented(FleetJson.Board(FleetGraph.Build(ThreeNodes()), "", FleetLens.All));
        var path = GoldenPath();
        if (Environment.GetEnvironmentVariable("PLAYMAP_WRITE_FIXTURES") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, board);
        }
        Assert.True(File.Exists(path), "board-3.json is written by this test: run it once with PLAYMAP_WRITE_FIXTURES=1");
        Assert.Equal((await File.ReadAllTextAsync(path)).ReplaceLineEndings("\n"), board);
    }

    [Fact]
    public void TheGraphSaysWhatTheFactsSay()
    {
        var g = FleetGraph.Build(ThreeNodes());
        Assert.Equal(
            ["machine:0badc0de", "output:0badc0de:op_1", "output:0badc0de:op_2", "projector:0badc0de:pj1",
             "machine:c0ffee00", "output:c0ffee00:op_9", "machine:9f8e7d6c", "output:9f8e7d6c:op_1"],
            g.Nodes.Select(n => n.Id));
        Assert.Equal(Light.Red, g.Find("machine:c0ffee00")!.Light);
        Assert.Equal("gone quiet 7 s", g.Find("machine:c0ffee00")!.Sub);
        Assert.Equal(Light.Grey, g.Find("output:c0ffee00:op_9")!.Light);
        Assert.Equal(Light.Amber, g.Find("output:9f8e7d6c:op_1")!.Light);
        Assert.Contains(g.Edges, e => e is { From: "machine:9f8e7d6c", To: "machine:0badc0de", Kind: "mirrors" });
        Assert.Contains(g.Edges, e => e is { From: "machine:9f8e7d6c", To: "machine:0badc0de", Kind: "follows" });
        Assert.Contains(g.Edges, e => e is { From: "output:0badc0de:op_1", To: "projector:0badc0de:pj1", Kind: "drives" });
        Assert.All(g.Edges, e => Assert.True(ContextLinkKinds.IsEyeVerb(e.Kind), e.Kind));
        Assert.Equal(["machine:c0ffee00", "machine:9f8e7d6c", "output:9f8e7d6c:op_1"], g.Problems.ToArray());
        Assert.Equal(Light.Red, g.WorstLight);
        Assert.Equal("3 machines · 4 outputs · 1 red · 2 amber — FOH-C: gone quiet 7 s", g.Headline);
    }

    [Fact]
    public void TheSameFactsGiveTheSameIdsAndPlacesInAnyOrder()
    {
        var facts = ThreeNodes();
        var once = FleetJson.Board(FleetGraph.Build(facts), "", FleetLens.All);
        Assert.Equal(once, FleetJson.Board(FleetGraph.Build([.. facts.Reverse()]), "", FleetLens.All));
        Assert.Equal(once, FleetJson.Board(FleetGraph.Build([facts[1], facts[2], facts[0], facts[1]]), "", FleetLens.All));

        // A machine keeps its row while another goes: FOH-B's row is the same with or without FOH-C… after it, by name.
        var withoutC = FleetGraph.Build([facts[0], facts[1]]);
        var all = FleetGraph.Build(facts);
        Assert.Equal(all.PlaceOf("machine:0badc0de"), withoutC.PlaceOf("machine:0badc0de"));
        Assert.Equal(all.PlaceOf("output:0badc0de:op_2").X, withoutC.PlaceOf("output:0badc0de:op_2").X);
    }

    [Fact]
    public void TheProblemsLensHoldsRedsAndAmbersAndTheirNeighbours()
    {
        var g = FleetGraph.Build(ThreeNodes());
        var visible = g.Nodes.Where(n => g.Visible(n, FleetLens.Problems)).Select(n => n.Id).ToHashSet();
        Assert.Contains("machine:c0ffee00", visible);           // red
        Assert.Contains("output:9f8e7d6c:op_1", visible);       // amber
        Assert.Contains("machine:9f8e7d6c", visible);           // amber, and a neighbour of an amber
        Assert.Contains("machine:0badc0de", visible);           // a neighbour of FOH-B (it mirrors and follows it)
        Assert.Contains("output:c0ffee00:op_9", visible);       // a neighbour of the red machine
        Assert.DoesNotContain("projector:0badc0de:pj1", visible);
        Assert.DoesNotContain("output:0badc0de:op_2", visible);

        Assert.All(g.Nodes.Where(n => g.Visible(n, FleetLens.Outputs)), n => Assert.Contains(n.Kind, new[] { "machine", "output" }));
        Assert.Equal(["machine:0badc0de", "projector:0badc0de:pj1", "machine:c0ffee00", "machine:9f8e7d6c"],
            g.Nodes.Where(n => g.Visible(n, FleetLens.Projectors)).Select(n => n.Id));
        Assert.Equal(["machine:0badc0de", "machine:9f8e7d6c"], g.Nodes.Where(n => g.Visible(n, FleetLens.Backup)).Select(n => n.Id));
        Assert.Equal(["machine:0badc0de", "machine:9f8e7d6c"], g.Nodes.Where(n => g.Visible(n, FleetLens.Sync)).Select(n => n.Id));
    }

    [Fact]
    public void NextAndPrevWalkTheProblemsAndWordsResolveToThings()
    {
        var g = FleetGraph.Build(ThreeNodes());
        Assert.Equal("machine:c0ffee00", g.Next(null));
        Assert.Equal("machine:9f8e7d6c", g.Next("machine:c0ffee00"));
        Assert.Equal("machine:c0ffee00", g.Next("output:9f8e7d6c:op_1"));
        Assert.Equal("output:9f8e7d6c:op_1", g.Prev("machine:c0ffee00"));
        Assert.Equal("machine:9f8e7d6c", g.Resolve("FOH-B"));
        Assert.Equal("machine:9f8e7d6c", g.Resolve("9f8e7d6c"));
        Assert.Equal("output:0badc0de:op_2", g.Resolve("output:0badc0de:op_2"));
        Assert.Equal("projector:0badc0de:pj1", g.Resolve("P1"));
        Assert.Null(g.Resolve("nothing here"));
        Assert.Equal("machine:0badc0de", g.Resolve("self"));
        var hops = g.Hops("machine:9f8e7d6c");
        Assert.Equal(1.0, FleetGraph.Emphasis("machine:0badc0de", hops));
        Assert.Equal(0.55, FleetGraph.Emphasis("output:0badc0de:op_1", hops));
        Assert.Equal(0.25, FleetGraph.Emphasis("machine:c0ffee00", hops));

        var green = FleetGraph.Build([ThreeNodes()[0]]);
        Assert.Null(green.Next(null));
        Assert.Equal("1 machine · 2 outputs · all green", green.Headline);
    }

    [Fact]
    public void TheBoardIsInPatternsEyeJsonFieldNames()
    {
        // Patterns' EyeJson.Write (patterns-svc bcdbf28, src/Patterns.Core/Services/EyeJson.cs), its fields in its order, as data.
        string[] patternsTop = ["headline", "worst", "worstLight", "worstWords", "counts", "focus", "lens", "problems", "nodes", "edges", "replay", "replayAt", "moment"];
        string[] patternsNode = ["id", "kind", "plane", "tier", "label", "sub", "light", "x", "y", "w", "h", "words", "page", "item", "wire"];
        string[] patternsEdge = ["from", "to", "kind", "light", "words"];
        string[] patternsCounts = ["green", "amber", "red", "grey", "things", "links"];
        using var doc = JsonDocument.Parse(FleetJson.Board(FleetGraph.Build(ThreeNodes()), "machine:9f8e7d6c", FleetLens.Problems));
        var root = doc.RootElement;
        Assert.Equal(patternsTop, root.EnumerateObject().Select(p => p.Name).Take(patternsTop.Length));
        Assert.Equal(patternsCounts, root.GetProperty("counts").EnumerateObject().Select(p => p.Name));
        foreach (var n in root.GetProperty("nodes").EnumerateArray())
        {
            Assert.Equal(patternsNode, n.EnumerateObject().Select(p => p.Name).Take(patternsNode.Length));
            Assert.Equal("EYE FOCUS " + n.GetProperty("id").GetString(), n.GetProperty("wire").GetString());
        }
        foreach (var e in root.GetProperty("edges").EnumerateArray())
            Assert.Equal(patternsEdge, e.EnumerateObject().Select(p => p.Name));
        Assert.Equal("problems", root.GetProperty("lens").GetString());
        Assert.Equal("machine:9f8e7d6c", root.GetProperty("focus").GetString());
        Assert.Equal(3, root.GetProperty("machines").GetArrayLength());
    }
}
