using System.Text.Json;
using PlayMap.Core.Model;
using PlayMap.Core.Playback;
using PlayMap.Core.Tc;
using PlayMap.Core.Time;
using PlayMap.Engine.Tests.Control;

namespace PlayMap.Engine.Tests.Tc;

/// <summary>The chase in the engine: a playlist follows a synthetic LTC input (our encoder's audio through the audio-input seam, on the
/// node's manual clock), STATE.timecode says what the code and the chase are doing, manual transport on a chased playlist is refused
/// with words, and every TIMECODE verb refuses a change that changes nothing.</summary>
public sealed class TimecodeEngineTests
{
    internal static Project Show() => Project.CreateDefault("Show") with
    {
        Playlists =
        [
            new Playlist("pl_show", "Show", [PlaylistItem.For("it_a", SourceRef.Solid("#FF0000"), "Song 1") with { DurationNs = 10_000_000_000, TcIn = "01:00:00:00" },
                PlaylistItem.For("it_b", SourceRef.Solid("#00FF00"), "Song 2") with { DurationNs = 8_000_000_000 }], false, null),
            new Playlist("pl_other", "Other", [PlaylistItem.For("it_x", SourceRef.Solid("#0000FF"), "Loop")], false, null),
        ],
    };

    /// <summary>Pushes LTC from a frame for a duration in 10 ms blocks, the node's clock moving with the audio.</summary>
    private static async Task Feed(ControlRig rig, FakeAudioInputs audio, ManualClock clock, long fromFrame, double seconds, long firstSample, long startNs)
    {
        var encoder = new LtcEncoder(TcFormat.Fps25, 48000);
        var frames = (int)Math.Ceiling(seconds * 25) + 1;
        var (samples, _) = encoder.Render(fromFrame, frames);
        var count = (int)(seconds * 48000);
        for (var at = 0; at < count; at += 480)
        {
            var n = Math.Min(480, count - at);
            var end = firstSample + at + n;
            clock.NowNs = startNs + (long)Math.Round((end - firstSample) * 1e9 / 48000);
            audio.Push(samples.AsSpan(at, n), firstSample + at, clock.NowNs);
            if (at % 4800 == 0)
                await rig.Engine.StepChasesAsync();
        }
        await rig.Engine.StepChasesAsync();
    }

    [Fact]
    public async Task APlaylistChasesSyntheticLtcAndStateSaysSo()
    {
        var clock = new ManualClock(5_000_000_000);
        var audio = new FakeAudioInputs();
        await using var rig = await ControlRig.StartAsync(Show(), service: true, audio: audio, clock: clock);
        var added = await rig.Ok("TIMECODE SOURCE ADD FOH LTC");
        Assert.False(added.Applied);
        var id = JsonDocument.Parse(added.Payload!).RootElement.GetProperty("id").GetString()!;
        Assert.StartsWith("tc", id, StringComparison.Ordinal);
        await ControlRig.Until(() => audio.Sink is not null, "the LTC input opened");
        Assert.Equal("Show chases FOH (locked), engaged — waiting for code", (await rig.Ok("TIMECODE CHASE Show FOH")).Effect);
        await rig.Engine.StepChasesAsync();
        var chase = rig.State().GetProperty("timecode").GetProperty("chases")[0];
        Assert.Equal(("waiting", true), (chase.GetProperty("state").GetString(), chase.GetProperty("engaged").GetBoolean()));
        Assert.Equal("Show chases FOH (waiting) — TIMECODE ENGAGE Show OFF to drive it by hand", await rig.Refused("PLAYLIST GO Show"));
        Assert.Equal("Show chases FOH (waiting) — TIMECODE ENGAGE Show OFF to drive it by hand", await rig.Refused("VIDEO PLAY ON Show"));

        // Two seconds of code from 01:00:03:00.
        Assert.True(TcFormat.Fps25.TryParseLabel("01:00:03:00", out var from));
        var start = clock.NowNs;
        await Feed(rig, audio, clock, from, 2, 0, start);
        var state = rig.State().GetProperty("timecode");
        var source = state.GetProperty("sources")[0];
        Assert.Equal(("rolling", "25", "ltc"), (source.GetProperty("state").GetString(), source.GetProperty("detected").GetString(), source.GetProperty("kind").GetString()));
        Assert.StartsWith("01:00:04:", source.GetProperty("text").GetString(), StringComparison.Ordinal);
        Assert.Equal(-12, source.GetProperty("levelDb").GetDouble(), 0);
        Assert.Equal("green", source.GetProperty("level").GetString());
        chase = state.GetProperty("chases")[0];
        Assert.Equal(("locked", "it_a"), (chase.GetProperty("state").GetString(), chase.GetProperty("item").GetString()));
        Assert.Equal("locked to FOH", chase.GetProperty("words").GetString());
        var playlist = rig.State().GetProperty("playlists").EnumerateArray().First(p => p.GetProperty("id").GetString() == "pl_show");
        Assert.Equal("locked", playlist.GetProperty("chase").GetProperty("state").GetString());
        // The lane is a function of the code: at the node's now, the item's media is the code minus its TC in (5 s).
        var lane = rig.Engine.Playback.Lane("pl_show")!;
        Assert.Equal(("it_a", TransportState.Playing), (lane.ItemId, lane.Clip.State));
        var media = lane.Clip.Anchor.MediaAt(rig.Engine.LeaderNowNs);
        Assert.InRange(media, 5_000_000_000 - 10_000_000, 5_000_000_000 + 10_000_000);
        Assert.Equal("Show chases FOH (locked) — TIMECODE ENGAGE Show OFF to drive it by hand", await rig.Refused("PLAYLIST NEXT Show"));

        // Disengaged, the playlist plays on by hand from where it is.
        var off = await rig.Ok("TIMECODE ENGAGE Show OFF");
        Assert.StartsWith("Show disengaged: plays on by hand from 01:00:0", off.Effect, StringComparison.Ordinal);
        Assert.Equal("Show is already disengaged", await rig.Refused("TIMECODE ENGAGE Show OFF"));
        await rig.Ok("PLAYLIST NEXT Show");
        Assert.Equal("it_b", rig.Engine.Playback.Lane("pl_show")!.ItemId);
        Assert.False(rig.State().GetProperty("timecode").GetProperty("chases")[0].GetProperty("engaged").GetBoolean());
        await rig.Ok("TIMECODE ENGAGE Show ON");
    }

