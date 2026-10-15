using System.Text.Json;
using PlayMap.Core.Tc;
using PlayMap.Core.Time;
using PlayMap.Engine.Tests.Control;

namespace PlayMap.Engine.Tests.Tc;

/// <summary>STATE.timecode is the code as it is when STATE is read, with nothing playing: a rolling source's code and a chase's code
/// read a few frames apart differ (as TIMECODE STATUS says), while STATE's revision stays where it was — the code's frames never
/// move it. STATE is written again at most once per frame of code that moves, and not at all while the code stands still.</summary>
public sealed class TimecodeStateTests
{
    private const long S = 1_000_000_000;

    private static JsonElement Timecode(string state) => JsonDocument.Parse(state).RootElement.GetProperty("timecode").Clone();

    private static string SourceText(string state) => Timecode(state).GetProperty("sources")[0].GetProperty("text").GetString()!;

    private static async Task<string> StatusText(ControlRig rig, string source) =>
        JsonDocument.Parse((await rig.Ok("TIMECODE STATUS " + source)).Payload!).RootElement.GetProperty("text").GetString()!;

    /// <summary>Waits until the lane's own tick has run at the manual clock's instant, so what the node notes once after it starts
    /// (the air settled) is behind the reads that follow.</summary>
    private static Task Ticked(ControlRig rig, ManualClock clock)
    {
        var now = clock.NowNs;
        return ControlRig.Until(() => rig.Engine.ShowTickedNs >= now, "the lane ticked");
    }

    /// <summary>The code's playlist starts at 01:00:00:00 and ends ten seconds later: code at 02:00:00:00 plays nothing.</summary>
    private static async Task<ControlRig> RollingOutsideTheShow(ManualClock clock, bool chased)
    {
        var rig = await ControlRig.StartAsync(TimecodeEngineTests.Show(), clock: clock);
        await rig.Ok("TIMECODE SOURCE ADD Rehearsal INTERNAL FORMAT 25");
        await rig.Ok("TIMECODE INTERNAL Rehearsal LOCATE 02:00:00:00");
        if (chased)
            await rig.Ok("TIMECODE CHASE Show Rehearsal");
        await rig.Ok("TIMECODE INTERNAL Rehearsal PLAY");
        await Ticked(rig, clock);
        await rig.Engine.StepChasesAsync();
        return rig;
    }

    [Fact]
    public async Task StateReadsARollingInternalSourceWhileNothingPlays()
    {
        var clock = new ManualClock(5 * S);
        await using var rig = await RollingOutsideTheShow(clock, chased: false);
        var (first, revision) = rig.Engine.State();
        Assert.Equal(("rolling", "02:00:00:00"), (Timecode(first).GetProperty("sources")[0].GetProperty("state").GetString(), SourceText(first)));
        Assert.False(rig.Engine.IsAnimating);

        // Five frames of code later STATE reads what TIMECODE STATUS reads, and its revision has not moved.
        clock.AdvanceMs(200);
        await rig.Engine.StepChasesAsync();
        var (later, laterRevision) = rig.Engine.State();
        Assert.Equal("02:00:00:05", SourceText(later));
        Assert.Equal("02:00:00:05", await StatusText(rig, "Rehearsal"));
        Assert.Equal(revision, laterRevision);
        Assert.False(rig.Engine.IsAnimating);
    }

    [Fact]
    public async Task StateReadsAChaseOutsideItsItemsAndOneDisengagedByHandWhileNothingPlays()
    {
        var clock = new ManualClock(5 * S);
        await using var rig = await RollingOutsideTheShow(clock, chased: true);
        Assert.False(rig.Engine.IsAnimating);
        var before = Timecode(rig.Engine.State().Json).GetProperty("chases")[0].GetProperty("text").GetString();
        clock.AdvanceMs(200);
        await rig.Engine.StepChasesAsync();
        var engaged = rig.State();
        Assert.Equal(("02:00:00:00", "02:00:00:05"), (before, engaged.GetProperty("timecode").GetProperty("chases")[0].GetProperty("text").GetString()));
        Assert.Equal("02:00:00:05", Chase(engaged).GetProperty("text").GetString());

        await rig.Ok("TIMECODE ENGAGE Show OFF");
        await rig.Engine.StepChasesAsync();
        Assert.False(rig.Engine.IsAnimating);
        clock.AdvanceMs(400);
        await rig.Engine.StepChasesAsync();
        var off = rig.State();
        var chase = off.GetProperty("timecode").GetProperty("chases")[0];
        Assert.Equal((false, "02:00:00:15"), (chase.GetProperty("engaged").GetBoolean(), chase.GetProperty("text").GetString()));
        Assert.Equal("02:00:00:15", Chase(off).GetProperty("text").GetString());
        Assert.Equal("02:00:00:15", SourceText(off.GetRawText()));

        static JsonElement Chase(JsonElement state) =>
            state.GetProperty("playlists").EnumerateArray().First(p => p.GetProperty("id").GetString() == "pl_show").GetProperty("chase");
    }

