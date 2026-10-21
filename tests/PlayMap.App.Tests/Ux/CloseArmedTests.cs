using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using PlayMap.App.ViewModels.Ux;

namespace PlayMap.App.Tests.Ux;

/// <summary>Closing the desk while the show is armed asks first, in words that say the show keeps playing on the node; the desk sends
/// nothing either way (no SHOW DISARM, no PANIC). Keep the desk open takes the question away; Close the desk closes it. Disarmed, the desk
/// closes at once.</summary>
public class CloseArmedTests
{
    [AvaloniaFact]
    public async Task ArmedTheDeskAsksBeforeClosingAndSendsNothing()
    {
        await using var desk = new DeskHarness();
        var window = MainWindowTests.Open(desk);
        var closed = false;
        window.Closed += (_, _) => closed = true;
        Assert.True((await desk.Session.SendAsync("SHOW ARM", Ct)).Ok);
        DeskHarness.Pump();
        desk.Link.ClearSent();

        window.Close();
        DeskHarness.Pump();
        Assert.False(closed);
        Assert.True(desk.Main.IsCloseAsked);
        Assert.True(MainWindowTests.Find<Border>(window, "CloseArmed").IsVisible);
        Assert.Equal(UxModule.CloseArmedWords, MainWindowTests.Find<TextBlock>(window, "CloseArmedWords").Text);
        Assert.Equal("The show keeps playing on the node", UxModule.CloseArmedWords);

        DeskHarness.Click(window, MainWindowTests.Find<Button>(window, "StayOpenButton"));
        Assert.False(desk.Main.IsCloseAsked);
        Assert.False(closed);

        window.Close();
        DeskHarness.Pump();
        Assert.True(desk.Main.IsCloseAsked);
        DeskHarness.Click(window, MainWindowTests.Find<Button>(window, "CloseAnywayButton"));
        Assert.True(closed);
        Assert.Empty(desk.Verbs());
    }

    [AvaloniaFact]
    public async Task DisarmedTheDeskClosesAtOnce()
    {
        await using var desk = new DeskHarness();
        var window = MainWindowTests.Open(desk);
        var closed = false;
        window.Closed += (_, _) => closed = true;
        window.Close();
        DeskHarness.Pump();
        Assert.True(closed);
        Assert.False(desk.Main.IsCloseAsked);
    }

    [AvaloniaFact]
    public async Task DisarmingWhileTheQuestionIsUpTakesItAway()
    {
        await using var desk = new DeskHarness();
        var window = MainWindowTests.Open(desk);
        Assert.True((await desk.Session.SendAsync("SHOW ARM", Ct)).Ok);
        DeskHarness.Pump();
        window.Close();
        DeskHarness.Pump();
        Assert.True(desk.Main.IsCloseAsked);
        Assert.True((await desk.Session.SendAsync("SHOW DISARM", Ct)).Ok);
        DeskHarness.Pump();
        Assert.False(desk.Main.IsCloseAsked);
        window.Close();
    }
}
