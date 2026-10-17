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

/// <summary>A held action (<see cref="DeskAction.IsHold"/>: its line's catalogue row presses hold, as a Companion key holds it, and is
/// not one the desk sends at every press) is held in a menu too, in a menu's terms: its first press arms the entry, which then says
/// what a second press sends, and sends nothing; a second press of the same entry within the window of the desk's clock sends exactly
/// the line its label names, once (a line a push moved in between arms it afresh); the window passing, another entry pressed, the
/// menu closing or made again disarms it; an entry shows its words as they are, underscores and all. Every action the desk offers —
/// its own controls, the cards' lines and the generated entries — holds every such line, on its buttons and in its menus.</summary>
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

    /// <summary>An entry is armed with the line its label names: a push that moves the line before the second press arms it afresh,
    /// its label naming the new line, and sends nothing; the press after that sends exactly the new line, once.</summary>
    [AvaloniaFact]
    public async Task APushThatMovesAnArmedLineArmsItAfreshAndOnlyTheLineItsLabelNamesIsSent()
    {
        long now = 1_000;
        await using var desk = new DeskHarness(nowMs: () => now);
        StateFixtures.Load(desk.Link, "backup", "fenced");
        var window = MainWindowTests.Open(desk);
        desk.Main.Drawers.Show("health", "fleet");
        DeskHarness.Pump();
        var board = desk.Main.Health.Fleet;
        var card = window.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "MachineCard" && ReferenceEquals(b.DataContext, board.Self));
        var menu = card.ContextMenu!;
        menu.Open(card);
        DeskHarness.Pump();
        var mirror = EntryOf(menu.Items, board.Mirror);
        var first = board.Mirror.Verb;
        Assert.StartsWith("FLEET BACKUP ", first, StringComparison.Ordinal);
        desk.Link.ClearSent();

        Press(mirror);
        Assert.Empty(desk.Verbs());
        Assert.Equal("Press again to send " + first, mirror.Header);

        // The node with the show moves before the second press: the line backs up another node now.
        desk.Link.SetSection("fleet", """{"of":"1234abcd"}""");
        DeskHarness.Pump();
        Assert.Equal("FLEET BACKUP 1234abcd HOT", board.Mirror.Verb);
        Assert.True(menu.IsOpen);
        now += 10;
        Press(mirror);
        await board.Mirror.Command.Last;
        DeskHarness.Pump();
        Assert.Empty(desk.Verbs());
        Assert.Equal("Press again to send FLEET BACKUP 1234abcd HOT", mirror.Header);
        Assert.Same(board.Mirror, desk.Session.Presses.Armed);
        Assert.True(menu.IsOpen);

        now += 10;
        Press(mirror);
        await board.Mirror.Command.Last;
        DeskHarness.Pump();
        Assert.Equal(["FLEET BACKUP 1234abcd HOT"], desk.Verbs());
        Assert.Null(desk.Session.Presses.Armed);
        window.Close();
    }

    /// <summary>The text a menu entry's header shows, as it is laid out, and whether the header takes a key press for the entry (access
    /// text, where an underscore marks the key).</summary>
    private static (string Shown, bool TakesAKey) Shown(MenuItem item)
    {
        var presenter = item.GetVisualDescendants().OfType<Avalonia.Controls.Presenters.ContentPresenter>().Single(p => p.Name == "PART_HeaderPresenter");
        var text = Assert.IsAssignableFrom<TextBlock>(presenter.Child);
        var shown = string.Concat(text.TextLayout.TextLines.SelectMany(l => l.TextRuns).Select(r => r.Text.ToString()));
        return (shown, text is Avalonia.Controls.Primitives.AccessText);
    }

    /// <summary>A menu entry shows its words as they are, underscores and all, armed or not: the node's ids carry underscores (op_1,
    /// sf_…, in_…), and none of them is taken for an access key, so the armed entry shows the very line a second press sends.</summary>
    [AvaloniaFact]
    public async Task AnEntryShowsALineWithUnderscoresAsItIsArmedOrNot()
    {
        long now = 1_000;
        await using var desk = new DeskHarness(nowMs: () => now);
        desk.Link.SetSection("videoio", """{"inputs":[{"id":"in_arena","name":"in_arena feed"}]}""");
        var window = MainWindowTests.Open(desk);
        desk.Main.Workspace = Workspace.Map;
        DeskHarness.Pump();
        var surface = DeskVerbRuleTests.Walk(desk.Main).Targets.First(t => t.Kind == "surface");
        var use = Assert.Single(desk.Main.ExtrasFor(surface), a => a.Id == "gen.input");
        var entry = Assert.Single(use.Children);
        Assert.True(entry.IsHold);
        var host = MainWindowTests.Find<Control>(window, "Viewport");
        var menu = new ContextMenu();
        DeskMenu.Fill(menu.Items, [use]);
        menu.Open(host);
        DeskHarness.Pump();
        var parent = menu.Items.OfType<MenuItem>().Single();
        parent.IsSubMenuOpen = true;
        DeskHarness.Pump();
        var item = EntryOf(menu.Items, entry);
        Assert.Equal(("in_arena feed", false), Shown(item));

        Press(item);
        Assert.Empty(desk.Verbs());
        Assert.Equal(("Press again to send SURFACE SOURCE " + surface.Id + " live:in_arena", false), Shown(item));
        menu.Close();
        window.Close();
    }

    /// <summary>The rows the desk sends at every press though a Companion key holds them: the transport's Stop and the air's own
    /// controls, the panic banner's Clear and the master (DESK.md).</summary>
    private static readonly string[] AtEveryPress = ["MASTER", "PANIC CLEAR", "VIDEO STOP"];

    private static string RowOf(string line) => WireGrammar.TryParse(line, out var w, out _) && VerbCatalog.Match(w) is { } m ? m.Spec.Name : "";

    /// <summary>Every action the desk offers that sends a line — its own controls, the cards' lines and the generated entries, in every
    /// workspace, drawer, inspector and menu, with every fixture laid on a node that has the show — is held exactly when its line's
    /// catalogue row presses hold (the table the Companion module's keys use) and is not one the desk sends at every press; every held
    /// one, pressed in a menu, sends nothing on its first press and exactly its line on its second, and every row the desk holds is
    /// pressed live (not greyed) at least once.</summary>
    [AvaloniaFact]
    public async Task EveryActionThatSendsAHeldRowIsHeldAndPressedTwiceInAMenu()
    {
        long now = 1_000;
        await using var desk = new DeskHarness(nowMs: () => now);
        StateFixtures.LoadAll(desk.Link);
        // This node has the show (a hot backup's door greys its lines: the fleet board's own held lines are pressed above), the show
        // is neither armed nor locked (Show Mode and the area locks grey what changes the air), and a live input to use on a surface.
        StateFixtures.Load(desk.Link, "backup", "primary");
        desk.Link.SetSection("show", """{"armed":false,"locks":[]}""");
        desk.Link.SetSection("videoio", """{"inputs":[{"id":"in_arena","name":"Arena (NDI)"}]}""");
        var window = MainWindowTests.Open(desk);
        Assert.Equal(AtEveryPress, DeskAction.SentAtOnce.Order(StringComparer.Ordinal));
        var heldRows = VerbCatalog.All.Where(s => VerbParams.Press(s) == PressShapes.Hold && !AtEveryPress.Contains(s.Name)).Select(s => s.Name)
            .ToHashSet(StringComparer.Ordinal);
        var (all, _, _) = DeskVerbRuleTests.Reach(desk, window);
        var actions = all.Distinct().Where(a => !a.HasChildren && a.Verb.Length > 0).ToList();
        foreach (var a in actions)
            Assert.True(a.IsHold == heldRows.Contains(RowOf(a.Verb)), $"{a.Id}: {a.Verb} is{(a.IsHold ? "" : " not")} held");
        Assert.Contains(actions, a => AtEveryPress.Contains(RowOf(a.Verb)) && !a.IsHold);

        var held = new SortedSet<string>(StringComparer.Ordinal);
        var live = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var a in actions.Where(a => a.IsHold))
        {
            if (!a.IsAvailable || !a.IsHold)
                continue;
            var menu = new ContextMenu();
            DeskMenu.Fill(menu.Items, [a]);
            var entry = EntryOf(menu.Items, a);
            Assert.Null(entry.Command);
            var line = a.Verb;
            var enabled = a.IsEnabled;
            var why = a.Refusal;
            desk.Link.ClearSent();
            Press(entry);
            Assert.Empty(desk.Verbs());
            now += 10;
            Press(entry);
            await a.Command.Last;
            DeskHarness.Pump();
            output.WriteLine($"{a.Id}: {line}{(enabled ? "" : " (greyed: " + why + ")")}");
            // A greyed entry sends nothing on either press; a live one sends exactly its line on the second.
            string[] expected = enabled ? [line] : [];
            Assert.Equal(expected, desk.Verbs());
            Assert.Null(desk.Session.Presses.Armed);
            held.Add(RowOf(line));
            if (enabled)
                live.Add(RowOf(line));
            // Whatever the line did, the next entry is pressed on a desk that has taken its push.
            await desk.FlushThrottle();
        }
        output.WriteLine($"held rows: {string.Join(", ", held)}; pressed live: {string.Join(", ", live)}");
        Assert.Equal(held, live);
        foreach (var row in new[] { "SURFACE SOURCE", "SURFACE SHOW", "OUTPUT ENABLE", "OUTPUT IDENTIFY", "PATTERN", "CALIBRATION RESOLVE" })
            Assert.Contains(row, held);
        window.Close();
    }

    /// <summary>A card's button whose line is held sends nothing on a click and exactly its line once its hold completes.</summary>
    [AvaloniaFact]
    public async Task ACardsHeldButtonSendsItsLineOnlyWhenItsHoldCompletes()
    {
        await using var desk = new DeskHarness();
        StateFixtures.LoadAll(desk.Link);
        StateFixtures.Load(desk.Link, "backup", "primary");
        desk.Link.SetSection("show", """{"armed":false,"locks":[]}""");
        var window = MainWindowTests.Open(desk);
        desk.Main.Drawers.Show("health", "cards");
        DeskHarness.Pump();
        var button = window.GetVisualDescendants().OfType<CardView>().SelectMany(v => v.GetVisualDescendants().OfType<ActionMenuButton>())
            .First(b => b.Action is { IsHold: true, IsEnabled: true, HasChildren: false } && Hittable(window, b));
        var action = button.Action!;
        var line = action.Verb;
        desk.Link.ClearSent();
        DeskHarness.Click(window, button);
        await action.Command.Last;
        Assert.False(button.Complete(600));
        Assert.Empty(desk.Verbs());

        // Pressed and held to its time: exactly its line, once.
        var at = button.TranslatePoint(new Avalonia.Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value;
        Avalonia.Headless.HeadlessWindowExtensions.MouseDown(window, at, MouseButton.Left, RawInputModifiers.None);
        DeskHarness.Pump();
        Assert.True(button.IsHeld);
        Assert.False(button.Complete(599));
        Assert.Empty(desk.Verbs());
        Assert.True(button.Complete(600));
        Avalonia.Headless.HeadlessWindowExtensions.MouseUp(window, at, MouseButton.Left, RawInputModifiers.None);
        await action.Command.Last;
        DeskHarness.Pump();
        output.WriteLine(line);
        Assert.Equal([line], desk.Verbs());
        window.Close();
    }
}
