using PlayMap.Core.Dmx;
using PlayMap.Core.Led;

namespace PlayMap.Core.Tests.Led;

/// <summary>LED output's packets against vectors built by hand from the offsets of the Art-Net 4 specification ("ArtDmx", "ArtSync",
/// "ArtPoll", "ArtPollReply"), ANSI E1.31-2018 (§4 root layer, §6.2 data framing, §6.3 synchronization, §7 DMP) and the DDP
/// specification (header bytes 0–9). Readers refuse what is short or garbled, with the reason.</summary>
public sealed class LedCodecTests
{
    private static byte[] Id => "Art-Net\0"u8.ToArray();

    [Fact]
    public void ArtDmxIsTheSpecificationsLayoutWithAnEvenLength()
    {
        var buf = new byte[600];
        var n = ArtNet.WriteDmx(buf, 7, 0x1234, [10, 20, 30]);
        byte[] expected = [.. Id, 0x00, 0x50, 0x00, 0x0E, 7, 0, 0x34, 0x12, 0x00, 0x04, 10, 20, 30, 0];
        Assert.Equal(expected, buf.AsSpan(0, n).ToArray());
        // Port-Address 32767: SubUni 0xFF, Net 0x7F (7 bits).
        n = ArtNet.WriteDmx(buf, 1, 32767, new byte[512]);
        Assert.Equal(18 + 512, n);
        Assert.Equal((0xFF, 0x7F, 0x02, 0x00), (buf[14], buf[15], buf[16], buf[17]));
    }

    [Fact]
    public void ArtSyncIsFourteenBytes()
    {
        var buf = new byte[32];
        var n = ArtNet.WriteSync(buf);
        Assert.Equal([.. Id, 0x00, 0x52, 0x00, 0x0E, 0, 0], buf.AsSpan(0, n).ToArray());
    }

    [Fact]
    public void ArtPollIsTheTwentyTwoByteFormWithEstaManAndOem()
    {
        var buf = new byte[32];
        var n = LedArtNet.WritePoll(buf);
        byte[] expected = [.. Id, 0x00, 0x20, 0x00, 0x0E, 0x02, 0x00, 0x7F, 0xFF, 0x00, 0x00, 0x7F, 0xF0, 0x00, 0xFF];
        Assert.Equal(22, n);
        Assert.Equal(expected, buf.AsSpan(0, n).ToArray());
        // Control's input reads it as a poll (receivers accept 14 bytes or more).
        Assert.True(ArtNet.IsPoll(buf.AsSpan(0, n)));
    }

    /// <summary>An ArtPollReply built byte by byte: IP 10.0.0.21, port 0x1936, NetSwitch 1, SubSwitch 2, Oem 0x1234, EstaMan 0x5678,
    /// ShortName "PixLite16", LongName, two output ports (SwOut 3 and 4) and an input port, MAC, RefreshRate 100 Hz.</summary>
    private static byte[] Reply(int refreshHi = 0, int refreshLo = 100, int length = 239)
    {
        var p = new byte[Math.Max(length, 239)];
        Id.CopyTo(p, 0);
        p[8] = 0x00;
        p[9] = 0x21;
        (p[10], p[11], p[12], p[13]) = (10, 0, 0, 21);
        (p[14], p[15]) = (0x36, 0x19);
        (p[18], p[19]) = (0x01, 0x02);
        (p[20], p[21]) = (0x12, 0x34);
        (p[24], p[25]) = (0x78, 0x56);
        "PixLite16"u8.CopyTo(p.AsSpan(26));
        "PixLite16 MkII Long"u8.CopyTo(p.AsSpan(44));
        (p[172], p[173]) = (0, 3);
        (p[174], p[175], p[176]) = (0x80, 0x80, 0x40);
        (p[190], p[191], p[192]) = (3, 4, 5);
        (p[201], p[202], p[203], p[204], p[205], p[206]) = (0x00, 0x11, 0x22, 0x33, 0x44, 0x55);
        (p[226], p[227]) = ((byte)refreshHi, (byte)refreshLo);
        return p[..length];
    }

