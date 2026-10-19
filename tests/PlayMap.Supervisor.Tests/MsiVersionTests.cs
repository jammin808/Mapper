using PlayMap.Core;
using PlayMap.Supervisor.Install;

namespace PlayMap.Supervisor.Tests;

/// <summary>The installer's version (docs/round3/PLAN.md §1.3): MAJOR.MINOR.BUILD from a numbered build; a major or minor of 256 and
/// build 0 refused; a release lower than the last one refused with "raise MINOR".</summary>
public sealed class MsiVersionTests
{
    [Theory]
    [InlineData("0.2.0", 17, "0.2.17")]
    [InlineData("0.2.3", 18, "0.2.18")]
    [InlineData("0.3.0", 19, "0.3.19")]
    [InlineData("255.255.9", 65535, "255.255.65535")]
    public void ANumberedBuildsInstallerVersionIsMajorMinorBuild(string version, int build, string msi)
    {
        Assert.Equal(msi, MsiVersionRule.Compose(version, build, out var why));
        Assert.Equal("", why);
        Assert.Equal(msi, new BuildInfo(version, build, "abc1234").MsiVersion);
    }

    [Fact]
    public void ALaterBuildOfAPatchIsGreaterThanTheBuildBeforeIt()
    {
        Assert.Null(MsiVersionRule.Refusal("0.2.18", "0.2.17"));
        Assert.True(new Version(MsiVersionRule.Compose("0.2.3", 18, out _)!) > new Version(MsiVersionRule.Compose("0.2.0", 17, out _)!));
    }

    [Theory]
    [InlineData("256.0.0")]
    [InlineData("0.256.0")]
    public void AMajorOrMinorOf256IsRefused(string version)
    {
        Assert.Null(MsiVersionRule.Compose(version, 17, out var why));
        Assert.Equal($"{version} has a major or minor above 255, the most an installer's version holds", why);
        Assert.Equal("", new BuildInfo(version, 17, "abc1234").MsiVersion);
    }

    [Fact]
    public void BuildZeroMakesNoInstaller()
    {
        Assert.Null(MsiVersionRule.Compose("0.2.0", 0, out var why));
        Assert.Equal("an installer is made from a numbered build only: a local build's would read as 0.2.0 and could not be upgraded in order", why);
        Assert.Equal("", new BuildInfo("0.2.0", 0, "abc1234").MsiVersion);
        Assert.Null(MsiVersionRule.Compose("0.2.0", 65536, out why));
        Assert.Equal("build 65536 is past 65535", why);
    }

    [Fact]
    public void AReleaseLowerThanTheLastIsRefusedWithRaiseMinor()
    {
        var next = MsiVersionRule.Compose("0.2.0", 1, out _)!;
        Assert.Equal("0.2.1 is lower than the last release 0.2.500: raise MINOR (Version.props) and set PlayMapRunBase to the last run's number",
            MsiVersionRule.Refusal(next, "0.2.500"));
        Assert.Equal("0.2.500 is the last release's version: a release is greater than the one before it: raise MINOR (Version.props) and set PlayMapRunBase to the last run's number",
            MsiVersionRule.Refusal("0.2.500", "0.2.500"));
        Assert.Equal("0.2.1 is lower than the last release 0.3.4", MsiVersionRule.Refusal("0.2.1", "0.3.4"));
        Assert.Null(MsiVersionRule.Refusal("0.3.1", "0.2.500"));
        Assert.Null(MsiVersionRule.Refusal("0.2.1", ""));
        Assert.Equal("'0.2' is not an installer version (MAJOR.MINOR.BUILD)", MsiVersionRule.Refusal("0.2", "0.2.1"));
        Assert.Equal("'0.2.0.1' is not an installer version (MAJOR.MINOR.BUILD)", MsiVersionRule.Refusal("0.2.0.1", ""));
    }

    [Fact]
    public async Task TheVersionCheckCommandAnswersWithItsExitCode()
    {
        using var said = new StringWriter();
        Assert.Equal(Program.Refused, await Program.RunAsync(["version-check", "0.2.1", "0.2.500"], said));
        Assert.Contains("lower than the last release 0.2.500: raise MINOR", said.ToString(), StringComparison.Ordinal);
        Assert.Equal(Program.Done, await Program.RunAsync(["version-check", "0.2.18", "0.2.17"], said));
        Assert.Equal(Program.Done, await Program.RunAsync(["version-check", "0.2.18", ""], said));
        Assert.Contains("0.2.18 follows the last release (none yet)", said.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ThisBuildsInstallerVersionIsItsStampsOrNoneWhenLocal()
    {
        var current = BuildInfo.Current;
        Assert.Equal(current.IsLocal ? "" : MsiVersionRule.Compose(current.Version, current.Build, out _), current.MsiVersion);
    }
}
