using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using PlayMap.App.Link;

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

    /// <summary>STATE.show.lastGo as the node writes it: one playlist's last GO, from the session named.</summary>
    private static string LastGo(string playlist, long atUnixMs, string session, string transport, string by) =>
        new System.Text.Json.Nodes.JsonObject
        {
            ["lastGo"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject
            {
                ["playlist"] = playlist, ["leaderNs"] = 5_000_000_000, ["at"] = atUnixMs, ["session"] = session, ["transport"] = transport, ["by"] = by,
            }),
        }.ToJsonString();

    private static long PushedAt(DeskHarness desk) => desk.Session.State.Root.GetProperty("at").GetInt64();

    /// <summary>A GO from another client (Companion here) reaches this desk only as STATE.show.lastGo: its playlist's GO controls draw the
    /// red border for what is left of the node's MinGo, the GO's age read on the node's clock (the row's <c>at</c> against the push's), so
    /// nothing of the node's clock is shared; the same row in a later push opens nothing again, a GO older than the window opens nothing,
    /// and another playlist's GO leaves this one's controls alone. Nothing is sent.</summary>
    [AvaloniaFact]
    public async Task AGoFromAnotherClientBordersThisDesksGoControlsForWhatIsLeftOfItsWindow()
    {
        await using var desk = Desk();
        var window = MainWindowTests.Open(desk);
        Assert.True((await desk.Session.SendAsync("SHOW SET MINGO 500", Ct)).Ok);
        DeskHarness.Pump();
        var next = desk.Main.Master.Next;
        var playlist = desk.Session.State.FocusedPlaylist!.Id;
        Assert.False(next.IsGoGuarded);

        // Companion's GO, 200 ms before the node wrote the push that carries it: 300 ms of the node's 500 are left.
        desk.Link.ClearSent();
        desk.Link.SetSection("show", LastGo(playlist, PushedAt(desk) - 200, "c1", "companion", "Companion: FOH deck"));
        DeskHarness.Pump();
        Assert.True(next.IsGoGuarded);
        Assert.True(desk.Main.Master.Prev.IsGoGuarded);
        Assert.False(desk.Main.Master.Stop.IsGoGuarded);
        Assert.Contains("goGuard", NextButton(window, desk).Classes);
        Assert.Equal(300, desk.Main.Ux.Guard.DueInMs);
        Assert.Empty(desk.Verbs());

        // The same row again 100 ms later on the desk's clock, in a push the node wrote at the same instant (a late delivery, the node's
        // clock not moved): a row seen once opens nothing again, so the window still closes 300 ms after it opened, not 300 ms from now.
        var pushedAt = PushedAt(desk);
        _now += 100;
        desk.Main.Tick();
        DeskHarness.Pump();
        desk.Link.SetSection("show", "{\"words\":\"the same GO again\"}");
        DeskHarness.Pump();
        Assert.Equal(pushedAt, PushedAt(desk));
        Assert.True(next.IsGoGuarded);
        Assert.Equal(200, desk.Main.Ux.Guard.DueInMs);
        Advance(desk, 199);
        Assert.True(next.IsGoGuarded);
        Advance(desk, 1);
        Assert.False(next.IsGoGuarded);
        Assert.DoesNotContain("goGuard", NextButton(window, desk).Classes);

        // A later push still carrying that GO opens nothing again.
        desk.Link.SetSection("show", "{\"words\":\"a later push\"}");
        DeskHarness.Pump();
        Assert.False(next.IsGoGuarded);
        Assert.Null(desk.Main.Ux.Guard.DueInMs);

        // A GO as old as the window opens nothing; another playlist's GO guards that playlist's controls, not this one's.
        desk.Link.SetSection("show", LastGo(playlist, PushedAt(desk) - 500, "w1", "wire", "Remote desk"));
        DeskHarness.Pump();
        Assert.False(next.IsGoGuarded);
        Assert.Null(desk.Main.Ux.Guard.DueInMs);
        desk.Link.SetSection("show", LastGo("pl_elsewhere", PushedAt(desk), "w1", "wire", "Remote desk"));
        DeskHarness.Pump();
        Assert.False(next.IsGoGuarded);
        Assert.True(desk.Main.Ux.Guard.IsOpen("PLAYLIST GO pl_elsewhere"));
        Assert.Empty(desk.Verbs());
        window.Close();
    }

    /// <summary>STATE's <c>at</c> as the link holds it now (the push a desk attaching now reads first).</summary>
    private static long LinkAt(FakeEngineLink link)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(link.State.Json);
        return doc.RootElement.GetProperty("at").GetInt64();
    }

    /// <summary>A show block with the node's MinGo of 500 ms and one playlist's last GO, as the node writes them.</summary>
    private static string ShowWithGo(string playlist, long leaderNs, long atUnixMs) =>
        new System.Text.Json.Nodes.JsonObject
        {
            ["minGoMs"] = 500,
            ["lastGo"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject
            {
                ["playlist"] = playlist, ["leaderNs"] = leaderNs, ["at"] = atUnixMs, ["session"] = "c1", ["transport"] = "companion",
                ["by"] = "Companion: FOH deck",
            }),
        }.ToJsonString();

    /// <summary>The first push a desk reads on a link may be the STATE the node wrote last, long before it reached the desk (a quiet node
    /// writes none), so the GO rows it carries are taken as seen and draw no border, however young they look against that push: on
    /// attach, on another node attached in its place, and on the first push after the link was down. A GO published after that first
    /// push draws the border for what is left of the window. Nothing is sent.</summary>
    [AvaloniaFact]
    public async Task AGoTheNodeNamedBeforeTheDeskAttachedDrawsNoGuard()
    {
        var playlist = "";
        await using var desk = new DeskHarness(setup: link =>
        {
            playlist = link.CurrentProject.Playlists[0].Id;
            link.SetSection("show", ShowWithGo(playlist, 5_000_000_000, LinkAt(link)));
        }, nowMs: () => _now);
        var window = MainWindowTests.Open(desk);
        var next = desk.Main.Master.Next;
        Assert.Equal(playlist, desk.Session.State.FocusedPlaylist!.Id);
        Assert.Equal(PushedAt(desk), desk.Session.State.Root.GetProperty("show").GetProperty("lastGo")[0].GetProperty("at").GetInt64());
        Assert.False(next.IsGoGuarded);
        Assert.Null(desk.Main.Ux.Guard.DueInMs);

        // A GO the node publishes after that first push: the border for what is left of the 500 ms.
        desk.Link.SetSection("show", ShowWithGo(playlist, 6_000_000_000, PushedAt(desk) - 100));
        DeskHarness.Pump();
        Assert.True(next.IsGoGuarded);
        Assert.Equal(400, desk.Main.Ux.Guard.DueInMs);
        Advance(desk, 400);
        Assert.False(next.IsGoGuarded);

        // Another node, whose first push names a GO on the same playlist at its own instant: seen, no border.
        await using var other = new FakeEngineLink(desk.Link.CurrentProject, clock: desk.Clock) { NodeId = "demo0002" };
        other.SetSection("show", ShowWithGo(playlist, 7_000_000_000, LinkAt(other)));
        desk.Main.Attach(other, "Node B");
        DeskHarness.Pump();
        Assert.Equal("demo0002", desk.Session.NodeId);
        Assert.False(next.IsGoGuarded);
        Assert.Null(desk.Main.Ux.Guard.DueInMs);

        // The link drops and comes back; the first push after it names a GO made while it was down: seen, no border. The next GO draws it.
        other.SetStatus(PlayMap.Core.Wire.LinkStatus.Down);
        DeskHarness.Pump();
        other.SetStatus(PlayMap.Core.Wire.LinkStatus.Live);
        DeskHarness.Pump();
        other.SetSection("show", ShowWithGo(playlist, 8_000_000_000, LinkAt(other)));
        DeskHarness.Pump();
        Assert.False(next.IsGoGuarded);
        Assert.Null(desk.Main.Ux.Guard.DueInMs);
        other.SetSection("show", ShowWithGo(playlist, 9_000_000_000, LinkAt(other)));
        DeskHarness.Pump();
        Assert.True(next.IsGoGuarded);
        Assert.Equal(500, desk.Main.Ux.Guard.DueInMs);
        Assert.Empty(desk.Verbs());
        Assert.DoesNotContain(other.SentLines, l => PlayMap.App.ViewModels.Ux.GoGuard.GoPlaylist(l) is not null);
        window.Close();
    }

    /// <summary>The demo node publishes an accepted GO in STATE.show.lastGo as the node does: the row has the node's keys (show.json's,
    /// written by the engine), the desk's own session, the GO's leader time and the push's wall time; a GO its fence refuses leaves the
    /// row as it was.</summary>
    [AvaloniaFact]
    public async Task TheDemoPublishesAnAcceptedGoAsTheNodeDoes()
    {
        await using var desk = Desk();
        Assert.True((await desk.Session.SendAsync("SHOW SET MINGO 500", Ct)).Ok);
        DeskHarness.Pump();
        var playlist = desk.Session.State.FocusedPlaylist!.Id;
        var leaderNs = desk.Clock.NowNs;
        await desk.Main.Master.Next.Command.ExecuteAsync();
        DeskHarness.Pump();
        var row = Assert.Single(desk.Session.State.Root.GetProperty("show").GetProperty("lastGo").EnumerateArray());
        var node = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(StateFixtures.Folder, "show.json"))).RootElement
            .GetProperty("show").GetProperty("lastGo")[0];
        Assert.Equal(node.EnumerateObject().Select(p => p.Name), row.EnumerateObject().Select(p => p.Name));
        Assert.Equal(playlist, row.GetProperty("playlist").GetString());
        Assert.Equal(leaderNs, row.GetProperty("leaderNs").GetInt64());
        Assert.Equal(PushedAt(desk), row.GetProperty("at").GetInt64());
        Assert.Equal(desk.Session.SessionId, row.GetProperty("session").GetString());
        Assert.Equal(desk.Session.Transport, row.GetProperty("transport").GetString());
        Assert.Equal(FakeEngineLink.ClientName, row.GetProperty("by").GetString());

        Advance(desk, 100);
        Assert.False((await desk.Session.SendAsync("PLAYLIST NEXT " + playlist, Ct)).Ok);
        DeskHarness.Pump();
        Assert.Equal(leaderNs, Assert.Single(desk.Session.State.Root.GetProperty("show").GetProperty("lastGo").EnumerateArray())
            .GetProperty("leaderNs").GetInt64());
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
