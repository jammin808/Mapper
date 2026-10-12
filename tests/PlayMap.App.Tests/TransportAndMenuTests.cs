using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using PlayMap.App.Controls;
using PlayMap.App.ViewModels;

namespace PlayMap.App.Tests;

/// <summary>A playlist card's transport buttons send the clip verbs ON that playlist (PLAYLIST GO when nothing
/// is loaded), and its right-click menu holds the same actions as its toolbar — the same labels, sending the
/// same verbs.</summary>
public class TransportAndMenuTests
{
    private static Border Card(Window window, string name) =>
        window.GetVisualDescendants().OfType<Border>().First(b => b.DataContext is PlaylistViewModel p && p.Name == name && DeskMenu.GetActions(b) is not null);

    private static List<Button> ToolbarButtons(Border card) =>
        card.GetVisualDescendants().OfType<ItemsControl>().First(i => i.Name == "CardToolbar").GetVisualDescendants().OfType<Button>().ToList();

    [AvaloniaFact]
    public async Task ThePlaylistTransportButtonsSendTheVideoAndPlaylistVerbs()
    {
        await using var desk = new DeskHarness();
        var window = MainWindowTests.Open(desk);
        var card = Card(window, "Show");
        var buttons = ToolbarButtons(card);
        var list = (PlaylistViewModel)card.DataContext!;

        DeskHarness.Click(window, buttons[1]);
        await DeskHarness.Drain(list.Actions);
        Assert.Equal("PLAYLIST GO pl_show", desk.Verbs()[^1]);
        Assert.Equal("Pause", list.Transport.PlayPause.Label);

        DeskHarness.Click(window, buttons[1]);
        await DeskHarness.Drain(list.Actions);
        Assert.Equal("VIDEO PAUSE ON pl_show", desk.Verbs()[^1]);

        DeskHarness.Click(window, buttons[1]);
        await DeskHarness.Drain(list.Actions);
        Assert.Equal("VIDEO PLAY ON pl_show", desk.Verbs()[^1]);

        DeskHarness.Click(window, buttons[3]);
        await DeskHarness.Drain(list.Actions);
        Assert.Equal("PLAYLIST NEXT pl_show", desk.Verbs()[^1]);
        Assert.Equal("Main loop", list.Transport.ItemLabel);

        DeskHarness.Click(window, buttons[0]);
        await DeskHarness.Drain(list.Actions);
        Assert.Equal("PLAYLIST PREV pl_show", desk.Verbs()[^1]);

        DeskHarness.Click(window, buttons[2]);
        await DeskHarness.Drain(list.Actions);
        Assert.Equal("VIDEO STOP ON pl_show", desk.Verbs()[^1]);

        DeskHarness.Click(window, buttons[4]);
        await DeskHarness.Drain(list.Actions);
        Assert.Equal("PLAYLIST LOOP pl_show ON", desk.Verbs()[^1]);
        Assert.True(desk.Link.CurrentProject.FindPlaylist("pl_show")!.Loop);
        window.Close();
    }

    [AvaloniaFact]
    public async Task TheMasterTransportAndTheSpaceKeyDriveTheFocusedPlaylist()
    {
        await using var desk = new DeskHarness();
        var window = MainWindowTests.Open(desk);
        Avalonia.Headless.HeadlessWindowExtensions.KeyPress(window, Key.Space, RawInputModifiers.None, PhysicalKey.Space, " ");
        await DeskHarness.Drain(desk.Main.Master.Actions);
        Assert.Equal("PLAYLIST GO pl_show", desk.Verbs()[^1]);
        Assert.True(desk.Main.Master.IsPlaying);
        Assert.Equal("Show Intro", desk.Main.FocusedName + " " + desk.Main.Master.ItemLabel);
        var buttons = MainWindowTests.Find<ItemsControl>(window, "MasterButtons").GetVisualDescendants().OfType<Button>().ToList();
        DeskHarness.Click(window, buttons[1]);
        await DeskHarness.Drain(desk.Main.Master.Actions);
        Assert.Equal("VIDEO PAUSE ON pl_show", desk.Verbs()[^1]);
        window.Close();
    }

    [AvaloniaFact]
    public async Task ARightClickMenuHoldsTheToolbarsActionsAndSendsTheSameVerbs()
    {
        await using var desk = new DeskHarness();
        var window = MainWindowTests.Open(desk);
        var card = Card(window, "Ambient");
        var list = (PlaylistViewModel)card.DataContext!;

        DeskHarness.Click(window, card, MouseButton.Right);
        var menu = card.ContextMenu;
        Assert.NotNull(menu);
        Assert.True(menu.IsOpen);
        var items = menu.Items.OfType<MenuItem>().ToList();

        foreach (var (button, action) in ToolbarButtons(card).Zip(list.Toolbar))
        {
            var item = items.Single(i => i.Tag == action);
            Assert.Equal(action.Label, item.Header);
            Assert.Same(button.Command, item.Command);
            Assert.Equal(action.Verb, ToolTip.GetTip(item));
        }
        menu.Close();

        // The same press through the menu and through the toolbar sends the same line.
        var loopItem = items.Single(i => i.Tag == list.Loop);
        loopItem.Command!.Execute(null);
        await DeskHarness.Drain(list.Actions);
        var fromMenu = desk.Verbs()[^1];
        desk.Link.ClearSent();
        await desk.Session.SendAsync("PLAYLIST LOOP pl_amb ON", Ct);
        DeskHarness.Pump();
        desk.Link.ClearSent();
        DeskHarness.Click(window, ToolbarButtons(card)[4]);
        await DeskHarness.Drain(list.Actions);
        Assert.Equal("PLAYLIST LOOP pl_amb OFF", fromMenu);
        Assert.Equal(fromMenu, desk.Verbs()[^1]);

        // The menu holds what the toolbar does not show, too (focus, sync, delete).
        Assert.Contains(items, i => (string?)i.Header == "Delete playlist" && i.Classes.Contains("danger"));
        Assert.Contains(items, i => (string?)i.Header == "Sync group" && i.Items.Count == 3);
        window.Close();
    }
}
