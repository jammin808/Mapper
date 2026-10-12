using System.Security.Cryptography;
using System.Text;
using PlayMap.Devices.Drivers.PjLink;
using PlayMap.Devices.Model;

namespace PlayMap.Devices.Tests;

/// <summary>PJLink's grammar as pure functions: the digest is the spec's, every class 1 reply is read, every error
/// has its meaning in words, and class 2's datagrams are told apart.</summary>
public class PjLinkProtocolTests
{
    [Fact]
    public void TheDigestEqualsTheSpecsWorkedExample()
    {
        Assert.Equal("5d8409bc1c3fa39749434aa3a5c38682", PjLinkProtocol.Digest("498e4a67", "JBMIAProjectorLink"));
    }

    [Fact]
    public void TheDigestIsTheLowercaseMd5OfTheSeedFollowedByThePassword()
    {
        var expected = Convert.ToHexString(MD5.HashData(Encoding.ASCII.GetBytes("1a2b3c4d" + "secret"))).ToLowerInvariant();
        Assert.Equal(expected, PjLinkProtocol.Digest("1a2b3c4d", "secret"));
    }

    [Fact]
    public void GreetingsSayWhetherAuthenticationIsOnAndCarryTheSeed()
    {
        Assert.Equal(PjLinkGreetingKind.Open, PjLinkProtocol.ParseGreeting("PJLINK 0\r").Kind);
        var p = PjLinkProtocol.ParseGreeting("PJLINK 1 498e4a67");
        Assert.Equal(PjLinkGreetingKind.Protected, p.Kind);
        Assert.Equal("498e4a67", p.Seed);
        Assert.Equal(PjLinkGreetingKind.AuthFailed, PjLinkProtocol.ParseGreeting("PJLINK ERRA").Kind);
        Assert.Equal(PjLinkGreetingKind.Invalid, PjLinkProtocol.ParseGreeting("HELLO").Kind);
    }

    [Fact]
    public void CommandsAreFramedWithTheClassAndACarriageReturn()
    {
        Assert.Equal("%1POWR 1\r", PjLinkProtocol.Frame(1, "POWR 1"));
        Assert.Equal("%2FREZ ?\r", PjLinkProtocol.Frame(2, "FREZ ?"));
    }

    [Fact]
    public void EveryPowerStateIsRead()
    {
        Assert.Equal(PowerState.Standby, PjLinkProtocol.Power("0"));
        Assert.Equal(PowerState.On, PjLinkProtocol.Power("1"));
        Assert.Equal(PowerState.CoolingDown, PjLinkProtocol.Power("2"));
        Assert.Equal(PowerState.WarmingUp, PjLinkProtocol.Power("3"));
        Assert.Equal(PowerState.Unknown, PjLinkProtocol.Power("7"));
    }

    [Fact]
    public void ErstsSixDigitsBecomeFaultsInPlainWordsWithTheirSeverity()
    {
        Assert.Empty(PjLinkProtocol.Faults("000000")!.Value);
        var f = PjLinkProtocol.Faults("120002")!.Value;
        Assert.Equal(3, f.Length);
        Assert.Equal(new DeviceFault("fan: warning", FaultSeverity.Warning), f[0]);
        Assert.Equal(new DeviceFault("lamp: error", FaultSeverity.Error), f[1]);
        Assert.Equal(new DeviceFault("other: error", FaultSeverity.Error), f[2]);
        Assert.Null(PjLinkProtocol.Faults("0000"));
        Assert.Null(PjLinkProtocol.Faults("00000x"));
    }

    [Fact]
    public void LampPairsGiveTheHoursAndWhetherEachLampIsLit()
    {
        var one = PjLinkProtocol.Lamps("1234 1")!.Value;
        Assert.Equal(new[] { 1234 }, one.Hours);
        Assert.Equal(new[] { true }, one.Lit);
        var two = PjLinkProtocol.Lamps("100 0 2500 1")!.Value;
        Assert.Equal(new[] { 100, 2500 }, two.Hours);
        Assert.Equal(new[] { false, true }, two.Lit);
        Assert.Null(PjLinkProtocol.Lamps("1234"));
        Assert.Null(PjLinkProtocol.Lamps("abc 1"));
        Assert.Null(PjLinkProtocol.Lamps("12 2"));
    }

