using System.Text;
using System.Text.Json;
using PlayMap.Core;
using PlayMap.Core.Bus;
using PlayMap.Net.Bus;

namespace PlayMap.Net.Tests;

/// <summary>PlayMap's hello names the build the node runs, the same SemVer string HEALTH's "build" and MCP's server info carry, and
/// a reader keeps a peer's build only when it reads as one, never dropping the rest of the hello for it.</summary>
public sealed class HelloBuildTests
{
    private static NodeSettings Node() =>
        TestSettings.Make() with { NodeId = "a1b2c3d4", Name = "Stage left", HttpPort = 9940, WirePort = 9941, SyncPort = 9942 };

    private static string? BuildIn(byte[] datagram)
    {
        using var doc = JsonDocument.Parse(datagram);
        return doc.RootElement.TryGetProperty("build", out var b) ? b.GetString() : null;
    }

    [Fact]
    public void TheHelloNamesTheBuildItIsGivenAndThisProcesssOwnUnlessTold()
    {
        var ci = HelloService.PlayMapHello(Node(), 1760101234567, null, "RACK-1", new BuildInfo("0.2.0", 17, "abc1234"));
        var local = HelloService.PlayMapHello(Node(), 1760101234567, null, "RACK-1", new BuildInfo("0.2.0", 0, "0a1b2c3"));
        var own = HelloService.PlayMapHello(Node(), 1760101234567, null, "RACK-1");

        Assert.Equal("0.2.0+17.abc1234", BuildIn(ci.ToJson()));
        Assert.Equal("0.2.0-dev+0a1b2c3", BuildIn(local.ToJson()));
        Assert.Equal(BuildInfo.Current.Informational, BuildIn(own.ToJson()));
        Assert.Equal("0.2.0+17.abc1234", ShowBusHello.Parse(ci.ToJson())!.Build);
        Assert.Equal("0.2.0-dev+0a1b2c3", ShowBusHello.Parse(local.ToJson())!.Build);
        Assert.InRange(ci.ToJson().Length, 1, ShowBusConstants.HelloMaxBytes);
    }

    [Theory]
    [InlineData("\"0.2.0 +17\"")]
    [InlineData("\"0.2.0+17.abc1234;rm\"")]
    [InlineData("\"\"")]
    [InlineData("17")]
    [InlineData("null")]
    public void APeersBuildThatDoesNotReadAsOneIsDroppedAndTheHelloKept(string build)
    {
        var json = "{\"t\":\"playmap\",\"v\":1,\"proto\":1,\"id\":\"cccc0003\",\"name\":\"FOH\",\"host\":\"FOH-PC\",\"ports\":{\"wire\":9941},\"caps\":[\"state\"],\"clock\":1,\"build\":" + build + "}";
        var hello = ShowBusHello.Parse(Encoding.UTF8.GetBytes(json));

        Assert.NotNull(hello);
        Assert.Null(hello.Build);
        Assert.Equal("cccc0003", hello.Id);
        Assert.Equal(9941, hello.Port("wire"));
    }

    [Fact]
    public void ABuildPastTheReadersBoundIsDroppedAndAHelloWithoutOneReadsAsNone()
    {
        var longest = new string('1', ShowBusHello.MaxBuildChars);
        string Hello(string? build) =>
            "{\"t\":\"main\",\"v\":1,\"id\":\"a1b2c3d4\",\"ports\":{\"http\":9924}" + (build is null ? "" : ",\"build\":\"" + build + "\"") + "}";

        Assert.Equal(longest, ShowBusHello.Parse(Encoding.UTF8.GetBytes(Hello(longest)))!.Build);
        Assert.Null(ShowBusHello.Parse(Encoding.UTF8.GetBytes(Hello(longest + "1")))!.Build);
        Assert.Null(ShowBusHello.Parse(Encoding.UTF8.GetBytes(Hello(null)))!.Build);
    }
}
