using System.Diagnostics;
using PlayMap.Core.Time;
using PlayMap.Media.Sources;
using Xunit.Abstractions;

namespace PlayMap.Media.Tests;

/// <summary>The frame path's costs, measured: no managed allocation per frame once warm (decode thread and
/// consumer), and the software decode rate of a 1080p H.264 clip through the whole source (demux, decode,
/// copy into the pool, queue, acquire), reported beside FFmpeg's own decode-only rate on this machine.</summary>
public class PerformanceTests(ITestOutputHelper output)
{
    [Fact]
    public void EverySlotsFirstRentAllocatesNothingOnceTheLayoutIsReserved()
    {
        // A producer that runs ahead rents slots it never needed before; that must not depend on when it happens.
        TestMedia.Ensure();
        using var pool = new PlayMap.Media.Frames.FramePool(6);
        var plan = PlayMap.Media.Frames.FrameFormats.Plan(FFmpeg.AutoGen.AVPixelFormat.AV_PIX_FMT_NV12, PlayMap.Media.Frames.PlanMode.Video);
        var layout = PlayMap.Media.Frames.FrameLayout.For(plan.Layout, 1920, 1080);
        pool.Reserve(layout);
        var slots = new PlayMap.Media.Frames.FrameSlot?[pool.Capacity];
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < slots.Length; i++)
            slots[i] = pool.TryRent(layout);
        var after = GC.GetAllocatedBytesForCurrentThread();
        foreach (var s in slots)
            s!.Release();
        Assert.All(slots, s => Assert.NotNull(s));
        Assert.Equal(0, after - before);
        Assert.Equal(0, pool.InUse);
    }

    [Fact]
    public async Task PlayingAllocatesNothingPerFrameOnceWarm()
    {
        using var source = new FfmpegVideoSource(TestMedia.Clip("h264_long.mp4"));
        Assert.True(await source.Opened);
        var rate = FrameRate.Fps25;
        // Three windows of 120 frames: the first warms what is made once (lazily bound entry points, a wait
        // handle's first block); the last must cost nothing on either side.
        var decodeAt = new long[4];
        var consumer = new long[3];
        for (var n = 0; n < 480; n++)
        {
            var t = rate.TimeOf(n) + 1;
            Assert.True(source.WaitUntilReady(t, TimeSpan.FromSeconds(10)));
            if (n % 120 == 0)
                decodeAt[n / 120] = source.DecodeThreadAllocatedBytes;
            var before = GC.GetAllocatedBytesForCurrentThread();
            source.Prepare(t, true, 1);
            var lease = source.Acquire(t);
            lease?.Dispose();
            var after = GC.GetAllocatedBytesForCurrentThread();
            if (n >= 120)
                consumer[n / 120 - 1] += after - before;
            Assert.NotNull(lease);
        }
        var windows = new[] { decodeAt[2] - decodeAt[1], decodeAt[3] - decodeAt[2] };
        output.WriteLine($"decode thread per 120 frames: {windows[0]} B, {windows[1]} B; consumer: {consumer[0]} B, {consumer[1]} B, {consumer[2]} B");
        Assert.Equal(0, windows[1]);
        Assert.Equal(0, consumer[1] + consumer[2]);
    }

    /// <summary>A lock's first wait makes its wait event, and a thread's first wait its wait record, on the waiting thread; a
    /// prepared thread's wait on a prepared lock makes nothing.</summary>
    [Fact]
    public void APreparedLocksFirstWaitAllocatesNothingWhereAnUnpreparedOnesDoes()
    {
        static long WaitOnce(Lock gate, bool prepareThread)
        {
            long allocated = 0;
            gate.Enter();
            try
            {
                var waiter = new Thread(() =>
                {
                    if (prepareThread)
                        PlayMap.Media.Frames.LockWaits.PrepareThisThread();
                    var before = GC.GetAllocatedBytesForCurrentThread();
                    if (gate.TryEnter(5))
                        gate.Exit();
                    allocated = GC.GetAllocatedBytesForCurrentThread() - before;
                });
                waiter.Start();
                waiter.Join();
            }
            finally
            {
                gate.Exit();
            }
            return allocated;
        }
        var unprepared = new Lock();
        var prepared = new Lock();
        PlayMap.Media.Frames.LockWaits.Prepare(prepared);
        Assert.True(WaitOnce(unprepared, prepareThread: true) > 0, "a first wait on a lock makes its event");
        Assert.True(WaitOnce(new Lock(), prepareThread: false) > WaitOnce(new Lock(), prepareThread: true), "a thread's first wait makes its record");
        Assert.Equal(0, WaitOnce(prepared, prepareThread: true));
    }

    /// <summary>On a loaded machine the decode thread may first have to wait for the queue's lock at any frame of a show (a
    /// consumer preempted while it holds it): that wait allocates nothing either. The lock is held here across three of the
    /// decode loop's 100 ms turns, each of which takes it, so the decode thread waits for it.</summary>
    [Fact]
    public async Task AFirstWaitForTheQueuesLockInTheShowAllocatesNothing()
    {
        using var source = new FfmpegVideoSource(TestMedia.Clip("h264_long.mp4"));
        Assert.True(await source.Opened);
        var rate = FrameRate.Fps25;
        void Play(int from, int to)
        {
            for (var n = from; n < to; n++)
            {
                var t = rate.TimeOf(n) + 1;
                Assert.True(source.WaitUntilReady(t, TimeSpan.FromSeconds(10)));
                source.Prepare(t, true, 1);
                source.Acquire(t)?.Dispose();
            }
        }
        Play(0, 120);
        var before = source.DecodeThreadAllocatedBytes;
        using (var never = new ManualResetEventSlim(false))
        {
            lock (source.QueueGate)
                never.Wait(TimeSpan.FromMilliseconds(350));
        }
        Play(120, 240);
        var after = source.DecodeThreadAllocatedBytes;
        output.WriteLine($"decode thread across the wait and 120 frames: {after - before} B");
        Assert.Equal(0, after - before);
    }

    [Fact]
    public async Task A1080pH264DecodesFasterThanRealTimeInSoftware()
    {
        var clip = TestMedia.Clip("h264_1080.mp4");
        using var source = new FfmpegVideoSource(clip, new VideoSourceOptions { Decoder = new Ffmpeg.DecoderOptions { AllowHardware = false } });
        Assert.True(await source.Opened, source.Health.Status);
        var rate = FrameRate.Fps25;
        var frames = (int)rate.FrameAt(source.Info.DurationNs - 1) + 1;
        var clock = Stopwatch.StartNew();
        for (var n = 0; n < frames; n++)
        {
            var t = rate.TimeOf(n);
            source.Prepare(t, true, 1);
            Assert.True(source.WaitUntilReady(t, TimeSpan.FromSeconds(20)), source.Health.Status);
            using var lease = source.Acquire(t);
            Assert.NotNull(lease);
        }
        var fps = frames / clock.Elapsed.TotalSeconds;

        // FFmpeg's own decode-only rate of the same file, for scale.
        var tool = Stopwatch.StartNew();
        TestMedia.Run("-threads", "0", "-i", clip, "-f", "null", "-");
        var toolFps = frames / tool.Elapsed.TotalSeconds;
        var health = source.Health;
        output.WriteLine($"1080p H.264, {frames} frames, {Environment.ProcessorCount} cores: source {fps:0.0} fps " +
            $"(decode p95 {health.DecodeMsP95:0.00} ms/frame, {health.Decoder}); ffmpeg decode-only {toolFps:0.0} fps");
        Assert.True(fps > 25, $"{fps:0.0} fps is slower than real time");
    }

    [Fact]
    public async Task EverySeekLandsOnItsFrame()
    {
        using var source = new FfmpegVideoSource(TestMedia.Clip("h264_long.mp4"));
        Assert.True(await source.Opened);
        var rate = FrameRate.Fps25;
        // A fixed scatter of targets over the 500 frames (a seeded walk, so a failure reproduces).
        var rng = new Core.Numerics.Pcg32(42);
        var exact = 0;
        var clock = Stopwatch.StartNew();
        const int seeks = 60;
        for (var i = 0; i < seeks; i++)
        {
            var n = rng.NextInt(500);
            var t = rate.TimeOf(n) + rng.NextInt((int)rate.FrameDurationNs);
            source.Prepare(t, false, 1);
            Assert.True(source.WaitUntilReady(t, TimeSpan.FromSeconds(10)));
            using var lease = source.Acquire(t)!;
            if (TestMedia.ReadIndex(lease) == n)
                exact++;
        }
        output.WriteLine($"{exact}/{seeks} random seeks frame-exact, {clock.Elapsed.TotalMilliseconds / seeks:0.0} ms per seek (320x240, GOP 50)");
        Assert.Equal(seeks, exact);
    }
}