    [Fact]
    public async Task AnInternalSourceRollsAndAChaseFollowsItExactly()
    {
        var clock = new ManualClock(5_000_000_000);
        await using var rig = await ControlRig.StartAsync(Show(), clock: clock);
        await rig.Ok("TIMECODE SOURCE ADD Rehearsal INTERNAL FORMAT 25");
        Assert.Equal("Rehearsal at 01:00:01:00", (await rig.Ok("TIMECODE INTERNAL Rehearsal LOCATE 01:00:01:00")).Effect);
        await rig.Ok("TIMECODE CHASE Show Rehearsal");
        await rig.Engine.StepChasesAsync();
        Assert.Equal("parked", rig.State().GetProperty("timecode").GetProperty("chases")[0].GetProperty("state").GetString());
        Assert.Equal("Rehearsal rolling from 01:00:01:00", (await rig.Ok("TIMECODE INTERNAL Rehearsal PLAY")).Effect);
        Assert.Equal("Rehearsal is already rolling", await rig.Refused("TIMECODE INTERNAL Rehearsal PLAY"));
        clock.AdvanceMs(2000);
        await rig.Engine.StepChasesAsync();
        var lane = rig.Engine.Playback.Lane("pl_show")!;
        Assert.Equal(3_000_000_000, lane.Clip.Anchor.MediaAt(rig.Engine.LeaderNowNs));
        // Past item 1's end the next item has taken over on its exact instant: 01:00:10:00 + 1 s.
        clock.AdvanceMs(8000);
        await rig.Engine.StepChasesAsync();
        lane = rig.Engine.Playback.Lane("pl_show")!;
        Assert.Equal("it_b", lane.ItemId);
        Assert.Equal(1_000_000_000, lane.Clip.Anchor.MediaAt(rig.Engine.LeaderNowNs));
        Assert.Equal("Rehearsal stopped at 01:00:11:00", (await rig.Ok("TIMECODE INTERNAL Rehearsal STOP")).Effect);
    }

