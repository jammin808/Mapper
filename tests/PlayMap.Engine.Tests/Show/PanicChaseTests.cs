using System.Text.Json;
using PlayMap.Core.Wire;

namespace PlayMap.Engine.Tests.Show;

/// <summary>show × control: a panic's transport outcome holds whatever drives the playlist. A panic step that moves a chased
/// playlist's transport (BLACK, HARD, REWIND, NEXT) first disengages its chase — journalled, named in the panic's receipt and words,
/// and shown in STATE as disengaged by the panic — then runs; the code rolls on and the playlist stays where the panic put it, PANIC
/// CLEAR does not re-engage the chase, and TIMECODE ENGAGE does. A panic that leaves transports alone (RUN, SAFE, FREEZE) leaves the
/// chase engaged and following the code. Every position is read from STATE.playlists.</summary>
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
        Assert.Equal(("it_a", "playing", 1000L), Published(rig));
        return rig;
    }

    private static JsonElement Chase(ShowRig rig) => rig.State().GetProperty("timecode").GetProperty("chases")[0];

    private static JsonElement PlaylistChase(ShowRig rig) =>
        rig.State().GetProperty("playlists").EnumerateArray().First(p => p.GetProperty("id").GetString() == "pl_show").GetProperty("chase");

    /// <summary>The playlist as STATE publishes it at the show clock's now: its item ("none"), its state and its position in the
    /// item in ms (-1 when nothing is loaded).</summary>
    private static (string, string, long) Published(ShowRig rig)
    {
        var p = rig.State().GetProperty("playlists").EnumerateArray().First(p => p.GetProperty("id").GetString() == "pl_show");
        var item = p.GetProperty("item");
        var position = p.GetProperty("positionMs");
        return (item.ValueKind == JsonValueKind.Null ? "none" : item.GetProperty("id").GetString()!, p.GetProperty("state").GetString()!,
            position.ValueKind == JsonValueKind.Null ? -1 : position.GetInt64());
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
        Assert.Equal(("it_a", "playing", 3000L), Published(rig));

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
        Assert.Equal(("it_a", "playing", 3250L), Published(rig));

        // At the fade's end the chase lets go and the playlist pauses there.
        await Advance(rig, 250);
        Assert.Equal(("it_a", "paused", 3500L), Published(rig));
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
        Assert.Equal(("it_a", "paused", 3500L), Published(rig));
        Assert.Equal("01:00:05:12", JsonDocument.Parse(await rig.Ok("TIMECODE STATUS Rehearsal")).RootElement.GetProperty("text").GetString());

        // PANIC CLEAR brings the air back, not the chase.
        await rig.Ok("PANIC CLEAR FADE 0");
        await Advance(rig, 100);
        Assert.Equal(("disengaged", false), (Chase(rig).GetProperty("state").GetString(), Chase(rig).GetProperty("engaged").GetBoolean()));
        Assert.Equal(("it_a", "paused", 3500L), Published(rig));

        // TIMECODE ENGAGE follows the code again: 01:00:05:15 is A at 5.6 s.
        await rig.Ok("TIMECODE ENGAGE Show ON");
        await rig.Engine.StepChasesAsync();
        Assert.Equal(("locked", true), (Chase(rig).GetProperty("state").GetString(), Chase(rig).GetProperty("engaged").GetBoolean()));
        Assert.Equal(("it_a", "playing", 5600L), Published(rig));
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
            "HARD" => ("it_a", "paused", (3 * Second + (moved - Second / 2)) / 1_000_000),
            // The item that was on, cued at its in-point; the next item, cued.
            "REWIND" => ("it_a", "cued", 0L),
            _ => ("it_b", "cued", 0L),
        };
        Assert.Equal(expected, Published(rig));
        // The code rolls on; the playlist holds.
        await Advance(rig, 2000);
        Assert.Equal(expected, Published(rig));
        Assert.False(Chase(rig).GetProperty("engaged").GetBoolean());
        Assert.DoesNotContain(rig.Engine.Journal.Recent(50), r => r.Kind == "panic" && !r.Ok);
    }

    /// <summary>A chase turned off on the line just before a HARD panic, with no tick of the lane between them, still guards the
    /// playlist: the panic lifts that guard, so the pause holds (nothing chases the playlist any more, so nothing is released).</summary>
    [Fact]
    public async Task AChaseTurnedOffJustBeforeAPanicDoesNotRefuseItsPause()
    {
        await using var rig = await Chased();
        await Advance(rig, 2000);
        // Both lines queue behind a held lane, in order, so they run one after the other.
        using var hold = new ManualResetEventSlim();
        var held = rig.Engine.Lane.RunAsync(() => hold.Wait(TimeSpan.FromSeconds(10)));
        var off = rig.Run("TIMECODE CHASE Show OFF");
        var panic = rig.Run("PANIC HARD");
        hold.Set();
        Assert.True(await held);
        Assert.True((await off).Ok);
        var reply = await panic;
        Assert.True(reply.Ok, reply.Payload);
        Assert.Empty(JsonDocument.Parse(reply.Payload!).RootElement.GetProperty("released").EnumerateArray());
        Assert.Equal(("it_a", "paused", 3000L), Published(rig));
        await Advance(rig, 1000);
        Assert.Equal(("it_a", "paused", 3000L), Published(rig));
        Assert.Empty(rig.State().GetProperty("timecode").GetProperty("chases").EnumerateArray());
        Assert.Contains(rig.Engine.Journal.Recent(50), r => r.Kind == "panic" && r.Text.StartsWith("panic hard: paused at ", StringComparison.Ordinal));
        Assert.DoesNotContain(rig.Engine.Journal.Recent(50), r => r.Kind == "panic" && !r.Ok);
    }

    /// <summary>Between two items (GAP BLACK) the lane holds only the chase's switch to the next item at its code: the panic's
    /// release leaves nothing loaded, so the next item does not start under the panic when its code comes.</summary>
    [Fact]
    public async Task BetweenItemsTheNextItemDoesNotStartUnderThePanic()
    {
        var p = ShowRig.OneScreen();
        var list = p.Playlists[0];
        var items = list.Items.SetItem(0, list.Items[0] with { TcIn = "01:00:00:00" }).SetItem(1, list.Items[1] with { TcIn = "01:00:20:00" });
        await using var rig = new ShowRig(p with { Playlists = [list with { Items = items }] });
        await rig.Ok("TIMECODE SOURCE ADD Rehearsal INTERNAL FORMAT 25");
        await rig.Ok("TIMECODE INTERNAL Rehearsal LOCATE 01:00:12:00");
        await rig.Ok("TIMECODE CHASE Show Rehearsal");
        await rig.Ok("TIMECODE INTERNAL Rehearsal PLAY");
        await rig.Engine.StepChasesAsync();
        Assert.Equal("outside", Chase(rig).GetProperty("state").GetString());
        var gap = rig.Engine.Playback.Lane("pl_show")!;
        Assert.Equal((false, null, "it_b"), (gap.IsIdle, gap.ItemId, gap.NextItemId));

        await rig.Ok("PANIC BLACK FADE 500");
        await Advance(rig, 500);
        Assert.Equal(("disengaged", false), (Chase(rig).GetProperty("state").GetString(), Chase(rig).GetProperty("engaged").GetBoolean()));
        Assert.Equal(("none", "idle", -1L), Published(rig));
        // 01:00:20:00 comes and goes: B does not start.
        await Advance(rig, 9000);
        Assert.Equal(("none", "idle", -1L), Published(rig));
        Assert.False(rig.Engine.IsAnimating);
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
        Assert.Equal(("it_a", "playing", 4000L), Published(rig));
        Assert.DoesNotContain(rig.Engine.Journal.Recent(50), r => r.Text.Contains("stops chasing", StringComparison.Ordinal));
    }

    /// <summary>A chase a panic disengaged comes back after a restart still disengaged by the panic, with the way back in its words.</summary>
    [Fact]
    public async Task AChaseDisengagedByAPanicSaysSoAfterARestart()
    {
        await using var rig = await Chased();
        await rig.Ok("PANIC HARD");
        Assert.Equal(Disengaged("HARD"), Chase(rig).GetProperty("words").GetString());
        var saved = rig.Engine.SaveModuleRecovery();
        await using var again = new ShowRig(rig.Engine.Store.Project);
        again.Engine.LoadModuleRecovery(saved);
        await again.Engine.StepChasesAsync();
        var chase = Chase(again);
        Assert.Equal(("disengaged", false, Disengaged("HARD")),
            (chase.GetProperty("state").GetString(), chase.GetProperty("engaged").GetBoolean(), chase.GetProperty("words").GetString()));
        // TIMECODE ENGAGE is still the way back, and the words go with the panic's reason.
        await again.Ok("TIMECODE ENGAGE Show ON");
        await again.Ok("TIMECODE ENGAGE Show OFF");
        Assert.Equal("disengaged — Show plays by hand", Chase(again).GetProperty("words").GetString());
    }
}
