using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using PlayMap.Core.Tests.Model;

namespace PlayMap.Core.Tests.Versions;

/// <summary>The stamp Version.props writes, read back from built files: a small probe assembly built with the repository's
/// Version.props as a CI build (a run number and a commit), as a local build given a commit and as a local build outside a
/// checkout; then MSBuild's own answer for this tree's checkout, for a run counted from a raised base, at and past the four-part
/// bound and for numbers outside the scheme; and PlayMap.Core as this run's build stamped it. Each probe is built in a folder of
/// its own outside the tree.</summary>
public sealed class BuildStampTests
{
    private const string Sha = "0123456789ABCDEF0123456789abcdef01234567";

    private static string VersionProps => Path.Combine(RoundTwoSample.RepoRoot(), "Version.props");

    /// <summary>The product version Version.props holds, as MSBuild reads it.</summary>
    private static async Task<string> ProductVersion()
    {
        var version = await CoreProperty("PlayMapVersion");
        Assert.Matches(@"^\d{1,5}\.\d{1,5}\.\d{1,5}$", version);
        return version;
    }

    /// <summary>The run Version.props counts this version's build numbers from, as MSBuild reads it.</summary>
    private static async Task<long> RunBase()
    {
        var text = await CoreProperty("PlayMapRunBase");
        Assert.Matches(@"^(0|[1-9]\d{0,8})$", text);
        return long.Parse(text, CultureInfo.InvariantCulture);
    }

    private static async Task<string> CoreProperty(string name)
    {
        var (exit, output, errors) = await Dotnet(RoundTwoSample.RepoRoot(), "msbuild", CoreProject, "-nologo", "-nodeReuse:false", "-getProperty:" + name);
        Assert.True(exit == 0, output + errors);
        return output.Trim();
    }

    private static string Run(long run) => "-p:PlayMapRun=" + run.ToString(CultureInfo.InvariantCulture);

    private static string CoreProject => Path.Combine(RoundTwoSample.RepoRoot(), "src", "PlayMap.Core", "PlayMap.Core.csproj");

    private static string AssemblyVersionOf(string productVersion)
    {
        var parts = productVersion.Split('.');
        return $"{parts[0]}.{parts[1]}.0.0";
    }

    [Fact]
    public async Task ACiBuildCarriesItsBuildNumberAndCommitInEveryStampedField()
    {
        // Run 17 of this version's count: its build number is 17 whatever run the count starts after.
        var v = await ProductVersion();
        var stamp = await BuildProbe(Run(await RunBase() + 17), "-p:PlayMapCommit=" + Sha);
        Assert.Equal(AssemblyVersionOf(v), stamp.AssemblyVersion?.ToString());
        Assert.Equal(v + ".17", stamp.FileVersionAttribute);
        Assert.Equal(v + "+17.0123456", stamp.InformationalAttribute);
        Assert.Equal(new BuildInfo(v, 17, "0123456"), BuildInfo.Parse(stamp.InformationalAttribute));
        // The Windows version resource: Explorer's "File version" and "Product version", an installer's four parts.
        Assert.NotNull(stamp.Resource);
        Assert.Equal(v + ".17", stamp.Resource.FixedFileVersion);
        Assert.Equal(v + ".17", stamp.Resource.String("FileVersion"));
        Assert.Equal(v + "+17.0123456", stamp.Resource.String("ProductVersion"));
        Assert.Equal("PlayMap", stamp.Resource.String("ProductName"));
    }

    [Fact]
    public async Task ALocalBuildGivenACommitIsMarkedDevWithIt()
    {
        // In a git checkout the SDK sets SourceRevisionId from .git (ALocalBuildOfThisTreeCarriesTheCommitItsCheckoutIsAt); the
        // probe is outside one, so it is given the property.
        var v = await ProductVersion();
        var stamp = await BuildProbe("-p:SourceRevisionId=" + Sha);
        Assert.Equal(AssemblyVersionOf(v), stamp.AssemblyVersion?.ToString());
        Assert.Equal(v + ".0", stamp.FileVersionAttribute);
        Assert.Equal(v + "-dev+0123456", stamp.InformationalAttribute);
        Assert.Equal(new BuildInfo(v, 0, "0123456"), BuildInfo.Parse(stamp.InformationalAttribute));
        Assert.NotNull(stamp.Resource);
        Assert.Equal(v + ".0", stamp.Resource.FixedFileVersion);
        Assert.Equal(v + "-dev+0123456", stamp.Resource.String("ProductVersion"));
    }

    [Fact]
    public async Task ALocalBuildOutsideAGitCheckoutBuildsAndIsMarkedDev()
    {
        var v = await ProductVersion();
        var stamp = await BuildProbe();
        Assert.Equal(v + ".0", stamp.FileVersionAttribute);
        Assert.Equal(v + "-dev", stamp.InformationalAttribute);
        Assert.Equal(new BuildInfo(v, 0, ""), BuildInfo.Parse(stamp.InformationalAttribute));
        Assert.Equal(v + ".0", stamp.Resource?.FixedFileVersion);
    }

