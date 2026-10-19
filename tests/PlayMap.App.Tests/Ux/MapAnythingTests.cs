using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using PlayMap.App.ViewModels;

namespace PlayMap.App.Tests.Ux;

/// <summary>Map anything: while it is on, every control a touch would make learn is outlined — the buttons bound straight to an action
/// (ARM) as well as the action buttons — and a pointer press on one sends BIND LEARN for its line, the gate asked first, once: the mode
/// ends with it. Every other door of the control (its shortcut, its menu entry, an Esc rung) sends the line itself. The air and a panic
/// are never learnt by a touch: PANIC and Blackout act, with the mode on or off. Armed, the mode ends and is greyed with the node's
/// words; Esc ends it, above PANIC.</summary>
public class MapAnythingTests
{
    private static Button ButtonOf(Window window, DeskAction action) =>
        window.GetVisualDescendants().OfType<Button>().First(b => ReferenceEquals(b.DataContext, action) && b.IsEffectivelyVisible);

    private static void Key(Window window, Key key, RawInputModifiers mods = RawInputModifiers.None)
    {
        window.KeyPress(key, mods, PhysicalKey.None, "");
        DeskHarness.Pump();
    }

    /// <summary>A desk on the show fixture (Show Mode present), panic cleared, disarmed, in PLAY.</summary>
    private static async Task<DeskHarness> ShowDesk()
    {
        var desk = new DeskHarness();
        StateFixtures.Load(desk.Link, "show");
        await desk.Session.SendAsync("PANIC CLEAR", Ct);
        await desk.Session.SendAsync("SHOW DISARM", Ct);
        DeskHarness.Pump();
        Assert.False(desk.Main.ShowModule.Strip.IsArmed);
        return desk;
    }

    [AvaloniaFact]
    public async Task ATouchLearnsTheControlsLineOnceAndEveryOtherDoorSendsTheLine()
    {
        await using var desk = new DeskHarness();
        var window = MainWindowTests.Open(desk);
        var next = desk.Main.Master.Next;
        Assert.False(next.IsBindable);

        await desk.Main.Ux.MapAnything.Command.ExecuteAsync();
        DeskHarness.Pump();
        Assert.True(desk.Session.MapAnything);
        Assert.True(next.IsBindable);
        Assert.True(desk.Main.Undo.IsBindable);
        // Every control a touch would make learn is outlined, the buttons bound straight to their action among them; the air's and
        // panic's never are, nor a desk-only control, a secret's line or a binding's own line.
        Assert.Contains("bindable", ButtonOf(window, next).Classes);
        Assert.Contains("bindable", MainWindowTests.Find<ContentControl>(window, "UndoButton").GetVisualDescendants().OfType<Button>().First().Classes);
        Assert.Contains("bindable", MainWindowTests.Find<Button>(window, "ArmButton").Classes);
        Assert.DoesNotContain("bindable", MainWindowTests.Find<Button>(window, "PanicButton").Classes);
        Assert.DoesNotContain("bindable", MainWindowTests.Find<Button>(window, "BlackoutButton").Classes);
        Assert.False(desk.Main.Blackout.IsBindable);
        Assert.False(desk.Main.Ux.Oops.Toggle.IsBindable);
        Assert.False(DeskAction.TouchLearns("PANIC"));
        Assert.False(DeskAction.TouchLearns("PANIC CLEAR"));
        Assert.False(DeskAction.TouchLearns("BLACKOUT ON"));
        Assert.False(DeskAction.TouchLearns("MASTER 50"));
        Assert.False(DeskAction.Bindable("CREDENTIAL SET pj_x admin hunter2"));
        Assert.False(DeskAction.Bindable("BIND LEARN BLACKOUT ON"));
        Assert.True(DeskAction.TouchLearns("SHOW ARM"));

        // The shortcut is the line itself, the mode staying on.
        var line = next.Verb;
        desk.Link.ClearSent();
        Key(window, Avalonia.Input.Key.Right, RawInputModifiers.Control);
        await next.Command.Last;
        DeskHarness.Pump();
        Assert.Equal([line], desk.Verbs());
        Assert.True(desk.Session.MapAnything);

        // A touch learns the control's line, once: the mode ends, and the node is learning that line.
        desk.Link.ClearSent();
        DeskHarness.Click(window, ButtonOf(window, next));
        await next.LastLearn;
        DeskHarness.Pump();
        Assert.Equal(["BIND LEARN " + line], desk.Verbs());
        Assert.Null(DeskVerbRuleTests.Fault("BIND LEARN " + line));
        Assert.False(desk.Session.MapAnything);
        Assert.False(next.IsBindable);
        Assert.DoesNotContain("bindable", ButtonOf(window, next).Classes);
        var learning = JsonDocument.Parse(desk.Link.State.Json).RootElement.GetProperty("control").GetProperty("learning");
        Assert.Equal(line, learning.GetProperty("target").GetString());

        // The mode is over: the next touch is the line itself.
        desk.Link.ClearSent();
        DeskHarness.Click(window, ButtonOf(window, next));
        await next.Command.Last;
        DeskHarness.Pump();
        Assert.Equal([desk.Main.Master.Next.Verb], desk.Verbs());
        window.Close();
    }