    [Fact]
    public void AvmtSaysWhetherThePictureIsMuted()
    {
        Assert.True(PjLinkProtocol.PictureMuted("11"));
        Assert.True(PjLinkProtocol.PictureMuted("31"));
        Assert.False(PjLinkProtocol.PictureMuted("10"));
        Assert.False(PjLinkProtocol.PictureMuted("30"));
        Assert.False(PjLinkProtocol.PictureMuted("21"));
        Assert.False(PjLinkProtocol.PictureMuted("20"));
        Assert.Null(PjLinkProtocol.PictureMuted("99"));
    }

    [Fact]
    public void InputCodesAreAFamilyAndANumberAndReadAsWords()
    {
        Assert.Equal("digital 1", PjLinkProtocol.InputWords("31"));
        Assert.Equal("RGB 2", PjLinkProtocol.InputWords("12"));
        Assert.Equal("network 1", PjLinkProtocol.InputWords("51"));
        Assert.True(PjLinkProtocol.IsInputCode("31"));
        Assert.True(PjLinkProtocol.IsInputCode("3A"));
        Assert.False(PjLinkProtocol.IsInputCode("71"));
        Assert.False(PjLinkProtocol.IsInputCode("3a"));
        Assert.False(PjLinkProtocol.IsInputCode("310"));
    }

    [Fact]
    public void RepliesCarryTheirClassCommandAndValueOrError()
    {
        var ok = PjLinkProtocol.ParseReply("%1POWR=OK\r")!;
        Assert.True(ok.IsOk);
        Assert.Equal("POWR", ok.Command);
        var value = PjLinkProtocol.ParseReply("%2IRES=1920x1080")!;
        Assert.Equal(2, value.Class);
        Assert.Equal("1920x1080", value.Value);
        var err = PjLinkProtocol.ParseReply("%1INPT=ERR2")!;
        Assert.True(err.IsError);
        Assert.Equal("ERR2", err.Error);
        Assert.Null(PjLinkProtocol.ParseReply("PJLINK 0"));
        Assert.Null(PjLinkProtocol.ParseReply("%1POWR"));
    }

    [Fact]
    public void EveryErrorCodeIsMappedToItsMeaning()
    {
        Assert.Contains("undefined command", PjLinkProtocol.ErrorWords("ERR1"), StringComparison.Ordinal);
        Assert.Contains("out of parameter", PjLinkProtocol.ErrorWords("ERR2"), StringComparison.Ordinal);
        Assert.Contains("unavailable now", PjLinkProtocol.ErrorWords("ERR3"), StringComparison.Ordinal);
        Assert.Contains("projector failure", PjLinkProtocol.ErrorWords("ERR4"), StringComparison.Ordinal);
        Assert.Contains("authentication failed", PjLinkProtocol.ErrorWords("ERRA"), StringComparison.Ordinal);
    }

    [Fact]
    public void InputResolutionReadsARasterNoSignalAndUnknown()
    {
        var s = PjLinkProtocol.Resolution("3840x2160");
        Assert.Equal((3840, 2160), (s.Width, s.Height));
        Assert.Equal("no signal", PjLinkProtocol.Resolution("-").Text);
        Assert.False(PjLinkProtocol.Resolution("*").HasRaster);
    }

    [Fact]
    public void Class2DatagramsAreToldApart()
    {
        Assert.Equal(new PjLinkNotice(PjLinkNoticeKind.LinkUp, "00:11:22:33:44:55"), PjLinkProtocol.ParseNotice("%2LKUP=00:11:22:33:44:55\r"));
        Assert.Equal(PjLinkNoticeKind.SearchAck, PjLinkProtocol.ParseNotice("%2ACKN=00:11:22:33:44:55")!.Kind);
        Assert.Equal(new PjLinkNotice(PjLinkNoticeKind.Errors, "002000"), PjLinkProtocol.ParseNotice("%2ERST=002000"));
        Assert.Equal(PjLinkNoticeKind.Power, PjLinkProtocol.ParseNotice("%2POWR=1")!.Kind);
        Assert.Equal(PjLinkNoticeKind.Input, PjLinkProtocol.ParseNotice("%2INPT=31")!.Kind);
        Assert.Equal(PjLinkNoticeKind.Search, PjLinkProtocol.ParseNotice("%2SRCH\r")!.Kind);
        Assert.Null(PjLinkProtocol.ParseNotice("%1POWR=1"));
        Assert.Null(PjLinkProtocol.ParseNotice("hello"));
    }
}
