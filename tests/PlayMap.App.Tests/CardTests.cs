using System.Text.Json;
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
/// fixture with its light and words; every action resolves to a catalogue line and sends it; the cards show under Health
/// and in the inspector of the object they are about; main.json's solve, cameras and the assistant have cards, and the
/// fake engine answers the assistant's verbs as the engine does.</summary>
public class CardTests(ITestOutputHelper output)
{
    private static JsonElement Root(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    /// <summary>A package's fixture with main's laid under it (a card may read a root key main carries).</summary>
    private static JsonElement FixtureRoot(string package)
    {
        var main = System.Text.Json.Nodes.JsonNode.Parse(StateFixtures.Read("main"))!.AsObject();
        if (package != "main")
        {
            foreach (var (k, v) in System.Text.Json.Nodes.JsonNode.Parse(StateFixtures.Read(package))!.AsObject())
                main[k] = v?.DeepClone();
        }
        return Root(main.ToJsonString());
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
            var root = FixtureRoot(package == "desk" ? "main" : package);
            foreach (var d in descriptors)
            {
                var cards = CardReader.Read(d, root).ToList();
                output.WriteLine($"{package}#{d.Index} {d.Title} ({d.Path}): {cards.Count} card(s)");
                foreach (var c in cards)
                {
                    output.WriteLine($"   [{c.Light}] {c.Title} — {c.Words} | {string.Join("; ", c.Fields.Select(f => f.Label + "=" + f.Value))} | entity {c.EntityId}");
                    foreach (var a in c.Actions)
                        output.WriteLine($"      {a.Label}: {a.Line ?? "(" + a.Missing + ")"}");
                    Assert.Contains(c.Light, lights);
                    Assert.True(c.Fields.Length <= CardDescriptor.MaxFields);
                    foreach (var a in c.Actions)
                    {
                        Assert.True(a.Line is not null, $"{package} {d.Title}: '{a.Label}' does not resolve: {a.Missing}");
                        Assert.Null(DeskVerbRuleTests.Fault(a.Line!));
                    }
                }
                // A descriptor whose package wrote a fixture shows at least one card from it (cameras: none are open in main's).
                if (!(package == "desk" && d.Path == "cameras[]"))
                    Assert.NotEmpty(cards);
            }
        }
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
        Assert.Equal("red", preflight.Light);
        Assert.Equal("3 green · 0 amber · 1 red · show-ready 4/5", preflight.Words);

        // A per-element card per output; its action names that output.
        var outputs = CardReader.ReadAll(CardDescriptor.BuiltIn.Where(d => d.Package == "output"), FixtureRoot("output"));
        foreach (var c in outputs.Where(c => c.Descriptor.Path == "outputs[].present"))
        {
            Assert.NotNull(c.EntityId);
            Assert.Equal("OUTPUT STATS " + c.EntityId, c.Actions[0].Line);
        }

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
        }

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
