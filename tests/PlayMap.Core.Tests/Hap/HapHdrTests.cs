using PlayMap.Core.Media;
using PlayMap.Core.Media.Hap;
using PlayMap.Core.Model;

namespace PlayMap.Core.Tests.Hap;

/// <summary>HAP HDR's explicit interpretation: 1.0 = 203 nits (BT.2408 reference white) by default, the item's override
/// first, then the container's colr nclx transfer (ITU-T H.273 code points), then PlayMap's white tag.</summary>
public class HapHdrTests
{
    [Fact]
    public void TheDefaultIsLinearWithOneAt203Nits()
    {
        var r = HapHdr.Read(null, 0, 0);
        Assert.Equal(new HapHdrReading(TransferFunction.Linear, 203, "default"), r);
        Assert.Equal(1.0, r.Scale);
        Assert.Equal(0, r.ShaderTransfer);
        Assert.Equal("linear · white 203 nits (default)", r.Words);
        Assert.Equal(r, HapHdr.Read(ColorOverride.Auto, 1, 0));   // a BT.709 curve says nothing about float values
    }

    [Fact]
    public void TheItemsOverrideComesFirst()
    {
        var r = HapHdr.Read(ColorOverride.Auto with { WhiteNits = 100 }, 16, 0);
        Assert.Equal(new HapHdrReading(TransferFunction.Pq, 100, "item"), r);
        var linear = HapHdr.Read(new ColorOverride("linear", 100, "auto", "auto"), 16, 400);
        Assert.Equal(new HapHdrReading(TransferFunction.Linear, 100, "item"), linear);
        Assert.Equal(100 / 203.0, linear.Scale, 9);
        Assert.Equal("linear · white 100 nits (item)", linear.Words);
        var hlg = HapHdr.Read(ColorOverride.Auto with { Transfer = "HLG" }, 8, 0);
        Assert.Equal((TransferFunction.Hlg, 2, "hlg (item)"), (hlg.Transfer, hlg.ShaderTransfer, hlg.Words));
    }

    [Fact]
    public void TheContainersTransferThenTheTag()
    {
        Assert.Equal("pq (colr)", HapHdr.Read(null, 16, 0).Words);
        Assert.Equal(new HapHdrReading(TransferFunction.Hlg, 203, "colr"), HapHdr.Read(null, 18, 0));
        Assert.Equal(new HapHdrReading(TransferFunction.Linear, 203, "colr"), HapHdr.Read(null, 8, 0));
        var tagged = HapHdr.Read(null, 0, 1000);
        Assert.Equal(new HapHdrReading(TransferFunction.Linear, 1000, "tag"), tagged);
        Assert.Equal(1000 / 203.0, tagged.Scale, 9);
        Assert.Equal(TransferFunction.Linear, HapHdr.TransferOfCode(8));
        Assert.Null(HapHdr.TransferOfCode(2));
    }

    [Fact]
    public void NclxIsReadFromAColrPayload()
    {
        // 'nclx', primaries 9 (BT.2020), transfer 16 (PQ), matrix 9, full range.
        byte[] colr = [(byte)'n', (byte)'c', (byte)'l', (byte)'x', 0, 9, 0, 16, 0, 9, 0x80];
        Assert.True(HapHdr.TryParseNclx(colr, out var n));
        Assert.Equal(new Nclx(9, 16, 9, true), n);
        Assert.Equal("pq (colr)", HapHdr.Read(null, n.Transfer, 0).Words);

        byte[] nclc = [(byte)'n', (byte)'c', (byte)'l', (byte)'c', 0, 1, 0, 8, 0, 1];
        Assert.True(HapHdr.TryParseNclx(nclc, out var c));
        Assert.Equal(new Nclx(1, 8, 1, false), c);

        Assert.False(HapHdr.TryParseNclx("prof...."u8.ToArray(), out _));
        Assert.False(HapHdr.TryParseNclx(colr.AsSpan(0, 10), out _));
    }
}
