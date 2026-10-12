using PlayMap.Core.Model;
using PlayMap.Core.Playback;
using PlayMap.Core.Time;

namespace PlayMap.Core.Tests;

/// <summary>The clock exchange, timecode, the timeline anchor and the clip transport's VIDEO semantics.</summary>
public class TimeAndPlaybackTests
{
    [Fact]
    public void TheEstimatorKeepsTheSampleWithTheSmallestRoundTrip()
    {
        var e = new ClockOffsetEstimator();
        // True offset +500; a symmetric fast exchange and two queued (asymmetric) slow ones.
        e.Add(new ClockSample(1000, 1500 + 40, 1500 + 41, 1081));
        e.Add(new ClockSample(2000, 2500 + 5, 2500 + 6, 2011));
        e.Add(new ClockSample(3000, 3500 + 90, 3500 + 91, 3120));
        Assert.Equal(10, e.RoundTrip);
        Assert.Equal(500, e.Offset, 0);
    }

    [Fact]
    public void ASteppedClockMidExchangeIsRefused()
    {
        var e = new ClockOffsetEstimator();
        Assert.False(e.Add(new ClockSample(1000, 100, 50, 1010)));
        Assert.False(e.Add(new ClockSample(1000, 100, 200, 1010)));
        Assert.False(e.HasEstimate);
    }

    [Fact]
    public void TheDriftEstimateFollowsAFastPeerClock()
    {
        var e = new ClockOffsetEstimator();
        const double ppm = 50e-6;
        for (var i = 0; i < 40; i++)
        {
            long t1 = i * 1_000_000_000L;
            var offset = 1_000_000 + ppm * t1;
            var t2 = (long)(t1 + 100_000 + offset);
            e.Add(new ClockSample(t1, t2, t2 + 1000, t1 + 201_000));
        }
        Assert.Equal(ppm, e.Drift, 6);
        var clock = new NetworkClock(new ManualClock(45_000_000_000));
        clock.Follow(e);
        var leader = clock.NowNs;
        var expected = 45_000_000_000 + 1_000_000 + ppm * 45_000_000_000;
        Assert.InRange(leader - expected, -2000, 2000);
        Assert.InRange(clock.ToLocal(leader) - 45_000_000_000, -2, 2);
    }

    [Theory]
    [InlineData(0L, "00:00:00;00")]
    [InlineData(1799L, "00:00:59;29")]
    [InlineData(1800L, "00:01:00;02")]
    [InlineData(17982L, "00:10:00;00")]
    public void DropFrameTimecodeSkipsTheRightNumbers(long frame, string text)
    {
        Assert.Equal(text, Timecode.Format(frame, FrameRate.Fps2997, dropFrame: true));
        Assert.True(Timecode.TryParse(text, FrameRate.Fps2997, out var back));
        Assert.Equal(frame, back);
    }

    [Fact]
    public void NonDropTimecodeRoundTrips()
    {
        var tc = Timecode.Format(25 * 3661 + 7, FrameRate.Fps25);
        Assert.Equal("01:01:01:07", tc);
        Assert.True(Timecode.TryParse(tc, FrameRate.Fps25, out var f));
        Assert.Equal(25 * 3661 + 7, f);
    }

    [Fact]
    public void FrameRatesParseToExactRationals()
    {
        Assert.True(FrameRate.TryParse("29.97", out var r));
        Assert.Equal(FrameRate.Fps2997, r);
        Assert.True(FrameRate.TryParse("30000/1001", out var q));
        Assert.Equal(r, q);
        Assert.Equal(1001, r.FrameAt(r.TimeOf(1001)));
    }

    [Fact]
    public void AFutureStartHoldsUntilItsInstant()
    {
        var a = new TimelineAnchor(5_000, 1_000_000, 1, true);
        Assert.Equal(5_000, a.MediaAt(0));
        Assert.Equal(5_000 + 500, a.MediaAt(1_000_500));
    }

    [Fact]
    public void FrameSelectionIsTheSamePureFunctionOnEveryNode()
    {
        var anchor = new TimelineAnchor(0, 10_000_000_000, 1, true);
        // 25 fps on two nodes whose vblanks fall at different instants within the same frame.
        var a = FrameSelector.FrameAt(anchor, 10_000_000_000 + 40_000_000 * 7 + 1_000_000, FrameRate.Fps25);
        var b = FrameSelector.FrameAt(anchor, 10_000_000_000 + 40_000_000 * 7 + 39_000_000, FrameRate.Fps25);
        Assert.Equal(7, a);
        Assert.Equal(7, b);
        Assert.Equal(99, FrameSelector.FrameAt(anchor, 99_000_000_000, FrameRate.Fps25, 100));
    }

