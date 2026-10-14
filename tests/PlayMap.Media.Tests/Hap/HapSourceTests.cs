using PlayMap.Core.Media;
using PlayMap.Core.Media.Hap;
using PlayMap.Core.Model;
using PlayMap.Core.Time;
using PlayMap.Media.Hap;

namespace PlayMap.Media.Tests.Hap;

/// <summary>The HAP source: clips FFmpeg's encoder made and clips of every other HAP codec play 120 frames with frame n =
/// f(time); the read-ahead stays inside its caps; nothing allocates per frame once warm; refused and corrupt frames are
/// counted with words while playback carries on; the codecs this renderer cannot sample do not open; probes name every
/// HAP codec.</summary>
public class HapSourceTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private static readonly FrameRate Rate = new(HapClips.Fps, 1);
    private static readonly HapSourceOptions All = new() { Formats = BlockFormats.Bc1 | BlockFormats.Bc3 | BlockFormats.Bc4 | BlockFormats.Bc7 | BlockFormats.Bc6h };

    private static VideoFrameLease At(HapVideoSource source, long t, bool playing = true)
    {
        source.Prepare(t, playing, 1);
        Assert.True(source.WaitUntilReady(t, Wait), $"not ready at {t} ns: {source.Health.Status}");
        var lease = source.Acquire(t);
        Assert.NotNull(lease);
        return lease;
    }

    public static TheoryData<string, int, string, PixelLayout> FfmpegClips() => new()
    {
        { "hap", 1, "snappy", PixelLayout.Bc1 },
        { "hap", 4, "none", PixelLayout.Bc1 },
        { "hap_alpha", 4, "snappy", PixelLayout.Bc3 },
        { "hap_q", 1, "none", PixelLayout.Bc3YCoCg },
        { "hap_q", 8, "snappy", PixelLayout.Bc3YCoCg },
    };

    [Theory]
    [MemberData(nameof(FfmpegClips))]
    public async Task FfmpegsClipsPlayEveryFrameAtItsTime(string format, int chunks, string compressor, PixelLayout layout)
    {
        using var source = new HapVideoSource(HapClips.Ffmpeg(format, chunks, compressor));
        Assert.True(await source.Opened, source.Health.Status);
        Assert.Equal(HapClips.Frames, Rate.FrameAt(source.Info.DurationNs - 1) + 1);
        for (var n = 0; n < HapClips.Frames; n++)
        {
            // Mid-frame times: the frame shown is floor(t × rate), read from the blocks' own pixels.
            var t = Rate.TimeOf(n) + Rate.FrameDurationNs / 2;
            using var lease = At(source, t);
            Assert.Equal(layout, lease.Layout);
            Assert.Equal((HapClips.Width, HapClips.Height), (lease.Width, lease.Height));
            Assert.Equal(n, Rate.FrameAt(lease.PtsNs));
            Assert.Equal(n, HapClips.ReadIndex(lease));
        }
        var h = source.Health;
        Assert.Equal(DecodePath.HapGpu, h.Path);
        Assert.True(h.ZeroCopy);
        Assert.NotNull(h.Hap);
        // FFmpeg's encoder cuts chunks only for its Snappy stage: an unstored frame is one chunk.
        Assert.Equal(compressor == "none" ? 1 : chunks, h.Hap.Chunks);
        Assert.Equal(compressor == "none" ? 1.0 : 0.0, h.Hap.RawShare);
        Assert.Equal(compressor == "none" ? "none" : "snappy", h.Hap.SecondStage);
        Assert.Equal(HapCodecs.TextureWord(layout), h.Hap.Texture);
        Assert.Equal(0, h.Hap.Corrupt);
        Assert.Equal(0, h.FramesDropped);
    }

    [Theory]
    [InlineData(HapCodec.HapQAlpha)]
    [InlineData(HapCodec.HapAlphaOnly)]
    [InlineData(HapCodec.HapR)]
    [InlineData(HapCodec.HapHdr)]
    [InlineData(HapCodec.Hap)]
    public async Task EveryOtherCodecPlaysItsAssembledFramesByteExact(HapCodec codec)
    {
        using var source = new HapVideoSource(HapClips.Assembled(codec), All);
        Assert.True(await source.Opened, source.Health.Status);
        Assert.Equal(codec, source.Codec);
        Assert.Equal(HapCodecs.Name(codec), source.Info.VideoCodec);
        for (var n = 0; n < HapClips.Frames; n += 7)
        {
            using var lease = At(source, Rate.TimeOf(n));
            Assert.Equal(n, Rate.FrameAt(lease.PtsNs));
            Assert.Equal(HapCodecs.Layout(codec, HapCodecs.FirstTexture(codec)), lease.Layout);
            var planes = HapClips.Planes(codec, n);
            Assert.Equal(planes.Length, lease.Planes.Count);
            for (var p = 0; p < planes.Length; p++)
                Assert.Equal(planes[p], HapClips.PlaneBytes(lease, p));
        }
        Assert.Equal(codec is HapCodec.HapQAlpha or HapCodec.HapR, source.Info.HasAlpha);
        Assert.Equal(codec == HapCodec.HapHdr ? "linear · white 203 nits (default)" : "", source.Health.Hap!.Hdr);
    }

    [Fact]
    public async Task SeeksAndTheLoopKeepFrameEqualsTimeFunction()
    {
        using var source = new HapVideoSource(HapClips.Assembled(HapCodec.HapQ, 2), All with { Loop = true });
        Assert.True(await source.Opened, source.Health.Status);
        foreach (var n in new[] { 0, 90, 3, 119, 45, 46, 7 })
        {
            using var lease = At(source, Rate.TimeOf(n) + 1000, playing: false);
            Assert.Equal(HapClips.Planes(HapCodec.HapQ, n)[0], HapClips.PlaneBytes(lease, 0));
        }
        // Past the end, the loop answers the frame at the time modulo the clip's length.
        var length = source.Info.DurationNs;
        foreach (var n in new[] { 118, 119, 120, 121, 125 })
        {
            using var lease = At(source, Rate.TimeOf(n) + 1000);
            Assert.Equal(HapClips.Planes(HapCodec.HapQ, n % HapClips.Frames)[0], HapClips.PlaneBytes(lease, 0));
            Assert.Equal(n % HapClips.Frames, Rate.FrameAt(lease.PtsNs % length));
        }
    }

    [Fact]
    public async Task ReadAheadStaysWithinItsCaps()
    {
        // 30 fps × 250 ms: seven frames by time.
        using var byTime = new HapVideoSource(HapClips.Assembled(HapCodec.HapQ, 4), All);
        Assert.True(await byTime.Opened, byTime.Health.Status);
        Assert.Equal(new HapReadAhead(7, "time", 7 * HapCodecs.FrameBytes(HapCodec.HapQ, HapClips.Width, HapClips.Height)), byTime.ReadAhead);
        for (var n = 0; n < 60; n++)
            At(byTime, Rate.TimeOf(n)).Dispose();
        Assert.InRange(byTime.ReadAheadPeak.Frames, 1, 7);

        // 1024×1024 HAP R frames are 1.05 MB each: a 3 MB cap holds two, however much time allows.
        using var byBytes = new HapVideoSource(HapClips.Assembled(HapCodec.HapR, 1, width: 1024, height: 1024, count: 30),
            All with { ReadAheadMs = 1000, ReadAheadMb = 3 });
        Assert.True(await byBytes.Opened, byBytes.Health.Status);
        Assert.Equal(("bytes", 2), (byBytes.ReadAhead.Binds, byBytes.ReadAhead.Frames));
        Assert.Equal(3_000_000, byBytes.ReadAheadCapBytes);
        for (var n = 0; n < 20; n++)
            At(byBytes, Rate.TimeOf(n)).Dispose();
        var (frames, bytes) = byBytes.ReadAheadPeak;
        Assert.InRange(frames, 1, 2);
        Assert.InRange(bytes, 1_048_576, 3_000_000);
    }

    [Fact]
    public async Task NothingAllocatesPerFrameOnceWarm()
    {
        using var source = new HapVideoSource(HapClips.Ffmpeg("hap_q", 8, "snappy"));
        Assert.True(await source.Opened, source.Health.Status);
        for (var n = 0; n < 40; n++)
            At(source, Rate.TimeOf(n)).Dispose();
        var decode = source.DecodeThreadAllocatedBytes;
        var read = source.ReadThreadAllocatedBytes;
        Assert.True(decode > 0 && read > 0);
        for (var n = 40; n < 110; n++)
            At(source, Rate.TimeOf(n)).Dispose();
        Assert.Equal(decode, source.DecodeThreadAllocatedBytes);
        Assert.Equal(read, source.ReadThreadAllocatedBytes);
    }

    [Fact]
    public async Task RefusedAndCorruptFramesAreCountedWithWordsAndPlaybackCarriesOn()
    {
        using var source = new HapVideoSource(HapClips.Assembled(HapCodec.Hap, 4, corrupt: [10], refused: [20]), All);
        Assert.True(await source.Opened, source.Health.Status);
        for (var n = 0; n < 30; n++)
        {
            using var lease = At(source, Rate.TimeOf(n) + 1000);
            // A frame that could not be decoded is not shown: the one before it holds.
            var expect = n is 10 or 20 ? n - 1 : n;
            Assert.Equal(HapClips.Planes(HapCodec.Hap, expect)[0], HapClips.PlaneBytes(lease, 0));
        }
        var h = source.Health;
        Assert.Equal(2, h.Hap!.Corrupt);
        Assert.Equal("frame 20 is 5 bytes shorter than its header says", h.Hap.LastRefusal);
        Assert.True(h.Ready);
        Assert.Equal("ready", h.Status);
    }

    [Fact]
    public async Task ACodecTheRendererCannotSampleDoesNotOpen()
    {
        using var source = new HapVideoSource(HapClips.Assembled(HapCodec.HapR), HapSourceOptions.Default);
        Assert.False(await source.Opened);
        Assert.Equal("HAP R needs BPTC textures, which this GPU's GL does not offer — optimise to HAP Q for this node", source.Health.Status);
        Assert.False(source.Health.Ready);
        Assert.Null(source.Acquire(0));
    }

    [Fact]
    public async Task HapHdrReadsTheContainersTransfer()
    {
        using var pq = new HapVideoSource(HapClips.Assembled(HapCodec.HapHdr, 1, colorTrc: 16), All);
        Assert.True(await pq.Opened, pq.Health.Status);
        Assert.Equal("pq (colr)", pq.Health.Hap!.Hdr);
        using var lease = At(pq, 0);
        Assert.Equal(TransferFunction.Pq, lease.Transfer);

        using var item = new HapVideoSource(HapClips.Assembled(HapCodec.HapHdr, 1), All with { Color = new ColorOverride("linear", 100, "auto", "auto") });
        Assert.True(await item.Opened, item.Health.Status);
        using var l2 = At(item, 0);
        Assert.Equal(TransferFunction.Linear, l2.Transfer);
        Assert.Equal(100 / 203.0, ((IHapPicture)l2).HdrScale, 9);
        Assert.Equal("linear · white 100 nits (item)", item.Health.Hap!.Hdr);
    }

    [Fact]
    public void ProbesNameEveryHapCodecAndProveTheFirstFrame()
    {
        foreach (var codec in Enum.GetValues<HapCodec>())
        {
            var probe = MediaProbe.Probe(HapClips.Assembled(codec));
            Assert.True(probe.Decodable, $"{codec}: {probe.Message}");
            Assert.Equal(HapCodecs.Name(codec), probe.Info.VideoCodec);
            Assert.Equal("hap", probe.Decoder);
            Assert.Equal(MediaKind.Video, probe.Kind);
            Assert.Equal(HapClips.Frames, probe.FrameCount);
        }
        var ffmpeg = MediaProbe.Probe(HapClips.Ffmpeg("hap_q", 4, "snappy"));
        Assert.Equal(("hap_q", true), (ffmpeg.Info.VideoCodec, ffmpeg.Decodable));
        var broken = MediaProbe.Probe(HapClips.Assembled(HapCodec.HapR, 4, corrupt: [0]));
        Assert.False(broken.Decodable);
        Assert.Equal("frame 0 has a chunk whose Snappy data is corrupt", broken.Message);
    }
}
