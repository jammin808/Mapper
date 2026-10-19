using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;

namespace PlayMap.App.Tests.Ux;

/// <summary>Map anything: while it is on, every control whose line a binding can carry is highlighted (a desk-only control, a secret's
/// line and a binding's own line are not), and touching one sends BIND LEARN for its line instead of the line — once: the mode ends with
/// it, and the node is learning that line.</summary>
public class MapAnythingTests
{
    [AvaloniaFact]
    public async Task ATouchedControlLearnsItsLineOnceAndNothingElseIsSent()
    {
        await using var desk = new DeskHarness();
        var window = MainWindowTests.Open(desk);
        var blackout = desk.Main.Blackout;
        Assert.False(blackout.IsBindable);

        await desk.Main.Ux.MapAnything.Command.ExecuteAsync();
        DeskHarness.Pump();
        Assert.True(desk.Session.MapAnything);
        Assert.True(blackout.IsBindable);
        Assert.True(desk.Main.Master.Next.IsBindable);
        Assert.True(desk.Main.Undo.IsBindable);
        var undo = MainWindowTests.Find<ContentControl>(window, "UndoButton").GetVisualDescendants().OfType<Button>().First();
        Assert.Contains("bindable", undo.Classes);
        // A desk-only control, a secret's line and a binding's own line are not bindable.
        Assert.False(desk.Main.Ux.Oops.Toggle.IsBindable);
        Assert.False(PlayMap.App.ViewModels.DeskAction.Bindable("CREDENTIAL SET pj_x admin hunter2"));
        Assert.False(PlayMap.App.ViewModels.DeskAction.Bindable("BIND LEARN BLACKOUT ON"));
        Assert.True(PlayMap.App.ViewModels.DeskAction.Bindable("BLACKOUT ON"));

        desk.Link.ClearSent();
        await blackout.Command.ExecuteAsync();
        DeskHarness.Pump();
        Assert.Equal(["BIND LEARN BLACKOUT ON"], desk.Verbs());
        Assert.Null(DeskVerbRuleTests.Fault("BIND LEARN BLACKOUT ON"));
        Assert.False(desk.Session.MapAnything);
        Assert.False(blackout.IsBindable);
        Assert.False(desk.Session.State.Air.Blackout);
        var learning = JsonDocument.Parse(desk.Link.State.Json).RootElement.GetProperty("control").GetProperty("learning");
        Assert.Equal("BLACKOUT ON", learning.GetProperty("target").GetString());

        // The mode is over: the next press is the line itself.
        desk.Link.ClearSent();
        await blackout.Command.ExecuteAsync();
        DeskHarness.Pump();
        Assert.Equal(["BLACKOUT ON"], desk.Verbs());
        window.Close();
    }
}
