using PlayMap.Core.Audio;

namespace PlayMap.Core.Tests.Audio;

/// <summary>Tempo detection on click and drum loops across the usual range: the tempo within half a BPM and on the right octave within
/// 6 s, the beat placed within 15 ms of the truth, a change followed, and flat sound left alone.</summary>
public sealed class TempoTrackerTests
{
    private const int Rate = AudioAnalyser.SampleRate;

    /// <summary>Feeds every frame's onset strength to the tracker, stamped at the middle of the two windows it compares.</summary>
    private sealed class Feed(TempoTracker tracker) : IAnalysisSink
    {
        public TempoEstimate? Last;

        public void Frame(AnalysisFrame frame)
        {
            tracker.Push(frame.OnsetStrength, (long)Math.Round((frame.CentreSample - AudioAnalyser.Hop / 2.0) * 1e9 / Rate));
            if (tracker.Frames % 100 == 0 && tracker.Update() is { } e)
                Last = e;
        }
    }

    private static (TempoEstimate? Estimate, long EndSample) Run(TestSignal signal, double seconds, TempoTracker? tracker = null, long from = 0)
    {
        tracker ??= new TempoTracker();
        var analyser = new AudioAnalyser();
        var feed = new Feed(tracker);
        var block = new float[480];
        var end = from + (long)(seconds * Rate);
        for (var at = from; at < end; at += block.Length)
        {
            signal.Fill(block, at);
            analyser.Push(block, feed);
        }
        return (feed.Last, end);
    }

    /// <summary>The distance in ms from an instant to the nearest beat of a loop at <paramref name="bpm"/> that started at sample 0.</summary>
    private static double BeatErrorMs(long leaderNs, double bpm)
    {
        var beatNs = 60e9 / bpm;
        var k = Math.Round(leaderNs / beatNs);
        return Math.Abs(leaderNs - k * beatNs) / 1e6;
    }

    [Theory]
    [InlineData(90)]
    [InlineData(100)]
    [InlineData(120)]
    [InlineData(128)]
    [InlineData(140)]
    [InlineData(174)]
    public void ADrumLoopsTempoAndBeatAreFoundWithinSixSeconds(double bpm)
    {
        var (e, _) = Run(new DrumSignal(bpm), 6);
        Assert.NotNull(e);
        Assert.InRange(e!.Value.Bpm, bpm - 0.5, bpm + 0.5);
        Assert.True(BeatErrorMs(e.Value.BeatLeaderNs, bpm) <= 15, $"the beat is placed {BeatErrorMs(e.Value.BeatLeaderNs, bpm):0.#} ms off");
        Assert.True(e.Value.Confidence > 0.3, $"confidence {e.Value.Confidence:0.##}");
    }

    [Theory]
    [InlineData(90)]
    [InlineData(128)]
    [InlineData(174)]
    public void AClickLoopsTempoIsFound(double bpm)
    {
        var (e, _) = Run(new ClickSignal(bpm), 6);
        Assert.NotNull(e);
        Assert.InRange(e!.Value.Bpm, bpm - 0.5, bpm + 0.5);
        Assert.True(BeatErrorMs(e.Value.BeatLeaderNs, bpm) <= 15, $"the beat is placed {BeatErrorMs(e.Value.BeatLeaderNs, bpm):0.#} ms off");
    }

    [Fact]
    public void AChangeOfTempoIsFollowed()
    {
        var tracker = new TempoTracker();
        var (first, end) = Run(new DrumSignal(120), 8, tracker);
        Assert.InRange(first!.Value.Bpm, 119.5, 120.5);
        var (second, _) = Run(new DrumSignal(128), 8, tracker, end);
        Assert.InRange(second!.Value.Bpm, 127.5, 128.5);
    }

    [Fact]
    public void FlatSoundGivesNoTempoAndShortSoundWaits()
    {
        var silence = new float[Rate];
        var tracker = new TempoTracker();
        var feed = new Feed(tracker);
        var analyser = new AudioAnalyser();
        for (var s = 0; s < 6; s++)
            analyser.Push(silence, feed);
        Assert.Null(feed.Last);
        Assert.Null(new TempoTracker().Update());
        Assert.Throws<ArgumentOutOfRangeException>(() => new TempoTracker(200, 100));
    }
}
