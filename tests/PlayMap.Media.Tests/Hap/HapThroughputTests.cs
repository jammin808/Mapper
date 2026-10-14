using System.Diagnostics;
using PlayMap.Core.Media.Hap;
using PlayMap.Media.Hap;
using Xunit.Abstractions;

namespace PlayMap.Media.Tests.Hap;

/// <summary>The second stage's speed on this machine, recorded: the allocation-free decoder against Snappier on the same
/// synthetic 4K HAP Q chunks, and a 4K HAP Q clip FFmpeg's encoder made, played through the source. Measurements, never a
/// pass/fail ceiling (the integration gate runs them serially).</summary>
[Trait("Category", "Perf")]
public sealed class HapThroughputTests(ITestOutputHelper log)
{
    [Fact]
    public void TheSecondStageIsTimedAgainstTheReferenceCodec()
    {
        const int texture = 3840 * 2160;
        var plan = new HapChunkOut[16];
        var count = HapChunking.Plan(texture / 16, 16, 16, plan);
        var seed = 12345u;
        var streams = Enumerable.Range(0, count).Select(i => HapThroughput.Synthetic(plan[i].Length, ref seed)).ToArray();
        var output = new byte[texture];
        double Time(Action<byte[], int> decode)
        {
            for (var i = 0; i < count; i++)
                decode(streams[i], i);
            var t = Stopwatch.StartNew();
            for (var round = 0; round < 5; round++)
                for (var i = 0; i < count; i++)
                    decode(streams[i], i);
            return t.Elapsed.TotalMilliseconds / 5;
        }
        var ours = Time((s, i) => Assert.True(SnappyBlock.TryDecompress(s, output.AsSpan(plan[i].Offset, plan[i].Length), out _)));
        var reference = new byte[texture];
        var theirs = Time((s, i) => Snappier.Snappy.Decompress(s, reference.AsSpan(plan[i].Offset, plan[i].Length)));
        Assert.Equal(reference, output);
        var ms = HapThroughput.DecodeMsPer4KFrame(5);
        log.WriteLine($"4K HAP Q second stage, one core: {ours:0.00} ms (this decoder), {theirs:0.00} ms (Snappier), median of runs {ms:0.00} ms; "
                      + $"{Environment.ProcessorCount} cores → {(int)(Environment.ProcessorCount * 16.67 / ms)} layers at 4K60");
        Assert.True(ours > 0 && theirs > 0 && ms > 0);
    }

    [Fact]
    public async Task A4KHapQClipPlaysThroughTheSource()
    {
        TestMedia.Ensure();
        var path = Path.Combine(TestMedia.Dir, "uhd_hapq.mov");
        if (!File.Exists(path))
            TestMedia.Run("-f", "lavfi", "-i", "testsrc2=s=3840x2160:r=60:d=1,format=rgba", "-frames:v", "30", "-c:v", "hap", "-format", "hap_q",
                "-chunks", "16", path);
        using var source = new HapVideoSource(path);
        Assert.True(await source.Opened, source.Health.Status);
        var rate = new Core.Time.FrameRate(60, 1);
        var t = Stopwatch.StartNew();
        for (var n = 0; n < 30; n++)
        {
            var at = rate.TimeOf(n);
            source.Prepare(at, true, 1);
            Assert.True(source.WaitUntilReady(at, TimeSpan.FromSeconds(30)), source.Health.Status);
            using var lease = source.Acquire(at);
            Assert.NotNull(lease);
        }
        var h = source.Health;
        log.WriteLine($"4K HAP Q, 16 chunks: 30 frames in {t.Elapsed.TotalMilliseconds:0} ms; stage p95 {h.Hap!.StageMsP95:0.00} ms; "
                      + $"second stage {h.Hap.SecondStage}; read-ahead {source.ReadAhead.Frames} frames ({source.ReadAhead.Binds})");
        Assert.Equal(16, h.Hap.Chunks);
    }
}
