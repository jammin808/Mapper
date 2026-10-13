using PlayMap.Core.Numerics;
using PlayMap.Core.Scan;

namespace PlayMap.Core.Tests.Scan;

/// <summary>The capture timing helpers: the latency estimator recovers a simulated webcam's latency from white
/// flashes, the settle detector takes exactly one steady frame per pattern and never a half-changed one, and
/// the capture schedule's windows each hold a whole camera frame of one pattern only.</summary>
public class CaptureTimingTests
{
    /// <summary>A 30 fps camera with jittered frame times, a 12 ms exposure and <paramref name="latencyMs"/> from
    /// the middle of the exposure to the frame's arrival, watching a picture that turns white at the given instants
    /// and back to black at the next.</summary>
    private static LatencyEstimator SimulateFlashes(double latencyMs, double[] changes, ulong seed)
    {
        var rng = new Pcg32(seed);
        var estimator = new LatencyEstimator();
        foreach (var c in changes)
            estimator.AddChange(c);
        const double period = 1000.0 / 30;
        const double exposure = 12;
        bool WhiteAt(double t)
        {
            var on = false;
            foreach (var c in changes)
                if (t >= c)
                    on = !on;
            return on;
        }
        for (var k = 0; k < 160; k++)
        {
            var mid = k * period + rng.Range(-2.0, 2.0);
            double lit = 0;
            const int slices = 48;
            for (var s = 0; s < slices; s++)
                lit += WhiteAt(mid - exposure / 2 + exposure * (s + 0.5) / slices) ? 1 : 0;
            var brightness = 20 + 150 * lit / slices + rng.NextGaussian() * 2;
            estimator.AddFrame(mid + latencyMs, brightness);
        }
        return estimator;
    }

    [Theory]
    [InlineData(110, 1u)]
    [InlineData(110, 2u)]
    [InlineData(65, 3u)]
    [InlineData(180, 4u)]
    public void TheLatencyEstimatorRecoversASimulatedLatencyWithinOneCameraFrame(double latency, ulong seed)
    {
        var estimator = SimulateFlashes(latency, [1000, 1600, 2200, 2800, 3400, 4000], seed);
        var estimate = estimator.Estimate();
        Assert.NotNull(estimate);
        Assert.Equal(6, estimate.Transitions);
        Assert.InRange(estimate.LatencyMs, latency - 1000.0 / 30, latency + 1000.0 / 30);
        Assert.InRange(estimate.LatencyMs, latency - 8, latency + 8);
        Assert.InRange(estimate.FramePeriodMs, 30, 37);
        Assert.StartsWith("Camera latency ", estimate.Words, StringComparison.Ordinal);
    }

    [Fact]
    public void ACameraThatSeesTheChangeWholeJustAfterThePresentIsNotMadeOutToSeeItEarly()
    {
        // A loopback camera on a headless node: it reads the finished picture 0.4 ms after each frame is presented
        // (every 16.6 ms), so the first picture after a change shows it whole. The change was not seen half a frame
        // before it was presented.
        const double period = 16.6;
        double[] changes = [1000 + 0 * period, 1000 + 20 * period, 1000 + 40 * period, 1000 + 60 * period];
        var estimator = new LatencyEstimator();
        foreach (var c in changes)
            estimator.AddChange(c);
        for (var k = -10; k < 80; k++)
        {
            var present = 1000 + k * period;
            var white = changes.Count(c => present >= c) % 2 == 1;
            estimator.AddFrame(present + 0.4, white ? 230 : 10);
        }
        var estimate = estimator.Estimate();
        Assert.NotNull(estimate);
        Assert.Equal(4, estimate.Transitions);
        Assert.InRange(estimate.LatencyMs, 0, 0.4);
    }

    [Fact]
    public void AFlashTheCameraNeverSawGivesNoEstimate()
    {
        var estimator = new LatencyEstimator();
        estimator.AddChange(500);
        for (var k = 0; k < 60; k++)
            estimator.AddFrame(k * 33.3, 40);
        Assert.Null(estimator.Estimate());
        Assert.Null(new LatencyEstimator().Estimate());
    }

