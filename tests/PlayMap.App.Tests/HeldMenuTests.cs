using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using PlayMap.App.Controls;
using PlayMap.App.Infrastructure;
using PlayMap.App.ViewModels;
using PlayMap.App.ViewModels.Cards;
using PlayMap.App.Views.Cards;
using PlayMap.Core.Wire;

namespace PlayMap.App.Tests;

/// <summary>A held action (<see cref="DeskAction.IsHold"/>: its catalogue row presses hold, as a Companion key holds it) is held in a
/// menu too, in a menu's terms: its first press arms the entry, which then says what a second press sends, and sends nothing; a
/// second press of the same entry within the window of the desk's clock sends exactly its line, once; the window passing, another
/// entry pressed, the menu closing or made again disarms it. Every menu the desk builds from the catalogue's data — the cards' and
/// the generated entries — holds every line whose row presses hold, on its buttons and in its menu.</summary>
public class HeldMenuTests(ITestOutputHelper output)
{
    /// <summary>A press of a menu entry, as a click raises it.</summary>
    private static void Press(MenuItem item)
    {
        item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        DeskHarness.Pump();
    }

    private static MenuItem EntryOf(ItemCollection items, DeskAction a) => Entries(items).Single(i => ReferenceEquals(i.Tag, a));

    private static IEnumerable<MenuItem> Entries(ItemCollection items)
    {
        foreach (var i in items.OfType<MenuItem>())
        {
            yield return i;
            foreach (var c in Entries(i.Items))
                yield return c;
        }
    }

    /// <summary>True when a click at the control's centre reaches it (it is on screen, not scrolled away or covered).</summary>
    private static bool Hittable(Window window, Control control)
    {
        if (!control.IsEffectivelyVisible || control.TranslatePoint(new Avalonia.Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window) is not { } at)
            return false;
        return window.InputHitTest(at) is Avalonia.Visual hit && hit.GetSelfAndVisualAncestors().Contains(control);
    }

    private static string PressOf(string line) =>
        WireGrammar.TryParse(line, out var w, out _) && VerbCatalog.Match(w) is { } m ? VerbParams.Press(m.Spec) : "";

    [AvaloniaFact]
    public async Task AHeldEntrySendsOnlyOnASecondPressWithinTheWindowAndAnythingElseDisarmsIt()
    {
        long now = 1_000;
        await using var desk = new DeskHarness(nowMs: () => now);
        StateFixtures.Load(desk.Link, "backup", "backup");
        var window = MainWindowTests.Open(desk);
        desk.Main.Drawers.Show("health", "fleet");
        DeskHarness.Pump();
        var board = desk.Main.Health.Fleet;
        var card = window.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "MachineCard" && ReferenceEquals(b.DataContext, board.Self));
        var menu = card.ContextMenu!;
        menu.Open(card);
        DeskHarness.Pump();
        var presses = desk.Session.Presses;
        var failover = EntryOf(menu.Items, board.Failover);
        Assert.True(board.Failover.IsHold);
        Assert.Equal(PressShapes.Hold, PressOf(board.Failover.Verb));
        Assert.Null(failover.Command);
        desk.Link.ClearSent();

        // One press sends nothing: the entry is armed and says what a second press sends, the menu still open.
        Press(failover);
        Assert.Empty(desk.Verbs());
        Assert.Equal("Press again to send FAILOVER", failover.Header);
        Assert.Same(board.Failover, presses.Armed);
        Assert.True(menu.IsOpen);

        // The window passing disarms it: a press after it arms it afresh and sends nothing.
        now += HeldPress.WindowMs + 1;
        Assert.Null(presses.Armed);
        Press(failover);
        Assert.Empty(desk.Verbs());
        Assert.Same(board.Failover, presses.Armed);

        // Another held entry pressed arms that one instead, FAILOVER's own label back; nothing sent.
        var solo = EntryOf(menu.Items, board.Solo);
        Press(solo);
        Assert.Empty(desk.Verbs());
        Assert.Equal(board.Failover.MenuLabel, failover.Header);
        Assert.Same(board.Solo, presses.Armed);

        // The menu closing disarms it.
        menu.Close();
        DeskHarness.Pump();
        Assert.Null(presses.Armed);
        Assert.Equal(board.Solo.MenuLabel, solo.Header);

