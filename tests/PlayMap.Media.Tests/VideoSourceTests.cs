using PlayMap.Core.Media;
using PlayMap.Core.Time;
using PlayMap.Media.Frames;
using PlayMap.Media.Sources;

namespace PlayMap.Media.Tests;

/// <summary>The clip source: frame-exact acquire on the media timeline, seeks, preroll, the gapless loop, rate,
/// colour metadata and a clean close while decoding. Every check reads the frame index painted into the
/// picture, so "the right frame" is a fact about pixels, not about timestamps.</summary>
public class VideoSourceTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private static int IndexAt(FfmpegVideoSource source, long t, bool playing = false, bool looping = false)
    {
        source.Prepare(t, playing, 1);
        Assert.True(source.WaitUntilReady(t, Wait), $"not ready at {t} ns: {source.Health.Status}");
        using var lease = source.Acquire(t);
        Assert.NotNull(lease);
        // A looping source's times run on across iterations; the wrapped request is behind them.
        Assert.True(looping || lease.PtsNs <= t, $"frame at {lease.PtsNs} ns answered for {t} ns");
        return TestMedia.ReadIndex(lease);
    }

    private static async Task AssertFrameExact(string clip, FrameRate rate)
    {
        using var source = new FfmpegVideoSource(TestMedia.Clip(clip));
        Assert.True(await source.Opened, source.Health.Status);
        var frames = rate.FrameAt(source.Info.DurationNs - 1) + 1;
        // Frame starts, the nanosecond before them, middles — forwards, then jumps both ways.
        var times = new List<long>();
        for (var n = 0L; n < frames; n += 7)
        {
            times.Add(rate.TimeOf(n));
            times.Add(rate.TimeOf(n) + rate.FrameDurationNs / 2);
            if (n > 0)
                times.Add(rate.TimeOf(n) - 1);
        }
        times.AddRange([rate.TimeOf(frames - 1), rate.TimeOf(3), rate.TimeOf(frames / 2) + 1, rate.TimeOf(1)]);
        foreach (var t in times)
            Assert.Equal(rate.FrameAt(t), IndexAt(source, t));
    }

    [Fact]
    public Task AcquireAnswersTheFrameWhoseIndexIsTheFloorOfTimeTimesRateAt25Fps() =>
        AssertFrameExact("h264_25.mp4", FrameRate.Fps25);

    [Fact]
    public Task AcquireAnswersTheFrameWhoseIndexIsTheFloorOfTimeTimesRateAt2997Fps() =>
        AssertFrameExact("h264_2997.mp4", FrameRate.Fps2997);

    [Fact]
    public async Task ASeekToOnePointThreeTwoSecondsLandsOnTheExactFrame()
    {
        using var source = new FfmpegVideoSource(TestMedia.Clip("h264_long.mp4"));
        Assert.True(await source.Opened, source.Health.Status);
        Assert.Equal(0, IndexAt(source, 0));
        Assert.Equal(33, IndexAt(source, Durations.FromSeconds(1.32)));
        Assert.Equal(412, IndexAt(source, Durations.FromSeconds(16.49)));
        Assert.Equal(33, IndexAt(source, Durations.FromSeconds(1.32)));
        Assert.Equal(299, IndexAt(source, Durations.FromSeconds(11.999)));
    }

    [Fact]
    public void PrerollMakesTheCueFrameReadyBeforeTheTimelineGetsThere()
    {
        using var source = new FfmpegVideoSource(TestMedia.Clip("h264_long.mp4"));
        var cue = Durations.FromSeconds(7.5);
        source.Prepare(cue, playing: false, 1);
        Assert.True(source.WaitUntilReady(cue, Wait), source.Health.Status);
        Assert.True(source.IsReadyAt(cue));
        using var lease = source.Acquire(cue);
        Assert.NotNull(lease);
        Assert.Equal(187, TestMedia.ReadIndex(lease));
        Assert.True(source.Health.Ready);
    }

    [Fact]
    public async Task TheLoopWrapsGaplesslyWithContinuousTimes()
    {
        var options = new VideoSourceOptions { Loop = true, InNs = Durations.FromSeconds(0.4), OutNs = Durations.FromSeconds(1.2) };
        using var source = new FfmpegVideoSource(TestMedia.Clip("h264_25.mp4"), options);
        Assert.True(await source.Opened, source.Health.Status);
        var rate = FrameRate.Fps25;
        var inNs = options.InNs;
        var len = options.OutNs - options.InNs;
        long lastPts = -1;
        // Three iterations, a frame at a time, the media time wrapped as the transport wraps it.
        for (var step = 0; step < 60; step++)
        {
            var continuous = inNs + step * rate.FrameDurationNs + rate.FrameDurationNs / 2;
            var wrapped = inNs + (continuous - inNs) % len;
            source.Prepare(wrapped, playing: true, 1);
            Assert.True(source.WaitUntilReady(wrapped, Wait), source.Health.Status);
            using var lease = source.Acquire(wrapped);
            Assert.NotNull(lease);
            Assert.Equal(rate.FrameAt(wrapped), TestMedia.ReadIndex(lease));
            if (lastPts >= 0)
                Assert.Equal(lastPts + rate.FrameDurationNs, lease.PtsNs);
            lastPts = lease.PtsNs;
            // Gapless: with the play head held on the last frame of an iteration, the decoder crosses the loop
            // point on its own and the next iteration's first frame is waiting before the timeline wraps.
            if (rate.FrameAt(wrapped) == rate.FrameAt(options.OutNs) - 1)
                Assert.True(source.WaitUntilReady(inNs + rate.FrameDurationNs / 2, Wait), "the next iteration was not decoded ahead of the loop point");
        }
        Assert.Equal(inNs + 59 * rate.FrameDurationNs, lastPts);
    }

    [Fact]
    public void ClosingWhileDecodingIsClean()
    {
        for (var i = 0; i < 12; i++)
        {
            var source = new FfmpegVideoSource(TestMedia.Clip("h264_long.mp4"));
            source.Prepare(Durations.FromSeconds(i), playing: true, 4);
            var lease = i % 2 == 0 && source.WaitUntilReady(Durations.FromSeconds(i), Wait) ? source.Acquire(Durations.FromSeconds(i)) : null;
            source.Dispose();
            Assert.Null(source.Acquire(0));
            if (lease is not null)
            {
                // A lease held across the close still reads valid memory until it is let go.
                Assert.Equal(i * 25, TestMedia.ReadIndex(lease));
                lease.Dispose();
            }
        }
    }

    [Fact]
    public void HealthNamesTheDecoderAndCountsFrames()
    {
        using var source = new FfmpegVideoSource(TestMedia.Clip("h264_25.mp4"));
        Assert.Equal(10, IndexAt(source, FrameRate.Fps25.TimeOf(10)));
        var health = source.Health;
        Assert.True(health.Ready);
        Assert.StartsWith("h264", health.Decoder, StringComparison.Ordinal);
        Assert.True(health.FramesDecoded >= 11);
        Assert.True(health.DecodeMsP95 > 0);
        Assert.Equal("320x240 25/1 h264 yuv420p mp4", $"{source.Info.Width}x{source.Info.Height} {source.Info.FrameRate} {source.Info.VideoCodec} {source.Info.PixelFormat} {source.Info.Container}");
        Assert.Equal(2_000_000_000, source.Info.DurationNs);
    }

    [Fact]
    public void YuvPassesThroughWithTheClipsColourTags()
    {
        using var source = new FfmpegVideoSource(TestMedia.Clip("h264_25.mp4"));
        source.Prepare(0, false, 1);
        Assert.True(source.WaitUntilReady(0, Wait));
        using var lease = source.Acquire(0)!;
        Assert.Equal(PixelLayout.Yuv420P, lease.Layout);
        Assert.Equal(3, lease.Planes.Count);
        Assert.Equal(ColorMatrix.Bt709, lease.Matrix);
        Assert.Equal(ColorRange.Limited, lease.Range);
        Assert.Equal(TransferFunction.Sdr, lease.Transfer);
        Assert.False(lease.HasAlpha);
        Assert.Equal(160, lease.Planes[1].Width);
        Assert.Equal(120, lease.Planes[1].Height);
        Assert.Equal(0, lease.Planes[0].Stride % FrameLayout.Alignment);
    }

    private static async Task<(PixelLayout Layout, ColorRange Range, bool Alpha)> AssertCodec(string clip, string decoder)
    {
        using var source = new FfmpegVideoSource(TestMedia.Clip(clip));
        Assert.True(await source.Opened, source.Health.Status);
        Assert.StartsWith(decoder, source.Health.Decoder, StringComparison.Ordinal);
        var rate = FrameRate.Fps25;
        foreach (var n in new long[] { 0, 1, 12, 24, 5 })
            Assert.Equal(n, IndexAt(source, rate.TimeOf(n) + rate.FrameDurationNs / 2));
        Assert.Equal(1_000_000_000, source.Info.DurationNs);
        using var lease = source.Acquire(rate.TimeOf(5))!;
        return (lease.Layout, lease.Range, lease.HasAlpha);
    }

    [Fact]
    public async Task HevcDecodesFrameExactAsPlanarYuv() =>
        Assert.Equal(PixelLayout.Yuv420P, (await AssertCodec("hevc.mp4", "hevc")).Layout);

    [Fact]
    public async Task TenBitHevcStaysTenBitYuv() =>
        Assert.Equal(PixelLayout.Yuv420P10, (await AssertCodec("hevc10.mp4", "hevc")).Layout);

    [Fact]
    public async Task Vp9InWebmDecodesFrameExactDespiteMillisecondTimestamps() =>
        Assert.Equal(PixelLayout.Yuv420P, (await AssertCodec("vp9.webm", "vp9")).Layout);

    [Fact]
    public async Task Av1DecodesFrameExact()
    {
        using (var probe = new FfmpegVideoSource(TestMedia.Clip("av1.mkv")))
        {
            if (!await probe.Opened)
            {
                // A build without an AV1 decoder must say so in words, not fail silently.
                Assert.Contains("decoder not available", probe.Health.Status, StringComparison.Ordinal);
                return;
            }
        }
        Assert.Equal(PixelLayout.Yuv420P, (await AssertCodec("av1.mkv", "")).Layout);
    }

    [Fact]
    public async Task ProRes422BecomesTenBitYuv420WithoutAnRgbStep() =>
        Assert.Equal(PixelLayout.Yuv420P10, (await AssertCodec("prores.mov", "prores")).Layout);

    [Fact]
    public async Task ProRes4444WithAlphaBecomesRgbaWithItsAlpha()
    {
        var (layout, range, alpha) = await AssertCodec("prores4444a.mov", "prores");
        Assert.Equal(PixelLayout.Rgba8, layout);
        Assert.Equal(ColorRange.Full, range);
        Assert.True(alpha);
        using var source = new FfmpegVideoSource(TestMedia.Clip("prores4444a.mov"));
        Assert.True(await source.Opened);
        Assert.True(source.Info.HasAlpha);
        Assert.Equal(0, IndexAt(source, 0));
        using var lease = source.Acquire(0)!;
        unsafe
        {
            var plane = lease.Planes[0];
            var opaque = ((byte*)plane.Data + 100 * plane.Stride)[50 * 4 + 3];
            var clear = ((byte*)plane.Data + 200 * plane.Stride)[50 * 4 + 3];
            Assert.Equal(255, opaque);
            Assert.Equal(0, clear);
        }
    }

    [Fact]
    public async Task MjpegKeepsItsFullRangeTag() =>
        Assert.Equal(ColorRange.Full, (await AssertCodec("mjpeg.mov", "mjpeg")).Range);

    [Fact]
    public async Task AClipWithoutADecoderSaysSoInItsHealth()
    {
        using var source = new FfmpegVideoSource(TestMedia.Clip("unknown.avi"));
        Assert.False(await source.Opened);
        var health = source.Health;
        Assert.False(health.Ready);
        Assert.Contains("decoder not available", health.Status, StringComparison.Ordinal);
        Assert.Null(source.Acquire(0));
        Assert.False(source.IsReadyAt(0));
    }

    [Fact]
    public async Task AFileThatIsNotThereSaysSoInItsHealth()
    {
        using var source = new FfmpegVideoSource(Path.Combine(TestMedia.Dir, "missing.mp4"));
        Assert.False(await source.Opened);
        Assert.Contains("cannot open", source.Health.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AtFourTimesTheRateTheQueueKeepsUp()
    {
        using var source = new FfmpegVideoSource(TestMedia.Clip("h264_long.mp4"));
        Assert.True(await source.Opened);
        var rate = FrameRate.Fps25;
        for (var step = 0; step < 60; step++)
        {
            var t = step * 4 * rate.FrameDurationNs + rate.FrameDurationNs / 2;
            source.Prepare(t, playing: true, 4);
            Assert.True(source.WaitUntilReady(t, Wait), source.Health.Status);
            using var lease = source.Acquire(t)!;
            var index = TestMedia.ReadIndex(lease);
            // Frames nothing references may be skipped at speed; the answer is never ahead of the time and at most
            // the skipped run behind it.
            Assert.InRange(index, rate.FrameAt(t) - 3, rate.FrameAt(t));
        }
    }

    [Fact]
    public async Task InRealTimeTheLoopNeverRunsDry()
    {
        var options = new VideoSourceOptions { Loop = true, InNs = Durations.FromSeconds(0.2), OutNs = Durations.FromSeconds(0.84) };
        using var source = new FfmpegVideoSource(TestMedia.Clip("h264_25.mp4"), options);
        Assert.True(await source.Opened);
        var rate = FrameRate.Fps25;
        var len = options.OutNs - options.InNs;
        source.Prepare(options.InNs, playing: false, 1);
        Assert.True(source.WaitUntilReady(options.InNs, Wait));
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var misses = new List<string>();
        var samples = 0;
        long lastPts = -1;
        // Two seconds of wall-clock playback: three loop points, a request every 5 ms like a fast render loop.
        while (clock.Elapsed < TimeSpan.FromSeconds(2))
        {
            var continuous = options.InNs + (long)(clock.Elapsed.TotalMilliseconds * 1e6);
            var wrapped = options.InNs + (continuous - options.InNs) % len;
            source.Prepare(wrapped, playing: true, 1);
            using (var lease = source.Acquire(wrapped))
            {
                samples++;
                if (lease is null || TestMedia.ReadIndex(lease) != rate.FrameAt(wrapped))
                    misses.Add($"at {wrapped} ns wanted frame {rate.FrameAt(wrapped)}, got {(lease is null ? "none" : TestMedia.ReadIndex(lease))}");
                else
                {
                    Assert.True(lease.PtsNs >= lastPts, "time went backwards across the loop point");
                    lastPts = lease.PtsNs;
                }
            }
            Thread.Sleep(5);
        }
        Assert.True(samples > 100);
        Assert.True(misses.Count == 0, string.Join("; ", misses));
        Assert.True(lastPts > len * 2, "the times did not continue across the loop points");
    }

    [Fact]
    public async Task AHardwareDecoderThatCannotOpenFallsBackToSoftware()
    {
        // No GPU on this machine: asking for VAAPI or CUDA must still play, in software, and say so.
        foreach (var device in new[] { "vaapi", "cuda", "no-such-device" })
        {
            var options = new VideoSourceOptions { Decoder = new Ffmpeg.DecoderOptions { AllowHardware = true, HardwareDevice = device } };
            using var source = new FfmpegVideoSource(TestMedia.Clip("h264_25.mp4"), options);
            Assert.True(await source.Opened, source.Health.Status);
            Assert.Equal(12, IndexAt(source, FrameRate.Fps25.TimeOf(12)));
            if (!source.Health.Hardware)
                Assert.Equal("h264", source.Health.Decoder);
        }
    }

    [Fact]
    public async Task TurningTheLoopOnWhilePlayingKeepsThePictureAndWrapsLater()
    {
        using var source = new FfmpegVideoSource(TestMedia.Clip("h264_25.mp4"));
        Assert.True(await source.Opened);
        var rate = FrameRate.Fps25;
        Assert.Equal(25, IndexAt(source, rate.TimeOf(25) + 1, playing: true));
        source.SetLoop(true, rate.TimeOf(10), rate.TimeOf(30));
        // Still on frame 25, then across the out point back to the in point: frames 25…29, 10, 11…
        var expected = new long[] { 25, 26, 27, 28, 29, 10, 11, 12 };
        for (var i = 0; i < expected.Length; i++)
        {
            var continuous = rate.TimeOf(25 + i) + 1;
            var wrapped = continuous >= rate.TimeOf(30) ? continuous - rate.TimeOf(20) : continuous;
            Assert.Equal(expected[i], IndexAt(source, wrapped, playing: true, looping: true));
        }
    }
}
