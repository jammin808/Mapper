using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using PlayMap.App.Controls;
using PlayMap.App.Infrastructure;
using PlayMap.App.ViewModels;

namespace PlayMap.App.Tests.Ux;

/// <summary>Blind made unmistakable: while STATE.show.blind names this desk's session the whole desk is drawn in the grey variant of its
/// theme (the status lights and the air's red kept), every pane that edits the draft carries the word BLIND and the window's frame
/// takes the blind colour; when the draft goes, so does all of it. Another desk's draft leaves this desk's colours and frame and names
/// its owner.</summary>
public class BlindLookTests
{
    private static Border Tag(Window window, Control pane) =>
        ((UserControl)pane).FindControl<Border>("BlindTag") ?? throw new InvalidOperationException($"{pane.GetType().Name} has no BlindTag");

    private static Control[] Panes(Window window) =>
    [
        MainWindowTests.Find<Views.PlayView>(window, "PlayView"),
        MainWindowTests.Find<Views.MapView>(window, "MapView"),
    ];

    [AvaloniaFact]
    public async Task ThisDesksDraftTurnsTheDeskGreyWithBlindOnEveryDraftPaneAndTheBlindFrame()
    {
        await using var desk = new DeskHarness();
        var window = MainWindowTests.Open(desk);
        var app = Application.Current!;
        Assert.Equal(DeskPalette.Dark.Tokens["DeskPanel"], DeskLookApply.Drawn(app, "DeskPanel"));

        await desk.Main.ShowModule.Strip.ArmMenu.Single(a => a.Id == "show.blind").Command.ExecuteAsync();
        DeskHarness.Pump();
        try
        {
            Assert.True(desk.Main.ShowModule.Strip.Blind.OwnsDraft);
            Assert.True(desk.Main.IsBlindMine);
            var grey = DeskPalette.Dark.Grey();
            Assert.Equal("dark+blind", desk.Main.Ux.Palette.Name);
            Assert.Equal(grey.Tokens["DeskPanel"], DeskLookApply.Drawn(app, "DeskPanel"));
            Assert.Equal(grey.Tokens["DeskAccent"], DeskLookApply.Drawn(app, "DeskAccent"));
            // A theme recolours neutrals, never a meaning: the status lights and the air's red stay.
            foreach (var token in DeskPalette.StatusTokens.Concat(DeskPalette.AirTokens))
                Assert.Equal(DeskPalette.Dark.Tokens[token], DeskLookApply.Drawn(app, token));
            Assert.Equal("blind", desk.Main.Frame);
            Assert.Equal(new Thickness(4), desk.Main.FrameThickness);
            Assert.NotEqual(LightBrushConverter.Brushes.For("red"), LightBrushConverter.Brushes.For("blind"));

            // Every pane that edits the draft says BLIND: PLAY, MAP (each as it shows), the inspector and the Outputs drawer.
            desk.Main.Workspace = Workspace.Play;
            DeskHarness.Pump();
            Assert.True(Tag(window, Panes(window)[0]).IsEffectivelyVisible);
            Assert.Equal("BLIND · edits go to this desk's draft", Tag(window, Panes(window)[0]).GetVisualDescendants().OfType<TextBlock>().Single().Text);
            desk.Main.Workspace = Workspace.Map;
            DeskHarness.Pump();
            Assert.True(Tag(window, Panes(window)[1]).IsEffectivelyVisible);
            var inspector = window.GetVisualDescendants().OfType<Views.InspectorView>().First(v => v.IsEffectivelyVisible);
            Assert.True(Tag(window, inspector).IsEffectivelyVisible);
            desk.Main.ToggleOutputs.Execute(null);
            DeskHarness.Pump();
            Assert.True(Tag(window, window.GetVisualDescendants().OfType<Views.OutputsDrawer>().First()).IsEffectivelyVisible);
            desk.Main.ToggleOutputs.Execute(null);
            DeskHarness.Pump();

            // The draft discarded: the theme's own colours, no BLIND, no frame.
            Assert.True((await desk.Session.SendAsync("BLIND DISCARD", Ct)).Ok);
            DeskHarness.Pump();
            Assert.False(desk.Main.IsBlindMine);
            Assert.Equal("dark", desk.Main.Ux.Palette.Name);
            Assert.Equal(DeskPalette.Dark.Tokens["DeskPanel"], DeskLookApply.Drawn(app, "DeskPanel"));
            Assert.False(Tag(window, Panes(window)[1]).IsEffectivelyVisible);
            Assert.Equal("", desk.Main.Frame);
        }
        finally
        {
            DeskLookApply.Apply(app, DeskPalette.Dark);
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task AnotherDesksDraftLeavesThisDesksColoursAndNamesItsOwner()
    {
        await using var desk = new DeskHarness();
        StateFixtures.Load(desk.Link, "show");
        await desk.Session.SendAsync("PANIC CLEAR", Ct);
        DeskHarness.Pump();
        var window = MainWindowTests.Open(desk);
        var app = Application.Current!;
        var blind = desk.Main.ShowModule.Strip.Blind;
        Assert.True(blind.IsPresent);
        Assert.False(blind.OwnsDraft);
        Assert.False(desk.Main.IsBlindMine);
        Assert.Equal("dark", desk.Main.Ux.Palette.Name);
        Assert.Equal(DeskPalette.Dark.Tokens["DeskPanel"], DeskLookApply.Drawn(app, "DeskPanel"));
        Assert.NotEqual("blind", desk.Main.Frame);
        foreach (var pane in Panes(window))
            Assert.False(Tag(window, pane).IsVisible);
        // Whose draft it is, in the chip's words.
        Assert.StartsWith("Blind editing by Desk:", blind.Words, StringComparison.Ordinal);
        Assert.DoesNotContain("(this desk)", blind.Words, StringComparison.Ordinal);
        Assert.True(MainWindowTests.Find<Button>(window, "BlindChip").IsVisible);
        window.Close();
    }
}
