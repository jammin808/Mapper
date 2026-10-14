using System.Text.Json;

namespace PlayMap.Engine.Tests.Audio;

/// <summary>The TEMPO verbs on a running engine: changes land one lead ahead as anchors, two engines given the same lines at the same
/// leader instants read the same beat at every instant, taps set the tempo by their median rule, and no-ops are refused.</summary>
public sealed class TempoVerbTests
{
    private const long Ms = 1_000_000;

    [Fact]
    public async Task ASetLandsOneLeadAheadAndTwoEnginesReadTheSameBeat()
    {
        await using var a = new AudioRig();
        await using var b = new AudioRig();
        foreach (var rig in new[] { a, b })
        {
            rig.AdvanceMs(1_234.5);
            var r = await rig.Ok("TEMPO SET 128");
            var anchor = AudioRig.Json(r);
            Assert.Equal(rig.Clock.NowNs + 40 * Ms, anchor.GetProperty("leaderNs").GetInt64());
            Assert.Equal(128, anchor.GetProperty("bpm").GetDouble());
            Assert.Equal(128, rig.Engine.Store.Project.Tempo.Bpm);
        }
        // Before the anchor's instant the old tempo holds; from it, the new one.
        Assert.Equal(120, a.Engine.AudioRt.Tempo.BpmAt(a.Clock.NowNs + 39 * Ms));
        Assert.Equal(128, a.Engine.AudioRt.Tempo.BpmAt(a.Clock.NowNs + 40 * Ms));
        for (var step = 0; step < 50; step++)
        {
            a.AdvanceMs(13.7);
            b.AdvanceMs(13.7);
            var sa = AudioRig.Json(await a.Ok("TEMPO STATUS"));
            var sb = AudioRig.Json(await b.Ok("TEMPO STATUS"));
            Assert.Equal(sa.GetProperty("phase").GetDouble(), sb.GetProperty("phase").GetDouble());
            Assert.Equal(sa.GetProperty("beat").GetInt64(), sb.GetProperty("beat").GetInt64());
            Assert.Equal(sa.GetProperty("barPhase").GetDouble(), sb.GetProperty("barPhase").GetDouble());
        }
        // The same instants, read on the clocks themselves: identical.
        for (var t = 0L; t < 20_000 * Ms; t += 7_919_191)
            Assert.Equal(a.Engine.AudioRt.Tempo.BeatAt(t), b.Engine.AudioRt.Tempo.BeatAt(t));
        Assert.Contains("already", await a.Refused("TEMPO SET 128"), StringComparison.Ordinal);
        Assert.Contains("20 to 999", await a.Refused("TEMPO SET 1500"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AtNamesTheInstantAndTheBarLine()
    {
        await using var rig = new AudioRig();
        var start = rig.Clock.NowNs;
        var later = AudioRig.Json(await rig.Ok("TEMPO SET 100 AT +500"));
        Assert.Equal(start + 500 * Ms, later.GetProperty("leaderNs").GetInt64());
        // AT BAR: the next bar line (of the clock as it will stand) at least one lead ahead.
        var bar = AudioRig.Json(await rig.Ok("TEMPO SET 90 AT BAR"));
        var at = bar.GetProperty("leaderNs").GetInt64();
        Assert.True(at >= rig.Clock.NowNs + 40 * Ms);
        Assert.Equal(0, rig.Engine.AudioRt.Tempo.PhasesAt(at).Bar, 6);
        Assert.Contains("40 ms ahead", await rig.Refused($"TEMPO SET 95 AT {rig.Engine.Services.Wall.UnixMs}"), StringComparison.Ordinal);
        await rig.Refused("TEMPO SET 95 AT SOON");
    }

    [Fact]
    public async Task TapsSetTheTempoWithTheLastTapOnTheBeat()
    {
        await using var rig = new AudioRig();
        var first = await rig.Ok("TEMPO TAP");
        Assert.Contains("tap again", first.Effect, StringComparison.Ordinal);
        long[] jitter = [20, -15, 10, -20, 5];
        JsonElement last = default;
        foreach (var j in jitter)
        {
            rig.AdvanceMs(500 + j);
            last = AudioRig.Json(await rig.Ok("TEMPO TAP"));
        }
        Assert.Equal(6, last.GetProperty("taps").GetInt32());
        Assert.InRange(last.GetProperty("bpm").GetDouble(), 119, 121);
        Assert.InRange(rig.Engine.Store.Project.Tempo.Bpm, 119, 121);
        // The last tap is a whole beat on the clock that follows.
        var tapAt = rig.Clock.NowNs;
        var beat = rig.Engine.AudioRt.Tempo.At(tapAt + 40 * Ms).BeatAt(tapAt);
        Assert.Equal(Math.Round(beat), beat, 6);
        var state = rig.State().GetProperty("audio");
        Assert.True(state.GetProperty("tempo").GetProperty("inUse").GetBoolean());
        Assert.Equal(JsonValueKind.Number, state.GetProperty("beat").ValueKind);
    }

    [Fact]
    public async Task HalfDoubleResyncNudgeMeterAndSourceAnswerWithFieldsOrReasons()
    {
        await using var rig = new AudioRig();
        await rig.Ok("TEMPO DOUBLE");
        Assert.Equal(240, rig.Engine.Store.Project.Tempo.Bpm);
        await rig.Ok("TEMPO HALF");
        await rig.Ok("TEMPO HALF");
        Assert.Equal(60, rig.Engine.Store.Project.Tempo.Bpm);
        await rig.Ok("TEMPO SET 25");
        Assert.Contains("outside 20 to 999", await rig.Refused("TEMPO HALF"), StringComparison.Ordinal);
        await rig.Ok("TEMPO SET 120");
        rig.AdvanceMs(1_000);

        // A resync: the press becomes the next bar line, seen one lead later.
        rig.AdvanceMs(310);
        var press = rig.Clock.NowNs;
        var anchor = AudioRig.Json(await rig.Ok("TEMPO RESYNC BAR"));
        Assert.Equal(press + 40 * Ms, anchor.GetProperty("leaderNs").GetInt64());
        var landed = rig.Engine.AudioRt.Tempo.At(press + 40 * Ms).BeatAt(press);
        Assert.Equal(Math.Round(landed / 4) * 4, landed, 6);
        // A press on a beat line already is a resync that changes nothing.
        rig.Clock.NowNs = rig.Engine.AudioRt.Tempo.NextBoundary(rig.Clock.NowNs + 100 * Ms, 1);
        Assert.Contains("already starts", await rig.Refused("TEMPO RESYNC BEAT"), StringComparison.Ordinal);

        var nudge = await rig.Ok("TEMPO NUDGE 30");
        Assert.Contains("slewed", nudge.Effect, StringComparison.Ordinal);
        await rig.Refused("TEMPO NUDGE 500");
        await rig.Ok("TEMPO NUDGE FASTER");
        Assert.Equal(120 * 1.04, rig.Engine.AudioRt.Tempo.BpmAt(rig.Clock.NowNs + 50 * Ms), 9);
        await rig.Ok("TEMPO NUDGE STOP");
        Assert.Equal(120, rig.Engine.AudioRt.Tempo.BpmAt(rig.Clock.NowNs + 50 * Ms), 9);
        rig.AdvanceMs(100);
        Assert.Contains("no nudge", await rig.Refused("TEMPO NUDGE STOP"), StringComparison.Ordinal);

        await rig.Ok("TEMPO METER 3 4");
        Assert.Equal(3, rig.Engine.AudioRt.Tempo.BeatsPerBar);
        Assert.Equal(4, rig.Engine.Store.Project.Tempo.BarsPerPhrase);
        Assert.Contains("already", await rig.Refused("TEMPO METER 3 4"), StringComparison.Ordinal);
        await rig.Refused("TEMPO METER 0");

        Assert.Contains("already manual", await rig.Refused("TEMPO SOURCE MANUAL"), StringComparison.Ordinal);
        await rig.Ok("TEMPO SOURCE DETECT");
        Assert.Equal("detect", rig.Engine.Store.Project.Tempo.Source);
        await rig.Refused("TEMPO SOURCE LINK");

        // Undo puts the show's tempo back; the clock follows it on the lane's tick, one lead ahead.
        var status = AudioRig.Json(await rig.Ok("TEMPO STATUS"));
        Assert.Equal(3, status.GetProperty("beatsPerBar").GetInt32());
        Assert.Equal("detect", status.GetProperty("source").GetString());
    }

    [Fact]
    public async Task TheClockFollowsAnUndoneTempo()
    {
        await using var rig = new AudioRig();
        await rig.Ok("TEMPO SET 140");
        await rig.Ok("UNDO");
        Assert.Equal(120, rig.Engine.Store.Project.Tempo.Bpm);
        // The lane's tick reconciles the clock with the show (it runs every 10 ms).
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (Math.Abs(rig.Engine.AudioRt.Tempo.Bpm - 120) > 1e-9 && DateTime.UtcNow < deadline)
            await Task.Delay(10);
        Assert.Equal(120, rig.Engine.AudioRt.Tempo.Bpm);
        Assert.Equal(rig.Clock.NowNs + 40 * Ms, rig.Engine.AudioRt.Tempo.Anchors[^1].LeaderNs);
    }
}
