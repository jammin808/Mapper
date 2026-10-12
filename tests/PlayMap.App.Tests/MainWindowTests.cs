using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PlayMap.App.ViewModels;
using PlayMap.App.Views;

namespace PlayMap.App.Tests;

/// <summary>The desk's window on the demo engine: it opens with the three workspaces, switches between them,
/// and its BLACKOUT button follows STATE.air.blackout rather than its own press.</summary>
public class MainWindowTests
{
    internal static MainWindow Open(DeskHarness desk, int width = 1600, int height = 960)
    {
        var window = new MainWindow { DataContext = desk.Main, Width = width, Height = height };
        window.Show();
        DeskHarness.Pump();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    internal static T Find<T>(Window window, string name) where T : Control =>
        window.GetVisualDescendants().OfType<T>().FirstOrDefault(c => c.Name == name)
        ?? throw new InvalidOperationException($"no {typeof(T).Name} named {name}");

    [AvaloniaFact]
    public async Task TheWindowOpensOnTheDemoAndShowsTheThreeWorkspaces()
    {
        await using var desk = new DeskHarness();
        var window = Open(desk);
        Assert.Equal("PLAY", Find<ToggleButton>(window, "PlayTab").Content);
        Assert.Equal("MAP", Find<ToggleButton>(window, "MapTab").Content);
        Assert.Equal("SCAN", Find<ToggleButton>(window, "ScanTab").Content);
        Assert.True(Find<PlayView>(window, "PlayView").IsEffectivelyVisible);
        Assert.Equal("Demo show", desk.Main.ProjectName);
        Screens.Save(window, "play-open");
        window.Close();
    }
}

/// <summary>The workspaces switch from the top bar and from the keyboard; BLACKOUT follows the engine.</summary>
public class WorkspaceAndBlackoutTests
{
    [AvaloniaFact]
    public async Task TheWorkspaceSwitchShowsOneWorkspaceAtATime()
    {
        await using var desk = new DeskHarness();
        var window = MainWindowTests.Open(desk);
        DeskHarness.Click(window, MainWindowTests.Find<ToggleButton>(window, "MapTab"));
        Assert.Equal(Workspace.Map, desk.Main.Workspace);
        Assert.True(MainWindowTests.Find<MapView>(window, "MapView").IsEffectivelyVisible);
        Assert.False(MainWindowTests.Find<PlayView>(window, "PlayView").IsEffectivelyVisible);
        Assert.True(MainWindowTests.Find<ToggleButton>(window, "MapTab").IsChecked);
        Assert.False(MainWindowTests.Find<ToggleButton>(window, "PlayTab").IsChecked);

        DeskHarness.Click(window, MainWindowTests.Find<ToggleButton>(window, "ScanTab"));
        Assert.Equal(Workspace.Scan, desk.Main.Workspace);
        Assert.True(MainWindowTests.Find<ScanView>(window, "ScanView").IsEffectivelyVisible);

        // A second press on the lit tab keeps it lit (a segmented switch never shows nothing).
        DeskHarness.Click(window, MainWindowTests.Find<ToggleButton>(window, "ScanTab"));
        Assert.True(MainWindowTests.Find<ToggleButton>(window, "ScanTab").IsChecked);

        Avalonia.Headless.HeadlessWindowExtensions.KeyPress(window, Avalonia.Input.Key.D1, Avalonia.Input.RawInputModifiers.Control, Avalonia.Input.PhysicalKey.Digit1, "1");
        DeskHarness.Pump();
        Assert.Equal(Workspace.Play, desk.Main.Workspace);
        Assert.Contains("VIEW op-op_1 SHOW output:op_1 480x270", desk.Link.SentLines);
        window.Close();
    }

    [AvaloniaFact]
    public async Task TheBlackoutButtonFollowsStateAirBlackoutNotItsOwnPress()
    {
        await using var desk = new DeskHarness();
        var window = MainWindowTests.Open(desk);
        var button = MainWindowTests.Find<Button>(window, "BlackoutButton");
        Assert.DoesNotContain("on", button.Classes);

        // Another peer (Patterns, an assistant) blacks the node out: the desk shows it.
        await desk.Link.SendAsync("BLACKOUT ON", Ct);
        DeskHarness.Pump();
        Assert.True(desk.Main.IsBlackout);
        Assert.Contains("on", button.Classes);
        Assert.Equal("Back on air", desk.Main.Blackout.Label);

        // The press sends the verb; the class changes only when STATE says so.
        desk.Link.ClearSent();
        DeskHarness.Click(window, button);
        await desk.Main.Blackout.Command.Last;
        Assert.Equal("BLACKOUT OFF", desk.Verbs()[^1]);
        DeskHarness.Pump();
        Assert.DoesNotContain("on", button.Classes);

        // A press the engine cannot honour leaves the button as STATE has it, and says why.
        desk.Link.SetStatus(PlayMap.Core.Wire.LinkStatus.Down);
        DeskHarness.Pump();
        Assert.False(desk.Main.Blackout.IsEnabled);
        desk.Link.SetStatus(PlayMap.Core.Wire.LinkStatus.Live);
        DeskHarness.Pump();
        await desk.Link.SendAsync("BLACKOUT ON", Ct);
        DeskHarness.Pump();
        await desk.Session.SendAsync("BLACKOUT ON", Ct);
        Assert.Equal(PlayMap.App.Link.ToastKind.Refused, desk.Session.LastToast!.Kind);
        Assert.StartsWith("Already", desk.Session.LastToast.Text, StringComparison.Ordinal);
        Assert.Contains("on", button.Classes);
        window.Close();
    }

    [AvaloniaFact]
    public async Task F1ShowsTheShortcutTableThatHandlesTheKeys()
    {
        await using var desk = new DeskHarness();
        var window = MainWindowTests.Open(desk);
        Avalonia.Headless.HeadlessWindowExtensions.KeyPress(window, Avalonia.Input.Key.F1, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.F1, "");
        DeskHarness.Pump();
        Assert.True(desk.Main.IsHelpOpen);
        var list = MainWindowTests.Find<ItemsControl>(window, "ShortcutList");
        Assert.True(list.IsEffectivelyVisible);
        Assert.Equal(desk.Main.Shortcuts.Entries.Count, list.ItemCount);
        Screens.Save(window, "help");
        Avalonia.Headless.HeadlessWindowExtensions.KeyPress(window, Avalonia.Input.Key.Escape, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.Escape, "");
        DeskHarness.Pump();
        Assert.False(desk.Main.IsHelpOpen);
        window.Close();
    }
}
