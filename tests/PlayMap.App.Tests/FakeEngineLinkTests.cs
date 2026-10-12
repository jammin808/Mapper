using PlayMap.App.Link;
using PlayMap.App.State;
using PlayMap.Core.Model;
using PlayMap.Core.Time;

namespace PlayMap.App.Tests;

/// <summary>The demo engine runs the generic edits and the transport verbs with the engine's meaning: an edit
/// lands in the project and the mirror, a verb that would change nothing is refused with the reason, and
/// STATE says what plays.</summary>
public class FakeEngineLinkTests
{
    [Fact]
    public async Task SetChangesAFieldAndUndoPutsItBack()
    {
        await using var link = new FakeEngineLink(clock: new ManualClock());
        var r = await link.SendAsync("SET sf_wall name \"Lobby wall\"", Ct);
        Assert.True(r.Ok, r.Text);
        Assert.Equal("Lobby wall", link.CurrentProject.FindSurface("sf_wall")!.Name);
        Assert.Equal("Lobby wall", ProjectJson.Deserialize(link.Project.Json).FindSurface("sf_wall")!.Name);
        Assert.True((await link.SendAsync("UNDO", Ct)).Ok);
        Assert.Equal("Back wall", link.CurrentProject.FindSurface("sf_wall")!.Name);
    }

    [Fact]
    public async Task AddAnswersTheNewIdAndRemoveTakesItAway()
    {
        await using var link = new FakeEngineLink(clock: new ManualClock());
        var r = await link.SendAsync("ADD surface " + ProjectJson.SerializeEntity(new Surface("", "Door", "ms_wall", Transform3.Identity, [],
            SurfaceContent.Empty, 1, [], true, false, "#FFFFFF", [])), Ct);
        Assert.True(r.Ok, r.Text);
        Assert.StartsWith("sf", r.Text, StringComparison.Ordinal);
        Assert.NotNull(link.CurrentProject.FindSurface(r.Text));
        Assert.True((await link.SendAsync("REMOVE " + r.Text, Ct)).Ok);
        Assert.Null(link.CurrentProject.FindSurface(r.Text));
    }

    [Fact]
    public async Task AVerbThatWouldChangeNothingIsRefusedWithTheReason()
    {
        await using var link = new FakeEngineLink(clock: new ManualClock());
        Assert.True((await link.SendAsync("BLACKOUT ON", Ct)).Ok);
        var again = await link.SendAsync("BLACKOUT ON", Ct);
        Assert.False(again.Ok);
        Assert.Contains("already", again.Text, StringComparison.Ordinal);
        Assert.True(StateSnapshot.Parse(link.State.Json).Air.Blackout);
    }

    [Fact]
    public async Task GoPlaysTheFirstItemAndTheClockMovesThePosition()
    {
        var clock = new ManualClock(1_000_000_000);
        await using var link = new FakeEngineLink(clock: clock);
        Assert.True((await link.SendAsync("PLAYLIST GO pl_show", Ct)).Ok);
        clock.AdvanceMs(2500);
        link.Tick();
        var s = StateSnapshot.Parse(link.State.Json);
        var show = s.FindPlaylist("pl_show")!;
        Assert.Equal("playing", show.State);
        Assert.Equal("it_intro", show.Item!.Id);
        Assert.InRange(show.PositionMs, 2400, 2600);
        Assert.Equal("Intro.mp4", s.Video!.File);
        var pause = await link.SendAsync("VIDEO PAUSE ON pl_show", Ct);
        Assert.True(pause.Ok, pause.Text);
        Assert.False((await link.SendAsync("VIDEO PAUSE ON pl_show", Ct)).Ok);
    }
}