    private static ClipTransport Loaded(long lengthS = 210, bool loop = false) =>
        ClipTransport.Load("clip", 0, lengthS * 1_000_000_000L, loop, 0);

    [Fact]
    public void PlayPauseAndStopMeanWhatTheyMeanOnAPatternsDesk()
    {
        var t = Loaded();
        var played = t.Play(0);
        Assert.True(played.Ok);
        Assert.False(played.Transport.Play(1).Ok);
        var paused = played.Transport.Pause(62_000_000_000);
        Assert.True(paused.Ok);
        Assert.Equal(62_000_000_000, paused.Transport.PositionAt(70_000_000_000));
        Assert.False(paused.Transport.Pause(70_000_000_000).Ok);
        var stopped = paused.Transport.Stop(70_000_000_000);
        Assert.True(stopped.Ok);
        Assert.Equal(0, stopped.Transport.PositionAt(80_000_000_000));
        Assert.Equal(TransportState.Cued, stopped.Transport.State);
        Assert.False(stopped.Transport.Stop(80_000_000_000).Ok);
        Assert.True(stopped.Transport.Seq > paused.Transport.Seq);
    }

    [Fact]
    public void EndInMovesToTenSecondsBeforeTheEndAndTheClipEnds()
    {
        var t = Loaded().Play(0).Transport;
        var end = t.EndIn(10, 5_000_000_000);
        Assert.True(end.Ok);
        Assert.Equal(200_000_000_000, end.Transport.PositionAt(5_000_000_000));
        Assert.Equal(TransportState.Playing, end.Transport.State);
        var later = end.Transport.Tick(16_000_000_000);
        Assert.Equal(TransportState.Ended, later.State);
        Assert.Equal(210_000_000_000, later.PositionAt(20_000_000_000));
        Assert.False(later.Play(20_000_000_000).Ok);
        Assert.True(later.Restart(20_000_000_000).Ok);
    }

    [Fact]
    public void ALoopWrapsAndTellsFollowersOnce()
    {
        var t = Loaded(10, loop: true).Play(0).Transport;
        Assert.Equal(3_000_000_000, t.PositionAt(13_000_000_000));
        var wrapped = t.Tick(13_000_000_000);
        Assert.Equal(t.Seq + 1, wrapped.Seq);
        Assert.Equal(3_000_000_000, wrapped.PositionAt(13_000_000_000));
        Assert.Null(wrapped.RemainingAt(13_000_000_000));
    }

    [Theory]
    [InlineData("83", 83.0)]
    [InlineData("83.5", 83.5)]
    [InlineData("2.5s", 2.5)]
    [InlineData("1:23", 83.0)]
    [InlineData("1:02:03", 3723.0)]
    [InlineData("top", 0.0)]
    public void SeekPointsParseThePatternsWay(string text, double seconds)
    {
        Assert.True(ClipTransport.TryParsePoint(text, out var ns));
        Assert.Equal((long)(seconds * 1e9), ns);
    }

    [Theory]
    [InlineData("1:75")]
    [InlineData("abc")]
    [InlineData("-3")]
    [InlineData("1:2:3:4")]
    public void BadSeekPointsAreRefused(string text) => Assert.False(ClipTransport.TryParsePoint(text, out _));

    [Fact]
    public void ThePlaylistCursorFollowsEachItemsEndAction()
    {
        var list = new Playlist("pl1", "Show",
        [
            PlaylistItem.For("a", SourceRef.Media("m1"), "A"),
            PlaylistItem.For("b", SourceRef.Media("m2"), "B") with { End = EndAction.Goto, GotoItemId = "a" },
            PlaylistItem.For("c", SourceRef.Media("m3"), "C") with { End = EndAction.Stop },
        ], Loop: false, SyncGroup: null);
        Assert.Equal(1, PlaylistCursor.NextIndex(list, 0));
        Assert.Equal(0, PlaylistCursor.NextIndex(list, 1));
        Assert.Equal(-1, PlaylistCursor.NextIndex(list, 2));
        Assert.Equal(-1, PlaylistCursor.StepIndex(list, 2, 1));
        Assert.Equal(0, PlaylistCursor.StepIndex(list with { Loop = true }, 2, 1));
        var cursor = PlaylistCursor.Idle("pl1").CueItem(list, 0, 5_000_000_000);
        Assert.Equal("a", cursor.ItemId);
        Assert.Equal(5_000_000_000, cursor.Clip.OutNs);
        Assert.Equal(TransportState.Cued, cursor.Clip.State);
    }
}
