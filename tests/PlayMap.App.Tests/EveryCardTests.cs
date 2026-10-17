using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using PlayMap.App.Controls;
using PlayMap.App.State;
using PlayMap.App.ViewModels;
using PlayMap.App.ViewModels.Cards;
using PlayMap.App.Views.Cards;

namespace PlayMap.App.Tests;

/// <summary>Every package's cards from the final fixtures, on the desk: for each descriptor file (each package's *.cards.json and
/// the desk's own) and each scenario of its fixture, laid over the demo engine as a node publishes it, every descriptor the fixture
/// fills shows on the desk, every card the desk's STATE yields renders under Health ▸ Cards with its light and words, its
/// right-click menu is its buttons, and every action either sends exactly the line it resolves to — a catalogue line — or, when the
/// fixture leaves it nothing to act on or the node's doors refuse it, is greyed with the words why and sends nothing.</summary>
public class EveryCardTests(ITestOutputHelper output)
{
    /// <summary>A desk on the demo engine with main's fixture and then the scenario laid over it, its sent lines cleared.</summary>
    private static async Task<DeskHarness> Laid(string fixture, string scenario)
    {
        var desk = new DeskHarness();
        StateFixtures.Load(desk.Link, "main");
        if (fixture != "main")
            StateFixtures.Load(desk.Link, fixture, scenario);
        await desk.FlushThrottle();
        desk.Link.ClearSent();
        return desk;
    }

    /// <summary>A descriptor path that names a list's element by its place ("outputs.0.measured").</summary>
    private static bool ByPlace(string path) => path.Split('.').Any(p => p.Length > 0 && p.All(char.IsAsciiDigit));

    [AvaloniaFact]
    public async Task EveryPackagesCardsRenderFromEachScenarioAndEveryActionSendsItsLine()
    {
        var packages = 0;
        var cards = 0;
        var sent = 0;
        var greyed = 0;
        foreach (var package in CardDescriptor.BuiltInPackages)
        {
            var descriptors = CardDescriptor.BuiltIn.Where(d => d.Package == package).ToList();
            if (descriptors.Count == 0)
                continue;
            packages++;
            var fixture = package == "desk" ? "main" : package;
            foreach (var (scenario, sections) in StateFixtures.Scenarios(fixture))
            {
                List<CardInstance> expected;
                await using (var desk = await Laid(fixture, scenario))
                {
                    var window = MainWindowTests.Open(desk);
                    desk.Main.Drawers.Show("health", "cards");
                    DeskHarness.Pump();
                    // The cards the descriptors read from the STATE the desk holds (the fixture laid over the demo engine's own).
                    expected = [.. CardReader.ReadAll(descriptors, desk.Session.State.Root)];
                    Assert.True(expected.Count > 0, $"{package}/{scenario} yields no card");
                    // Each descriptor the fixture itself fills shows on the desk too — but one that reads a list's element by its
                    // place (outputs.0), which on the desk is the demo show's own first output.
                    foreach (var filled in CardReader.ReadAll(descriptors, CardTests.FixtureRoot(fixture, fixture == "main" ? null : sections))
                        .Select(c => c.Descriptor).Distinct())
                        Assert.True(ByPlace(filled.Path) || expected.Any(c => c.Descriptor.Index == filled.Index), $"{package}/{scenario}: {filled.Title} is not on the desk");
                    var views = window.GetVisualDescendants().OfType<CardView>().ToList();
                    foreach (var want in expected)
                    {
                        var card = desk.Main.Cards.Shown.SingleOrDefault(c => c.Key == CardViewModel.KeyOf(want));
                        Assert.True(card is not null, $"{package}/{scenario}: {want.Title} is not on the desk");
                        Assert.Equal((want.Title, want.Light, want.Words), (card.Title, card.Light, card.Words));
                        var view = views.Single(v => ReferenceEquals(v.DataContext, card));
                        Assert.Equal(LightBrushConverter.Brushes.For(card.Light), view.GetVisualDescendants().OfType<Ellipse>().First(e => e.Name == "CardLight").Fill);
                        if (card.HasWords)
                            Assert.Equal(card.Words, view.GetVisualDescendants().OfType<TextBlock>().First(t => t.Name == "CardWords").Text);
                        // Its right-click menu is its buttons, one per action.
                        Assert.Same(card.Actions, DeskMenu.GetActions(view.GetVisualDescendants().OfType<Control>().First(c => DeskMenu.GetActions(c) is not null)));
                        Assert.Equal(want.Actions.Length, card.Actions.Count);
                        var buttons = view.GetVisualDescendants().OfType<ActionMenuButton>().Select(b => b.Action).ToList();
                        Assert.All(card.Actions, a => Assert.Contains(a, buttons));
                        cards++;
                    }
                    window.Close();
                }

                // Each action pressed on a desk the scenario was just laid on (a press that lands moves the node).
                foreach (var want in expected)
                {
                    for (var i = 0; i < want.Actions.Length; i++)
                    {
                        var line = want.Actions[i].Line;
                        await using var desk = await Laid(fixture, scenario);
                        var a = desk.Main.Cards.Shown.Single(c => c.Key == CardViewModel.KeyOf(want)).Actions[i];
                        if (line is null)
                        {
                            // Nothing in this fixture to act on: greyed with the descriptor's words.
                            Assert.False(a.IsEnabled, $"{package}/{scenario} {want.Title}: '{a.Label}'");
                            Assert.False(string.IsNullOrEmpty(want.Actions[i].Missing));
                        }
                        else
                        {
                            Assert.Equal(line, a.Verb);
                            Assert.Null(DeskVerbRuleTests.Fault(line));
                        }
                        var refusal = a.Refusal;
                        await a.Command.ExecuteAsync();
                        DeskHarness.Pump();
                        output.WriteLine($"{package}/{scenario} {want.Title} · {a.Label}: {line ?? "(" + want.Actions[i].Missing + ")"}{(refusal is { } r ? " — greyed: " + r : "")}");
                        if (line is null || refusal is not null)
                        {
                            // Greyed (nothing to act on, or the node's doors refuse it here: an armed show, a backup's door): nothing sent.
                            Assert.Empty(desk.Verbs());
                            greyed++;
                        }
                        else
                        {
                            Assert.Equal(line, Assert.Single(desk.Verbs()));
                            sent++;
                        }
                    }
                }
            }
        }
        output.WriteLine($"{packages} packages, {cards} cards, {sent} lines sent, {greyed} greyed");
        Assert.Equal(StateFixtures.Cards.Count + 1, packages);
        Assert.True(sent > 100, $"{sent} lines sent");
    }
}
