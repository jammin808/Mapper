using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using PlayMap.App.Infrastructure;
using PlayMap.App.ViewModels;

namespace PlayMap.App.Tests;

/// <summary>The Esc ladder as the window drives it: a pattern task is cancelled in the engine's own state words; a cancel the gate
/// would refuse is not sent and Esc goes on down; while a field types Esc is the field's and only the overlays answer; anything that
/// closes (a drawer by its button, a right-click menu) holds PANIC off for the guard; the guard's clock never overflows.</summary>
public class EscLadderTests
{
    private static void Key(Window window, Key key)
    {
        window.KeyPress(key, RawInputModifiers.None, PhysicalKey.None, "");
        DeskHarness.Pump();
    }

    /// <summary>A desk on the show fixture, panic cleared, in PLAY with "Esc is panic" on: armed or in rehearsal.</summary>
    private static async Task<DeskHarness> ShowDesk(bool armed, Func<long> nowMs)
    {
        var desk = new DeskHarness(nowMs: nowMs);
        StateFixtures.Load(desk.Link, "show");
        await desk.Session.SendAsync("PANIC CLEAR", Ct);
        if (!armed)
            await desk.Session.SendAsync("SHOW DISARM", Ct);
        desk.Main.Prefs.EscIsPanic = true;
        DeskHarness.Pump();
        Assert.Equal(armed, desk.Main.ShowModule.Strip.IsArmed);
        return desk;
    }

    [AvaloniaTheory]
    [InlineData("scan", """{"id":"sc_1","state":"running","stage":"patterns","pattern":3,"patterns":20,"progress":0.15}""", "SCAN CANCEL")]
    [InlineData("align", """{"run":{"id":"ar1","group":"ag1","state":"running","step":"identify","progress":0.2}}""", "ALIGN CANCEL")]
    [InlineData("calibration", """{"task":{"id":"kt1","kind":"resolve","state":"capturing","projector":"pj_1","progress":0.1,"words":"capturing"}}""", "CALIBRATION CANCEL")]
    [InlineData("calibration", """{"task":{"id":"kt1","kind":"resolve","state":"matching","projector":"pj_1","progress":0.3,"words":"matching"}}""", "CALIBRATION CANCEL")]
    [InlineData("calibration", """{"task":{"id":"kt1","kind":"resolve","state":"solving","projector":"pj_1","progress":0.4,"words":"solving"}}""", "CALIBRATION CANCEL")]
    [InlineData("calibration", """{"task":{"id":"kt1","kind":"resolve","state":"verifying","projector":"pj_1","progress":0.9,"words":"verifying"}}""", "CALIBRATION CANCEL")]
    public async Task EscCancelsAPatternTaskInTheEnginesOwnStateWordsBeforePanic(string section, string json, string cancel)
    {
        var now = 10_000L;
        await using var desk = await ShowDesk(armed: false, () => now);
        desk.Link.SetSection(section, json);
        var window = MainWindowTests.Open(desk);
        desk.Link.ClearSent();
        Key(window, Avalonia.Input.Key.Escape);
        Assert.Equal([cancel], desk.Verbs());
        window.Close();
    }

    [AvaloniaFact]
    public async Task ArmedACancelTheGateRefusesIsNotSentAndEscGoesOnToPanic()
    {
        var now = 10_000L;
        await using var desk = await ShowDesk(armed: true, () => now);
        // A learn still waiting, and a scan still running, when the show armed: the show's policy holds both stops while armed.
        desk.Link.SetSection("control", """{"learning":{"line":"GO","untilS":30}}""");
        desk.Link.SetSection("scan", """{"id":"sc_1","state":"running","stage":"patterns","pattern":3,"patterns":20,"progress":0.15}""");
        var window = MainWindowTests.Open(desk);
        Assert.NotNull(desk.Session.GateWords("BIND LEARN OFF"));
        Assert.NotNull(desk.Session.GateWords("SCAN CANCEL"));
        desk.Link.ClearSent();
        Key(window, Avalonia.Input.Key.Escape);
        await DeskHarness.Drain([desk.Main.ShowModule.Strip.Panic]);
        Assert.Equal(["PANIC"], desk.Verbs());
        Assert.DoesNotContain(desk.Session.Toasts, t => t.Text.StartsWith("Not while armed", StringComparison.Ordinal));
        window.Close();
    }