    [Fact]
    public async Task EveryTimecodeVerbRefusesWithTheReason()
    {
        await using var rig = await ControlRig.StartAsync(Show());
        await rig.Ok("TIMECODE SOURCE ADD FOH LTC Focusrite:2");
        Assert.Equal("there is already a timecode source called foh", await rig.Refused("TIMECODE SOURCE ADD foh MTC IAC*"));
        Assert.Equal("no timecode source nowhere — TIMECODE SOURCE ADD names one", await rig.Refused("TIMECODE CHASE Show nowhere"));
        Assert.Equal("Show chases no timecode", await rig.Refused("TIMECODE CHASE Show OFF"));
        await rig.Ok("TIMECODE CHASE Show FOH");
        Assert.Equal("Show already chases FOH", await rig.Refused("TIMECODE CHASE Show FOH"));
        Assert.Equal("Show is already engaged", await rig.Refused("TIMECODE ENGAGE Show ON"));
        Assert.Equal("Show chases only FOH — TIMECODE CHASE Show OFF first", await rig.Refused("TIMECODE SOURCE REMOVE FOH"));
        Assert.Equal("Show: picture 40 ms later than its timecode", (await rig.Ok("TIMECODE SET Show DELAY 40")).Effect);
        Assert.Equal("Show is already so", await rig.Refused("TIMECODE SET Show DELAY 40"));
        Assert.Equal("DELAY is −2000 to 2000 ms", await rig.Refused("TIMECODE SET Show DELAY 5000"));
        Assert.Equal("Show: when the code stops it continues", (await rig.Ok("TIMECODE SET Show ONLOSS CONTINUE")).Effect);
        Assert.Equal("Other chases no timecode — TIMECODE CHASE Other <source>", await rig.Refused("TIMECODE SET Other GAP HOLD"));
        Assert.Equal("00:01:00;00 does not exist at 29.97df — 00:01:00;02 is the first frame of that minute", await rig.Refused("TIMECODE IN Other it_x 00:01:00;00"));
        Assert.Equal("Loop starts at 00:01:00;02 (in Other)", (await rig.Ok("TIMECODE IN Other it_x 00:01:00;02")).Effect);
        Assert.Equal("Loop already starts at 00:01:00;02", await rig.Refused("TIMECODE IN Other it_x 00:01:00;02"));
        Assert.Equal("Show has no code now to take — NOW takes the chase's current timecode", await rig.Refused("TIMECODE IN Show it_b NOW"));
        Assert.Equal("FOH is not an internal source", await rig.Refused("TIMECODE INTERNAL FOH PLAY"));
        Assert.Equal("no audio inputs on this node (the audio hub is not running): LTC cannot arrive here",
            JsonDocument.Parse((await rig.Ok("TIMECODE DEVICES")).Payload!).RootElement.GetProperty("words").GetString());
        // The source's device and channel are what was asked.
        var source = rig.State().GetProperty("timecode").GetProperty("sources")[0];
        Assert.Equal("Focusrite:2", source.GetProperty("device").GetString());
        await rig.Ok("TIMECODE CHASE Show OFF");
        await rig.Ok("TIMECODE SOURCE REMOVE FOH");
        Assert.Empty(rig.Engine.Store.Project.Timecode.Sources);
    }

    [Fact]
    public async Task IndexListsTheChasesAndADisengagedChaseIsKeptForRecovery()
    {
        await using var rig = await ControlRig.StartAsync(Show());
        await rig.Ok("TIMECODE SOURCE ADD Rehearsal INTERNAL");
        await rig.Ok("TIMECODE CHASE Show Rehearsal");
        await rig.Engine.StepChasesAsync();
        var section = Assert.Single(rig.Engine.Routes.IndexSections, s => s.Section == "timecode");
        Assert.Equal(("Timecode", "chase", "TIMECODE ENGAGE $(id) TOGGLE"), (section.Hint.Title, section.Hint.RowKind, section.Hint.Keys[0].DownLine));
        var rows = JsonDocument.Parse(EngineCore.JsonOut(w => section.WriteRows(rig.Engine.Store.Project, rig.Engine.Snapshot, w))).RootElement;
        var row = Assert.Single(rows.EnumerateArray());
        Assert.Equal(("pl_show", "Show", "parked", "green"), (row.GetProperty("id").GetString(), row.GetProperty("label").GetString(),
            row.GetProperty("state").GetString(), row.GetProperty("light").GetString()));

        await rig.Ok("TIMECODE ENGAGE Show OFF");
        var saved = rig.Engine.SaveModuleRecovery();
        Assert.Equal("pl_show", saved["control.disengaged"]);
        await using var again = await ControlRig.StartAsync(rig.Engine.Store.Project);
        again.Engine.LoadModuleRecovery(saved);
        await again.Engine.StepChasesAsync();
        Assert.False(again.State().GetProperty("timecode").GetProperty("chases")[0].GetProperty("engaged").GetBoolean());
        Assert.Equal("Show is already disengaged", await again.Refused("TIMECODE ENGAGE Show OFF"));
    }
}
