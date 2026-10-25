using PlayMap.Net.Links;

namespace PlayMap.Net.Tests;

/// <summary>Stagewatch's screen commands as this node takes them: identify is one journal row that says nothing flashed, and
/// no view; redirect picks the timer's view with today's words; reload and disconnect keep theirs, the disconnect's naming the
/// link's hold; a type that is none of the four is no plan at all.</summary>
public class StagewatchScreenCommandsTests
{
    private static readonly TimeSpan Hold = new StagewatchLinkOptions { Host = "10.0.0.6", SelfId = "a1b2c3d4" }.DisconnectHold;

    private static StagewatchScreenCommand Command(string type, string to = "", string name = "", string id = "") => new(type, to, name, id);

    [Fact]
    public void AnIdentifyIsOneRowThatSaysNothingFlashedAndPicksNoView()
    {
        var plan = StagewatchScreenCommands.Plan(Command("identify", name: "Stage left", id: "a1b2c3d4"), Hold);

        Assert.NotNull(plan);
        Assert.Null(plan.View);
        Assert.Equal(
            "Stagewatch's operator asked this screen to identify itself — PlayMap flashes no output for a screen command (OUTPUT IDENTIFY on this node does)",
            plan.Row);
        // The same row whatever name or id Stagewatch gives the screen: the plan reads nothing from the peer into it, and the row's
        // asker is the one constant word.
        Assert.Equal(plan, StagewatchScreenCommands.Plan(Command("identify"), Hold));
        Assert.Equal("Stagewatch", StagewatchScreenCommands.Who);
    }

    [Fact]
    public void ARedirectPicksTheTimersViewInTodaysWords()
    {
        var plan = StagewatchScreenCommands.Plan(Command("redirect", to: "/layout?name=Keynote"), Hold);
        Assert.Equal(new StagewatchScreenPlan("Stagewatch's operator shows /layout?name=Keynote on this node's timer", "/layout?name=Keynote"), plan);

        var back = StagewatchScreenCommands.Plan(Command("redirect"), Hold);
        Assert.Equal(new StagewatchScreenPlan("Stagewatch's operator shows the default view on this node's timer", ""), back);
    }

    [Fact]
    public void AReloadAndADisconnectAreRowsInTodaysWordsTheDisconnectNamingTheLinksHold()
    {
        Assert.Equal(new StagewatchScreenPlan("Stagewatch's operator asked this screen to reload: its timer is read afresh from the next frame", null),
            StagewatchScreenCommands.Plan(Command("reload"), Hold));
        Assert.Equal(new StagewatchScreenPlan("Stagewatch's operator disconnected this screen; the link dials again in 30 s", null),
            StagewatchScreenCommands.Plan(Command("disconnect"), Hold));
        Assert.Equal("Stagewatch's operator disconnected this screen; the link dials again in 2.5 s",
            StagewatchScreenCommands.Plan(Command("disconnect"), TimeSpan.FromMilliseconds(2500))!.Row);
    }

    [Theory]
    [InlineData("")]
    [InlineData("state")]
    [InlineData("IDENTIFY")]
    [InlineData("name")]
    [InlineData("go")]
    public void ATypeThatIsNoneOfTheFourIsNoPlan(string type)
    {
        Assert.Null(StagewatchScreenCommands.Plan(Command(type, to: "/layout"), Hold));
    }
}