    [AvaloniaFact]
    public async Task PanicAndTheAirActWithTheModeOnAndArmedTheModeEndsGreyedWithTheNodesWords()
    {
        await using var desk = await ShowDesk();
        var window = MainWindowTests.Open(desk);
        var strip = desk.Main.ShowModule.Strip;
        desk.Session.MapAnything = true;
        DeskHarness.Pump();

        // With the mode on, Blackout and PANIC act: their touch sends their own line, and the mode stays on.
        desk.Link.ClearSent();
        DeskHarness.Click(window, MainWindowTests.Find<Button>(window, "BlackoutButton"));
        await DeskHarness.Drain([desk.Main.Blackout]);
        Assert.Equal(["BLACKOUT ON"], desk.Verbs());
        Assert.True(desk.Session.State.Air.Blackout);
        Assert.True(desk.Session.MapAnything);
        DeskHarness.Click(window, MainWindowTests.Find<Button>(window, "BlackoutButton"));
        await DeskHarness.Drain([desk.Main.Blackout]);

        // Armed: the mode ends, greyed with the node's words, and Ctrl+Alt+M does nothing.
        await desk.Session.SendAsync("SHOW ARM", Ct);
        DeskHarness.Pump();
        Assert.True(strip.IsArmed);
        Assert.False(desk.Session.MapAnything);
        Assert.False(desk.Main.Ux.MapAnything.IsEnabled);
        Assert.Contains(desk.Session.GateWords("BIND LEARN OFF")!, desk.Main.Ux.MapAnything.Tip, StringComparison.Ordinal);
        Key(window, Avalonia.Input.Key.M, RawInputModifiers.Control | RawInputModifiers.Alt);
        Assert.False(desk.Session.MapAnything);

        // Armed, every door of PANIC sends PANIC: F12, the button, and Esc's rung in PLAY.
        desk.Main.Prefs.EscIsPanic = true;
        desk.Link.ClearSent();
        Key(window, Avalonia.Input.Key.F12);
        await DeskHarness.Drain([strip.Panic]);
        Assert.Equal(["PANIC"], desk.Verbs());
        Assert.Contains(desk.Main.Banners, b => b.Id == "panic");
        await desk.Session.SendAsync("PANIC CLEAR", Ct);
        DeskHarness.Pump();
        desk.Link.ClearSent();
        DeskHarness.Click(window, MainWindowTests.Find<Button>(window, "PanicButton"));
        await DeskHarness.Drain([strip.Panic]);
        Assert.Equal(["PANIC"], desk.Verbs());
        await desk.Session.SendAsync("PANIC CLEAR", Ct);
        DeskHarness.Pump();
        desk.Link.ClearSent();
        Key(window, Avalonia.Input.Key.Escape);
        await DeskHarness.Drain([strip.Panic]);
        Assert.Equal(["PANIC"], desk.Verbs());
        Assert.Contains(desk.Main.Banners, b => b.Id == "panic");
        Assert.DoesNotContain(desk.Link.SentLines, l => l.StartsWith("BIND", StringComparison.Ordinal));
        window.Close();
    }

    [AvaloniaFact]
    public async Task EscEndsTheModeAboveThePanicRung()
    {
        var now = 10_000L;
        await using var desk = new DeskHarness(nowMs: () => now);
        StateFixtures.Load(desk.Link, "show");
        await desk.Session.SendAsync("PANIC CLEAR", Ct);
        await desk.Session.SendAsync("SHOW DISARM", Ct);
        desk.Main.Prefs.EscIsPanic = true;
        var window = MainWindowTests.Open(desk);
        Key(window, Avalonia.Input.Key.M, RawInputModifiers.Control | RawInputModifiers.Alt);
        Assert.True(desk.Session.MapAnything);
        desk.Link.ClearSent();

        // Esc ends the mode and sends nothing; an Esc right after it does not reach PANIC (the guard).
        Key(window, Avalonia.Input.Key.Escape);
        Assert.False(desk.Session.MapAnything);
        Key(window, Avalonia.Input.Key.Escape);
        Assert.Empty(desk.Verbs());
        window.Close();
    }
}