    [Fact]
    public async Task ALocalBuildOfThisTreeCarriesTheCommitItsCheckoutIsAt()
    {
        // PlayMap.Core's own project in this tree, its stamp target run now (nothing is compiled): the commit is what the SDK reads
        // from the checkout, HEAD is read here from .git itself. Outside a checkout the stamp names no commit.
        var v = await ProductVersion();
        var local = await StampTarget();
        Assert.True(local.Exit == 0, local.Output);
        Assert.Equal(v + ".0", local.Properties["FileVersion"]);
        var head = GitHead.Of(RoundTwoSample.RepoRoot());
        Assert.Equal(head is null ? v + "-dev" : v + "-dev+" + head[..7], local.Properties["InformationalVersion"]);
    }

    [Fact]
    public async Task TheBuildNumberIsTheRunLessTheBaseSoARaisedBaseStartsTheCountAgain()
    {
        var v = await ProductVersion();
        var counted = await StampTarget("-p:PlayMapRun=300", "-p:PlayMapRunBase=263", "-p:PlayMapCommit=" + Sha);
        Assert.True(counted.Exit == 0, counted.Output);
        Assert.Equal(v + ".37", counted.Properties["FileVersion"]);
        Assert.Equal(v + "+37.0123456", counted.Properties["InformationalVersion"]);
        Assert.Equal(new BuildInfo(v, 37, "0123456"), BuildInfo.Parse(counted.Properties["InformationalVersion"]));

        var first = await StampTarget("-p:PlayMapRun=264", "-p:PlayMapRunBase=263", "-p:PlayMapCommit=" + Sha);
        Assert.True(first.Exit == 0, first.Output);
        Assert.Equal(v + ".1", first.Properties["FileVersion"]);

        // The run the count starts after, or one before it, has no build number of this version.
        foreach (var run in new[] { "263", "5" })
        {
            var before = await StampTarget("-p:PlayMapRun=" + run, "-p:PlayMapRunBase=263", "-p:PlayMapCommit=" + Sha);
            Assert.NotEqual(0, before.Exit);
            Assert.Contains($"Run {run} is not past PlayMapRunBase 263", before.Output, StringComparison.Ordinal);
            Assert.Equal("", before.Properties["InformationalVersion"]);
        }
    }

    [Fact]
    public async Task BuildNumber65535IsTheLastAndOnePastItIsRefusedWithWords()
    {
        var v = await ProductVersion();
        var runBase = await RunBase();
        var last = await StampTarget(Run(runBase + 65535), "-p:PlayMapCommit=" + Sha);
        Assert.True(last.Exit == 0, last.Output);
        Assert.Equal(v + ".65535", last.Properties["FileVersion"]);
        Assert.Equal(v + "+65535.0123456", last.Properties["InformationalVersion"]);

        var past = await StampTarget(Run(runBase + 65536), "-p:PlayMapCommit=" + Sha);
        Assert.NotEqual(0, past.Exit);
        Assert.Contains(string.Create(CultureInfo.InvariantCulture, $"Build number 65536 (run {runBase + 65536} less PlayMapRunBase {runBase}) is past 65535"),
            past.Output, StringComparison.Ordinal);
        Assert.Contains("set PlayMapRunBase to the last run's number", past.Output, StringComparison.Ordinal);
        Assert.Equal("", past.Properties["InformationalVersion"]);

        // A raised base carries the same bound: run 263 + 65536 is build 65536 of a count that starts after run 263.
        var raised = await StampTarget("-p:PlayMapRun=65799", "-p:PlayMapRunBase=263", "-p:PlayMapCommit=" + Sha);
        Assert.NotEqual(0, raised.Exit);
        Assert.Contains("Build number 65536 (run 65799 less PlayMapRunBase 263) is past 65535", raised.Output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("PlayMapRun '0' is not a run number", "-p:PlayMapRun=0 -p:PlayMapRunBase=0 -p:PlayMapCommit=" + Sha)]
    [InlineData("PlayMapRun '17a' is not a run number", "-p:PlayMapRun=17a -p:PlayMapRunBase=0 -p:PlayMapCommit=" + Sha)]
    [InlineData("PlayMapRunBase 'x' is not a whole number", "-p:PlayMapRunBase=x")]
    [InlineData("PlayMapRunBase '-1' is not a whole number", "-p:PlayMapRun=17 -p:PlayMapRunBase=-1 -p:PlayMapCommit=" + Sha)]
    [InlineData("names the commit it built", "-p:PlayMapRun=17 -p:PlayMapRunBase=0")]
    [InlineData("names the commit it built", "-p:PlayMapRun=17 -p:PlayMapRunBase=0 -p:PlayMapCommit=main")]
    [InlineData("without PlayMapRun", "-p:PlayMapCommit=" + Sha)]
    [InlineData("is not MAJOR.MINOR.PATCH", "-p:PlayMapVersion=0.2")]
    [InlineData("has a part past 65535", "-p:PlayMapVersion=0.70000.0")]
    public async Task AStampOutsideTheSchemeIsRefusedBeforeAnythingIsCompiled(string words, string properties)
    {
        var refused = await StampTarget(properties.Split(' '));
        Assert.NotEqual(0, refused.Exit);
        Assert.Contains(words, refused.Output, StringComparison.Ordinal);
        Assert.Equal("", refused.Properties["InformationalVersion"]);
    }

    [Fact]
    public void PlayMapCoreCarriesThisRunsStampInItsAttributesAndItsVersionResource()
    {
        var current = BuildInfo.Current;
        var stamp = Stamp.Read(typeof(BuildInfo).Assembly.Location);
        Assert.Equal(AssemblyVersionOf(current.Version), stamp.AssemblyVersion?.ToString());
        Assert.Equal(current.FileVersion, stamp.FileVersionAttribute);
        Assert.Equal(current.Informational, stamp.InformationalAttribute);
        Assert.NotNull(stamp.Resource);
        Assert.Equal(current.FileVersion, stamp.Resource.FixedFileVersion);
        Assert.Equal(current.FileVersion, stamp.Resource.String("FileVersion"));
        Assert.Equal(current.Informational, stamp.Resource.String("ProductVersion"));
        // A numbered build names its commit; a local one may (in a checkout) or may not.
        if (!current.IsLocal)
            Assert.Matches("^[0-9a-f]{7}$", current.Commit);
        Assert.InRange(current.Build, 0, BuildInfo.MaxBuild);
    }

    private sealed record Evaluated(int Exit, string Output, IReadOnlyDictionary<string, string> Properties);

    /// <summary>PlayMap.Core's project with these global properties, its stamp target run alone (nothing is compiled or written).</summary>
    private static async Task<Evaluated> StampTarget(params string[] properties)
    {
        var (exit, output, errors) = await Dotnet(RoundTwoSample.RepoRoot(), ["msbuild", CoreProject, "-nologo", "-nodeReuse:false",
            "-t:PlayMapStampVersion", "-getProperty:FileVersion", "-getProperty:InformationalVersion", .. properties]);
        // The properties are one JSON object, after any error lines.
        var start = output.IndexOf('{', StringComparison.Ordinal);
        Assert.True(start >= 0, output + errors);
        using var doc = JsonDocument.Parse(output[start..]);
        var values = doc.RootElement.GetProperty("Properties").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString() ?? "",
            StringComparer.Ordinal);
        return new Evaluated(exit, output + errors, values);
    }