    [AvaloniaFact]
    public async Task WhileAFieldTypesEscIsTheFieldsAndOnlyTheOverlaysAnswerIt()
    {
        var now = 10_000L;
        await using var desk = await ShowDesk(armed: false, () => now);
        desk.Link.SetSection("align", """{"run":{"id":"ar1","group":"ag1","state":"running","step":"identify","progress":0.2}}""");
        desk.Link.SetSection("control", """{"learning":{"line":"GO","untilS":30}}""");
        var window = MainWindowTests.Open(desk);
        var box = new TextBox();
        ((Panel)MainWindowTests.Find<Views.PlayView>(window, "PlayView").Parent!).Children.Add(box);
        DeskHarness.Pump();
        var fieldSawEsc = 0;
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Escape)
                fieldSawEsc++;
        };
        box.Focus();
        DeskHarness.Pump();
        desk.Link.ClearSent();

        // No task is cancelled, no learn stopped, no PANIC: the field gets its Esc.
        Key(window, Avalonia.Input.Key.Escape);
        Assert.Empty(desk.Verbs());
        Assert.Equal(1, fieldSawEsc);

        // An open drawer still closes, and the field still gets the key.
        desk.Main.IsHealthOpen = true;
        DeskHarness.Pump();
        box.Focus();
        DeskHarness.Pump();
        Key(window, Avalonia.Input.Key.Escape);
        Assert.False(desk.Main.Drawers.IsOpen);
        Assert.Equal(2, fieldSawEsc);
        Assert.Empty(desk.Verbs());
        window.Close();
    }

    [AvaloniaFact]
    public async Task EscRightAfterADrawerClosedByItsButtonDoesNotPanic()
    {
        var now = 10_000L;
        await using var desk = await ShowDesk(armed: false, () => now);
        var window = MainWindowTests.Open(desk);
        desk.Main.IsOutputsOpen = true;
        DeskHarness.Pump();
        DeskHarness.Click(window, MainWindowTests.Find<Button>(window, "CloseDrawer"));
        Assert.False(desk.Main.Drawers.IsOpen);
        desk.Link.ClearSent();
        now += 100;
        Key(window, Avalonia.Input.Key.Escape);
        now += EscapeLadder.GuardMs - 101;
        Key(window, Avalonia.Input.Key.Escape);
        Assert.Empty(desk.Verbs());
        now += 1;
        Key(window, Avalonia.Input.Key.Escape);
        await DeskHarness.Drain([desk.Main.ShowModule.Strip.Panic]);
        Assert.Equal(["PANIC"], desk.Verbs());
        window.Close();
    }

    [AvaloniaFact]
    public async Task TwoQuickEscsThatCloseARightClickMenuOrAFlyoutDoNotPanic()
    {
        var now = 10_000L;
        await using var desk = await ShowDesk(armed: false, () => now);
        var window = MainWindowTests.Open(desk);
        var arm = window.GetVisualDescendants().OfType<Button>().First(b => b.Name == "ArmButton");
        desk.Link.ClearSent();
        DeskHarness.Click(window, arm, MouseButton.Right);
        Assert.True(arm.ContextMenu?.IsOpen);
        // The first Esc is the menu's: it closes and nothing is sent. The second, a moment later, is the operator backing out.
        Key(window, Avalonia.Input.Key.Escape);
        Assert.False(arm.ContextMenu!.IsOpen);
        Assert.Empty(desk.Verbs());
        now += 50;
        Key(window, Avalonia.Input.Key.Escape);
        await DeskHarness.Drain([desk.Main.ShowModule.Strip.Panic]);
        Assert.Empty(desk.Verbs());
        now += EscapeLadder.GuardMs;
        Key(window, Avalonia.Input.Key.Escape);
        await DeskHarness.Drain([desk.Main.ShowModule.Strip.Panic]);
        Assert.Equal(["PANIC"], desk.Verbs());

        // A flyout (an "Add effect ▸" on a toolbar) that closes holds PANIC off the same way.
        await desk.Session.SendAsync("PANIC CLEAR", Ct);
        DeskHarness.Pump();
        desk.Link.ClearSent();
        now += 5_000;
        var flyout = new MenuFlyout();
        flyout.Items.Add(new MenuItem { Header = "Blur" });
        flyout.ShowAt(arm);
        DeskHarness.Pump();
        Assert.True(flyout.IsOpen);
        flyout.Hide();
        DeskHarness.Pump();
        now += 50;
        Key(window, Avalonia.Input.Key.Escape);
        await DeskHarness.Drain([desk.Main.ShowModule.Strip.Panic]);
        Assert.Empty(desk.Verbs());
        window.Close();
    }

    [Fact]
    public void TheGuardsClockNeverOverflowsHoweverLongTheMachineHasRun()
    {
        const long nanosecondClock = 1_000_000_000;
        // Past 2^63 / 1000 ticks of a nanosecond clock (about 106.7 days) ticks × 1000 no longer fits in a long.
        var edge = long.MaxValue / 1000;
        var before = EscapeLadder.ElapsedMs(edge - nanosecondClock, nanosecondClock);
        var after = EscapeLadder.ElapsedMs(edge + nanosecondClock, nanosecondClock);
        Assert.True(before > 0);
        Assert.Equal(2000, after - before);
        Assert.Equal(long.MaxValue / nanosecondClock * 1000 + long.MaxValue % nanosecondClock / 1_000_000, EscapeLadder.ElapsedMs(long.MaxValue, nanosecondClock));
        Assert.Equal(1234, EscapeLadder.ElapsedMs(12_340_000, 10_000_000));
    }
}
