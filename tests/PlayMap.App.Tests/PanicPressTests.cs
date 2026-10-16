using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using PlayMap.Core.Wire;

namespace PlayMap.App.Tests;

/// <summary>The air's own controls send every press at once: a second PANIC is sent while the first still waits for its reply (the
/// node makes it HARD), and PANIC, its outcomes, Clear, BLACKOUT and the master stay live on a stale link, which still carries a
/// line.</summary>
public class PanicPressTests
{
    private static void Key(Window window, Key key)
    {
        window.KeyPress(key, RawInputModifiers.None, PhysicalKey.None, "");
        DeskHarness.Pump();
    }

    [AvaloniaFact]
    public async Task ASecondPanicIsSentWhileTheFirstStillWaitsForItsReply()
    {
        await using var desk = new DeskHarness();
        StateFixtures.Load(desk.Link, "show");
        await desk.Session.SendAsync("PANIC CLEAR", Ct);
        var window = MainWindowTests.Open(desk);
        var strip = desk.Main.ShowModule.Strip;
        var reply = new TaskCompletionSource();
        desk.Link.ReplyAfter = line => line.StartsWith("PANIC", StringComparison.Ordinal) ? reply.Task : Task.CompletedTask;
        desk.Link.ClearSent();

        Key(window, Avalonia.Input.Key.F12);
        Assert.False(strip.Panic.Command.Last.IsCompleted);
        Assert.True(strip.Panic.Command.CanExecute(null));
        Key(window, Avalonia.Input.Key.F12);
        Assert.Equal(["PANIC", "PANIC"], desk.Verbs());
        // The second press landed within the first one's fade: the node made it HARD.
        Assert.StartsWith("PANIC hard", Assert.Single(desk.Main.Banners, b => b.Id == "panic").Words, StringComparison.Ordinal);
        // The menu's outcomes and the banner's Clear go out the same way while replies wait.
        strip.PanicMenu.Single(a => a.Verb == "PANIC FREEZE").Command.Execute(null);
        DeskHarness.Pump();
        Assert.Equal("PANIC FREEZE", desk.Verbs()[^1]);
        Assert.False(strip.PanicMenu.Single(a => a.Verb == "PANIC FREEZE").Command.Last.IsCompleted);
        var clear = Assert.Single(desk.Main.Banners, b => b.Id == "panic").Actions[0];
        clear.Command.Execute(null);
        clear.Command.Execute(null);
        DeskHarness.Pump();
        Assert.Equal(["PANIC CLEAR FADE 1000", "PANIC CLEAR FADE 1000"], desk.Verbs().TakeLast(2));

        reply.SetResult();
        await DeskHarness.Drain(strip.PanicMenu);
        await DeskHarness.Drain([clear]);
        window.Close();
    }

    [AvaloniaFact]
    public async Task OnAStaleLinkTheAirsOwnControlsStayLiveAndSend()
    {
        await using var desk = new DeskHarness();
        StateFixtures.Load(desk.Link, "show");
        await desk.Session.SendAsync("PANIC CLEAR", Ct);
        var window = MainWindowTests.Open(desk);
        var strip = desk.Main.ShowModule.Strip;
        desk.Link.SetStatus(LinkStatus.Stale);
        DeskHarness.Pump();
        Assert.False(desk.Session.IsLive);
        Assert.True(desk.Session.CanSend);
        Assert.True(strip.Panic.IsEnabled);
        Assert.True(desk.Main.Blackout.IsEnabled);
        Assert.All(desk.Main.BlackoutMenu[1].Children, a => Assert.True(a.IsEnabled, a.Verb));
        Assert.All(strip.PanicMenu, a => Assert.True(a.IsEnabled, a.Verb));
        Assert.True(MainWindowTests.Find<Button>(window, "PanicButton").IsEffectivelyEnabled);
        // An edit is not the air's: it waits for a live link.
        Assert.False(desk.Main.Undo.IsEnabled);

        desk.Link.ClearSent();
        Key(window, Avalonia.Input.Key.F12);
        await DeskHarness.Drain([strip.Panic]);
        Assert.Equal(["PANIC"], desk.Verbs());
        var clear = Assert.Single(desk.Main.Banners, b => b.Id == "panic").Actions[0];
        Assert.True(clear.IsEnabled);
        await clear.Command.ExecuteAsync();
        DeskHarness.Pump();
        Assert.Equal("PANIC CLEAR FADE 1000", desk.Verbs()[^1]);
        window.KeyPress(Avalonia.Input.Key.B, RawInputModifiers.Control, PhysicalKey.None, "");
        DeskHarness.Pump();
        await DeskHarness.Drain([desk.Main.Blackout]);
        Assert.Equal("BLACKOUT ON", desk.Verbs()[^1]);

        // A link that is down carries nothing: PANIC greys.
        desk.Link.SetStatus(LinkStatus.Down);
        DeskHarness.Pump();
        Assert.False(strip.Panic.IsEnabled);
        window.Close();
    }
}
