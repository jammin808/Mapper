using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using PlayMap.App.Controls;
using PlayMap.Core.Wire;

namespace PlayMap.App.Tests.Ux;

/// <summary>The Oops panel: opened from its button (or Ctrl+Alt+Z) it lists the node's undo steps newest first as UNDO LIST answers them,
/// with who and when; "Undo to here" on a step — its button and its right-click entry — sends UNDO TO that step, the node takes that step
/// and every newer one back, and the open panel reads the shorter list. For the blind owner the steps are its draft's.</summary>
public class OopsPanelTests
{
    private static readonly string[] Names = ["One", "Two", "Three", "Four", "Five"];

    private static async Task FiveEdits(DeskHarness desk)
    {
        foreach (var name in Names)
        {
            Assert.True((await desk.Session.SendAsync($"SET op_1 name \"{name}\"", Ct)).Ok);
            desk.Clock.AdvanceMs(1000);
        }
        DeskHarness.Pump();
    }

    [AvaloniaFact]
    public async Task TheStepsListNewestFirstAndUndoToHereTakesThatStepAndEveryNewerOneBack()
    {
        await using var desk = new DeskHarness();
        var window = MainWindowTests.Open(desk);
        await FiveEdits(desk);
        var oops = desk.Main.Ux.Oops;
        desk.Link.ClearSent();

        QuadToolTests.Key(window, Avalonia.Input.Key.Z, Avalonia.Input.RawInputModifiers.Control | Avalonia.Input.RawInputModifiers.Alt, Avalonia.Input.PhysicalKey.Z);
        Assert.True(oops.IsOpen);
        await oops.Fetching;
        DeskHarness.Pump();
        Assert.Equal("UNDO LIST 50", Assert.Single(desk.Verbs()));
        Assert.Equal(5, oops.Steps.Count);
        var numbers = oops.Steps.Select(s => s.Number).ToArray();
        Assert.Equal(numbers.OrderDescending(), numbers);
        Assert.True(oops.Steps[0].IsNext);
        Assert.All(oops.Steps, s => Assert.StartsWith("set op_1 name", s.Label, StringComparison.Ordinal));
        Assert.All(oops.Steps, s => Assert.StartsWith("PlayMapDesk · ", s.Meta, StringComparison.Ordinal));
        Assert.True(MainWindowTests.Find<ContentControl>(window, "OopsHost").IsVisible);
        Assert.Equal(5, window.GetVisualDescendants().OfType<ItemsControl>().Single(i => i.Name == "OopsSteps").ItemCount);

        // Each step's right-click is its Undo to here, the same line as its button.
        var third = oops.Steps[2];
        Assert.Equal("UNDO TO " + third.Number.ToString(System.Globalization.CultureInfo.InvariantCulture), third.UndoTo.Verb);
        Assert.Same(third.UndoTo, Assert.Single(third.MenuActions));
        Assert.True(WireGrammar.TryParse(third.UndoTo.Verb, out var parsed, out _) && VerbCatalog.Match(parsed)?.Spec.Name == "UNDO TO");
        desk.Link.ClearSent();
        await third.UndoTo.Command.ExecuteAsync();
        DeskHarness.Pump();
        await oops.Fetching;
        DeskHarness.Pump();
        Assert.Equal(["UNDO TO " + third.Number.ToString(System.Globalization.CultureInfo.InvariantCulture), "UNDO LIST 50"], desk.Verbs());
        Assert.Equal("Two", desk.Link.CurrentProject.Outputs.Single(o => o.Id == "op_1").Name);
        Assert.Equal(numbers[3..], oops.Steps.Select(s => s.Number));

        // UNDO TO the newest is one UNDO; an empty list says so.
        foreach (var left in new[] { 1, 0 })
        {
            await oops.Steps[0].UndoTo.Command.ExecuteAsync();
            DeskHarness.Pump();
            await oops.Fetching;
            DeskHarness.Pump();
            Assert.Equal(left, oops.Steps.Count);
        }
        Assert.Equal("Left projector", desk.Link.CurrentProject.Outputs.Single(o => o.Id == "op_1").Name);
        Assert.Equal("Nothing to undo", oops.Words);
        window.Close();
    }

    [AvaloniaFact]
    public async Task TheBlindOwnersHistoryIsItsDrafts()
    {
        await using var desk = new DeskHarness();
        var window = MainWindowTests.Open(desk);
        Assert.True((await desk.Session.SendAsync("SET op_1 name \"Live\"", Ct)).Ok);
        desk.Clock.AdvanceMs(1000);
        await desk.Main.ShowModule.Strip.ArmMenu.Single(a => a.Id == "show.blind").Command.ExecuteAsync();
        DeskHarness.Pump();
        await FiveEdits(desk);
        var oops = desk.Main.Ux.Oops;
        oops.IsOpen = true;
        await oops.Fetching;
        DeskHarness.Pump();
        try
        {
            Assert.True(oops.IsDraft);
            Assert.Equal("Oops — this desk's blind draft", oops.Title);
            Assert.Equal(5, oops.Steps.Count);
            await oops.Steps[2].UndoTo.Command.ExecuteAsync();
            DeskHarness.Pump();
            Assert.Equal("Live", desk.Link.CurrentProject.Outputs.Single(o => o.Id == "op_1").Name);
            await oops.Fetching;
            DeskHarness.Pump();
            Assert.Equal(2, oops.Steps.Count);
        }
        finally
        {
            DeskLookApply.Apply(Avalonia.Application.Current!, PlayMap.App.Infrastructure.DeskPalette.Dark);
            window.Close();
        }
    }
}
