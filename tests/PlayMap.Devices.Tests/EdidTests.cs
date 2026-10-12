using System.Text;
using PlayMap.Devices.Display;
using PlayMap.Devices.Model;

namespace PlayMap.Devices.Tests;

/// <summary>The EDID base block a GPU output reads from a projector: manufacturer, name, serial text and the
/// native mode with its refresh, checked against its checksum, and matched to a networked projector's identity.</summary>
public class EdidTests
{
    /// <summary>A 1920×1080 at 60 Hz block (148.5 MHz, 2200 × 1125 total) for "PAN" PT-RZ120, serial SH1234567.</summary>
    private static byte[] Block()
    {
        var b = new byte[128];
        new byte[] { 0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00 }.CopyTo(b, 0);
        var packed = (('P' - 'A' + 1) << 10) | (('A' - 'A' + 1) << 5) | ('N' - 'A' + 1);
        b[8] = (byte)(packed >> 8);
        b[9] = (byte)packed;
        b[10] = 0x34;
        b[11] = 0x12;
        b[17] = 33;
        b[18] = 1;
        b[19] = 4;
        // Detailed timing: clock 14850 × 10 kHz, h 1920 + 280, v 1080 + 45.
        var t = new byte[] { 0x02, 0x3A, 0x80, 0x18, 0x71, 0x38, 0x2D, 0x40 };
        t.CopyTo(b, 54);
        Descriptor(b, 72, 0xFC, "PT-RZ120");
        Descriptor(b, 90, 0xFF, "SH1234567");
        Descriptor(b, 108, 0x10, "");
        var sum = 0;
        for (var i = 0; i < 127; i++)
            sum += b[i];
        b[127] = (byte)((256 - (sum & 0xFF)) & 0xFF);
        return b;
    }

    private static void Descriptor(byte[] b, int at, byte tag, string text)
    {
        b[at + 3] = tag;
        var bytes = Encoding.ASCII.GetBytes(text + "\n").Concat(Enumerable.Repeat((byte)0x20, 13)).Take(13).ToArray();
        if (text.Length > 0)
            bytes.CopyTo(b, at + 5);
    }

    [Fact]
    public void TheBaseBlockGivesMakerNameSerialAndNativeMode()
    {
        var info = Edid.Parse(Block(), out var problem)!;
        Assert.Equal("", problem);
        Assert.Equal("PAN", info.Manufacturer);
        Assert.Equal(0x1234, info.ProductCode);
        Assert.Equal("PT-RZ120", info.Name);
        Assert.Equal("SH1234567", info.SerialText);
        Assert.Equal(2023, info.Year);
        Assert.Equal("1.4", info.Version);
        Assert.Equal((1920, 1080), (info.NativeWidth, info.NativeHeight));
        Assert.Equal(60.0, info.RefreshHz, 3);
    }

    [Fact]
    public void ABadChecksumOrHeaderIsRefusedWithTheReason()
    {
        var b = Block();
        b[100] ^= 0x01;
        Assert.Null(Edid.Parse(b, out var problem));
        Assert.Contains("checksum", problem, StringComparison.Ordinal);
        Assert.Null(Edid.Parse(new byte[64], out problem));
        Assert.Contains("128 bytes", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void AnOutputIsMatchedToANetworkedProjectorBySerialThenModel()
    {
        var info = Edid.Parse(Block(), out _)!;
        Assert.True(info.Matches(new DeviceState { LinkId = "dv1", Serial = "SH1234567" }));
        Assert.False(info.Matches(new DeviceState { LinkId = "dv1", Serial = "OTHER", Model = "PT-RZ120" }));
        Assert.True((info with { SerialText = "" }).Matches(new DeviceState { LinkId = "dv1", Model = "PT-RZ120" }));
    }
}
