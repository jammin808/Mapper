using System.Text.Json;
using PlayMap.Core.Model;
using PlayMap.Core.Tc;
using PlayMap.Core.Time;
using PlayMap.Engine.Tests.Control;

namespace PlayMap.Engine.Tests.Tc;

/// <summary>TIMECODE SET &lt;playlist&gt; JUMP governs the chase: its sources' code is judged by the chase's jump threshold, so a step of
/// the code past it relocates the picture at once (STATE's lastJump says from where to where) and a step within it is drift, which
/// the picture follows as the fit takes it in; AUTO is max(2 frames, 5σ). STATE and TIMECODE STATUS say the threshold in force, and
/// it is kept in the project.</summary>
public sealed class ChaseJumpTests
{
    private const long S = 1_000_000_000;
    private const long FrameNs = 40_000_000;

    /// <summary>An Art-Net source at 25 fps from 01:00:00:12, chased by Show (Song 1 from 01:00:00:00 for 10 s), one packet a frame
    /// sent by hand.</summary>
    private sealed class Desk(ControlRig rig, ManualClock clock, string id)
    {
        private long _frame = Frame("01:00:00:12");
        private long _sentAt;

        private static long Frame(string label) => TcFormat.Fps25.TryParseLabel(label, out var f) ? f : throw new ArgumentException(label);

        public static async Task<Desk> StartAsync(ControlRig rig, ManualClock clock)
        {
            await rig.Ok("TIMECODE SOURCE ADD Desk ARTNET FORMAT 25");
            await rig.Ok("TIMECODE CHASE Show Desk");
            await rig.Engine.StepChasesAsync();
            return new Desk(rig, clock, rig.Engine.Store.Project.Timecode.Sources[0].Id);
        }

        /// <summary>A packet a frame, the lane ticking every 10 ms between them; <paramref name="each"/> looks after every frame.</summary>
        public async Task Roll(int frames, Action? each = null)
        {
            for (var i = 0; i < frames; i++)
            {
                _sentAt = clock.NowNs;
                rig.Engine.TimecodeSources.Observe(id, new TcObservation(_sentAt, _frame++, 0, TcFormat.Fps25, 1, TcObsKind.Packet, 0, false));
                for (var t = 0; t < 4; t++)
                {
                    clock.AdvanceMs(10);
                    await rig.Engine.StepChasesAsync();
                }
                each?.Invoke();
            }
        }

        /// <summary>The code steps ahead by some frames at once (the next packet carries it).</summary>
        public void Step(int frames) => _frame += frames;

        /// <summary>The picture's code now less the code the desk sends now (− = behind it).</summary>
        public long PictureOff()
        {
            var lane = rig.Engine.Playback.Lane("pl_show")!;
            Assert.Equal("it_a", lane.ItemId);
            var codeNow = TcFormat.Fps25.Ns(_frame - 1) + (clock.NowNs - _sentAt);
            return lane.Clip.Anchor.MediaAt(rig.Engine.LeaderNowNs) - (codeNow - TcFormat.Fps25.Ns(Frame("01:00:00:00")));
        }

        public JsonElement Chase() => rig.State().GetProperty("timecode").GetProperty("chases")[0];

        public string? LastJump() => Chase().GetProperty("lastJump").GetString();
    }

    /// <summary>Each step comes once the fit's window (1.5 s) is full of the code before it.</summary>
    [Fact]
    public async Task AStepPastTheChasesJumpThresholdRelocatesAtOnceAndOneWithinItIsDrift()
    {
        var clock = new ManualClock(5 * S);
        await using var rig = await ControlRig.StartAsync(TimecodeEngineTests.Show(), clock: clock);
        var desk = await Desk.StartAsync(rig, clock);
        await desk.Roll(40);
        Assert.Equal("locked", desk.Chase().GetProperty("state").GetString());
        Assert.InRange(desk.PictureOff(), -2_000_000, 2_000_000);

        // JUMP 200 ms: a three-frame step is drift — three frames on the picture is still on the line it was on; once the window
        // has taken the step in it is on the code, still locked, with no jump said.
        await rig.Ok("TIMECODE SET Show JUMP 200");
        desk.Step(3);
        await desk.Roll(3);
        Assert.InRange(desk.PictureOff(), -3 * FrameNs - 2_000_000, -3 * FrameNs + 2_000_000);
        await desk.Roll(40);
        Assert.InRange(desk.PictureOff(), -2_000_000, 2_000_000);
        Assert.Equal(("locked", null), (desk.Chase().GetProperty("state").GetString(), desk.LastJump()));

        // AUTO (2 frames): the same step is a jump — relocated by the third frame, and said.
        await rig.Ok("TIMECODE SET Show JUMP AUTO");
        desk.Step(3);
        await desk.Roll(3);
        Assert.InRange(desk.PictureOff(), -2_000_000, 2_000_000);
        var jumped = desk.LastJump();
        Assert.NotNull(jumped);

        // AUTO rides a one-frame step as drift; JUMP 20 ms relocates on the same step at once.
        await desk.Roll(40);
        desk.Step(1);
        await desk.Roll(3);
        Assert.InRange(desk.PictureOff(), -FrameNs - 2_000_000, -FrameNs + 2_000_000);
        await desk.Roll(40);
        Assert.InRange(desk.PictureOff(), -2_000_000, 2_000_000);
        Assert.Equal(jumped, desk.LastJump());
        await rig.Ok("TIMECODE SET Show JUMP 20");
        desk.Step(1);
        await desk.Roll(3);
        Assert.InRange(desk.PictureOff(), -2_000_000, 2_000_000);
        Assert.NotEqual(jumped, desk.LastJump());
        Assert.Equal("locked", desk.Chase().GetProperty("state").GetString());
    }