    [Fact]
    public void TheMeanBrightnessOfAFrameIsItsAverageLevel()
    {
        var frame = new byte[64];
        Array.Fill(frame, (byte)100);
        Assert.Equal(100, LatencyEstimator.MeanBrightness(frame), 9);
        frame[0] = 200;
        Assert.Equal(100 + 100.0 / 64, LatencyEstimator.MeanBrightness(frame, 1), 9);
    }

    [Fact]
    public void TheSettleDetectorTakesOneSteadyFramePerPatternAndNeverAHalfChangedOne()
    {
        const int w = 64, h = 48;
        var rng = new Pcg32(9);
        byte[] Pattern(int p) => Enumerable.Range(0, w * h).Select(i => (byte)(((i % w) / (4 + p) + p) % 2 == 0 ? 200 : 30)).ToArray();
        byte[] Noisy(byte[] img) => img.Select(v => (byte)Math.Clamp(v + rng.NextGaussian() * 1.5, 0, 255)).ToArray();
        byte[] Blend(byte[] a, byte[] b) => a.Zip(b, (x, y) => (byte)((x + y) / 2)).ToArray();
        var detector = new SettleDetector(w, h);
        var accepted = new List<(int Pattern, int Frame)>();
        var frameNo = 0;
        byte[]? last = null;
        for (var p = 0; p < 6; p++)
        {
            var img = Pattern(p);
            var sequence = new List<(byte[] Frame, bool Clean)>();
            if (last is not null)
                sequence.Add((Noisy(Blend(last, img)), false));
            for (var r = 0; r < 4; r++)
                sequence.Add((Noisy(img), true));
            foreach (var (frame, clean) in sequence)
            {
                if (detector.Offer(frame) == SettleState.Settled)
                {
                    Assert.True(clean, $"a half-changed frame was taken for pattern {p}");
                    accepted.Add((p, frameNo));
                }
                frameNo++;
            }
            last = img;
        }
        Assert.Equal([0, 1, 2, 3, 4, 5], accepted.Select(a => a.Pattern));
        Assert.Equal(6, detector.Accepted);
        detector.Reset();
        Assert.Equal(0, detector.Accepted);
        Assert.Equal(SettleState.Waiting, detector.Offer(Pattern(0)));
        Assert.Equal(SettleState.Settled, detector.Offer(Pattern(0)));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void EveryCaptureWindowHoldsAWholeCameraFrameOfOnePatternOnly(bool pipelined)
    {
        var timing = new CaptureTiming(LatencyMs: 110, ExposureMs: 16, CameraFramePeriodMs: 1000.0 / 30, Pipelined: pipelined);
        var schedule = CaptureSchedule.Build(30, timing, startMs: 1000);
        Assert.Equal(30, schedule.Slots.Count);
        // Whole refreshes: 1.5 × 16 + 2 × 8 + 33.3 = 73.3 ms → 5; waiting out the latency too, 183.3 ms → 11.
        Assert.Equal(pipelined ? 5 : 11, schedule.HoldMs / timing.DisplayRefreshMs, 6);
        for (var i = 0; i < schedule.Slots.Count; i++)
        {
            var s = schedule.Slots[i];
            Assert.Equal(1000 + i * schedule.HoldMs, s.DisplayAtMs, 9);
            Assert.Equal(s.DisplayAtMs + 110 + 16 + 8, s.EarliestCaptureMs, 9);
            Assert.True(s.WindowMs >= timing.CameraFramePeriodMs, $"window {s.WindowMs:0.0} ms");
            // Any frame received inside the window exposed only while this pattern was on the glass.
            foreach (var t in new[] { s.EarliestCaptureMs, (s.EarliestCaptureMs + s.LatestCaptureMs) / 2, s.LatestCaptureMs })
            {
                var exposureStart = t - timing.LatencyMs - timing.ExposureMs / 2;
                var exposureEnd = t - timing.LatencyMs + timing.ExposureMs / 2;
                Assert.True(exposureStart >= s.DisplayAtMs && exposureEnd <= s.DisplayAtMs + schedule.HoldMs);
                Assert.Equal(i, schedule.SlotFor(t));
            }
            if (i > 0)
                Assert.Equal(-1, schedule.SlotFor((schedule.Slots[i - 1].LatestCaptureMs + s.EarliestCaptureMs) / 2));
        }
        Assert.Equal(-1, schedule.SlotFor(0));
        Assert.True(schedule.TotalMs > 30 * schedule.HoldMs);
    }
}
