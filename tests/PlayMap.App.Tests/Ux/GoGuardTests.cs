using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;

namespace PlayMap.App.Tests.Ux;

/// <summary>The GO guard made visible, on the desk's manual clock: with the node's MinGo at 500 ms, a GO the node accepts draws the red
/// border on every GO control of its playlist, and it goes at 500 ms; a GO the node's fence refuses flashes once — never a second time
/// within 333 ms — with the node's words in its tooltip and the attention bar, which go when the window has closed.</summary>
public class GoGuardTests
{
    private long _now = 10_000;

    private DeskHarness Desk() => new(nowMs: () => _now);

    /// <summary>Moves the desk's clock and the node's show clock together, then lets the desk's tick run.</summary>
    private void Advance(DeskHarness desk, long ms)
    {
        _now += ms;
        desk.Clock.AdvanceMs(ms);
        desk.Main.Tick();
        DeskHarness.Pump();
    }

    private static Button NextButton(Window window, DeskHarness desk) =>
        window.GetVisualDescendants().OfType<Button>().Single(b => b.DataContext == desk.Main.Master.Next && b.IsVisible);

    [AvaloniaFact]
    public async Task AnAcceptedGoBordersTheGoControlsForTheNodesMinGoAndARefusedGoFlashesOnce()
    {
        await using var desk = Desk();
        var window = MainWindowTests.Open(desk);
        Assert.True((await desk.Session.SendAsync("SHOW SET MINGO 500", Ct)).Ok);
        DeskHarness.Pump();
        var next = desk.Main.Master.Next;
        var playlist = desk.Session.State.FocusedPlaylist!.Id;
        Assert.False(next.IsGoGuarded);

        // GO: accepted, so the window opens for the node's 500 ms on this playlist's GO controls.
        desk.Link.ClearSent();
        await desk.Main.Master.Next.Command.ExecuteAsync();
        DeskHarness.Pump();
        Assert.Equal("PLAYLIST NEXT " + playlist, Assert.Single(desk.Verbs()));
        Assert.True(next.IsGoGuarded);
        Assert.True(desk.Main.Master.Prev.IsGoGuarded);
        Assert.Contains("goGuard", NextButton(window, desk).Classes);
        Assert.False(desk.Main.Master.Stop.IsGoGuarded);
        Assert.Equal(500, desk.Main.Ux.Guard.DueInMs);

        // Inside the window the node refuses a second GO: one flash, its words in the tooltip and the bar.
        Advance(desk, 100);
        await next.Command.ExecuteAsync();
        DeskHarness.Pump();
        Assert.Equal(1, desk.Main.Ux.Guard.Flashes);
        Assert.True(next.IsGoFlash);
        Assert.Contains("goFlash", NextButton(window, desk).Classes);
        var words = desk.Main.Ux.Guard.RefusedWords;
        Assert.NotNull(words);
        Assert.StartsWith("Double GO protection: 0.10 s since the last GO on", words, StringComparison.Ordinal);
        Assert.Contains(words, next.Tip, StringComparison.Ordinal);
        var banner = Assert.Single(desk.Main.Banners, b => b.Id == "go-guard");
        Assert.Equal("GO refused — " + words, banner.Words);
        Assert.Equal("amber", banner.Light);

        // Refused again within 333 ms of the flash: no second flash.
        Advance(desk, 200);
        await next.Command.ExecuteAsync();
        DeskHarness.Pump();
        Assert.Equal(1, desk.Main.Ux.Guard.Flashes);
        Assert.True(next.IsGoFlash);
        // The flash is one event of 333 ms from the first refusal.
        Advance(desk, 132);
        Assert.True(next.IsGoFlash);
        Advance(desk, 1);
        Assert.False(next.IsGoFlash);
        Assert.DoesNotContain("goFlash", NextButton(window, desk).Classes);

        // At 499 ms the border is still up; at 500 ms it goes.
        Advance(desk, 66);
        Assert.True(next.IsGoGuarded);
        Advance(desk, 1);
        Assert.False(next.IsGoGuarded);
        Assert.False(desk.Main.Master.Prev.IsGoGuarded);
        Assert.DoesNotContain("goGuard", NextButton(window, desk).Classes);
        // The words of the last refusal stay a flash's length after it (300 + 333 ms), then go.
        Assert.NotNull(desk.Main.Ux.Guard.RefusedWords);
        Advance(desk, 132);
        Assert.NotNull(desk.Main.Ux.Guard.RefusedWords);
        Advance(desk, 1);
        Assert.Null(desk.Main.Ux.Guard.RefusedWords);
        Assert.DoesNotContain(desk.Main.Banners, b => b.Id == "go-guard");
        Assert.DoesNotContain("Double GO protection", next.Tip, StringComparison.Ordinal);

        // The window has closed: the node takes the next GO.
        desk.Link.ClearSent();
        await next.Command.ExecuteAsync();
        DeskHarness.Pump();
        Assert.True(next.IsGoGuarded);
        Assert.Equal(1, desk.Main.Ux.Guard.Flashes);
        window.Close();
    }

