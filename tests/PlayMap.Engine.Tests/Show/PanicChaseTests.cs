using System.Text.Json;
using PlayMap.Core.Playback;
using PlayMap.Core.Wire;

namespace PlayMap.Engine.Tests.Show;

/// <summary>show × control: a panic's transport outcome holds whatever drives the playlist. A panic step that moves a chased
/// playlist's transport (BLACK, HARD, REWIND, NEXT) first disengages its chase — journalled, named in the panic's receipt and words,
/// and shown in STATE as disengaged by the panic — then runs; the code rolls on and the playlist stays where the panic put it, PANIC
/// CLEAR does not re-engage the chase, and TIMECODE ENGAGE does. A panic that leaves transports alone (RUN, SAFE, FREEZE) leaves the
/// chase engaged and following the code.</summary>
public sealed class PanicChaseTests
{
    private const long Second = 1_000_000_000;

    private static string Released(string outcome) =>
        $"Show stops chasing Rehearsal: disengaged by PANIC {outcome} — TIMECODE ENGAGE Show ON to follow the code again";

    private static string Disengaged(string outcome) => $"disengaged by PANIC {outcome} — TIMECODE ENGAGE Show ON to follow the code again";

    /// <summary>op_1 shows pl_show (A, B, C, ten seconds each; A starts at 01:00:00:00), chasing an internal 25 fps source rolling
    /// from 01:00:01:00: locked, A one second in.</summary>
    private static async Task<ShowRig> Chased()
    {
        var p = ShowRig.OneScreen();
        var list = p.Playlists[0];
        var rig = new ShowRig(p with { Playlists = [list with { Items = list.Items.SetItem(0, list.Items[0] with { TcIn = "01:00:00:00" }) }] });
        await rig.Ok("TIMECODE SOURCE ADD Rehearsal INTERNAL FORMAT 25");
        await rig.Ok("TIMECODE INTERNAL Rehearsal LOCATE 01:00:01:00");
        await rig.Ok("TIMECODE CHASE Show Rehearsal");
        await rig.Ok("TIMECODE INTERNAL Rehearsal PLAY");
        await rig.Engine.StepChasesAsync();
        Assert.Equal(("locked", true), (Chase(rig).GetProperty("state").GetString(), Chase(rig).GetProperty("engaged").GetBoolean()));
        Assert.Equal(("it_a", TransportState.Playing, Second), Lane(rig));
        return rig;
    }

    private static JsonElement Chase(ShowRig rig) => rig.State().GetProperty("timecode").GetProperty("chases")[0];

    private static JsonElement PlaylistChase(ShowRig rig) =>
        rig.State().GetProperty("playlists").EnumerateArray().First(p => p.GetProperty("id").GetString() == "pl_show").GetProperty("chase");

    /// <summary>The playlist's item, its transport and its position in the item's media at the show clock's now.</summary>
    private static (string?, TransportState, long) Lane(ShowRig rig)
    {
        var lane = rig.Engine.Playback.Lane("pl_show")!;
        return (lane.ItemId, lane.Clip.State, lane.Clip.PositionAt(rig.Now) - lane.Clip.InNs);
    }

    /// <summary>Moves the show clock, lets the lane's tick run the panic steps due, then the chase once more at that instant.</summary>
    private static async Task Advance(ShowRig rig, double ms)
    {
        rig.Advance(ms);
        await rig.Ticked();
        await rig.Engine.StepChasesAsync();
    }

    [Fact]
    public async Task BlackDisengagesTheChaseAtTheFadesEndAndThePauseHolds()
    {
        await using var rig = await Chased();
        await Advance(rig, 2000);
        Assert.Equal(("it_a", TransportState.Playing, 3 * Second), Lane(rig));

        var reply = await rig.Run("PANIC BLACK FADE 500");
        Assert.True(reply.Ok, reply.Payload);
        var released = Assert.Single(JsonDocument.Parse(reply.Payload!).RootElement.GetProperty("released").EnumerateArray());
        Assert.Equal(("pl_show", false, Released("BLACK")),
            (released.GetProperty("playlist").GetString(), released.GetProperty("done").GetBoolean(), released.GetProperty("words").GetString()));
        Assert.Equal(rig.Now + Second / 2, released.GetProperty("atLeaderNs").GetInt64());
        Assert.Contains("at the fade's end, " + Released("BLACK"), reply.Effect, StringComparison.Ordinal);
        // The fade runs on the chase: still engaged, still following the code.
        await Advance(rig, 250);
        Assert.True(Chase(rig).GetProperty("engaged").GetBoolean());
        Assert.Equal(("it_a", TransportState.Playing, 3 * Second + Second / 4), Lane(rig));

        // At the fade's end the chase lets go and the playlist pauses there.
        await Advance(rig, 250);
        Assert.Equal(("it_a", TransportState.Paused, 3 * Second + Second / 2), Lane(rig));
        var chase = Chase(rig);
        Assert.Equal(("disengaged", false, Disengaged("BLACK")),
            (chase.GetProperty("state").GetString(), chase.GetProperty("engaged").GetBoolean(), chase.GetProperty("words").GetString()));
        Assert.False(PlaylistChase(rig).GetProperty("engaged").GetBoolean());
        // Journalled in order: the chase let go, then the step ran.
        var journal = rig.Engine.Journal.Recent(50).ToList();
        var letGo = journal.FindIndex(r => r.Kind == "panic" && r.Text == "panic black: " + Released("BLACK"));
        var paused = journal.FindIndex(r => r.Kind == "panic" && r.Text.StartsWith("panic black: paused at ", StringComparison.Ordinal));
        Assert.True(letGo >= 0 && paused > letGo, $"journal: let go at {letGo}, paused at {paused}");

        // The code rolls on; the playlist stays where the panic put it.
        await Advance(rig, 2000);
        Assert.Equal(("it_a", TransportState.Paused, 3 * Second + Second / 2), Lane(rig));
        Assert.Equal("01:00:05:12", JsonDocument.Parse(await rig.Ok("TIMECODE STATUS Rehearsal")).RootElement.GetProperty("text").GetString());

        // PANIC CLEAR brings the air back, not the chase.
        await rig.Ok("PANIC CLEAR FADE 0");
        await Advance(rig, 100);
        Assert.Equal(("disengaged", false), (Chase(rig).GetProperty("state").GetString(), Chase(rig).GetProperty("engaged").GetBoolean()));
        Assert.Equal(("it_a", TransportState.Paused, 3 * Second + Second / 2), Lane(rig));

        // TIMECODE ENGAGE follows the code again: 01:00:05:15 is A at 5.6 s.
        await rig.Ok("TIMECODE ENGAGE Show ON");
        await rig.Engine.StepChasesAsync();
        Assert.Equal(("locked", true), (Chase(rig).GetProperty("state").GetString(), Chase(rig).GetProperty("engaged").GetBoolean()));
        Assert.Equal(("it_a", TransportState.Playing, 5 * Second + 6 * Second / 10), Lane(rig));
    }

