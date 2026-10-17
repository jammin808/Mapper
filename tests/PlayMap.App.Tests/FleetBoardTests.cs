using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using PlayMap.App.Controls;
using PlayMap.App.ViewModels;
using PlayMap.App.ViewModels.Health;
using PlayMap.Core.Wire;

namespace PlayMap.App.Tests;

/// <summary>Health ▸ Fleet: the fleet fixture renders a card per machine — this node's, and each other machine lit by how recently it
/// was heard (amber "quiet" when stale, red "gone"); a card focuses the board (FLEET FOCUS), the lens chips and ‹ › send the board's
/// view verbs. On a hot backup (backup.json) this node's card says BACKUP HOT with the mirror's words and its FAILOVER — held to
/// confirm, as the module's key is, and the same button across STATE pushes — sends FAILOVER; the node then has the show; a primary
/// shows its backup's card; a fenced node offers to mirror the node with the show.</summary>
public class FleetBoardTests
{
    /// <summary>A rendered machine card: its light's fill and the heard chip's words.</summary>
    private static (Avalonia.Media.IBrush? Light, string Heard, string Role) Rendered(Window window, MachineCardViewModel card)
    {
        var border = window.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "MachineCard" && ReferenceEquals(b.DataContext, card));
        var light = border.GetVisualDescendants().OfType<Ellipse>().Single(e => e.Name == "MachineLight");
        var heard = border.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "MachineHeard");
        var role = border.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "MachineRole");
        return (light.Fill, heard.IsEffectivelyVisible ? heard.Text ?? "" : "", role.IsEffectivelyVisible ? role.Text ?? "" : "");
    }

    private static Avalonia.Media.IBrush Brush(string light) => LightBrushConverter.Brushes.For(light);

    [AvaloniaFact]
    public async Task TheFleetFixtureRendersCardsWithStaleAndGoneColours()
    {
        await using var desk = new DeskHarness();
        StateFixtures.Load(desk.Link, "fleet");
        var window = MainWindowTests.Open(desk);
        desk.Main.Drawers.Show("health", "fleet");
        DeskHarness.Pump();
        var board = desk.Main.Health.Fleet;
        Assert.Equal("3 machines · 3 outputs · 1 red · 2 amber — FOH-C: gone quiet 7 s", board.Headline);
        Assert.Equal("red", board.Light);

        // This machine, then FOH-C (gone: red) and FOH-B (live, its own amber), as STATE.fleet.nodes names them.
        Assert.Equal(["Demo node (this machine)", "FOH-C", "FOH-B"], board.Machines.Select(m => m.Name).ToArray());
        var self = board.Machines[0];
        Assert.True(self.IsSelf);
        Assert.Equal(("red", "SOLO"), (self.Light, self.Role));
        var gone = board.Machines[1];
        Assert.Equal(("red", "gone", "SOLO"), (gone.Light, gone.Heard, gone.Role));
        Assert.Equal((Brush("red"), "gone", "SOLO"), Rendered(window, gone));
        var live = board.Machines[2];
        Assert.Equal(("amber", "", "BACKUP HOT"), (live.Light, live.Heard, live.Role));
        Assert.Equal((Brush("amber"), "", "BACKUP HOT"), Rendered(window, live));

        // FOH-B goes quiet (stale): amber with "quiet"; heard again and green: green, no chip. FOH-C stays on the board, red.
        desk.Link.SetSection("fleet", """{"nodes":[{"id":"9f8e7d6c","heard":"stale","light":"green"}]}""");
        DeskHarness.Pump();
        Assert.Same(live, board.Machines[2]);
        Assert.Equal((Brush("amber"), "quiet", "BACKUP HOT"), Rendered(window, live));
        desk.Link.SetSection("fleet", """{"nodes":[{"id":"9f8e7d6c","heard":"live","light":"green"}]}""");
        DeskHarness.Pump();
        Assert.Equal((Brush("green"), "", "BACKUP HOT"), Rendered(window, live));
        Assert.Equal((Brush("red"), "gone", "SOLO"), Rendered(window, gone));

        // A card focuses the board on its machine; the lens chips and ‹ › are the board's view verbs (Problems is lit, as eye.lens says).
        desk.Link.ClearSent();
        var focus = Assert.Single(gone.Actions);
        await focus.Command.ExecuteAsync();
        Assert.Equal("FLEET FOCUS c0ffee00", desk.Verbs()[^1]);
        Assert.True(board.LensActions.Single(a => a.Label == "Problems").IsChecked);
        await board.LensActions.Single(a => a.Label == "Outputs").Command.ExecuteAsync();
        Assert.Equal("FLEET LENS OUTPUTS", desk.Verbs()[^1]);
        await board.Actions.Single(a => a.Id == "fleet.next").Command.ExecuteAsync();
        Assert.Equal("FLEET NEXT", desk.Verbs()[^1]);
        Assert.All(board.LensActions.Concat(board.Actions), a => Assert.Null(DeskVerbRuleTests.Fault(a.Verb)));
        Screens.Save(window, "health-fleet");
        window.Close();
    }

    [AvaloniaFact]
    public async Task OnAHotBackupFailoverSendsFailoverAndThisNodeThenHasTheShow()
    {
        await using var desk = new DeskHarness();
        StateFixtures.Load(desk.Link, "backup", "backup");
        var window = MainWindowTests.Open(desk);
        desk.Main.Drawers.Show("health", "fleet");
        DeskHarness.Pump();
        var board = desk.Main.Health.Fleet;
        var self = board.Self;
        Assert.Equal(("green", "BACKUP HOT"), (self.Light, self.Role));
        Assert.Contains("same show (rev 4), clock, transports and air as FOH-A", self.Detail, StringComparison.Ordinal);
        // The primary it mirrors has a card of its own, lit by the mirror's state.
        var primary = board.Machines.Single(m => m.Id == "0badc0de");
        Assert.Equal(("FOH-A", "PRIMARY", "green"), (primary.Name, primary.Role, primary.Light));

        // FAILOVER is on this node's card, live on a backup (the fleet's door lets it through); Stop backing up is in its menu only.
        Assert.Equal(["FAILOVER", "FLEET SOLO"], self.Actions.Select(a => a.Verb).ToArray());
        var border = window.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "MachineCard" && ReferenceEquals(b.DataContext, self));
        var buttons = border.GetVisualDescendants().OfType<ActionMenuButton>().ToList();
        var button = Assert.Single(buttons);
        Assert.Same(board.Failover, button.Action);
        var menu = new ContextMenu();
        DeskMenu.Fill(menu.Items, DeskMenu.GetActions(border)!);
        Assert.Equal([board.Failover, board.Solo], menu.Items.OfType<MenuItem>().Select(i => i.Tag).ToArray());
        Assert.True(button.IsEffectivelyEnabled);

        // It is held to confirm, as the module's key holds it (the catalogue row presses hold): a click sends nothing.
        Assert.All(new[] { board.Failover, board.Solo, board.Mirror }, a => Assert.True(a.IsHold));
        static string PressOf(string line) => WireGrammar.TryParse(line, out var w, out _) && VerbCatalog.Match(w) is { } m ? VerbParams.Press(m.Spec) : "";
        Assert.All(new[] { "FAILOVER", "FLEET SOLO", "FLEET BACKUP 9f8e7d6c HOT" }, l => Assert.Equal(PressShapes.Hold, PressOf(l)));
        Assert.Contains("hold to confirm", board.Failover.Tip, StringComparison.Ordinal);
        desk.Link.ClearSent();
        DeskHarness.Click(window, button);
        await board.Failover.Command.Last;
        Assert.False(button.Complete(600));
        Assert.Empty(desk.Verbs());

        // Pressed, then a STATE push while it is held (the node animates): the same button holds on, and held to its time it sends
        // exactly FAILOVER, once.
        var at = button.TranslatePoint(new Avalonia.Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value;
        Avalonia.Headless.HeadlessWindowExtensions.MouseDown(window, at, Avalonia.Input.MouseButton.Left, Avalonia.Input.RawInputModifiers.None);
        DeskHarness.Pump();
        Assert.True(button.IsHeld);
        var actions = self.Actions;
        desk.Link.SetSection("fleet", """{"headline":"FOH-B mirrors FOH-A: identical (pushed while held)"}""");
        DeskHarness.Pump();
        Assert.Same(actions, self.Actions);
        Assert.Same(button, border.GetVisualDescendants().OfType<ActionMenuButton>().Single());
        Assert.True(button.IsHeld);
        Assert.False(button.Complete(599));
        Assert.True(button.Complete(600));
        Avalonia.Headless.HeadlessWindowExtensions.MouseUp(window, at, Avalonia.Input.MouseButton.Left, Avalonia.Input.RawInputModifiers.None);
        await board.Failover.Command.Last;
        DeskHarness.Pump();
        Assert.Equal("FAILOVER", Assert.Single(desk.Verbs()));

        // The node took the show: its card says PRIMARY with the takeover's words, FAILOVER is gone from it, the door with it.
        Assert.Equal("PRIMARY", self.Role);
        Assert.Contains("takeover tk00000000 of FOH-A by Demo node (pressed) — epoch 1 committed", self.Detail, StringComparison.Ordinal);
        Assert.Empty(self.Actions);
        Assert.DoesNotContain(desk.Main.Banners, b => b.Id == "fleet");
        // Again: this node backs up no primary now, and the node refuses with its words.
        var again = await desk.Session.SendQuietAsync("FAILOVER", Ct);
        Assert.False(again.Ok);
        Assert.Equal("Demo node backs up no primary — FAILOVER runs on a hot backup (FLEET BACKUP <primary> first)", again.Text);
        window.Close();
    }

    [AvaloniaFact]
    public async Task APrimaryShowsItsBackupAndAFencedNodeOffersToMirrorTheNodeWithTheShow()
    {
        await using var desk = new DeskHarness();
        StateFixtures.Load(desk.Link, "backup", "primary");
        var window = MainWindowTests.Open(desk);
        desk.Main.Drawers.Show("health", "fleet");
        DeskHarness.Pump();
        var board = desk.Main.Health.Fleet;
        Assert.Equal("PRIMARY", board.Self.Role);
        Assert.Empty(board.Self.Actions);
        var backup = board.Machines.Single(m => m.Id == "9f8e7d6c");
        Assert.Equal(("FOH-B", "BACKUP HOT", "green", "FOH-B mirrors this node: identical"), (backup.Name, backup.Role, backup.Light, backup.Headline));
        Assert.Equal((Brush("green"), "", "BACKUP HOT"), Rendered(window, backup));

        StateFixtures.Load(desk.Link, "backup", "fenced");
        DeskHarness.Pump();
        Assert.Equal("FENCED", board.Self.Role);
        var mirror = Assert.Single(board.Self.Actions);
        Assert.Equal("FLEET BACKUP 9f8e7d6c HOT", mirror.Verb);
        Assert.Null(DeskVerbRuleTests.Fault(mirror.Verb));
        // The fleet's door lets a fenced node's own role change through.
        Assert.Null(mirror.Refusal);
        window.Close();
    }
}
