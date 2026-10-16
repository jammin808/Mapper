using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using PlayMap.App.Controls;
using PlayMap.App.ViewModels;
using PlayMap.App.ViewModels.Cards;
using PlayMap.App.Views.Cards;

namespace PlayMap.App.Tests;

/// <summary>The cards follow each push as the window shows them: every card's buttons ask the gate with the push's own facts (the
/// cards take a push before the show's module does), a button greyed by its own card says why in its tooltip, a push that moves
/// nothing of a card or of the show repaints nothing of them, and a card that comes or goes leaves the others' views alone.</summary>
public class CardRefreshTests
{
    private static List<(ActionMenuButton Button, DeskAction Action)> CardButtons(Window window) =>
    [
        .. window.GetVisualDescendants().OfType<CardView>().SelectMany(v => v.GetVisualDescendants().OfType<ActionMenuButton>())
            .Where(b => b.Action is { HasChildren: false }).Select(b => (b, b.Action!)),
    ];

    /// <summary>Each shown button is greyed exactly when its action is (the gate asked now), and a refused one's tooltip carries
    /// the node's words. Returns how many the gate refuses.</summary>
    private static int AssertButtonsFollowTheGate(Window window, string when)
    {
        var refused = 0;
        var buttons = CardButtons(window);
        Assert.NotEmpty(buttons);
        foreach (var (button, action) in buttons)
        {
            Assert.True(button.IsEnabled == action.IsEnabled,
                $"{when}: '{action.Label}' ({action.Verb}) shows enabled={button.IsEnabled}, the gate says {action.Refusal ?? "live"}");
            if (action.Refusal is not { } why)
                continue;
            refused++;
            Assert.Contains(why, ToolTip.GetTip(button) as string ?? "", StringComparison.Ordinal);
        }
        return refused;
    }

    [AvaloniaFact]
    public async Task EveryCardsButtonsAskTheGateWithThePushsOwnFactsWhenTheShowArmsAndDisarms()
    {
        await using var desk = new DeskHarness();
        foreach (var package in StateFixtures.Packages.Where(p => p != "backup"))
            StateFixtures.Load(desk.Link, package);
        await desk.Session.SendAsync("PANIC CLEAR", Ct);
        await desk.Session.SendAsync("SHOW DISARM", Ct);
        await desk.Session.SendAsync("UNLOCK ALL", Ct);
        var window = MainWindowTests.Open(desk);
        desk.Main.Drawers.Show("health", "cards");
        DeskHarness.Pump();
        var rehearsal = AssertButtonsFollowTheGate(window, "rehearsal");

        await desk.Session.SendAsync("SHOW ARM", Ct);
        DeskHarness.Pump();
        Assert.True(desk.Main.ShowModule.Strip.IsArmed);
        var armed = AssertButtonsFollowTheGate(window, "armed");
        Assert.True(armed > rehearsal, $"arming greys card buttons ({rehearsal} → {armed})");

        await desk.Session.SendAsync("SHOW DISARM", Ct);
        DeskHarness.Pump();
        Assert.Equal(rehearsal, AssertButtonsFollowTheGate(window, "disarmed"));
        window.Close();
    }

    [AvaloniaFact]
    public async Task AButtonItsOwnCardGreysSaysWhyInItsTooltip()
    {
        await using var desk = new DeskHarness();
        // The backup's own fixture: it lists no hot backups of its own, so the Backups card has no node to focus.
        StateFixtures.Load(desk.Link, "backup", "backup");
        var window = MainWindowTests.Open(desk);
        desk.Main.Drawers.Show("health", "cards");
        DeskHarness.Pump();
        var card = desk.Main.Cards.Shown.Single(c => c.Instance.Descriptor.Path == "fleet.backups");
        var focus = card.Instance.Actions.Single(a => a.Label.StartsWith("Fleet focus", StringComparison.Ordinal));
        Assert.Null(focus.Line);
        Assert.NotNull(focus.Missing);
        var (button, _) = CardButtons(window).Single(p => card.Actions.Contains(p.Action) && p.Action.Label == focus.Label);
        Assert.False(button.IsEnabled);
        Assert.Contains(focus.Missing, ToolTip.GetTip(button) as string ?? "", StringComparison.Ordinal);
        window.Close();
    }

    [AvaloniaFact]
    public async Task APushThatMovesNothingOfACardOrTheShowRepaintsNothingOfThem()
    {
        await using var desk = new DeskHarness();
        StateFixtures.LoadAll(desk.Link);
        var window = MainWindowTests.Open(desk);
        desk.Main.Drawers.Show("health", "cards");
        DeskHarness.Pump();
        var moved = new List<string>();
        foreach (var a in desk.Main.Cards.Shown.SelectMany(c => c.Actions))
            a.PropertyChanged += (_, e) => moved.Add(a.Label + "." + e.PropertyName);
        desk.Main.ShowModule.Strip.PropertyChanged += (_, e) => moved.Add("strip." + e.PropertyName);
        foreach (var a in desk.Main.ShowModule.Strip.ArmMenu.Concat(desk.Main.ShowModule.Strip.PanicMenu))
            a.PropertyChanged += (_, e) => moved.Add(a.Label + "." + e.PropertyName);

        // A control bound since the last push reads its action whole once; from then on only what moves is raised.
        desk.Link.SetSection("probe", "{\"n\":0}");
        DeskHarness.Pump();
        moved.Clear();
        desk.Link.SetSection("probe", "{\"n\":1}");
        DeskHarness.Pump();
        desk.Link.SetSection("probe", "{\"n\":2}");
        DeskHarness.Pump();
        Assert.Empty(moved);

        // A push that does move the show repaints what it moved.
        await desk.Session.SendAsync("PANIC CLEAR", Ct);
        DeskHarness.Pump();
        Assert.Contains("strip.IsPanic", moved);
        window.Close();
    }

    [AvaloniaFact]
    public async Task ACardThatComesOrGoesLeavesTheOtherCardsViewsWhereTheyAre()
    {
        await using var desk = new DeskHarness();
        StateFixtures.LoadAll(desk.Link);
        var window = MainWindowTests.Open(desk);
        desk.Main.Drawers.Show("health", "cards");
        DeskHarness.Pump();
        var before = window.GetVisualDescendants().OfType<CardView>().ToList();
        var open = before.First(v => v.DataContext is CardViewModel { HasMore: true });
        var more = open.GetVisualDescendants().OfType<Expander>().Single(e => e.Name == "CardMore");
        more.IsExpanded = true;
        DeskHarness.Pump();

        // A camera that fails to open brings a card of its own.
        desk.Link.SetSection("cameras", """[{"id":"cm_1","name":"Front","state":"failed","views":1,"problem":"the camera did not open: no device"}]""");
        DeskHarness.Pump();
        var after = window.GetVisualDescendants().OfType<CardView>().ToList();
        Assert.Equal(before.Count + 1, after.Count);
        Assert.All(before, v => Assert.Contains(v, after));
        Assert.True(more.IsExpanded);

        // It goes again; the others stay.
        desk.Link.SetSection("cameras", null);
        DeskHarness.Pump();
        Assert.Equal(before, window.GetVisualDescendants().OfType<CardView>().ToList());
        Assert.True(more.IsExpanded);
        window.Close();
    }
}