    [Fact]
    public async Task TheJumpThresholdInForceIsInStateAndKeptInTheProject()
    {
        var clock = new ManualClock(5 * S);
        await using var rig = await ControlRig.StartAsync(TimecodeEngineTests.Show(), clock: clock);
        var desk = await Desk.StartAsync(rig, clock);
        Assert.Equal((80.0, true), Threshold(desk.Chase()));

        Assert.Equal("Show: a jump is code more than 150 ms off its line, relocated at once; less is drift", (await rig.Ok("TIMECODE SET Show JUMP 150")).Effect);
        Assert.Equal(150_000_000, rig.Engine.Store.Project.FindPlaylist("pl_show")!.Chase!.JumpNs);
        Assert.Equal((150.0, false), Threshold(desk.Chase()));
        Assert.Equal((150.0, false), Threshold(JsonDocument.Parse((await rig.Ok("TIMECODE STATUS Show")).Payload!).RootElement));
        Assert.Equal("Show is already so", await rig.Refused("TIMECODE SET Show JUMP 150"));
        Assert.Equal("JUMP <1-10000 ms>|AUTO", await rig.Refused("TIMECODE SET Show JUMP 0"));
        Assert.Equal("JUMP <1-10000 ms>|AUTO", await rig.Refused("TIMECODE SET Show JUMP 20000"));

        // The project as saved and opened again: the chase judges its code at 150 ms — a three-frame step is drift there.
        var reopened = ProjectJson.Deserialize(ProjectJson.Serialize(rig.Engine.Store.Project));
        Assert.Equal(150_000_000, reopened.FindPlaylist("pl_show")!.Chase!.JumpNs);
        var clock2 = new ManualClock(5 * S);
        await using var again = await ControlRig.StartAsync(reopened, clock: clock2);
        await again.Engine.StepChasesAsync();
        var desk2 = new Desk(again, clock2, reopened.Timecode.Sources[0].Id);
        Assert.Equal((150.0, false), Threshold(desk2.Chase()));
        await desk2.Roll(40);
        desk2.Step(3);
        await desk2.Roll(3);
        Assert.InRange(desk2.PictureOff(), -3 * FrameNs - 2_000_000, -3 * FrameNs + 2_000_000);

        Assert.Equal("Show: a jump is code more than 2 frames (or 5σ) off its line, relocated at once; less is drift", (await again.Ok("TIMECODE SET Show JUMP AUTO")).Effect);
        Assert.Equal(0, again.Engine.Store.Project.FindPlaylist("pl_show")!.Chase!.JumpNs);
        Assert.Equal((80.0, true), Threshold(desk2.Chase()));
        Assert.Equal("Show is already so", await again.Refused("TIMECODE SET Show JUMP AUTO"));

        static (double, bool) Threshold(JsonElement chase) => (chase.GetProperty("jumpMs").GetDouble(), chase.GetProperty("jumpAuto").GetBoolean());
    }

    /// <summary>JUMP 500 ms: a six-frame step is drift, but steeper than the fit can glide within its slope bounds — the chase's own
    /// line (its code judged at 500 ms) reads varispeed out of range while the source, judged at auto, rolls on the new code. The
    /// chase says so in its words until the window holds only the new code, then it is locked on the code with no jump said.</summary>
    [Fact]
    public async Task AStepTooSteepForTheFitToGlideIsSaidInTheChasesWords()
    {
        var clock = new ManualClock(5 * S);
        await using var rig = await ControlRig.StartAsync(TimecodeEngineTests.Show(), clock: clock);
        var desk = await Desk.StartAsync(rig, clock);
        await desk.Roll(40);
        await rig.Ok("TIMECODE SET Show JUMP 500");
        desk.Step(6);
        var seen = new List<(string Chase, string Words, string Source)>();
        await desk.Roll(50, () =>
        {
            var timecode = rig.State().GetProperty("timecode");
            var chase = timecode.GetProperty("chases")[0];
            seen.Add((chase.GetProperty("state").GetString()!, chase.GetProperty("words").GetString()!, timecode.GetProperty("sources")[0].GetProperty("state").GetString()!));
        });
        var unlocked = seen.Where(s => s.Chase != "locked").ToList();
        Assert.NotEmpty(unlocked);
        Assert.All(unlocked, s =>
        {
            Assert.Equal(("acquiring", "rolling"), (s.Chase, s.Source));
            Assert.StartsWith("varispeed out of range (", s.Words, StringComparison.Ordinal);
        });
        Assert.Equal(("locked", "locked to Desk"), (seen[^1].Chase, seen[^1].Words));
        Assert.InRange(desk.PictureOff(), -2_000_000, 2_000_000);
        Assert.Null(desk.LastJump());
    }

