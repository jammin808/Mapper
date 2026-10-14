using System.Text.Json;
using PlayMap.Core.Model;
using PlayMap.Core.Numerics;
using PlayMap.Core.Playback;
using PlayMap.Devices.Tests.Simulators;

namespace PlayMap.Engine.Tests.Show;

/// <summary>Panic and the master on a manual show clock: every level is a fade of the leader instant, picture and sound alike; a
/// second PANIC inside the fade is the hard stop; CLEAR fades back; the transport outcomes land at the fade's end (REWIND cues the
/// item at its in-point, NEXT the next item, BLACK pauses); SHUTTER closes the linked projector (observed); FX-OFF sends its line
/// as the node; SAFE dips to the planned look; a panic survives a restart; PANIC is never refused for an operator.</summary>
public sealed class PanicTests
{
    private const double Tolerance = 1e-6;

    [Fact]
    public async Task APanicFadesPictureAndSoundOnTheShowClockAndASecondPressIsTheHardStop()
    {
        await using var rig = new ShowRig();
        var t0 = rig.Now;
        var reply = JsonDocument.Parse(await rig.Ok("PANIC FADE 1000")).RootElement;
        Assert.Equal("black", reply.GetProperty("outcome").GetString());
        Assert.False(reply.GetProperty("hard").GetBoolean());
        Assert.Equal(1000, reply.GetProperty("outputs")[0].GetProperty("fadeMs").GetInt32());

        Assert.Equal(1.0, rig.Engine.AirGain("op_1", t0), Tolerance);
        Assert.Equal(0.5, rig.Engine.AirGain("op_1", t0 + 500_000_000), Tolerance);
        Assert.Equal(0.5, rig.Engine.AirAudioGain(t0 + 500_000_000), Tolerance);
        Assert.Equal(0.25, rig.Engine.AirGain("op_1", t0 + 750_000_000), Tolerance);
        Assert.Equal(0.0, rig.Engine.AirGain("op_1", t0 + 1_000_000_000), Tolerance);

        var air = rig.State().GetProperty("air").GetProperty("panic");
        Assert.Equal("black", air.GetProperty("outcome").GetString());
        Assert.Equal("Desk", air.GetProperty("by").GetString());
        Assert.Equal("op_1", air.GetProperty("outputs")[0].GetProperty("id").GetString());

        rig.Advance(300);
        var hard = JsonDocument.Parse(await rig.Ok("PANIC")).RootElement;
        Assert.True(hard.GetProperty("hard").GetBoolean());
        Assert.Equal(0.0, rig.Engine.AirGain("op_1", rig.Now), Tolerance);
        Assert.Equal(0.0, rig.Engine.AirAudioGain(rig.Now), Tolerance);
        Assert.True(rig.State().GetProperty("air").GetProperty("panic").GetProperty("hard").GetBoolean());
        Assert.Equal("red", rig.Context().GetProperty("items")[0].GetProperty("light").GetString());
        Assert.StartsWith("PANIC black · ", rig.Context().GetProperty("headline").GetString(), StringComparison.Ordinal);

        // Settled, a third PANIC changes nothing and says so.
        rig.Advance(1000);
        Assert.StartsWith("already in panic since ", await rig.Refused("PANIC"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ClearFadesTheAirBackAndTheMasterMultipliesIn()
    {
        await using var rig = new ShowRig();
        Assert.Equal("not in panic — the air is the show's", await rig.Refused("PANIC CLEAR"));
        await rig.Ok("PANIC FADE 1000");
        rig.Advance(1000);
        var t = rig.Now;
        var clear = JsonDocument.Parse(await rig.Ok("PANIC CLEAR FADE 500")).RootElement;
        Assert.Equal("op_1", clear.GetProperty("cleared")[0].GetString());
        Assert.Equal(500, clear.GetProperty("fadeMs").GetInt32());
        Assert.Equal(0.0, rig.Engine.AirGain("op_1", t), Tolerance);
        Assert.Equal(0.5, rig.Engine.AirGain("op_1", t + 250_000_000), Tolerance);
        Assert.Equal(1.0, rig.Engine.AirGain("op_1", t + 500_000_000), Tolerance);
        Assert.Equal(1.0, rig.Engine.AirAudioGain(t + 500_000_000), Tolerance);
        Assert.Equal(JsonValueKind.Null, rig.State().GetProperty("air").GetProperty("panic").ValueKind);

        var m0 = rig.Now;
        var master = JsonDocument.Parse(await rig.Ok("MASTER 50 FADE 1000")).RootElement;
        Assert.Equal(0.5, master.GetProperty("master").GetDouble(), Tolerance);
        Assert.Equal(0.75, rig.Engine.AirGain("op_1", m0 + 500_000_000), Tolerance);
        Assert.Equal(0.75, rig.Engine.AirAudioGain(m0 + 500_000_000), Tolerance);
        rig.Advance(1000);
        Assert.Equal("the master is already at 50", await rig.Refused("MASTER 50"));
        Assert.Equal(0.5, rig.State().GetProperty("air").GetProperty("master").GetProperty("level").GetDouble(), Tolerance);
        await rig.Ok("PANIC FADE 1000");
        Assert.Equal(0.25, rig.Engine.AirGain("op_1", rig.Now + 500_000_000), Tolerance);
        await rig.Ok("MASTER 100");
        Assert.Equal(0.5, rig.Engine.AirGain("op_1", rig.Now + 500_000_000), Tolerance);
    }

    [Fact]
    public async Task RewindCuesTheItemAtItsInPointAtTheFadesEnd()
    {
        await using var rig = new ShowRig();
        await rig.Ok("PLAYLIST GO pl_show");
        rig.Advance(3000);
        await rig.Ok("PANIC REWIND FADE 1000");
        var end = rig.Now + 1_000_000_000;
        rig.Advance(990);
        await Task.Delay(50);
        var lane = rig.Engine.Playback.Lane("pl_show")!;
        Assert.Equal(TransportState.Playing, lane.Clip.State);

        rig.Advance(10);
        Assert.True(await ShowRig.Eventually(() => rig.Engine.Playback.Lane("pl_show")!.Clip.State == TransportState.Cued));
        lane = rig.Engine.Playback.Lane("pl_show")!;
        Assert.Equal("it_a", lane.ItemId);
        Assert.Equal(lane.Clip.InNs, lane.Clip.PositionAt(end));
        Assert.Contains(rig.Engine.Journal.Recent(20), r => r.Kind == "panic" && r.Text.Contains("cued A", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NextCuesTheNextItemAndBlackPausesAtTheFadesEnd()
    {
        await using (var rig = new ShowRig())
        {
            await rig.Ok("PLAYLIST GO pl_show");
            rig.Advance(2000);
            await rig.Ok("PANIC NEXT FADE 500");
            rig.Advance(500);
            Assert.True(await ShowRig.Eventually(() => rig.Engine.Playback.Lane("pl_show")!.ItemId == "it_b"));
            Assert.Equal(TransportState.Cued, rig.Engine.Playback.Lane("pl_show")!.Clip.State);
        }

        await using (var rig = new ShowRig())
        {
            await rig.Ok("PLAYLIST GO pl_show");
            var start = rig.Now;
            rig.Advance(2000);
            await rig.Ok("PANIC BLACK FADE 500");
            rig.Advance(2000);
            Assert.True(await ShowRig.Eventually(() => rig.Engine.Playback.Lane("pl_show")!.Clip.State == TransportState.Paused));
            // Paused at the fade's end on the show clock, not when the lane noticed.
            var clip = rig.Engine.Playback.Lane("pl_show")!.Clip;
            Assert.Equal(2_500_000_000, clip.PositionAt(rig.Now) - clip.InNs);
            Assert.Equal(start + 4_000_000_000, rig.Now);
        }
    }

    [Fact]
    public async Task SafeDipsToThePlannedLookAndAPanicSurvivesARestart()
    {
        RecoveryRecord record;
        await using (var rig = new ShowRig())
        {
            Assert.StartsWith("a SAFE plan names the look it shows", await rig.Refused("PANIC SET op_1 SAFE"), StringComparison.Ordinal);
            var plan = JsonDocument.Parse(await rig.Ok("PANIC SET op_1 SAFE FADE 2000 SAFE color:#FF0000")).RootElement[0];
            Assert.Equal(("safe", 2000, "color:#FF0000"), (plan.GetProperty("outcome").GetString(), plan.GetProperty("fadeMs").GetInt32(), plan.GetProperty("safe").GetString()));
            Assert.Equal("already planned so", await rig.Refused("PANIC SET op_1 SAFE"));
            var t = rig.Now;
            await rig.Ok("PANIC");
            var levels = rig.Engine.AirLevelsNow!;
            Assert.Equal(0.5, levels.Gain("op_1", t + 500_000_000), Tolerance);
            Assert.Null(levels.Override("op_1", t + 500_000_000));
            Assert.Equal(0.0, levels.Gain("op_1", t + 1_000_000_000), Tolerance);
            Assert.Equal(SourceRef.Solid("#FF0000"), levels.Override("op_1", t + 1_000_000_000));
            Assert.Equal(0.5, levels.Gain("op_1", t + 1_500_000_000), Tolerance);
            Assert.Equal(1.0, levels.Gain("op_1", t + 2_000_000_000), Tolerance);
            // The safe look has no show sound.
            Assert.Equal(0.0, levels.AudioGain(t + 2_000_000_000), Tolerance);
            Assert.Equal("safe", rig.State().GetProperty("air").GetProperty("panic").GetProperty("outputs")[0].GetProperty("outcome").GetString());
            rig.Advance(2000);
            record = await rig.Engine.Lane.RunAsync(() => RecoveryRecord.Capture(rig.Engine));
        }

        await using var after = new ShowRig();
        await after.Engine.RestoreAsync(record);
        var panic = after.State().GetProperty("air").GetProperty("panic");
        Assert.Equal("safe", panic.GetProperty("outputs")[0].GetProperty("outcome").GetString());
        Assert.Equal(SourceRef.Solid("#FF0000"), after.Engine.AirLevelsNow!.Override("op_1", after.Now + 1));
        Assert.Equal(1.0, after.Engine.AirGain("op_1", after.Now + 1), Tolerance);
        await after.Ok("PANIC CLEAR FADE 1000");
        var c = after.Now;
        Assert.Equal(SourceRef.Solid("#FF0000"), after.Engine.AirLevelsNow!.Override("op_1", c + 250_000_000));
        Assert.Equal(0.5, after.Engine.AirGain("op_1", c + 250_000_000), Tolerance);
        Assert.Null(after.Engine.AirLevelsNow!.Override("op_1", c + 500_000_000));
        Assert.Equal(1.0, after.Engine.AirGain("op_1", c + 1_000_000_000), Tolerance);
    }

    [Fact]
    public async Task PanicIsNeverRefusedForAnOperatorAndAnAssistantsWaitsForAPerson()
    {
        await using var rig = new ShowRig();
        await rig.Ok("SHOW ARM");
        await rig.Ok("LOCK AREA content");
        await rig.Ok("LOCK op_1");
        await rig.Ok("PANIC FREEZE ON op_1", ShowRig.Remote);
        Assert.True(rig.Engine.AirLevelsNow!.Hold("op_1", rig.Now));
        Assert.Equal(1.0, rig.Engine.AirGain("op_1", rig.Now), Tolerance);
        await rig.Ok("PANIC CLEAR", ShowRig.Companion);
        Assert.Null(rig.Engine.AirLevelsNow);
        Assert.Contains("waits for a person", await rig.Refused("PANIC", ShowRig.Mcp), StringComparison.Ordinal);
        Assert.Equal(JsonValueKind.Null, rig.State().GetProperty("air").GetProperty("panic").ValueKind);
        Assert.Equal("no output op_9", await rig.Refused("PANIC ON op_9"));
        Assert.StartsWith("PANIC [BLACK|", await rig.Refused("PANIC SIDEWAYS"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunTakesThePictureDownAndLeavesTheShowRunning()
    {
        await using var rig = new ShowRig();
        await rig.Ok("PLAYLIST GO pl_show");
        var t = rig.Now;
        var reply = JsonDocument.Parse(await rig.Ok("PANIC RUN FADE 1000")).RootElement;
        Assert.Equal("run", reply.GetProperty("outcome").GetString());
        Assert.Equal(0.5, rig.Engine.AirGain("op_1", t + 500_000_000), Tolerance);
        Assert.Equal(0.5, rig.Engine.AirAudioGain(t + 500_000_000), Tolerance);
        rig.Advance(1500);
        await Task.Delay(60);
        var lane = rig.Engine.Playback.Lane("pl_show")!;
        Assert.Equal(TransportState.Playing, lane.Clip.State);
        // CLEAR fades up into the show as it is now.
        await rig.Ok("PANIC CLEAR FADE 0");
        Assert.Equal(1.0, rig.Engine.AirGain("op_1", rig.Now), Tolerance);
        Assert.Equal(1_500_000_000, lane.Clip.PositionAt(rig.Now) - lane.Clip.InNs);
        await rig.Ok("PANIC SET op_1 RUN FADE 300");
        Assert.Equal("run", JsonDocument.Parse(await rig.Ok("PANIC")).RootElement.GetProperty("outputs")[0].GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task APanicAtAnInstantLandsOnTheShowClock()
    {
        await using var rig = new ShowRig();
        await rig.Ok("PLAYLIST GO pl_show");
        var t = rig.Now;
        var reply = JsonDocument.Parse(await rig.Ok("PANIC REWIND FADE 1000 AT +2000")).RootElement;
        Assert.Equal(t + 3_000_000_000, reply.GetProperty("outputs")[0].GetProperty("until").GetInt64());
        Assert.Equal(1.0, rig.Engine.AirGain("op_1", t + 1_999_000_000), Tolerance);
        Assert.Equal(0.5, rig.Engine.AirGain("op_1", t + 2_500_000_000), Tolerance);
        Assert.Equal(0.0, rig.Engine.AirGain("op_1", t + 3_000_000_000), Tolerance);

        rig.Advance(2500);
        await Task.Delay(60);
        Assert.Equal(TransportState.Playing, rig.Engine.Playback.Lane("pl_show")!.Clip.State);
        rig.Advance(500);
        Assert.True(await ShowRig.Eventually(() => rig.Engine.Playback.Lane("pl_show")!.Clip.State == TransportState.Cued));
        Assert.Equal("it_a", rig.Engine.Playback.Lane("pl_show")!.ItemId);
        Assert.Equal("AT takes +<ms> or a Unix ms", await rig.Refused("PANIC AT soon"));
    }

    [Fact]
    public async Task APanicsOwnLineWaitsForItsInstant()
    {
        await using var rig = new ShowRig();
        await rig.Ok("PANIC FX-OFF AT +500");
        await Task.Delay(60);
        Assert.DoesNotContain(rig.Engine.Journal.Recent(50), r => r.Line == "FX BYPASS ALL ON");
        Assert.Equal("scheduled", rig.State().GetProperty("air").GetProperty("panic").GetProperty("devices")[0].GetProperty("stage").GetString());
        rig.Advance(500);
        Assert.True(await ShowRig.Eventually(() => rig.Engine.Journal.Recent(50).Any(r => r.Line == "FX BYPASS ALL ON" && r.Who == "PlayMap")));
    }

    [Fact]
    public async Task FxOffSendsItsLineAsTheNode()
    {
        await using var rig = new ShowRig();
        var reply = JsonDocument.Parse(await rig.Ok("PANIC FX-OFF")).RootElement;
        Assert.Equal("fxOff", reply.GetProperty("outputs")[0].GetProperty("outcome").GetString());
        Assert.Equal(1.0, rig.Engine.AirGain("op_1", rig.Now + 1_000_000_000), Tolerance);
        Assert.True(await ShowRig.Eventually(() => rig.Engine.Journal.Recent(50).Any(r => r.Line == "FX BYPASS ALL ON" && r.Who == "PlayMap")));
        Assert.True(await ShowRig.Eventually(() => rig.State().GetProperty("air").GetProperty("panic").GetProperty("devices").EnumerateArray()
            .Any(d => d.GetProperty("id").GetString() == "fx" && d.GetProperty("stage").GetString() != "sent")));
    }

    [Fact]
    public async Task ShutterClosesTheLinkedProjectorObservedAndClearOpensIt()
    {
        await using var projector = new PjLinkSimulator();
        var project = ShowRig.OneScreen().WithStage(s => s with
        {
            Projectors =
            [
                new Projector("pj00000001", "Left", Intrinsics.FromThrowRatio(256, 160, 1.4), LensDistortion.None,
                    Pose.LookAt(new Vec3d(0.1, 1.5, 2.3), new Vec3d(0, 0.2, 0), Vec3d.UnitY), 0, "#FF8800", false, [], SolveLocks.None, 0),
            ],
        });
        var rig = new OpsRig(project: project);
        try
        {
            var dv = await rig.Ok($"DEVICE ADD \"Left box\" pjlink 127.0.0.1:{projector.Port} OUTPUT op_1");
            // The show's projector is on, as it is during a show (a box in standby reports no shutter).
            await rig.Ok($"DEVICE POWER {dv} ON");
            await rig.Ok("PANIC SHUTTER");
            Assert.True(await OpsRig.Eventually(() => projector.Avmt == "31"), $"AVMT {projector.Avmt}");
            Assert.True(await OpsRig.Eventually(() => rig.State().GetProperty("devices").EnumerateArray()
                .Any(d => d.GetProperty("id").GetString() == dv && d.GetProperty("shutter").GetString() == "closed")));
            Assert.True(await OpsRig.Eventually(() => rig.State().GetProperty("air").GetProperty("panic").GetProperty("devices").EnumerateArray()
                .Any(d => d.GetProperty("id").GetString() == dv && d.GetProperty("stage").GetString() is "applied" or "accepted")));
            Assert.Equal(1.0, rig.Engine.AirGain("op_1", rig.Engine.LeaderNowNs), Tolerance);

            await rig.Ok("PANIC CLEAR");
            Assert.True(await OpsRig.Eventually(() => projector.Avmt == "30"), $"AVMT {projector.Avmt}");
        }
        finally
        {
            await rig.DisposeAsync();
            rig.DeleteFolder();
        }
    }
}