        // Opened again (made again): two presses within the window send exactly FAILOVER, once, and close the menu.
        menu.Open(card);
        DeskHarness.Pump();
        failover = EntryOf(menu.Items, board.Failover);
        Assert.Equal(board.Failover.MenuLabel, failover.Header);
        Press(failover);
        now += HeldPress.WindowMs;
        Press(failover);
        await board.Failover.Command.Last;
        DeskHarness.Pump();
        Assert.Equal(["FAILOVER"], desk.Verbs());
        Assert.Null(presses.Armed);
        Assert.False(menu.IsOpen);
        window.Close();
    }

    [AvaloniaFact]
    public async Task AnotherEntryPressedDisarmsAHeldOneAndSendsOnlyItsOwnLine()
    {
        long now = 1_000;
        await using var desk = new DeskHarness(nowMs: () => now);
        StateFixtures.Load(desk.Link, "backup", "backup");
        desk.Main.Drawers.Show("health", "fleet");
        DeskHarness.Pump();
        var board = desk.Main.Health.Fleet;
        // A line a hot backup runs, pressed at once.
        var fleet = DeskAction.Send(desk.Session, "test.fleet", "Fleet", () => "FLEET");
        Assert.False(fleet.IsHold);
        var menu = new ContextMenu();
        DeskMenu.Fill(menu.Items, [board.Failover, fleet]);
        desk.Link.ClearSent();

        Press(EntryOf(menu.Items, board.Failover));
        Assert.Same(board.Failover, desk.Session.Presses.Armed);
        Press(EntryOf(menu.Items, fleet));
        await fleet.Command.Last;
        DeskHarness.Pump();
        Assert.Null(desk.Session.Presses.Armed);
        Assert.Equal(board.Failover.MenuLabel, EntryOf(menu.Items, board.Failover).Header);
        // FAILOVER pressed once more is a first press again: only FLEET went.
        Press(EntryOf(menu.Items, board.Failover));
        Assert.Equal(["FLEET"], desk.Verbs());
    }

    /// <summary>Over every catalogue row that presses hold: each entry of a menu built from the catalogue's data that sends it — every
    /// package's cards with every fixture laid, and the generated entries of every object (Use input's SURFACE SOURCE) — is held (and
    /// an entry whose row does not press hold is not), its card button sends nothing on a click, its first press sends nothing, and two
    /// presses send exactly its line; the desk's own held actions in any menu do the same.</summary>
    [AvaloniaFact]
    public async Task EveryMenuBuiltFromTheCatalogueHoldsEveryHeldRow()
    {
        long now = 1_000;
        await using var desk = new DeskHarness(nowMs: () => now);
        StateFixtures.LoadAll(desk.Link);
        // This node a hot backup (its card offers FAILOVER), and a live input to use on a surface.
        StateFixtures.Load(desk.Link, "backup", "backup");
        desk.Link.SetSection("videoio", """{"inputs":[{"id":"in_arena","name":"Arena (NDI)"}]}""");
        var window = MainWindowTests.Open(desk);
        desk.Main.Drawers.Show("health", "cards");
        DeskHarness.Pump();
        var heldRows = VerbCatalog.All.Where(s => VerbParams.Press(s) == PressShapes.Hold).Select(s => s.Name).ToHashSet(StringComparer.Ordinal);

        // The menus built from the catalogue's data: each card's (its buttons' list) and each object's generated entries.
        var menus = new List<(string Where, IReadOnlyList<DeskAction> Actions, Control? Card)>();
        foreach (var view in window.GetVisualDescendants().OfType<CardView>().ToList())
        {
            var card = (CardViewModel)view.DataContext!;
            menus.Add(("card " + card.Key, card.Actions, view));
        }
        var (_, targets, _) = DeskVerbRuleTests.Walk(desk.Main);
        foreach (var t in targets.DistinctBy(t => t.Id))
            menus.Add(("generated for " + t.Id, desk.Main.ExtrasFor(t), null));
        // The desk's own held actions: this node's machine card on Health ▸ Fleet.
        menus.Add(("fleet board", desk.Main.Health.Fleet.Self.Actions, null));
        Assert.Contains(menus, m => m.Where.StartsWith("card ", StringComparison.Ordinal));

        var covered = new SortedSet<string>(StringComparer.Ordinal);
        var pressed = 0;
        foreach (var (where, actions, cardView) in menus)
        {
            var menu = new ContextMenu();
            DeskMenu.Fill(menu.Items, actions);
            foreach (var entry in Entries(menu.Items).ToList())
            {
                if (entry.Tag is not DeskAction { HasChildren: false } a || a.Verb.Length == 0)
                    continue;
                var row = WireGrammar.TryParse(a.Verb, out var w, out _) && VerbCatalog.Match(w) is { } m ? m.Spec.Name : "";
                Assert.True(a.IsHold == heldRows.Contains(row), $"{where}: {a.Verb} ({row}) is{(a.IsHold ? "" : " not")} held");
                if (!a.IsHold)
                    continue;
                covered.Add(row);
                Assert.Null(entry.Command);

                // Its card button is held: a click on it sends nothing.
                if (cardView?.GetVisualDescendants().OfType<ActionMenuButton>().FirstOrDefault(b => ReferenceEquals(b.Action, a)) is { } button
                    && Hittable(window, button))
                {
                    desk.Link.ClearSent();
                    DeskHarness.Click(window, button);
                    await a.Command.Last;
                    Assert.Empty(desk.Verbs());
                }

                desk.Link.ClearSent();
                var line = a.Verb;
                var enabled = a.IsEnabled;
                Press(entry);
                Assert.Empty(desk.Verbs());
                now += 10;
                Press(entry);
                await a.Command.Last;
                DeskHarness.Pump();
                output.WriteLine($"{where}: {line}{(enabled ? "" : " (greyed)")}");
                // A greyed entry sends nothing on either press; a live one sends exactly its line on the second.
                string[] expected = enabled ? [line] : [];
                Assert.Equal(expected, desk.Verbs());
                Assert.Null(desk.Session.Presses.Armed);
                pressed++;
                // Whatever the line did, the next entry is pressed on a desk that has taken its push.
                await desk.FlushThrottle();
            }
        }
        output.WriteLine($"{pressed} held entries pressed; rows: {string.Join(", ", covered)}");
        Assert.Contains("FAILOVER", covered);
        Assert.Contains("SURFACE SOURCE", covered);
        Assert.True(covered.Count >= 10, string.Join(", ", covered));
        window.Close();
    }
}
