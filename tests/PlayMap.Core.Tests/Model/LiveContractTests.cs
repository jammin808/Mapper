using PlayMap.Core.Media;

namespace PlayMap.Core.Tests.Model;

/// <summary>The live-input location reader every video I/O provider shares.</summary>
public sealed class LiveContractTests
{
    [Theory]
    [InlineData("ndi:STUDIO (Cam 1)", LiveProtocol.Ndi, "STUDIO (Cam 1)", false)]
    [InlineData("NDI:STUDIO (Cam 1)", LiveProtocol.Ndi, "STUDIO (Cam 1)", false)]
    [InlineData("ndi://10.0.0.5:5961", LiveProtocol.Ndi, "10.0.0.5:5961", true)]
    [InlineData("omt:VM (Program)", LiveProtocol.Omt, "VM (Program)", false)]
    [InlineData("omt://h:6401", LiveProtocol.Omt, "h:6401", true)]
    [InlineData("ndi:*(Cam 1)", LiveProtocol.Ndi, "*(Cam 1)", false)]
    [InlineData("ndi:* (Cam 1)", LiveProtocol.Ndi, "* (Cam 1)", false)]
    [InlineData("spout:Arena", LiveProtocol.Spout, "Arena", false)]
    [InlineData("spout:*", LiveProtocol.Spout, "*", false)]
    [InlineData("syphon:Resolume", LiveProtocol.Syphon, "Resolume", false)]
    [InlineData("rtsp://10.0.0.9:554/stream", LiveProtocol.Ffmpeg, "rtsp://10.0.0.9:554/stream", true)]
    [InlineData("srt://10.0.0.9:9000", LiveProtocol.Ffmpeg, "srt://10.0.0.9:9000", true)]
    [InlineData("/dev/video0", LiveProtocol.Ffmpeg, "/dev/video0", false)]
    [InlineData("video=USB Capture", LiveProtocol.Ffmpeg, "video=USB Capture", false)]
    public void ALocationReadsAsItsProtocolAndAddress(string location, LiveProtocol protocol, string address, bool byUrl)
    {
        Assert.True(LiveUri.TryParse(location, out var p, out var a, out var u));
        Assert.Equal((protocol, address, byUrl), (p, a, u));
        Assert.True(LiveUri.TryParse(LiveUri.Format(p, a), out var p2, out var a2, out var u2));
        Assert.Equal((p, a, u), (p2, a2, u2));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ndi:")]
    [InlineData("ndi:  ")]
    [InlineData("omt:")]
    [InlineData("spout:")]
    [InlineData("ndi:*")]
    [InlineData("ndi:ST*UDIO (Cam 1)")]
    [InlineData("ndi:STUDIO (Cam*)")]
    [InlineData("ndi:STUDIO ()")]
    [InlineData("ndi://10.0.0.5")]
    [InlineData("ndi://10.0.0.5:99999")]
    [InlineData("omt://:6401")]
    [InlineData("spout://10.0.0.5:1")]
    [InlineData("spout:Are*na")]
    [InlineData("just words")]
    [InlineData("C:\\clips\\a.mov")]
    public void AMalformedLocationIsRefused(string? location)
    {
        Assert.False(LiveUri.TryParse(location, out var p, out var a, out var u));
        Assert.Equal((LiveProtocol.Ffmpeg, "", false), (p, a, u));
    }

    [Fact]
    public void AFullNameSplitsIntoHostAndName()
    {
        Assert.Equal(("STUDIO", "Cam 1"), LiveUri.Split("STUDIO (Cam 1)"));
        Assert.Equal(("*", "Cam 1"), LiveUri.Split("*(Cam 1)"));
        Assert.Equal(("PC", "Out (2)"), LiveUri.Split("PC (Out (2))"));
        Assert.Equal(("Arena", ""), LiveUri.Split("Arena"));
    }

    [Theory]
    [InlineData("STUDIO (Cam 1)", "STUDIO (Cam 1)", true)]
    [InlineData("studio (Cam 1)", "STUDIO (Cam 1)", true)]
    [InlineData("STUDIO (cam 1)", "STUDIO (Cam 1)", false)]
    [InlineData("*(Cam 1)", "ANY-HOST (Cam 1)", true)]
    [InlineData("* (Cam 1)", "ANY-HOST (Cam 2)", false)]
    [InlineData("*", "whatever", true)]
    [InlineData("OTHER (Cam 1)", "STUDIO (Cam 1)", false)]
    public void APatternMatchesFullNamesByHostAndName(string pattern, string fullName, bool matches) =>
        Assert.Equal(matches, LiveUri.Matches(pattern, fullName));

    [Fact]
    public void AFormattedAddressTakesTheUrlFormOnlyForHostAndPort()
    {
        Assert.Equal("ndi://10.0.0.5:5961", LiveUri.Format(LiveProtocol.Ndi, "10.0.0.5:5961"));
        Assert.Equal("omt:VM (Program)", LiveUri.Format(LiveProtocol.Omt, "VM (Program)"));
        Assert.Equal("spout:Arena", LiveUri.Format(LiveProtocol.Spout, "Arena"));
        Assert.Equal("rtsp://x:554/a", LiveUri.Format(LiveProtocol.Ffmpeg, "rtsp://x:554/a"));
    }
}
