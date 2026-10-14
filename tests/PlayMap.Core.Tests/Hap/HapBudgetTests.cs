using PlayMap.Core.Media.Hap;

namespace PlayMap.Core.Tests.Hap;

/// <summary>The HAP layer budget's arithmetic and the read-ahead caps.</summary>
public class HapBudgetTests
{
    [Fact]
    public void TheBudgetIsTheSmallestMeasuredTermAndSaysWhichBinds()
    {
        // 4K60 HAP Q: 8.29 MB of BC3 per frame; frames stored at 1.9 MB at their largest; 4.5 ms to decode one on one core.
        var layer = HapBudget.Layer4K60(HapCodec.HapQ, 4.5, 1.9);
        Assert.Equal(8.2944, layer.TextureMbPerFrame, 6);
        var b = HapBudget.Layers(new HapNodeFacts(1900, 4, 6000), layer);
        Assert.Equal(16, b.ByDisk);      // 1900 ÷ (1.9 × 60)
        Assert.Equal(14, b.ByCores);     // 4 × 16.67 ÷ 4.5
        Assert.Equal(12, b.ByUpload);    // 6000 ÷ (8.2944 × 60)
        Assert.Equal(12, b.Layers);
        Assert.Equal("upload", b.Limit);
        Assert.Equal("upload 6.0 GB/s ÷ 498 MB/s per layer (8.3 MB of BC3 at 60 fps) = 12; disk 1.9 GB/s ÷ 114 MB/s at peak = 16; "
                     + "cores 4 × 16.7 ms ÷ 4.5 ms = 14", b.Words);

        var slowDisk = HapBudget.Layers(new HapNodeFacts(500, 4, 6000), layer);
        Assert.Equal(("disk", 4), (slowDisk.Limit, slowDisk.Layers));
        Assert.StartsWith("disk 500 MB/s ÷ 114 MB/s at peak = 4", slowDisk.Words, StringComparison.Ordinal);

        var oneChunk = HapBudget.Layers(new HapNodeFacts(1900, 4, 6000), HapBudget.Layer4K60(HapCodec.HapQ, 9, 1.9));
        Assert.Equal(("cores", 7), (oneChunk.Limit, oneChunk.Layers));
    }

    [Fact]
    public void ATermNotMeasuredTakesNoPart()
    {
        var layer = HapBudget.Layer4K60(HapCodec.Hap, 2.0);
        Assert.Equal(4.1472, layer.TextureMbPerFrame, 6);
        Assert.Equal(layer.TextureMbPerFrame, layer.PeakMbPerFrame);
        var b = HapBudget.Layers(new HapNodeFacts(0, 4, 0), layer);
        Assert.Equal(("cores", 33, -1, -1), (b.Limit, b.Layers, b.ByDisk, b.ByUpload));
        Assert.Equal("cores 4 × 16.7 ms ÷ 2.0 ms = 33; disk not measured; upload not measured", b.Words);
        var none = HapBudget.Layers(default, layer);
        Assert.Equal(("unmeasured", 0), (none.Limit, none.Layers));
    }

    [Fact]
    public void TheTimeCapBindsAtFifteenFramesOf4K60HapQ()
    {
        var hapQ = HapCodecs.FrameBytes(HapCodec.HapQ, 3840, 2160);
        var r = HapBudget.ReadAhead(250, 192, 60, hapQ);
        Assert.Equal(new HapReadAhead(15, "time", 15 * hapQ), r);

        // The byte cap binds only above 12.8 MB per frame at 60 fps (192 MB ÷ 15 frames).
        Assert.Equal("time", HapBudget.ReadAhead(250, 192, 60, 12_800_000).Binds);
        var big = HapBudget.ReadAhead(250, 192, 60, 13_000_000);
        Assert.Equal(new HapReadAhead(14, "bytes", 14 * 13_000_000L), big);

        // 8K HAP R (BC7, 1 byte a pixel: 33.2 MB) binds on bytes at five frames.
        var eightK = HapCodecs.FrameBytes(HapCodec.HapR, 7680, 4320);
        Assert.Equal(("bytes", 5), (HapBudget.ReadAhead(250, 192, 60, eightK).Binds, HapBudget.ReadAhead(250, 192, 60, eightK).Frames));

        // At 25 fps the time cap is six frames; at least one frame is always read ahead.
        Assert.Equal(6, HapBudget.ReadAhead(250, 192, 25, hapQ).Frames);
        Assert.Equal(1, HapBudget.ReadAhead(1, 1, 25, 50_000_000).Frames);
    }
}
