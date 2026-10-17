using System.Reflection;

namespace PlayMap.Core.Tests.Versions;

/// <summary>BuildInfo reads the stamp in both of its shapes, back to the same string, and refuses to see a numbered build in
/// anything else; this process's BuildInfo is the stamp PlayMap.Core was built with.</summary>
public sealed class BuildInfoTests
{
    [Fact]
    public void ACiStampReadsAsItsVersionBuildAndCommit()
    {
        var b = BuildInfo.Parse("0.2.0+17.abc1234");
        Assert.Equal(new BuildInfo("0.2.0", 17, "abc1234"), b);
        Assert.False(b.IsLocal);
        Assert.Equal("0.2.0+17.abc1234", b.Informational);
        Assert.Equal("0.2.0.17", b.FileVersion);
        Assert.Equal("PlayMap 0.2.0 (build 17, abc1234)", b.Display);
        Assert.Equal("PlayMap 0.2.0", b.Short);
    }

    [Fact]
    public void ALocalStampReadsAsBuildZeroWithItsCommitIfAny()
    {
        var withCommit = BuildInfo.Parse("0.2.0-dev+abc1234");
        Assert.Equal(new BuildInfo("0.2.0", 0, "abc1234"), withCommit);
        Assert.True(withCommit.IsLocal);
        Assert.Equal("0.2.0-dev+abc1234", withCommit.Informational);
        Assert.Equal("0.2.0.0", withCommit.FileVersion);
        Assert.Equal("PlayMap 0.2.0-dev (local build, abc1234)", withCommit.Display);
        Assert.Equal("PlayMap 0.2.0-dev", withCommit.Short);

        var outsideACheckout = BuildInfo.Parse("0.2.0-dev");
        Assert.Equal(new BuildInfo("0.2.0", 0, ""), outsideACheckout);
        Assert.Equal("0.2.0-dev", outsideACheckout.Informational);
        Assert.Equal("PlayMap 0.2.0-dev (local build)", outsideACheckout.Display);
    }

    [Theory]
    [InlineData("0.2.0+0.abc1234")]       // 0 is a local build's number
    [InlineData("0.2.0+65536.abc1234")]   // past the fourth part's bound
    [InlineData("0.2.0+17.ABC1234")]      // the stamp writes the commit in lower case
    [InlineData("0.2.0+17.abc12345")]     // the stamp keeps seven hex digits
    [InlineData("0.2.0+17")]
    [InlineData("0.2.0+abc1234")]
    public void NothingButTheCiShapeReadsAsANumberedBuild(string stamp)
    {
        var b = BuildInfo.Parse(stamp);
        Assert.Equal(new BuildInfo("0.2.0", 0, ""), b);
        Assert.True(b.IsLocal);
    }

    [Fact]
    public void TheFourPartBoundIsTheLastNumberedBuild()
    {
        Assert.Equal(65535, BuildInfo.MaxBuild);
        Assert.Equal(new BuildInfo("0.2.0", 65535, "abc1234"), BuildInfo.Parse("0.2.0+65535.abc1234"));
        Assert.Equal("0.2.0.65535", BuildInfo.Parse("0.2.0+65535.abc1234").FileVersion);
    }

    [Theory]
    [InlineData(null, "0.0.0")]
    [InlineData("", "0.0.0")]
    [InlineData("1.0.0", "1.0.0")]
    [InlineData("1.0.0+0123456789abcdef0123456789abcdef01234567", "1.0.0")]
    [InlineData("1.2.3.4", "0.0.0")]
    public void AForeignOrMissingStampReadsAsALocalBuildOfTheVersionItStartsWith(string? stamp, string version)
    {
        Assert.Equal(new BuildInfo(version, 0, ""), BuildInfo.Parse(stamp));
    }

    [Theory]
    [InlineData("0.2.0+17.abc1234")]
    [InlineData("0.2.0+65535.0000000")]
    [InlineData("0.2.0-dev+abc1234")]
    [InlineData("0.2.0-dev")]
    [InlineData("12.345.6789+1.fedcba9")]
    public void EachShapeReadsBackToTheSameString(string stamp) => Assert.Equal(stamp, BuildInfo.Parse(stamp).Informational);

    [Fact]
    public void ThisProcessReadsTheStampCoreWasBuiltWith()
    {
        var core = typeof(BuildInfo).Assembly;
        var current = BuildInfo.Current;
        Assert.Equal(core.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion, current.Informational);
        Assert.Equal(core.GetCustomAttribute<AssemblyFileVersionAttribute>()!.Version, current.FileVersion);
        Assert.Matches(@"^\d{1,5}\.\d{1,5}\.\d{1,5}$", current.Version);
        var parts = current.Version.Split('.').Select(p => int.Parse(p, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        Assert.Equal(new Version(parts[0], parts[1], 0, 0), core.GetName().Version);
    }
}