    [Fact]
    public async Task StateReadsAnExternalSourceRollingWhileNothingPlays()
    {
        var clock = new ManualClock(5 * S);
        await using var rig = await ControlRig.StartAsync(TimecodeEngineTests.Show(), clock: clock);
        await rig.Ok("TIMECODE SOURCE ADD Desk ARTNET FORMAT 25");
        await Ticked(rig, clock);
        await rig.Engine.StepChasesAsync();
        var id = rig.Engine.Store.Project.Timecode.Sources[0].Id;
        Assert.True(TcFormat.Fps25.TryParseLabel("03:00:00:00", out var from));

        // A packet a frame, the lane ticking every 10 ms between them.
        async Task Roll(int frames)
        {
            for (var i = 0; i < frames; i++)
            {
                rig.Engine.TimecodeSources.Observe(id, new TcObservation(clock.NowNs, from++, 0, TcFormat.Fps25, 1, TcObsKind.Packet, 0, false));
                for (var t = 0; t < 4; t++)
                {
                    clock.AdvanceMs(10);
                    await rig.Engine.StepChasesAsync();
                }
            }
        }

        await Roll(25);
        var (first, revision) = rig.Engine.State();
        Assert.Equal(("rolling", "03:00:00:24"), (Timecode(first).GetProperty("sources")[0].GetProperty("state").GetString(), SourceText(first)));
        await Roll(5);
        var (later, laterRevision) = rig.Engine.State();
        Assert.Equal("03:00:01:04", SourceText(later));
        Assert.Equal("03:00:01:04", await StatusText(rig, "Desk"));
        Assert.Equal(revision, laterRevision);
        Assert.False(rig.Engine.IsAnimating);
    }

    [Fact]
    public async Task StateReadsAChaseOnCodeRunningBackwardsAsTheCodeIs()
    {
        var clock = new ManualClock(5 * S);
        await using var rig = await ControlRig.StartAsync(TimecodeEngineTests.Show(), clock: clock);
        await rig.Ok("TIMECODE SOURCE ADD Desk ARTNET FORMAT 25");
        await rig.Ok("TIMECODE CHASE Show Desk");
        await Ticked(rig, clock);
        await rig.Engine.StepChasesAsync();
        var id = rig.Engine.Store.Project.Timecode.Sources[0].Id;
        Assert.True(TcFormat.Fps25.TryParseLabel("03:00:00:00", out var from));

        // A packet a frame from the first, running forward (+1) or backward (−1), the lane ticking every 10 ms between them.
        async Task Roll(long first, int frames, int direction)
        {
            for (var i = 0; i < frames; i++)
            {
                rig.Engine.TimecodeSources.Observe(id, new TcObservation(clock.NowNs, first + i * direction, 0, TcFormat.Fps25, direction, TcObsKind.Packet, 0, false));
                for (var t = 0; t < 4; t++)
                {
                    clock.AdvanceMs(10);
                    await rig.Engine.StepChasesAsync();
                }
            }
        }

        await Roll(from, 25, 1);
        await Roll(from + 23, 5, -1);
        var state = rig.State();
        var chase = state.GetProperty("timecode").GetProperty("chases")[0];
        // The code ran back to 03:00:00:19: the chase says that code, as its source and TIMECODE STATUS do.
        Assert.Equal(("reverse", "03:00:00:19"), (chase.GetProperty("state").GetString(), chase.GetProperty("text").GetString()));
        Assert.Equal("03:00:00:19", SourceText(state.GetRawText()));
        Assert.Equal("03:00:00:19", await StatusText(rig, "Show"));
    }

    [Fact]
    public async Task StateIsWrittenOncePerFrameOfRollingCodeAndNotAgainWhileItStandsStill()
    {
        var clock = new ManualClock(5 * S);
        await using var rig = await RollingOutsideTheShow(clock, chased: true);

        // Rolling at 25 fps with the lane ticking every 10 ms: the ticks inside a frame read the STATE already written.
        var (frame, _) = rig.Engine.State();
        for (var tick = 1; tick <= 12; tick++)
        {
            clock.AdvanceMs(10);
            await rig.Engine.StepChasesAsync();
            var (json, _) = rig.Engine.State();
            if (tick % 4 == 0)
                Assert.NotSame(frame, json);
            else
                Assert.Same(frame, json);
            frame = json;
        }
        Assert.Equal("02:00:00:03", SourceText(frame));

        // Stopped, a second of ticks writes nothing again: STATE and its revision stay as they were.
        await rig.Ok("TIMECODE INTERNAL Rehearsal STOP");
        await rig.Engine.StepChasesAsync();
        var (still, revision) = rig.Engine.State();
        Assert.Equal(("parked", "02:00:00:03"), (Timecode(still).GetProperty("sources")[0].GetProperty("state").GetString(), SourceText(still)));
        for (var tick = 0; tick < 100; tick++)
        {
            clock.AdvanceMs(10);
            await rig.Engine.StepChasesAsync();
            var (json, rev) = rig.Engine.State();
            Assert.Same(still, json);
            Assert.Equal(revision, rev);
        }
    }
}
