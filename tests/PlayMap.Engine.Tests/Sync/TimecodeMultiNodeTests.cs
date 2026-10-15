using System.Net;
using PlayMap.Core.Model;
using PlayMap.Core.Playback;
using PlayMap.Core.Tc;
using PlayMap.Core.Time;
using PlayMap.Engine.Control;
using PlayMap.Engine.Host;
using PlayMap.Engine.Sync;
using PlayMap.Engine.Tests.Control;
using PlayMap.Engine.Tests.Host;

namespace PlayMap.Engine.Tests.Sync;

/// <summary>Control × sync (PLAN §6.1): a group's leader chases LTC (a synthetic input on the leader's manual clock) and its follower,
/// which has no timecode of its own, adopts the leader's anchors — the same item at the same media instant for the same leader
/// instant on both nodes — and, when the code stops and the leader's chase holds, holds with it.</summary>
public sealed class TimecodeMultiNodeTests
{
    private const long Second = SyncRig.Second;
    private const long Ahead = 7_300_000_000;

    /// <summary>pl_show in group "show": "Song 1" at TC 01:00:00:00 for ten seconds, "Song 2" eight.</summary>
    private static Project Show()
    {
        var p = Project.CreateDefault("Chase");
        return p with
        {
            Playlists =
            [
                new Playlist("pl_show", "Show", [PlaylistItem.For("it_a", SourceRef.Solid("#FF0000"), "Song 1") with { DurationNs = 10 * Second, TcIn = "01:00:00:00" },
                    PlaylistItem.For("it_b", SourceRef.Solid("#00FF00"), "Song 2") with { DurationNs = 8 * Second }], false, "show"),
            ],
        };
    }

    /// <summary>The control part with a synthetic audio input (the test pushes LTC into it), as the host attaches parts.</summary>
    private sealed class ChaseExtension(FakeAudioInputs audio) : IEngineExtension
    {
        private ControlService? _service;

        public string Name => "control (test)";

        public EngineServices Configure(EngineServices services)
        {
            _service = new ControlService(new ControlServiceOptions
            {
                Midi = new PlayMap.Devices.Midi.FakeMidiBackend(),
                Audio = audio,
                OscBind = IPAddress.Loopback,
                OscPort = 0,
            });
            return services with { Control = _service };
        }

        public Task StartAsync(EngineCore engine, CancellationToken ct) => _service!.AttachAsync(engine);

        public ValueTask DisposeAsync() => _service?.DisposeAsync() ?? ValueTask.CompletedTask;
    }

    /// <summary>LTC from a frame for a duration in 10 ms blocks, the shared clock moving with the audio.</summary>
    private static async Task Feed(EngineCore engine, FakeAudioInputs audio, ManualClock clock, long fromFrame, double seconds, long firstSample)
    {
        var encoder = new LtcEncoder(TcFormat.Fps25, 48000);
        var (samples, _) = encoder.Render(fromFrame, (int)Math.Ceiling(seconds * 25) + 1);
        var count = (int)(seconds * 48000);
        var start = clock.NowNs;
        for (var at = 0; at < count; at += 480)
        {
            var n = Math.Min(480, count - at);
            clock.NowNs = start + (long)Math.Round((at + n) * 1e9 / 48000);
            // The leader reads its audio on its own clock: the shared clock plus its offset.
            audio.Push(samples.AsSpan(at, n), firstSample + at, clock.NowNs + Ahead);
            if (at % 4800 == 0)
                await engine.StepChasesAsync();
        }
        await engine.StepChasesAsync();
    }

    [Fact]
    public async Task ALeaderChasingLtcPutsItsFollowerOnTheSameItemAndMediaInstant()
    {
        var clock = new ManualClock(1_000 * Second);
        var audio = new FakeAudioInputs();
        var parts = new EngineParts { Extensions = [_ => new ChaseExtension(audio), SyncExtension.With(SyncRig.Fast())] };
        await using var leader = await HostRig.StartAsync("FOH", network: true, mono: new OffsetClock(clock, Ahead), parts: parts,
            settings: s => s with { NodeId = SyncRig.LeaderId });
        await using var follower = await SyncRig.StartAsync("Stage", SyncRig.FollowerId, clock);
        await SyncRig.OpenShowAsync(leader, Show());
        await SyncRig.OpenShowAsync(follower, Show());
        await SyncRig.GroupAsync(leader, follower);

        await leader.OkAsync("TIMECODE SOURCE ADD FOH LTC");
        Assert.True(await Eventually.TrueAsync(() => audio.Sink is not null), "the LTC input opened");
        await leader.OkAsync("TIMECODE CHASE Show FOH");
        Assert.True(TcFormat.Fps25.TryParseLabel("01:00:03:00", out var from));
        await Feed(leader.Engine, audio, clock, from, 2, 0);
        var chase = leader.State().GetProperty("timecode").GetProperty("chases")[0];
        Assert.Equal("locked", chase.GetProperty("state").GetString());

        // The follower took the chased lane's anchor through its own PLAYLIST ANCHOR: the same item, the same media for the
        // same leader instant (about five seconds into Song 1).
        var anchor = leader.Engine.AnchorJson("pl_show");
        Assert.True(await Eventually.TrueAsync(() => follower.Engine.AnchorJson("pl_show") == leader.Engine.AnchorJson("pl_show")),
            $"{follower.Engine.AnchorJson("pl_show")} != {leader.Engine.AnchorJson("pl_show")}");
        Assert.NotNull(anchor);
        var now = leader.Engine.LeaderNowNs;
        Assert.Equal(now, follower.Engine.LeaderNowNs);
        var theirs = leader.Engine.Playback.Lane("pl_show")!;
        var ours = follower.Engine.Playback.Lane("pl_show")!;
        Assert.Equal(("it_a", TransportState.Playing), (ours.ItemId, ours.Clip.State));
        Assert.Equal(theirs.Clip.Anchor.MediaAt(now), ours.Clip.Anchor.MediaAt(now));
        Assert.InRange(ours.Clip.Anchor.MediaAt(now), 5 * Second - 10_000_000, 5 * Second + 10_000_000);
        Assert.Equal(0, follower.State().GetProperty("timecode").GetProperty("chases").GetArrayLength());   // it chases nothing itself

        // The code stops: the leader's chase rides the dropout, then holds by rule; whatever its lane does, the follower's does.
        clock.AdvanceMs(200);
        await leader.Engine.StepChasesAsync();
        clock.AdvanceMs(1_000);
        await leader.Engine.StepChasesAsync();
        Assert.Equal("held", leader.State().GetProperty("timecode").GetProperty("chases")[0].GetProperty("state").GetString());
        Assert.True(await Eventually.TrueAsync(() => follower.Engine.AnchorJson("pl_show") == leader.Engine.AnchorJson("pl_show")),
            $"{follower.Engine.AnchorJson("pl_show")} != {leader.Engine.AnchorJson("pl_show")}");
        now = leader.Engine.LeaderNowNs;
        Assert.Equal(leader.Engine.Playback.Lane("pl_show")!.Clip.State, follower.Engine.Playback.Lane("pl_show")!.Clip.State);
        Assert.Equal(leader.Engine.Playback.Lane("pl_show")!.Clip.Anchor.MediaAt(now), follower.Engine.Playback.Lane("pl_show")!.Clip.Anchor.MediaAt(now));
    }
}
