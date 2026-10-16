using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.VisualTree;
using PlayMap.App.Controls;
using PlayMap.App.State;
using PlayMap.App.ViewModels.Cards;
using PlayMap.App.Views.Cards;

namespace PlayMap.App.Tests;

/// <summary>The generic STATE cards: every descriptor a package wrote is built into the desk and renders from its own
/// fixture (each scenario of a fixture of several nodes) with its light and words; every action resolves to a catalogue line
/// and sends it; each package's fixture yields a card; the cards show under Health and in the inspector of the object they are
/// about; main.json's solve, cameras and the assistant have cards, and the fake engine answers the assistant's verbs as the
/// engine does.</summary>
public class CardTests(ITestOutputHelper output)
{
    private static JsonElement Root(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    /// <summary>A package's fixture (its first scenario, or the sections given) with main's laid under it (a card may read a root
    /// key main carries).</summary>
    internal static JsonElement FixtureRoot(string package, JsonObject? sections = null)
    {
        var main = JsonNode.Parse(StateFixtures.Read("main"))!.AsObject();
        if (package != "main")
        {
            foreach (var (k, v) in sections ?? StateFixtures.Scenarios(package)[0].Sections)
                main[k] = v?.DeepClone();
        }
        return Root(main.ToJsonString());
    }

    /// <summary>The list a per-element path names ("align.devices[]" → align.devices), or null when STATE has none there.</summary>
    private static JsonElement? ListAt(JsonElement root, string path)
    {
        var node = root;
        foreach (var part in path.TrimEnd('[', ']').Split('.'))
        {
            if (node.ValueKind != JsonValueKind.Object || !node.TryGetProperty(part, out node))
                return null;
        }
        return node;
    }

    [Fact]
    public void EveryDescriptorFilePresentIsBuiltIntoTheDesk()
    {
        var files = StateFixtures.Cards;
        Assert.NotEmpty(files);
        var builtIn = CardDescriptor.BuiltInPackages.ToHashSet(StringComparer.Ordinal);
        Assert.Contains("desk", builtIn);
        foreach (var (package, json) in files)
        {
            Assert.Contains(package, builtIn);
            using var doc = JsonDocument.Parse(json);
            var withPath = doc.RootElement.EnumerateArray().Count(e => e.TryGetProperty("path", out _));
            Assert.Equal(withPath, CardDescriptor.BuiltIn.Count(d => d.Package == package));
        }
    }

    [Fact]
    public void EveryDescriptorRendersFromItsFixtureWithItsLightAndWordsAndEveryActionIsACatalogueLine()
    {
        var lights = new HashSet<string>(StringComparer.Ordinal) { "green", "amber", "red", "grey" };
        foreach (var package in CardDescriptor.BuiltInPackages)
        {
            var descriptors = CardDescriptor.BuiltIn.Where(d => d.Package == package).ToList();
            if (descriptors.Count == 0)
                continue;
            var scenarios = StateFixtures.Scenarios(package == "desk" ? "main" : package);
            var shown = descriptors.ToDictionary(d => d.Index, _ => 0);
            foreach (var (scenario, sections) in scenarios)
            {
                var root = FixtureRoot(package == "desk" ? "main" : package, sections);
                foreach (var d in descriptors)
                {
                    var cards = CardReader.Read(d, root).ToList();
                    output.WriteLine($"{package}/{scenario}#{d.Index} {d.Title} ({d.Path}): {cards.Count} card(s)");
                    foreach (var c in cards)
                    {
                        output.WriteLine($"   [{c.Light}] {c.Title} — {c.Words} | {string.Join("; ", c.Fields.Select(f => f.Label + "=" + f.Value))} | entity {c.EntityId}");
                        foreach (var a in c.Actions)
                            output.WriteLine($"      {a.Label}: {a.Line ?? "(" + a.Missing + ")"}");
                        Assert.Contains(c.Light, lights);
                        // Every field the descriptor names is on the card (six on its face, the rest under "more").
                        Assert.Equal(d.Fields.Length, c.Fields.Length);
                        // Words a descriptor gives render whole: no template left unfilled.
                        if (d.Words.Length > 0)
                            Assert.DoesNotContain("{", c.Words, StringComparison.Ordinal);
                        // An action resolves to a catalogue line; one that names an element's field of a list block left empty here
                        // ("FLEET FOCUS $(node)" with no backups) stays greyed with the words why.
                        var emptyList = ListAt(root, d.Path) is { ValueKind: JsonValueKind.Array } block && block.GetArrayLength() == 0;
                        foreach (var a in c.Actions)
                        {
                            Assert.True(a.Line is not null || (emptyList && a.Missing is not null), $"{package} {d.Title}: '{a.Label}' does not resolve: {a.Missing}");
                            if (a.Line is not null)
                                Assert.Null(DeskVerbRuleTests.Fault(a.Line));
                        }
                    }
                    shown[d.Index] += cards.Count;
                    // A card per element shows none for an empty list: its fixture has none of them.
                    if (cards.Count == 0 && d.Path.EndsWith("[]", StringComparison.Ordinal))
                        Assert.True(ListAt(root, d.Path) is not { ValueKind: JsonValueKind.Array } list || list.GetArrayLength() == 0, $"{package} {d.Title}");
                }
            }
            // Each package's fixture yields a card, and every descriptor shows in some scenario of it unless it is a card per element
            // of a list that fixture leaves empty (main's cameras: none are open; align's guarded devices: none are linked).
            Assert.True(shown.Values.Sum() > 0, package + " yields no card");
            foreach (var d in descriptors.Where(d => shown[d.Index] == 0))
                Assert.True(d.Path.EndsWith("[]", StringComparison.Ordinal), $"{package} {d.Title} ({d.Path}) shows no card from its fixture");
        }
    }

    /// <summary>The fields whose values name a thing on one node (an id, a node, a leader, a group, a session): a word an action line
    /// carries that only a fixture holds is one of these.</summary>
    private static readonly HashSet<string> IdentityKeys = new(StringComparer.Ordinal) { "id", "node", "leader", "group", "groups", "session", "of", "name" };

    private static void IdentityValues(JsonNode? node, string? key, HashSet<string> into)
    {
        switch (node)
        {
            case JsonObject o:
                foreach (var (k, v) in o)
                    IdentityValues(v, k, into);
                break;
            case JsonArray a:
                foreach (var v in a)
                    IdentityValues(v, key, into);
                break;
            case JsonValue v when key is not null && IdentityKeys.Contains(key) && v.TryGetValue<string>(out var text) && text.Length > 0:
                into.Add(text);
                break;
        }
    }

    /// <summary>The literal words of a row's grammar (its keywords and alternatives, not the names of its slots).</summary>
    private static HashSet<string> Keywords(PlayMap.Core.Wire.VerbSpec spec) =>
        [.. System.Text.RegularExpressions.Regex.Replace(spec.Syntax, "<[^>]*>", " ")
            .Split([' ', '|', '[', ']', '(', ')', ','], StringSplitOptions.RemoveEmptyEntries)];

    /// <summary>A card's actions read apart (two lines that differ after a placeholder — set and clear — never share a label), and
    /// no action line carries a value only its package's fixture holds: a group, a node, a leader or an id comes from the card's
    /// STATE through a placeholder, so a button on a real node never acts on the fixture's; nor a slot of its row's syntax left for
    /// the operator to fill, which a button cannot ask for.</summary>
    [Fact]
    public void ACardsActionsReadApartAndTakeTheirValuesFromStateNotFromAFixture()
    {
        var problems = new List<string>();
        foreach (var d in CardDescriptor.BuiltIn)
        {
            var labels = d.Actions.Select(a => a.Label).ToList();
            foreach (var twice in labels.GroupBy(l => l, StringComparer.Ordinal).Where(g => g.Count() > 1))
                problems.Add($"{d.Package} '{d.Title}': two actions read '{twice.Key}'");
            var values = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (_, sections) in StateFixtures.Scenarios(d.Package == "desk" ? "main" : d.Package))
                IdentityValues(sections, null, values);
            foreach (var a in d.Actions)
            {
                var bare = CardReader.WithoutPlaceholders(a.Line);
                Assert.True(PlayMap.Core.Wire.WireGrammar.TryParse(a.Line.Replace("$(", "x", StringComparison.Ordinal).Replace(")", "", StringComparison.Ordinal)
                    .Replace("{", "x", StringComparison.Ordinal).Replace("}", "", StringComparison.Ordinal), out var line, out _), a.Line);
                var spec = PlayMap.Core.Wire.VerbCatalog.Match(line!)?.Spec;
                Assert.True(spec is not null, a.Line);
                var keywords = Keywords(spec);
                foreach (var word in bare.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => values.Contains(w) && !keywords.Contains(w)))
                    problems.Add($"{d.Package} '{d.Title}': '{a.Line}' carries '{word}', a value of its fixture");
                // A word written as its row's slot ("<scan>") is a value only the operator gives: a button would send it as it is.
                foreach (var slot in bare.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => w.Length > 2 && w[0] == '<' && w[^1] == '>'))
                    problems.Add($"{d.Package} '{d.Title}': '{a.Line}' leaves {slot} for the operator to give");
            }
        }
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    [Fact]
    public void TheCardsReadTheirLightsAndWordsAsTheDescriptorsSay()
    {
        var show = FixtureRoot("show");
        var cards = CardReader.ReadAll(CardDescriptor.BuiltIn.Where(d => d.Package == "show"), show);
        // show.json: armed by Desk with a panic up — the panic rule comes first.
        var showCard = cards.Single(c => c.Descriptor.Path == "show");
        Assert.Equal("red", showCard.Light);
        Assert.Equal("ARMED by Desk · MinGo 300 ms", showCard.Words);
        var panic = cards.Single(c => c.Descriptor.Path == "show.panic");
        Assert.Equal("red", panic.Light);
        Assert.Equal("PANIC black by Desk", panic.Words);
        Assert.Equal("3 locks", cards.Single(c => c.Descriptor.Path == "show.locks").Words);
        var preflight = cards.Single(c => c.Descriptor.Path == "show.preflight");
        var run = show.GetProperty("show").GetProperty("preflight");
        Assert.Equal(run.GetProperty("light").GetString(), preflight.Light);
        Assert.Equal(string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"{run.GetProperty("green").GetInt32()} green · {run.GetProperty("amber").GetInt32()} amber · {run.GetProperty("red").GetInt32()} red · show-ready {run.GetProperty("ready").GetString()}"),
            preflight.Words);

        // A per-element card per output; its action names that output.
        var outputs = CardReader.ReadAll(CardDescriptor.BuiltIn.Where(d => d.Package == "output"), FixtureRoot("output"));
        foreach (var c in outputs.Where(c => c.Descriptor.Path == "outputs[].present"))
        {
            Assert.NotNull(c.EntityId);
            Assert.Equal("OUTPUT STATS " + c.EntityId, c.Actions[0].Line);
        }

        // Each dialect, against its own fixture's values.
        using (var decodeDoc = JsonDocument.Parse(StateFixtures.Read("decode")))
        {
            var decode = CardReader.ReadAll(CardDescriptor.BuiltIn.Where(d => d.Package == "decode"), FixtureRoot("decode"));
            var platform = decodeDoc.RootElement.GetProperty("decode").GetProperty("platform");
            var light = platform.GetProperty("light").GetString();
            Assert.Equal(light, decode.Single(c => c.Descriptor.Path == "decode.platform").Light);   // a light given as a path
            Assert.Equal(platform.GetProperty("words").GetString(), decode.Single(c => c.Descriptor.Path == "decode.platform").Words);   // words as a path
            var source = decode.Single(c => c.Descriptor.Path == "decode.sources[]");
            var first = decodeDoc.RootElement.GetProperty("decode").GetProperty("sources")[0];
            Assert.Equal(first.GetProperty("media").GetString(), source.EntityId);   // "key": the entity is the media item
            Assert.Equal("MEDIA INSPECT " + first.GetProperty("media").GetString(), source.Actions[0].Line);
        }
        using (var controlDoc = JsonDocument.Parse(StateFixtures.Read("control")))
        {
            var control = CardReader.ReadAll(CardDescriptor.BuiltIn.Where(d => d.Package == "control"), FixtureRoot("control"));
            var surfaces = controlDoc.RootElement.GetProperty("control").GetProperty("surfaces").EnumerateArray().ToList();
            var surfaceCards = control.Where(c => c.Descriptor.Path == "control.surfaces[]").ToList();
            Assert.Equal(surfaces.Count, surfaceCards.Count);
            for (var i = 0; i < surfaceCards.Count; i++)
            {
                Assert.Equal(surfaces[i].GetProperty("name").GetString(), surfaceCards[i].Words);   // words as an element field
                Assert.Equal(surfaces[i].GetProperty("light").GetString(), surfaceCards[i].Light);   // a value list over the element
                Assert.Equal("Control surface · " + surfaces[i].GetProperty("name").GetString(), surfaceCards[i].Title);
            }
            var chase = control.Single(c => c.Descriptor.Path == "timecode.chases[]");
            Assert.Equal("TIMECODE ENGAGE " + controlDoc.RootElement.GetProperty("timecode").GetProperty("chases")[0].GetProperty("playlist").GetString() + " ON", chase.Actions[0].Line);
        }
        var companion = CardReader.ReadAll(CardDescriptor.BuiltIn.Where(d => d.Package == "companion"), FixtureRoot("companion"));
        var clients = companion.Where(c => c.Descriptor.Path == "clients[]").ToList();
        Assert.Equal(["green", "amber"], clients.Select(c => c.Light).ToArray());   // if-rules per element ("paired")
        Assert.StartsWith("Controllers · ", clients[0].Title, StringComparison.Ordinal);   // "each" with a "label" field

        // A light from a value list over every element is the worst of them.
        var d = CardDescriptor.ParseFile("t", """[{"title":"T","path":"x","light":{"path":"x.items[].state","red":["bad"],"green":["ok"]},"words":"{n?{n} items:none}","fields":[{"label":"N","path":"n"}],"actions":["GET $(id)"]}]""")[0];
        var two = CardReader.Read(d, Root("""{"x":{"id":"op_1","n":2,"items":[{"state":"ok"},{"state":"bad"}]}}""")).Single();
        Assert.Equal("red", two.Light);
        Assert.Equal("2 items", two.Words);
        Assert.Equal("GET op_1", two.Actions[0].Line);
        var none = CardReader.Read(d, Root("""{"x":{"n":0,"items":[{"state":"ok"}]}}""")).Single();
        Assert.Equal("green", none.Light);
        Assert.Equal("none", none.Words);
        Assert.Null(none.Actions[0].Line);
        Assert.Empty(CardReader.Read(d, Root("""{"x":null}""")));
        Assert.Empty(CardReader.Read(d, Root("""{"y":{}}""")));
    }

    /// <summary>The descriptors new to the desk read their fixtures as their packages mean them: the hot backup's (a light from a state
    /// with its own words per light, a fixed light, one fixture per node), facade's and align's blocks owned by a list element (their
    /// actions name the element, their cards sit in its inspector), the facade guide's next step and a stack, multiscan's lines naming the
    /// scan they sit in, the decode path's words and fields past the sixth, the frame lock's words as lights, videoio's per-element
    /// cards.</summary>
    [Fact]
    public void TheNewPackagesCardsReadTheirFixturesAsTheyMeanThem()
    {
        IEnumerable<CardInstance> Read(string package, string? scenario = null)
        {
            var sections = scenario is null ? null : StateFixtures.Scenarios(package).Single(x => x.Name == scenario).Sections;
            return CardReader.ReadAll(CardDescriptor.BuiltIn.Where(d => d.Package == package), FixtureRoot(package, sections));
        }

        // backup: the mirror's light from its state ("identical" green; "lost" red; a state the descriptor names no light for reads
        // as the engine lights it: "joining" amber), a fixed light, and each scenario's own cards.
        var mirror = Read("backup", "backup").Single(c => c.Descriptor.Path == "fleet.mirror");
        Assert.Equal(("green", "same show (rev 4), clock, transports and air as FOH-A"), (mirror.Light, mirror.Words));
        Assert.Equal(["FAILOVER", "FLEET SOLO", "PREFLIGHT QUICK"], mirror.Actions.Select(a => a.Line ?? "").ToArray());
        foreach (var (state, light) in new[] { ("lost", "red"), ("joining", "amber"), ("identical", "green") })
        {
            var root = Root("""{"fleet":{"mirror":{"of":"0badc0de","name":"FOH-A","state":"STATE","rev":4,"behindRev":1,"words":"w"}}}""".Replace("STATE", state, StringComparison.Ordinal));
            Assert.Equal(light, CardReader.Read(mirror.Descriptor, root).Single().Light);
        }
        Assert.DoesNotContain(Read("backup", "primary"), c => c.Descriptor.Path == "fleet.mirror");
        var backups = Read("backup", "primary").Single(c => c.Descriptor.Path == "fleet.backups");
        Assert.Equal(("green", "1 hot backups", "FLEET FOCUS 9f8e7d6c"), (backups.Light, backups.Words, backups.Actions[0].Line));
        var last = Read("backup", "takenOver").Single(c => c.Descriptor.Path == "fleet.last");
        Assert.Equal("green", last.Light);
        Assert.Equal("grey", Read("backup", "fenced").Single(c => c.Descriptor.Path == "fleet.routes").Light);

        // facade: the uncertainty block is the first projector's — its actions name it and its card sits in its inspector.
        using (var doc = JsonDocument.Parse(StateFixtures.Read("facade")))
        {
            var stage = doc.RootElement.GetProperty("stage");
            var first = stage.GetProperty("projectors")[0];
            var pj = first.GetProperty("id").GetString();
            var uncertainty = Read("facade").Single(c => c.Descriptor.Path == "stage.projectors.0.uncertainty");
            Assert.Equal(("amber", pj), (uncertainty.Light, uncertainty.EntityId));
            Assert.Equal("PROJECTOR SOLVE " + pj, uncertainty.Actions[0].Line);
            var wallPx = Math.Round(first.GetProperty("uncertainty").GetProperty("wallPx").GetDouble(), 3)
                .ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
            Assert.StartsWith($"on the wall ±{wallPx} px", uncertainty.Words, StringComparison.Ordinal);

            // stacks: the guide's next step is amber while it names a line; a stack not yet aligned is amber, its card is the stack's
            // and its buttons are its list and its REMOVE (the scan it aligns from is the operator's to type).
            var next = stage.GetProperty("site").GetProperty("next");
            var guide = Read("facade").Single(c => c.Descriptor.Path == "stage.site.next");
            Assert.Equal(("amber", $"{next.GetProperty("step").GetString()}: {next.GetProperty("words").GetString()} — {next.GetProperty("line").GetString()}"),
                (guide.Light, guide.Words));
            Assert.Equal(["SITE GUIDE"], guide.Actions.Select(a => a.Line ?? "").ToArray());
            var sk = stage.GetProperty("stacks")[0];
            var stack = Read("facade").Single(c => c.Descriptor.Path == "stage.stacks.0");
            Assert.False(sk.GetProperty("aligned").GetBoolean());
            Assert.Equal(("amber", sk.GetProperty("id").GetString()), (stack.Light, stack.EntityId));
            Assert.StartsWith($"{sk.GetProperty("name").GetString()}: {sk.GetProperty("members").GetArrayLength()} on {sk.GetProperty("master").GetString()} · ",
                stack.Words, StringComparison.Ordinal);
            Assert.Equal(["STACK LIST", "REMOVE " + sk.GetProperty("id").GetString()], stack.Actions.Select(a => a.Line ?? "").ToArray());
        }
        // align: the measured correction is the first output's; a guarded device would have a card of its own.
        Assert.Equal("op1", Read("align").Single(c => c.Descriptor.Path == "outputs.0.measured").EntityId);
        var guard = CardDescriptor.BuiltIn.Single(d => d.Package == "align" && d.Path == "align.devices[]");
        var device = CardReader.Read(guard, Root("""{"align":{"devices":[{"id":"dv1","outputs":["op1"],"read":true,"geometry":true,"blend":false,"at":1,"light":"amber","words":"own geometry is on while PlayMap corrects op1","fix":"DEVICE RAW dv1 VXX:GMMI0=+00000"}]}}""")).Single();
        Assert.Equal(("amber", "own geometry is on while PlayMap corrects op1", "DEVICE NATIVE dv1", "dv1"), (device.Light, device.Words, device.Actions[0].Line, device.EntityId));

        // multiscan: a region's lines name the scan it sits in; a suggestion waits for APPROVE of its proposal.
        var region = Read("phonescan").Single(c => c.Descriptor.Path == "scan.regions.0");
        Assert.Equal("SCAN SURFACE sc_fixture1 REGION rg1", region.Actions[0].Line);
        Assert.Equal("APPROVE ap1", Read("phonescan").Single(c => c.Descriptor.Path == "scan.suggestions.0").Actions[0].Line);

        // decode: the platform's words name the GL binding's decode path; every field is on the card, the ones past six under "more".
        using (var doc = JsonDocument.Parse(StateFixtures.Read("decode")))
        {
            var platform = doc.RootElement.GetProperty("decode").GetProperty("platform");
            var card = Read("decode").Single(c => c.Descriptor.Path == "decode.platform");
            Assert.Equal(platform.GetProperty("words").GetString(), card.Words);
            Assert.Equal(platform.GetProperty("zeroCopy").GetString(), card.Fields.Single(f => f.Label == "Zero-copy").Value);
            Assert.Equal(platform.GetProperty("why").GetString(), card.Fields.Single(f => f.Label == "Why not").Value);
            Assert.True(card.Fields.ToList().FindIndex(f => f.Label == "Why not") >= CardDescriptor.MaxFields);
        }

        // output and d3d11: the frame lock's words light as the engine's context lights them; each output's latency and present path.
        Assert.All(Read("output").Where(c => c.Descriptor.Path is "outputs[].lock" or "sync.lock"), c => Assert.Equal("green", c.Light));
        var present = Read("output").First(c => c.Descriptor.Path == "outputs[].present");
        Assert.Equal("33 ms", present.Fields.Single(f => f.Label == "Latency").Value);
        Assert.Equal("offscreen", Read("output").Single(c => c.Descriptor.Path == "machine.present").Fields.Single(f => f.Label == "Path").Value);

        // videoio: a card per input and send, each lit by its own state ("connecting" is not measured yet: grey).
        var inputs = Read("videoio").Where(c => c.Descriptor.Path == "videoio.inputs[]").ToList();
        Assert.Equal([("input_1", "green"), ("input_2", "grey")], inputs.Select(c => (c.EntityId!, c.Light)).ToArray());
        Assert.Equal("INPUT RECONNECT input_2", inputs[1].Actions[0].Line);
        Assert.Equal("green", Read("videoio").Single(c => c.Descriptor.Path == "videoio.sends[]").Light);
    }

    [AvaloniaFact]
    public async Task UnderHealthEveryCardShowsItsLightAndWordsAndEachActionSendsItsLine()
    {
        await using var desk = new DeskHarness();
        StateFixtures.LoadAll(desk.Link);
        var window = MainWindowTests.Open(desk);
        desk.Main.Drawers.Show("health", "cards");
        DeskHarness.Pump();
        var shown = desk.Main.Cards.Shown;
        Assert.NotEmpty(shown);
        var views = window.GetVisualDescendants().OfType<CardView>().ToList();
        Assert.Equal(shown.Count, views.Count);
        foreach (var view in views)
        {
            var card = (CardViewModel)view.DataContext!;
            var light = view.GetVisualDescendants().OfType<Ellipse>().First(e => e.Name == "CardLight");
            Assert.Equal(LightBrushConverter.Brushes.For(card.Light), light.Fill);
            if (card.HasWords)
                Assert.Equal(card.Words, view.GetVisualDescendants().OfType<TextBlock>().First(t => t.Name == "CardWords").Text);
            // Six fields on the face; the descriptor's others under "more", none dropped.
            Assert.Equal(card.Instance.Descriptor.Fields.Length, card.Fields.Count + card.More.Count);
            Assert.True(card.Fields.Count <= CardDescriptor.MaxFields);
            var more = view.GetVisualDescendants().OfType<Expander>().Single(e => e.Name == "CardMore");
            Assert.Equal(card.HasMore, more.IsVisible);
            if (card.HasMore)
                Assert.Equal(card.More.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) + " more", more.Header);
        }
        Assert.Contains(shown, c => c.HasMore);

        // Each action button sends its line, as the wire, MCP and Companion would.
        var sent = 0;
        foreach (var card in shown.Take(12).ToList())
        {
            foreach (var a in card.Actions.Where(a => a.IsEnabled).Take(1))
            {
                desk.Link.ClearSent();
                var line = a.Verb;
                await a.Command.ExecuteAsync();
                DeskHarness.Pump();
                Assert.Equal(line, desk.Verbs()[^1]);
                sent++;
            }
        }
        Assert.True(sent > 0);
        Screens.Save(window, "health-cards");
        window.Close();
    }

    [AvaloniaFact]
    public async Task AnOutputsCardsShowInItsInspector()
    {
        await using var desk = new DeskHarness();
        // output.json's outputs, named as the demo project's first output (the engine wrote them for its own project).
        using (var doc = JsonDocument.Parse(StateFixtures.Read("output")))
            desk.Link.SetSection("outputs", doc.RootElement.GetProperty("outputs").GetRawText().Replace("\"op_left\"", "\"op_1\"", StringComparison.Ordinal));
        var window = MainWindowTests.Open(desk);
        const string op = "op_1";
        Assert.Contains(desk.Main.Cards.Shown, c => c.EntityId == op);
        desk.Main.Workspace = ViewModels.Workspace.Map;
        desk.Main.Map.Select("output", op);
        DeskHarness.Pump();
        var sections = desk.Main.Map.Inspector.Sections.OfType<CardViewModel>().ToList();
        Assert.NotEmpty(sections);
        Assert.All(sections, c => Assert.Equal(op, c.EntityId));
        Assert.Contains(window.GetVisualDescendants().OfType<CardView>(), v => v.DataContext is CardViewModel c && c.EntityId == op);
        window.Close();
    }

    [AvaloniaFact]
    public async Task SolveCamerasAndTheAssistantHaveCardsAndTheAssistantsVerbsAreAnswered()
    {
        await using var desk = new DeskHarness();
        StateFixtures.Load(desk.Link, "main");
        DeskHarness.Pump();
        var solve = desk.Main.Cards.Shown.Single(c => c.Package == "desk" && c.Instance.Descriptor.Path == "solve");
        Assert.Equal("green", solve.Light);
        Assert.StartsWith("blend solved for 2 outputs", solve.Words, StringComparison.Ordinal);
        Assert.Equal("BLEND SOLVE ALL", solve.Actions[0].Verb);

        // A camera that fails to open is red with its words.
        Assert.DoesNotContain(desk.Main.Cards.Shown, c => c.Instance.Descriptor.Path == "cameras[]");
        desk.Link.SetSection("cameras", """[{"id":"cm_1","name":"Front","state":"failed","views":1,"problem":"the camera did not open: no device"}]""");
        DeskHarness.Pump();
        var camera = desk.Main.Cards.Shown.Single(c => c.Instance.Descriptor.Path == "cameras[]");
        Assert.Equal("red", camera.Light);
        Assert.Equal("Camera · Front", camera.Title);
        Assert.Equal("the camera did not open: no device · 1 views", camera.Words);
        Assert.Equal("GET cm_1", camera.Actions[0].Verb);

        var assistant = desk.Main.Cards.Shown.Single(c => c.Instance.Descriptor.Path == "ai.assistant");
        Assert.Equal("green", assistant.Light);
        Assert.Equal("openAiCompatible · show-model · effort medium", assistant.Words);
        var high = assistant.Actions.Single(a => a.Verb == "ASSISTANT SET effort high");
        await high.Command.ExecuteAsync();
        DeskHarness.Pump();
        Assert.Equal("ASSISTANT SET effort high", desk.Verbs()[^1]);
        Assert.Equal("openAiCompatible · show-model · effort high", assistant.Words);
        Assert.Equal("high", assistant.Fields.Single(f => f.Label == "Effort").Value);
        // Again: nothing would change, so the node refuses with the reason.
        var again = await desk.Session.SendAsync("ASSISTANT SET effort high", Ct);
        Assert.False(again.Ok);
        Assert.StartsWith("already so", again.Text, StringComparison.Ordinal);
        var status = await desk.Session.SendAsync("ASSISTANT STATUS", Ct);
        Assert.True(status.Ok);
        Assert.Equal("high", JsonDocument.Parse(status.Text).RootElement.GetProperty("effort").GetString());
        await assistant.Actions.Single(a => a.Verb == "ASSISTANT SET enabled off").Command.ExecuteAsync();
        DeskHarness.Pump();
        Assert.Equal("grey", assistant.Light);
        Assert.Equal("the assistant is off", assistant.Words);
    }
}