    [Fact]
    public void ArtPollReplyIsReadAtTheSpecificationsOffsets()
    {
        Assert.True(LedArtNet.TryReadReply(Reply(), out var r, out var why), why);
        Assert.Equal("10.0.0.21", r.Address);
        Assert.Equal(0x1936, r.Port);
        Assert.Equal("PixLite16", r.ShortName);
        Assert.Equal("PixLite16 MkII Long", r.LongName);
        // Port-Address = Net << 8 | Sub-Net << 4 | SwOut; the input-only port is not a universe the node receives.
        Assert.Equal([0x123, 0x124], r.Universes.ToArray());
        Assert.Equal("00:11:22:33:44:55", r.Mac);
        Assert.Equal(100, r.RefreshHz);
        Assert.Equal(0x1234, r.Oem);
        Assert.Equal(0x5678, r.EstaMan);
        // RefreshRate 0–44 reads as 44 Hz; a reply that stops before it reads as 44 too.
        Assert.True(LedArtNet.TryReadReply(Reply(0, 0), out r, out _));
        Assert.Equal(44, r.RefreshHz);
        Assert.True(LedArtNet.TryReadReply(Reply(length: 220), out r, out _));
        Assert.Equal(44, r.RefreshHz);
    }

    [Fact]
    public void ControlsOwnPollReplyReadsBack()
    {
        var buf = new byte[ArtNet.PollReplyLength];
        ArtNet.WritePollReply(buf, new ArtPollReplyInfo([127, 0, 0, 9], "PlayMap", "PlayMap node", "#0001 [0000] ok", [0x210, 0x211], 0x00FF));
        Assert.True(LedArtNet.TryReadReply(buf, out var r, out var why), why);
        Assert.Equal(("127.0.0.9", "PlayMap"), (r.Address, r.ShortName));
        Assert.Equal([0x210, 0x211], r.Universes.ToArray());
    }

    [Theory]
    [InlineData(100, "shorter than its 207-byte fixed part")]
    [InlineData(8, "shorter than an Art-Net header")]
    public void AShortReplyIsRefusedWithTheReason(int length, string words)
    {
        Assert.False(LedArtNet.TryReadReply(Reply()[..length], out _, out var why));
        Assert.Contains(words, why, StringComparison.Ordinal);
    }

    [Fact]
    public void AnotherOpCodeIsNotAReply()
    {
        var poll = new byte[22];
        LedArtNet.WritePoll(poll);
        Assert.False(LedArtNet.TryReadReply(poll, out _, out var why));
        Assert.Equal("not an ArtPollReply", why);
    }

    [Fact]
    public void SacnDataIsTheStandardsLayout()
    {
        var cid = Guid.Parse("01234567-89ab-cdef-0123-456789abcdef");
        var buf = new byte[700];
        var n = Sacn.WriteData(buf, new SacnData(cid, "PlayMap", 150, 7, 42, false, false, false, 300, 0, 3), [1, 2, 3]);
        Assert.Equal(126 + 3, n);
        var p = buf.AsSpan(0, n);
        Assert.Equal([0x00, 0x10, 0x00, 0x00], p[..4].ToArray());
        Assert.Equal("ASC-E1.17\0\0\0"u8.ToArray(), p[4..16].ToArray());
        Assert.Equal([0x70, 110 + 3], p[16..18].ToArray());
        Assert.Equal([0, 0, 0, 4], p[18..22].ToArray());
        Assert.Equal(new byte[] { 0x01, 0x23, 0x45, 0x67, 0x89, 0xAB, 0xCD, 0xEF, 0x01, 0x23, 0x45, 0x67, 0x89, 0xAB, 0xCD, 0xEF }, p[22..38].ToArray());
        Assert.Equal([0x70, 88 + 3], p[38..40].ToArray());
        Assert.Equal([0, 0, 0, 2], p[40..44].ToArray());
        Assert.Equal("PlayMap\0"u8.ToArray(), p[44..52].ToArray());
        Assert.Equal(150, p[108]);
        Assert.Equal([0, 7], p[109..111].ToArray());
        Assert.Equal(42, p[111]);
        Assert.Equal(0, p[112]);
        Assert.Equal([0x01, 0x2C], p[113..115].ToArray());
        Assert.Equal([0x70, 11 + 3], p[115..117].ToArray());
        Assert.Equal([0x02, 0xA1, 0x00, 0x00, 0x00, 0x01, 0x00, 0x04, 0x00, 1, 2, 3], p[117..129].ToArray());
        // A termination: options bit 6.
        Sacn.WriteData(buf, new SacnData(cid, "PlayMap", 100, 0, 1, false, true, false, 1, 0, 1), [0]);
        Assert.Equal(0x40, buf[112]);
    }