    /// <summary>The red border is drawn on the button's presenter, so it holds under the pointer and while pressed (where Fluent's own
    /// states set the presenter's border), and over Map anything's accent outline.</summary>
    [AvaloniaFact]
    public async Task TheGuardsRedBorderHoldsUnderThePointerAndOverMapAnything()
    {
        await using var desk = Desk();
        var window = MainWindowTests.Open(desk);
        Assert.True((await desk.Session.SendAsync("SHOW SET MINGO 500", Ct)).Ok);
        DeskHarness.Pump();
        await desk.Main.Master.Next.Command.ExecuteAsync();
        DeskHarness.Pump();
        var button = NextButton(window, desk);
        Assert.Contains("goGuard", button.Classes);
        var red = PlayMap.App.Infrastructure.DeskPalette.Dark.Tokens["LightRed"];
        Assert.Equal(red, BorderOf(button));

        // The pointer on GO: Fluent's :pointerover sets the presenter's border; the guard's stays.
        var centre = button.TranslatePoint(new Avalonia.Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value;
        Avalonia.Headless.HeadlessWindowExtensions.MouseMove(window, centre, Avalonia.Input.RawInputModifiers.None);
        DeskHarness.Pump();
        Assert.True(button.IsPointerOver);
        Assert.Equal(red, BorderOf(button));

        // Map anything on: the guarded GO is still red, not the accent.
        desk.Session.MapAnything = true;
        DeskHarness.Pump();
        Assert.Contains("bindable", button.Classes);
        Assert.Equal(red, BorderOf(button));
        desk.Session.MapAnything = false;
        window.Close();
    }

    private static uint BorderOf(Button button)
    {
        var presenter = button.GetVisualDescendants().OfType<Avalonia.Controls.Presenters.ContentPresenter>().First(p => p.Name == "PART_ContentPresenter");
        var color = Assert.IsAssignableFrom<Avalonia.Media.ISolidColorBrush>(presenter.BorderBrush).Color;
        return ((uint)color.R << 16) | ((uint)color.G << 8) | color.B;
    }

    [Fact]
    public void RefusalsFasterThanTwoHertzFlashNoFasterThanTwoHertz()
    {
        // Refusals at 0, 340 and 680 ms of the desk's clock: the second falls 340 ms after the first flash began, so it adds none; the
        // third, 680 ms after it, is the second flash. No two flash starts are within 500 ms.
        long now = 0;
        var guard = new PlayMap.App.ViewModels.Ux.GoGuard(() => now);
        var starts = new List<long>();
        guard.Changed += () =>
        {
            if (guard.Flashes > starts.Count)
                starts.Add(now);
        };
        foreach (var at in new long[] { 0, 340, 680 })
        {
            now = at;
            guard.Tick();
            guard.Replied("PLAYLIST NEXT pl_show", ok: false, "ERR double GO protection: 0.10 s since the last GO on Show", 1000);
        }
        Assert.Equal(2, guard.Flashes);
        Assert.Equal([0L, 680L], starts);
        Assert.All(starts.Zip(starts.Skip(1)), p => Assert.True(p.Second - p.First >= PlayMap.App.Infrastructure.StatusShapes.MinAlarmPeriodMs));
        // A refusal that is no GO, or no fence refusal, never flashes.
        now = 2000;
        guard.Replied("BLACKOUT ON", ok: false, "ERR double GO protection", 1000);
        guard.Replied("PLAYLIST NEXT pl_show", ok: false, "ERR no such playlist", 1000);
        Assert.Equal(2, guard.Flashes);
    }

    [AvaloniaFact]
    public async Task WithNoMinGoNoGoDrawsTheGuardAndOtherLinesNeverDo()
    {
        await using var desk = Desk();
        var window = MainWindowTests.Open(desk);
        Assert.True((await desk.Session.SendAsync("SHOW SET MINGO 0", Ct)).Ok);
        DeskHarness.Pump();
        await desk.Main.Master.Next.Command.ExecuteAsync();
        DeskHarness.Pump();
        Assert.False(desk.Main.Master.Next.IsGoGuarded);
        Assert.Null(desk.Main.Ux.Guard.DueInMs);
        Assert.Null(PlayMap.App.ViewModels.Ux.GoGuard.GoPlaylist("BLACKOUT ON"));
        Assert.Equal("pl_show", PlayMap.App.ViewModels.Ux.GoGuard.GoPlaylist("VIDEO RESTART ON pl_show"));
        Assert.Equal("pl_show", PlayMap.App.ViewModels.Ux.GoGuard.GoPlaylist("PLAYLIST GO pl_show it_a"));
        window.Close();
    }
}