    /// <summary>TIMECODE SET and TIMECODE INTERNAL take numbers: "NaN" is none, and is refused with the range rather than taken as 0
    /// (JUMP AUTO, no freewheel, no delay) — or, for RATE, as a rate STATE cannot write.</summary>
    [Fact]
    public async Task ATimecodeSettingThatIsNotANumberIsRefused()
    {
        var clock = new ManualClock(5 * S);
        await using var rig = await ControlRig.StartAsync(TimecodeEngineTests.Show(), clock: clock);
        await Desk.StartAsync(rig, clock);
        await rig.Ok("TIMECODE SET Show JUMP 150");
        await rig.Ok("TIMECODE SET Show DELAY 40");
        Assert.Equal("JUMP <1-10000 ms>|AUTO", await rig.Refused("TIMECODE SET Show JUMP NaN"));
        Assert.Equal("FREEWHEEL is 0 to 10000 ms", await rig.Refused("TIMECODE SET Show FREEWHEEL NaN"));
        Assert.Equal("DELAY is −2000 to 2000 ms", await rig.Refused("TIMECODE SET Show DELAY NaN"));
        var chase = rig.Engine.Store.Project.FindPlaylist("pl_show")!.Chase!;
        Assert.Equal((150_000_000L, 500_000_000L, 40_000_000L), (chase.JumpNs, chase.FreewheelNs, chase.DelayNs));

        await rig.Ok("TIMECODE SOURCE ADD Rehearsal INTERNAL FORMAT 25");
        await rig.Ok("TIMECODE INTERNAL Rehearsal PLAY");
        Assert.Equal("RATE is 0.5 to 2", await rig.Refused("TIMECODE INTERNAL Rehearsal RATE NaN"));
        clock.AdvanceMs(100);
        await rig.Engine.StepChasesAsync();
        var rehearsal = rig.State().GetProperty("timecode").GetProperty("sources")[1];
        Assert.Equal(("rolling", 25.0), (rehearsal.GetProperty("state").GetString(), rehearsal.GetProperty("fpsMeasured").GetDouble()));
    }

    /// <summary>A generic SET of a chase's field is held to the bounds TIMECODE SET keeps: JUMP 0 (auto) or 1–10000 ms, DELAY
    /// −2000–2000 ms, FREEWHEEL 0–10000 ms. A project that carries a threshold of 0 or less (written by hand) is judged at auto, and
    /// STATE says so.</summary>
    [Fact]
    public async Task AChasesFieldsSetGenericallyKeepTheVerbsBounds()
    {
        var clock = new ManualClock(5 * S);
        await using var rig = await ControlRig.StartAsync(TimecodeEngineTests.Show(), clock: clock);
        await Desk.StartAsync(rig, clock);
        Assert.Equal("a chase's jumpNs is 0 (auto) or 1 to 10000 ms", await rig.Refused("SET pl_show chase.jumpNs 1"));
        Assert.Equal("a chase's jumpNs is 0 (auto) or 1 to 10000 ms", await rig.Refused("SET pl_show chase.jumpNs -5"));
        Assert.Equal("a chase's jumpNs is 0 (auto) or 1 to 10000 ms", await rig.Refused("SET pl_show chase.jumpNs 10000000001"));
        Assert.Equal("a chase's freewheelNs is 0 to 10000 ms", await rig.Refused("SET pl_show chase.freewheelNs -5"));
        Assert.Equal("a chase's delayNs is −2000 to 2000 ms", await rig.Refused("SET pl_show chase.delayNs 2000000001"));
        Assert.Equal(0, rig.Engine.Store.Project.FindPlaylist("pl_show")!.Chase!.JumpNs);
        await rig.Ok("SET pl_show chase.jumpNs 150000000");
        Assert.Equal(150_000_000, rig.Engine.Store.Project.FindPlaylist("pl_show")!.Chase!.JumpNs);
        await rig.Ok("SET pl_show chase.jumpNs 0");

        var written = TimecodeEngineTests.Show();
        written = written with
        {
            Timecode = rig.Engine.Store.Project.Timecode,
            Playlists = written.Playlists.Update("pl_show", l => l with { Chase = new TimecodeChase([rig.Engine.Store.Project.Timecode.Sources[0].Id]) { JumpNs = -5 } }),
        };
        await using var again = await ControlRig.StartAsync(written, clock: new ManualClock(5 * S));
        await again.Engine.StepChasesAsync();
        var chase = again.State().GetProperty("timecode").GetProperty("chases")[0];
        Assert.Equal((80.0, true), (chase.GetProperty("jumpMs").GetDouble(), chase.GetProperty("jumpAuto").GetBoolean()));
    }
}