    [Theory]
    [InlineData("HARD")]
    [InlineData("REWIND")]
    [InlineData("NEXT")]
    [InlineData("SECOND-PRESS")]
    public async Task EveryPanicThatMovesTransportHoldsAChasedPlaylist(string outcome)
    {
        await using var rig = await Chased();
        await Advance(rig, 2000);
        var start = rig.Now;
        CommandReply reply;
        if (outcome == "SECOND-PRESS")
        {
            // A second press inside the fade is the hard stop: the chase lets go now, not at the fade's end.
            await rig.Ok("PANIC RUN FADE 1000");
            await Advance(rig, 300);
            Assert.True(Chase(rig).GetProperty("engaged").GetBoolean());
            reply = await rig.Run("PANIC");
            outcome = "HARD";
        }
        else
        {
            reply = await rig.Run($"PANIC {outcome} FADE 500");
        }
        Assert.True(reply.Ok, reply.Payload);
        var released = Assert.Single(JsonDocument.Parse(reply.Payload!).RootElement.GetProperty("released").EnumerateArray());
        Assert.Equal(Released(outcome), released.GetProperty("words").GetString());
        // HARD lets go at once; the others at their fade's end.
        Assert.Equal(outcome == "HARD", released.GetProperty("done").GetBoolean());
        Assert.Contains(Released(outcome), reply.Effect, StringComparison.Ordinal);
        await Advance(rig, 500);
        var moved = rig.Now - start;
        var chase = Chase(rig);
        Assert.Equal(("disengaged", false, Disengaged(outcome)),
            (chase.GetProperty("state").GetString(), chase.GetProperty("engaged").GetBoolean(), chase.GetProperty("words").GetString()));
        Assert.Contains(rig.Engine.Journal.Recent(50), r => r.Kind == "panic" && r.Text == $"panic {outcome.ToLowerInvariant()}: {Released(outcome)}");
        var expected = outcome switch
        {
            // Paused where the press found it (the second press: 300 ms into the first panic's fade).
            "HARD" => ("it_a", TransportState.Paused, 3 * Second + (moved - Second / 2)),
            // The item that was on, cued at its in-point; the next item, cued.
            "REWIND" => ("it_a", TransportState.Cued, 0L),
            _ => ("it_b", TransportState.Cued, 0L),
        };
        Assert.Equal(expected, Lane(rig));
        // The code rolls on; the playlist holds.
        await Advance(rig, 2000);
        Assert.Equal(expected, Lane(rig));
        Assert.False(Chase(rig).GetProperty("engaged").GetBoolean());
    }

    [Theory]
    [InlineData("RUN")]
    [InlineData("SAFE")]
    [InlineData("FREEZE")]
    public async Task APanicThatLeavesTransportAloneLeavesTheChaseEngaged(string outcome)
    {
        await using var rig = await Chased();
        if (outcome == "SAFE")
            await rig.Ok("PANIC SET op_1 SAFE SAFE color:#FF0000");
        await Advance(rig, 2000);
        var reply = await rig.Run($"PANIC {outcome} FADE 500");
        Assert.True(reply.Ok, reply.Payload);
        Assert.Empty(JsonDocument.Parse(reply.Payload!).RootElement.GetProperty("released").EnumerateArray());
        await Advance(rig, 1000);
        // Locked and following the code: A at 4 s (01:00:04:00).
        var chase = Chase(rig);
        Assert.Equal(("locked", true, "locked to Rehearsal"),
            (chase.GetProperty("state").GetString(), chase.GetProperty("engaged").GetBoolean(), chase.GetProperty("words").GetString()));
        Assert.Equal(("it_a", TransportState.Playing, 4 * Second), Lane(rig));
        Assert.DoesNotContain(rig.Engine.Journal.Recent(50), r => r.Text.Contains("stops chasing", StringComparison.Ordinal));
    }
}