    [Fact]
    public void SacnSyncIsFortyNineBytes()
    {
        var cid = Guid.Parse("01234567-89ab-cdef-0123-456789abcdef");
        var buf = new byte[64];
        var n = Sacn.WriteSync(buf, new SacnSync(cid, 9, 1000));
        Assert.Equal(49, n);
        Assert.Equal([0x70, 49 - 16], buf[16..18]);
        Assert.Equal([0, 0, 0, 8], buf[18..22]);
        Assert.Equal([0x70, 49 - 38], buf[38..40]);
        Assert.Equal([0, 0, 0, 1], buf[40..44]);
        Assert.Equal(9, buf[44]);
        Assert.Equal([0x03, 0xE8, 0, 0], buf[45..49]);
    }

    [Fact]
    public void DdpIsItsTenByteHeaderThenTheData()
    {
        var buf = new byte[Ddp.HeaderLength + Ddp.MaxData];
        var n = Ddp.Write(buf, push: true, 5, Ddp.Rgb8, 1440, [9, 8, 7]);
        Assert.Equal([0x41, 0x05, 0x0B, 0x01, 0x00, 0x00, 0x05, 0xA0, 0x00, 0x03, 9, 8, 7], buf[..n]);
        n = Ddp.Write(buf, push: false, 15, Ddp.Rgbw16, 0, new byte[1440]);
        Assert.Equal((0x40, 0x0F, 0x1C, 0x05, 0xA0), (buf[0], buf[1], buf[2], buf[8], buf[9]));
        Assert.Equal(1450, n);
        Assert.Throws<ArgumentException>(() => Ddp.Write(buf, false, 1, Ddp.Rgb8, 0, new byte[1441]));
        n = Ddp.Write(buf, true, 3, Ddp.Rgb8, 6, [1, 2, 3]);
        Assert.True(Ddp.TryRead(buf.AsSpan(0, n), out var h, out var data, out var why), why);
        Assert.Equal(new DdpHeader(true, 3, Ddp.Rgb8, 1, 6, 3), h);
        Assert.Equal([1, 2, 3], data.ToArray());
    }

    [Theory]
    [InlineData(new byte[] { 0x41, 1, 0x0B }, "shorter than DDP's 10-byte header")]
    [InlineData(new byte[] { 0x81, 1, 0x0B, 1, 0, 0, 0, 0, 0, 0 }, "is not version 1")]
    [InlineData(new byte[] { 0x41, 1, 0x0B, 1, 0, 0, 0, 0, 0, 9, 1, 2 }, "does not fit the packet's 2 bytes")]
    public void AGarbledDdpPacketIsRefusedWithTheReason(byte[] packet, string words)
    {
        Assert.False(Ddp.TryRead(packet, out _, out _, out var why));
        Assert.Contains(words, why, StringComparison.Ordinal);
    }

    [Fact]
    public void TheCidIsStableAndWellFormed()
    {
        var a = LedPrograms.CidOf("0badc0de");
        Assert.Equal(a, LedPrograms.CidOf("0badc0de"));
        Assert.NotEqual(a, LedPrograms.CidOf("0badc0df"));
        var bytes = a.ToByteArray(bigEndian: true);
        Assert.Equal(0x50, bytes[6] & 0xF0);
        Assert.Equal(0x80, bytes[8] & 0xC0);
    }
}