    /// <summary>A one-class assembly whose folder's Directory.Build.props imports the repository's Version.props (the way the
    /// repository's own Directory.Build.props does), built with these global properties; its stamp read from the built file.</summary>
    private static async Task<Stamp> BuildProbe(params string[] properties)
    {
        var folder = Path.Combine(Path.GetTempPath(), "playmap-stamp-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(folder);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(folder, "Directory.Build.props"),
                $"<Project>\n  <PropertyGroup><Product>PlayMap</Product></PropertyGroup>\n  <Import Project=\"{VersionProps}\" />\n</Project>\n");
            var tfm = string.Create(CultureInfo.InvariantCulture, $"net{Environment.Version.Major}.{Environment.Version.Minor}");
            await File.WriteAllTextAsync(Path.Combine(folder, "Probe.csproj"),
                $"<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup><TargetFramework>{tfm}</TargetFramework><AssemblyName>StampProbe</AssemblyName></PropertyGroup>\n</Project>\n");
            await File.WriteAllTextAsync(Path.Combine(folder, "Marker.cs"), "namespace StampProbe;\n\npublic static class Marker\n{\n    public const int Value = 1;\n}\n");
            var output = Path.Combine(folder, "out");
            // No compiler server: nothing the probe starts outlives its build.
            var (exit, said, errors) = await Dotnet(folder, ["build", Path.Combine(folder, "Probe.csproj"), "-nologo", "-nodeReuse:false", "-v:q",
                "-p:UseSharedCompilation=false", "-c", "Release", "-o", output, .. properties]);
            Assert.True(exit == 0, said + errors);
            return Stamp.Read(Path.Combine(output, "StampProbe.dll"));
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    /// <summary>The dotnet that runs these tests, with nothing of this run's own build in its environment.</summary>
    private static async Task<(int Exit, string Output, string Errors)> Dotnet(string folder, params string[] args)
    {
        var host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } h && File.Exists(h) ? h : "dotnet";
        var start = new ProcessStartInfo(host)
        {
            WorkingDirectory = folder,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in args)
            start.ArgumentList.Add(a);
        foreach (var name in new[] { "PlayMapRun", "PlayMapRunBase", "PlayMapCommit", "PlayMapVersion", "MSBuildExtensionsPath", "MSBuildSDKsPath", "MSBUILD_EXE_PATH",
                     "MSBuildLoadMicrosoftTargetsReadOnly" })
            start.Environment.Remove(name);
        start.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        start.Environment["DOTNET_NOLOGO"] = "1";
        using var process = Process.Start(start)!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
        await process.WaitForExitAsync(timeout.Token);
        return (process.ExitCode, await stdout, await stderr);
    }
}
