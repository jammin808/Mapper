using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PlayMap.Core.Bus;
using PlayMap.Core.Wire;
using PlayMap.Net.Links;

namespace PlayMap.Net.Tests;

/// <summary>A context as one post carries it to Stagewatch (Show Bus 1.3 §6, §14 <c>context.bytes</c>): within the bytes, byte for byte;
/// over them, its recent lines first within what a reader keeps of one, then trimmed in §6's order only until it fits, every element
/// it keeps the given one byte for byte, with one grey <c>context:trimmed</c> item saying what it left out; nothing posted when even
/// its own fields are over, or when it is not a context §6 reads.</summary>
public class ContextFitTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PlayMap.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("the repository root (PlayMap.sln) is not above the test");
    }

    /// <summary>The busy node's context, as the engine's own test writes it (199 commands, 33 items, 25 links).</summary>
    private static string Golden() =>
        File.ReadAllText(Path.Combine(RepoRoot(), "tests", "PlayMap.Engine.Tests", "Fleet", "Golden", "context-busy.json")).ReplaceLineEndings("\n").TrimEnd('\n');

    private static long Section14(string name)
    {
        var text = File.ReadAllText(Path.Combine(RepoRoot(), "docs", "SHOWBUS.md"));
        var row = text.Split('\n').Select(l => l.TrimEnd('\r')).Single(l => l.StartsWith(name + " ", StringComparison.Ordinal));
        return long.Parse(row[(name.Length + 1)..], CultureInfo.InvariantCulture);
    }

    private static int Bytes(string s) => Encoding.UTF8.GetByteCount(s);

    private static string Context(IEnumerable<ContextItem>? items = null, IEnumerable<ContextLink>? links = null,
        IEnumerable<ContextRecent>? recent = null, IEnumerable<ContextCommand>? commands = null, string headline = "fine") =>
        new ShowContext("playmap", "a1b2c3d4", "Stage left", 1_760_000_000_000, headline, [.. items ?? []], [.. links ?? []],
            [.. recent ?? []], [.. commands ?? []], [new ContextPeer("main", "5a5a5a5a", "Hall A", Light.Green, "Keynote · 12:30 left")]).ToJson();

    /// <summary>Each array's elements as sent.</summary>
    private static Dictionary<string, List<string>> Arrays(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return new[] { "items", "links", "recent", "commands" }
            .ToDictionary(a => a, a => doc.RootElement.GetProperty(a).EnumerateArray().Select(e => e.GetRawText()).ToList());
    }

    private static string Field(string element, string name)
    {
        using var doc = JsonDocument.Parse(element);
        return doc.RootElement.GetProperty(name).GetString()!;
    }

    /// <summary>An array's bytes as sent: each element's, and the comma between two.</summary>
    private static long ArrayBytes(IReadOnlyCollection<string> parts) => parts.Count == 0 ? 0 : parts.Sum(p => (long)Bytes(p)) + parts.Count - 1;

    /// <summary>The four arrays' bytes as sent.</summary>
    private static long ArraysBytes(Dictionary<string, List<string>> arrays) => arrays.Values.Sum(ArrayBytes);

    /// <summary>The context:trimmed item as §6 words it.</summary>
    private static string TrimmedItem(ContextLeftOut leftOut) =>
        "{\"id\":\"context:trimmed\",\"kind\":\"health\",\"label\":\"Context\",\"light\":\"grey\",\"state\":\"" + leftOut.Words + "\"}";

    /// <summary>What trimming works within at <paramref name="max"/>: the bytes less the context's own fields (everything but its four
    /// arrays' elements) and room for the context:trimmed item at its longest, every count at its most, with the comma before it.</summary>
    private static long Budget(Dictionary<string, List<string>> given, long fixedBytes, int max) =>
        max - fixedBytes - (Bytes(TrimmedItem(new ContextLeftOut(given["recent"].Count, given["links"].Count, given["commands"].Count, given["items"].Count))) + 1);

    /// <summary>The panic, air and transport commands of the catalogue, and the rest, as the node's context lists them.</summary>
    private static (List<ContextCommand> Air, List<ContextCommand> Others) CatalogueCommands()
    {
        var all = ShowContext.CommandsFromCatalog();
        var air = VerbCatalog.All.Where(v => v.Show is ShowClass.Panic or ShowClass.Air or ShowClass.Transport).Select(v => v.Syntax)
            .ToHashSet(StringComparer.Ordinal);
        return ([.. all.Where(c => air.Contains(c.Verb))], [.. all.Where(c => !air.Contains(c.Verb))]);
    }

    [Fact]
    public void TheBytesAndARecentLinesSizeAreSection14s()
    {
        Assert.Equal(ContextFit.MaxBytes, Section14("context.bytes"));
        Assert.Equal(ContextFit.RecentChars, Section14("context.recent.chars"));
        Assert.Equal("left out: 12 recent, 40 links, 30 commands, 5 items", new ContextLeftOut(12, 40, 30, 5).Words);
        Assert.Equal("left out: 1 recent, 1 link, 1 command, 1 item", new ContextLeftOut(1, 1, 1, 1).Words);
        Assert.Equal("left out: 3 links", new ContextLeftOut(0, 3, 0, 0).Words);
    }

    [Fact]
    public void AContextWithinTheBytesIsPostedByteForByte()
    {
        var golden = Golden();
        var post = ContextFit.ForPost(golden);
        Assert.Equal(golden, post.Body);
        Assert.Equal((Bytes(golden), Bytes(golden), false, "", default(ContextLeftOut)), (post.Bytes, post.ContextBytes, post.Trimmed, post.What, post.LeftOut));
        Assert.False(post.OnlyRecentCut);

        // At the bound exactly, byte for byte; one byte over, trimmed.
        var recent = Enumerable.Range(0, 20).Select(i => new ContextRecent(1_760_000_000_000 + i, "Show: line " + i)).ToList();
        var bare = Bytes(Context(recent: recent, headline: ""));
        var atBound = Context(recent: recent, headline: new string('h', ContextFit.MaxBytes - bare));
        Assert.Equal(ContextFit.MaxBytes, Bytes(atBound));
        Assert.Same(atBound, ContextFit.ForPost(atBound).Body);
        var over = Context(recent: recent, headline: new string('h', ContextFit.MaxBytes - bare + 1));
        var trimmed = ContextFit.ForPost(over);
        Assert.True(trimmed.Trimmed);
        Assert.InRange(trimmed.Bytes, 1, ContextFit.MaxBytes);
        Assert.Equal(Bytes(trimmed.Body!), trimmed.Bytes);
        Assert.Equal(ContextFit.MaxBytes + 1, trimmed.ContextBytes);
    }

    [Fact]
    public void LongRecentLinesAreCutToWhatAReaderKeepsAndNothingElseMoves()
    {
        // The busy node with twenty journal rows each a 64 KB line: 1.3 MB as built.
        var golden = JsonNode.Parse(Golden())!.AsObject();
        var rows = Enumerable.Range(0, 20).Select(i => $"Show: {i:00} " + new string((char)('a' + i), 64 * 1024)).ToList();
        golden["recent"] = new JsonArray([.. rows.Select((t, i) => (JsonNode)new JsonObject { ["at"] = 1_760_000_000_000L + i, ["text"] = t })]);
        var json = golden.ToJsonString();
        Assert.True(Bytes(json) > 20 * 64 * 1024);

        var post = ContextFit.ForPost(json);
        Assert.NotNull(post.Body);
        Assert.InRange(post.Bytes, 1, ContextFit.MaxBytes);
        Assert.Equal((false, 20, 0, "cut short: 20 recent"), (post.LeftOut.Any, post.RecentCut, post.StatesCut, post.What));
        Assert.True(post.Trimmed && post.OnlyRecentCut);

        var given = Arrays(json);
        var sent = Arrays(post.Body);
        // Every row kept, cut to the 200 characters a reader keeps and ended with "…"; every item, link and command as built.
        Assert.Equal(rows.Select(r => r[..199] + "…"), sent["recent"].Select(r => Field(r, "text")));
        Assert.Equal(given["items"], sent["items"]);
        Assert.Equal(given["links"], sent["links"]);
        Assert.Equal(given["commands"], sent["commands"]);
        Assert.Equal(199, sent["commands"].Count);
        Assert.DoesNotContain(sent["items"], i => Field(i, "id") == ContextFit.TrimmedId);
    }

    /// <summary>A context with every kind of element in mixed order: twenty recent lines, grey and green links, the catalogue's commands
    /// (panic, air and transport first), and items of every light.</summary>
    private static string Mixed()
    {
        static Light LightOf(int i) => (i % 10) switch
        {
            < 4 => Light.Grey,
            < 8 => Light.Green,
            8 => Light.Amber,
            _ => Light.Red,
        };
        var items = Enumerable.Range(0, 100).Select(i => new ContextItem("thing:" + i, "output", "Thing " + i, LightOf(i),
            $"state of thing {i}: " + new string('s', 80))).ToList();
        var links = Enumerable.Range(0, 60).Select(i => new ContextLink("thing:" + i, "thing:" + (i + 1), "feeds", i % 2 == 0 ? Light.Grey : Light.Green)).ToList();
        var recent = Enumerable.Range(0, 20).Select(i => new ContextRecent(1_760_000_000_000 + i, $"Show: row {i} " + new string('r', 150))).ToList();
        return Context(items, links, recent, ShowContext.CommandsFromCatalog());
    }

    [Fact]
    public void OverTheBytesTheContextIsTrimmedInSection6sOrderOnlyUntilItFits()
    {
        var json = Mixed();
        var given = Arrays(json);
        var whole = Bytes(json);
        var fixedBytes = whole - ArraysBytes(given);
        var air = CatalogueCommands().Air.Select(c => c.Verb).ToHashSet(StringComparer.Ordinal);
        string LightOf(string element) => Field(element, "light");
        bool IsAir(string command) => air.Contains(Field(command, "verb"));

        // Just over: only the oldest recent line or two go, and nothing else moves.
        var just = ContextFit.ForPost(json, whole - 50);
        Assert.InRange(just.LeftOut.Recent, 1, 2);
        Assert.Equal((0, 0, 0), (just.LeftOut.Links, just.LeftOut.Commands, just.LeftOut.Items));
        Assert.Equal(given["recent"].Skip(just.LeftOut.Recent), Arrays(just.Body!)["recent"]);

        for (var max = whole - 1; max > 1500; max -= 613)
        {
            var post = ContextFit.ForPost(json, max);
            Assert.NotNull(post.Body);
            Assert.InRange(post.Bytes, 1, max);
            Assert.Equal(Bytes(post.Body), post.Bytes);
            var sent = Arrays(post.Body);
            var items = sent["items"];
            var trimmed = items.Where(i => Field(i, "id") == ContextFit.TrimmedId).ToList();
            var keptItems = items.Where(i => Field(i, "id") != ContextFit.TrimmedId).ToList();

            // The one grey item says what was left out, last among the items, and only when something was.
            Assert.Equal(post.LeftOut.Any ? 1 : 0, trimmed.Count);
            if (post.LeftOut.Any)
            {
                Assert.Equal(trimmed[0], items[^1]);
                Assert.Equal(("health", "Context", "grey", post.LeftOut.Words),
                    (Field(trimmed[0], "kind"), Field(trimmed[0], "label"), Field(trimmed[0], "light"), Field(trimmed[0], "state")));
            }
            Assert.Equal(given["recent"].Count - sent["recent"].Count, post.LeftOut.Recent);
            Assert.Equal(given["links"].Count - sent["links"].Count, post.LeftOut.Links);
            Assert.Equal(given["commands"].Count - sent["commands"].Count, post.LeftOut.Commands);
            Assert.Equal(given["items"].Count - keptItems.Count, post.LeftOut.Items);

            // What is kept is what was given, in its order: the newest recent lines, and a subsequence of each other array; an item's
            // state is cut only on an amber or a red one, to 40 characters ending "…".
            Assert.Equal(given["recent"].Skip(post.LeftOut.Recent), sent["recent"]);
            Assert.True(IsSubsequence(given["links"], sent["links"]), $"links at {max}");
            Assert.True(IsSubsequence(given["commands"], sent["commands"]), $"commands at {max}");
            var keptIds = keptItems.Select(i => Field(i, "id")).ToList();
            Assert.True(IsSubsequence(given["items"].Select(i => Field(i, "id")).ToList(), keptIds), $"items at {max}");
            var cut = keptItems.Where(i => !given["items"].Contains(i)).ToList();
            Assert.Equal(post.StatesCut, cut.Count);
            Assert.All(cut, i => Assert.True(LightOf(i) is "amber" or "red" && Field(i, "state").Length == ContextFit.LastResortChars && Field(i, "state").EndsWith('…')));

            // §6's order: each step only once every earlier one is spent.
            var leftLinks = given["links"].Except(sent["links"]).ToList();
            var leftCommands = given["commands"].Except(sent["commands"]).ToList();
            var leftItems = given["items"].Where(i => !keptIds.Contains(Field(i, "id"))).ToList();
            if (leftLinks.Count > 0)
                Assert.Empty(sent["recent"]);
            if (leftLinks.Any(l => LightOf(l) != "grey"))
                Assert.DoesNotContain(sent["links"], l => LightOf(l) == "grey");
            if (leftCommands.Count > 0)
                Assert.Empty(sent["links"]);
            if (leftItems.Count > 0)
                Assert.All(sent["commands"], c => Assert.True(IsAir(c), $"{Field(c, "verb")} kept at {max} while items went"));
            if (leftItems.Any(i => LightOf(i) != "grey"))
                Assert.DoesNotContain(keptItems, i => LightOf(i) == "grey");
            if (cut.Count > 0 || leftItems.Any(i => LightOf(i) is "amber" or "red"))
                Assert.DoesNotContain(keptItems, i => LightOf(i) is "grey" or "green");
            if (cut.Any(i => LightOf(i) == "red"))
                Assert.DoesNotContain(keptItems, i => LightOf(i) == "amber" && Field(i, "state").Length > ContextFit.LastResortChars);
            if (leftItems.Any(i => LightOf(i) == "red"))
                Assert.DoesNotContain(keptItems, i => LightOf(i) == "amber");
            if (leftCommands.Any(IsAir))
                Assert.Empty(keptItems);
            if (leftCommands.Any(IsAir))
                Assert.DoesNotContain(sent["commands"], c => !IsAir(c));

            // Only until it fits: what it keeps is within the bytes the item leaves, and the last thing the last step it took did, undone,
            // would be over them again (the context's own fields and every array's elements are the whole body).
            Assert.Equal(post.Bytes, fixedBytes + ArraysBytes(sent));
            var budget = Budget(given, fixedBytes, max);
            var kept = ArrayBytes(sent["recent"]) + ArrayBytes(sent["links"]) + ArrayBytes(sent["commands"]) + ArrayBytes(keptItems);
            Assert.True(kept <= budget, $"{kept} kept at {max}, over {budget}");
            if (UndoneLast(given, sent, keptItems, cut, IsAir, LightOf) is { } undone)
                Assert.True(kept + undone > budget, $"at {max}: {kept} kept and {undone} more, the last step's last, would be within {budget}");

            // Its own fields are the given ones.
            using var a = JsonDocument.Parse(json);
            using var b = JsonDocument.Parse(post.Body);
            foreach (var name in new[] { "v", "node", "at", "light", "headline", "peers" })
                Assert.Equal(a.RootElement.GetProperty(name).GetRawText(), b.RootElement.GetProperty(name).GetRawText());
            Assert.Equal(a.RootElement.EnumerateObject().Select(p => p.Name), b.RootElement.EnumerateObject().Select(p => p.Name));
        }
    }

    /// <summary>The bytes the last change of the last §6 step a fit took would add back if undone: the last element it left out, and the
    /// comma beside it when its array still holds one, or the last state it cut, put back whole. Null when that step left out an amber
    /// or a red item, whose bytes when it went were those of its state cut short, which only the fit wrote.</summary>
    private static long? UndoneLast(Dictionary<string, List<string>> given, Dictionary<string, List<string>> sent, List<string> keptItems,
        List<string> cut, Func<string, bool> isAir, Func<string, string> lightOf)
    {
        var keptIds = keptItems.Select(i => Field(i, "id")).ToHashSet(StringComparer.Ordinal);
        // A step that leaves out from the end of its list left out last the first of its kind it did not keep.
        long? FromTheEnd(List<string> whole, List<string> kept, Func<string, bool> kind, Func<string, bool> isKept)
        {
            var last = whole.FirstOrDefault(e => kind(e) && !isKept(e));
            return last is null ? null : Bytes(last) + (kept.Count > 0 ? 1 : 0);
        }
        bool CommandKept(string c) => sent["commands"].Contains(c);
        bool LinkKept(string l) => sent["links"].Contains(l);
        bool ItemKept(string i) => keptIds.Contains(Field(i, "id"));
        if (given["commands"].Any(c => isAir(c) && !CommandKept(c)))
            return FromTheEnd(given["commands"], sent["commands"], isAir, CommandKept);
        if (given["items"].Any(i => lightOf(i) is "amber" or "red" && !ItemKept(i)))
            return null;
        foreach (var light in new[] { "red", "amber" })
        {
            // The cuts run from the first item of the light on: the last it made is the last of them.
            if (cut.LastOrDefault(i => lightOf(i) == light) is { } last)
                return Bytes(given["items"].Single(i => Field(i, "id") == Field(last, "id"))) - Bytes(last);
        }
        foreach (var light in new[] { "green", "grey" })
        {
            if (FromTheEnd(given["items"], keptItems, i => lightOf(i) == light, ItemKept) is { } item)
                return item;
        }
        if (FromTheEnd(given["commands"], sent["commands"], c => !isAir(c), CommandKept) is { } command)
            return command;
        if (FromTheEnd(given["links"], sent["links"], l => lightOf(l) != "grey", LinkKept) is { } link)
            return link;
        if (FromTheEnd(given["links"], sent["links"], l => lightOf(l) == "grey", LinkKept) is { } grey)
            return grey;
        // The oldest recent lines go first: the last to go is the newest of them.
        var gone = given["recent"].Count - sent["recent"].Count;
        return gone == 0 ? null : Bytes(given["recent"][gone - 1]) + (sent["recent"].Count > 0 ? 1 : 0);
    }

    /// <summary>Only until it fits, at each step of §6's order: a context whose earlier steps have nothing to take, over what the bytes
    /// leave its arrays and the trimmed item by a byte and more, loses exactly the fewest elements of that step, in the step's order, that
    /// bring it within (or cuts exactly the fewest states), and nothing of any other step.</summary>
    [Fact]
    public void EachStepOfSection6sOrderTakesOnlyWhatBringsTheContextWithin()
    {
        var (air, others) = CatalogueCommands();
        static List<ContextItem> Things(string prefix, int n, Func<int, Light> light, int state) =>
            [.. Enumerable.Range(0, n).Select(i => new ContextItem($"{prefix}:{i}", "output", $"Thing {i}", light(i), $"{prefix} {i}: " + new string('s', state)))];
        Light[] lights = [Light.Grey, Light.Green, Light.Amber, Light.Red];
        var mixedItems = Things("thing", 24, i => lights[i % 4], 60);
        var links = Enumerable.Range(0, 30).Select(i => new ContextLink("thing:" + i, "thing:" + (i + 1), "feeds", i % 3 == 0 ? Light.Green : Light.Grey)).ToList();
        var recent = Enumerable.Range(0, 20).Select(i => new ContextRecent(1_760_000_000_000 + i, $"Show: row {i} " + new string('r', 120))).ToList();
        List<string> Reversed(IEnumerable<string> e) => [.. e.Reverse()];

        // Each step: its context, the array it takes from, and the order it takes in.
        var steps = new (string Step, string Json, string Array, Func<Dictionary<string, List<string>>, List<string>> Order)[]
        {
            ("the oldest recent lines", Context(mixedItems, links, recent, [.. air, .. others]), "recent", g => g["recent"]),
            ("grey links", Context(mixedItems, links, commands: [.. air, .. others]), "links", g => Reversed(g["links"].Where(l => Field(l, "light") == "grey"))),
            ("the other links", Context(mixedItems, links.Where(l => l.Light == Light.Green), commands: [.. air, .. others]), "links", g => Reversed(g["links"])),
            ("commands not panic, air or transport", Context(mixedItems, commands: [.. air, .. others]), "commands",
                g => Reversed(g["commands"].Where(c => !air.Any(a => a.Verb == Field(c, "verb"))))),
            ("grey items", Context(mixedItems, commands: air), "items", g => Reversed(g["items"].Where(i => Field(i, "light") == "grey"))),
            ("green items", Context(mixedItems.Where(i => i.Light != Light.Grey), commands: air), "items", g => Reversed(g["items"].Where(i => Field(i, "light") == "green"))),
            ("amber items", Context(Things("bad", 24, i => i % 2 == 0 ? Light.Amber : Light.Red, 20), commands: air), "items",
                g => Reversed(g["items"].Where(i => Field(i, "light") == "amber"))),
            ("red items", Context(Things("bad", 16, _ => Light.Red, 20), commands: air), "items", g => Reversed(g["items"])),
            ("panic, air and transport commands", Context(commands: air), "commands", g => Reversed(g["commands"])),
        };
        foreach (var (step, json, array, order) in steps)
        {
            var given = Arrays(json);
            var whole = Bytes(json);
            var fixedBytes = whole - ArraysBytes(given);
            foreach (var over in new[] { 0, 1, 57, 301 })
            {
                var max = whole - 1 - over;
                var post = ContextFit.ForPost(json, max);
                Assert.NotNull(post.Body);
                var needed = ArraysBytes(given) - Budget(given, fixedBytes, max);
                // The fewest of the step's elements, in its order, whose bytes and commas bring the arrays within.
                var taken = order(given);
                var expected = 0;
                for (long freed = 0; freed < needed; expected++)
                {
                    Assert.True(expected < taken.Count, $"{step}: the test's context has too little to take at {max}");
                    freed += Bytes(taken[expected]) + (given[array].Count - expected > 1 ? 1 : 0);
                }
                var left = post.LeftOut;
                var counts = new Dictionary<string, int> { ["recent"] = left.Recent, ["links"] = left.Links, ["commands"] = left.Commands, ["items"] = left.Items };
                Assert.True(counts[array] == expected, $"{step}, {over} over: {counts[array]} left out where {expected} bring it within");
                Assert.All(counts.Where(c => c.Key != array), c => Assert.Equal(0, c.Value));
                Assert.Equal((0, 0), (post.RecentCut, post.StatesCut));
                var sent = Arrays(post.Body)[array].Where(e => !e.Contains(ContextFit.TrimmedId, StringComparison.Ordinal)).ToList();
                Assert.Equal(given[array].Except(taken.Take(expected)), sent);
            }
        }

        // The cuts: the fewest states, from the first of the light, ambers before reds.
        foreach (var (step, light, items) in new[]
        {
            ("amber states", "amber", Things("bad", 24, i => i % 2 == 0 ? Light.Amber : Light.Red, 140)),
            ("red states", "red", Things("bad", 16, _ => Light.Red, 140)),
        })
        {
            var json = Context(items, commands: air);
            var given = Arrays(json);
            var whole = Bytes(json);
            var fixedBytes = whole - ArraysBytes(given);
            foreach (var over in new[] { 0, 1, 57, 301 })
            {
                var max = whole - 1 - over;
                var post = ContextFit.ForPost(json, max);
                Assert.NotNull(post.Body);
                Assert.False(post.LeftOut.Any, step);
                var needed = ArraysBytes(given) - Budget(given, fixedBytes, max);
                var sent = Arrays(post.Body)["items"];
                var ofLight = given["items"].Select((g, i) => (Given: g, Sent: sent[i])).Where(p => Field(p.Given, "light") == light).ToList();
                var cuts = ofLight.TakeWhile(p => p.Sent != p.Given).Select(p => (long)(Bytes(p.Given) - Bytes(p.Sent))).ToList();
                Assert.Equal(post.StatesCut, cuts.Count);
                Assert.All(ofLight.Skip(cuts.Count), p => Assert.Equal(p.Given, p.Sent));
                Assert.All(given["items"].Where(g => Field(g, "light") != light), g => Assert.Contains(g, sent));
                Assert.True(cuts.Sum() >= needed && cuts.SkipLast(1).Sum() < needed, $"{step}, {over} over: {cuts.Count} cut for {needed} bytes");
            }
        }
    }

    [Fact]
    public void TheLastResortCutsAmberStatesThenRedThenLeavesThemOutAndThePanicAirAndTransportCommandsLast()
    {
        var items = Enumerable.Range(0, 30).Select(i => new ContextItem("bad:" + i, "output", "Bad " + i, i % 2 == 0 ? Light.Amber : Light.Red,
            $"{(i % 2 == 0 ? "amber" : "red")} {i}: " + new string('w', 140))).ToList();
        var json = Context(items, commands: CatalogueCommands().Air);
        var whole = Bytes(json);

        // A little over: only amber states are cut, from the first, and nothing is left out.
        var little = ContextFit.ForPost(json, whole - 400);
        Assert.False(little.LeftOut.Any);
        var sent = Arrays(little.Body!)["items"];
        var cut = sent.Where(i => Field(i, "state").Length == ContextFit.LastResortChars).ToList();
        Assert.Equal(little.StatesCut, cut.Count);
        Assert.NotEmpty(cut);
        Assert.All(cut, i => Assert.Equal("amber", Field(i, "light")));
        Assert.StartsWith("amber 0: www", Field(sent[0], "state"), StringComparison.Ordinal);
        Assert.EndsWith("…", Field(sent[0], "state"), StringComparison.Ordinal);
        var given = Arrays(json)["items"];
        Assert.All(sent.Where(i => Field(i, "light") == "red"), i => Assert.Contains(i, given));

        // Every state cut and still over: ambers go before reds.
        var everyCut = Bytes(Context(items.Select(i => i with { State = ContextFit.Cut(i.State, ContextFit.LastResortChars) }), commands: CatalogueCommands().Air));
        var lessAmber = ContextFit.ForPost(json, everyCut - 300);
        Assert.Equal(30 - lessAmber.LeftOut.Items, lessAmber.StatesCut);
        Assert.True(lessAmber.LeftOut.Items > 0);
        Assert.Equal(0, lessAmber.LeftOut.Commands);
        var kept = Arrays(lessAmber.Body!)["items"].Where(i => Field(i, "id") != ContextFit.TrimmedId).ToList();
        Assert.Equal(15, kept.Count(i => Field(i, "light") == "red"));
        Assert.Equal(15 - lessAmber.LeftOut.Items, kept.Count(i => Field(i, "light") == "amber"));

        // Down to the fields of its own: every item out, then the panic, air and transport commands from the end.
        var tight = ContextFit.ForPost(json, Bytes(Context(commands: CatalogueCommands().Air.Take(3))) + 150);
        var commands = Arrays(tight.Body!)["commands"];
        Assert.Equal(CatalogueCommands().Air.Take(commands.Count).Select(c => c.Verb), commands.Select(c => Field(c, "verb")));
        Assert.Equal(30, tight.LeftOut.Items);
        Assert.Equal(ContextFit.TrimmedId, Field(Assert.Single(Arrays(tight.Body!)["items"]), "id"));
    }

    [Fact]
    public void NothingIsPostedWhenTheContextsOwnFieldsAreOver()
    {
        // The headline, the node and its peers are never trimmed.
        var json = Context(recent: [new ContextRecent(1, "Show: one")], headline: new string('h', 70_000));
        var post = ContextFit.ForPost(json);
        Assert.Null(post.Body);
        Assert.False(post.Unreadable);
        Assert.Equal(1, post.LeftOut.Recent);
        Assert.True(post.Bytes > 70_000);
    }

    [Fact]
    public void AContextSection6DoesNotReadIsPostedOnlyWithinTheBytes()
    {
        foreach (var odd in new[] { new string('x', 70_000), "[" + new string('1', 70_000) + "]",
                     "{\"items\":\"none\",\"links\":[],\"recent\":[],\"commands\":[],\"h\":\"" + new string('h', 70_000) + "\"}",
                     "{\"links\":[],\"recent\":[],\"commands\":[],\"h\":\"" + new string('h', 70_000) + "\"}" })
        {
            var post = ContextFit.ForPost(odd);
            Assert.Null(post.Body);
            Assert.True(post.Unreadable);
            Assert.Equal(Bytes(odd), post.Bytes);
        }
        Assert.Equal("not json", ContextFit.ForPost("not json").Body);
    }

    [Fact]
    public void ACutNeverSplitsASurrogatePair()
    {
        var face = char.ConvertFromUtf32(0x1F600);
        Assert.Equal("ab…", ContextFit.Cut("ab" + face, 3));
        Assert.Equal("ab…", ContextFit.Cut("ab" + face + "c", 4));
        Assert.Equal("abc", ContextFit.Cut("abc", 3));
    }

    private static bool IsSubsequence(List<string> whole, List<string> part)
    {
        var at = 0;
        foreach (var p in part)
        {
            while (at < whole.Count && whole[at] != p)
                at++;
            if (at == whole.Count)
                return false;
            at++;
        }
        return true;
    }
}
