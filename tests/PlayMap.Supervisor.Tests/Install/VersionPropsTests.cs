using System.Diagnostics;
using System.Text.Json;

namespace PlayMap.Supervisor.Tests.Install;

/// <summary>Version.props as MSBuild reads it: a numbered build sets the installer's version MAJOR.MINOR.BUILD, a local build sets
/// none, and a major or minor above 255 fails every build with the words before anything is compiled.</summary>
public sealed class VersionPropsTests
{
    private const string Sha = "0123456789abcdef0123456789abcdef01234567";

    [Fact]
    public async Task ANumberedBuildSetsTheInstallersVersion()
    {
        var numbered = await Evaluate("-p:PlayMapVersion=0.2.3", "-p:PlayMapRun=318", "-p:PlayMapRunBase=300", "-p:PlayMapCommit=" + Sha);
        Assert.Equal("0.2.18", numbered.Properties["PlayMapMsiVersion"]);
        var local = await Evaluate("-p:PlayMapVersion=0.2.3");
        Assert.Equal("", local.Properties["PlayMapMsiVersion"]);
    }

    [Theory]
    [InlineData("256.0.0")]
    [InlineData("0.256.0")]
    public async Task AMajorOrMinorAbove255FailsTheBuildWithTheWords(string version)
    {
        var refused = await Evaluate("-t:PlayMapStampVersion", "-p:PlayMapVersion=" + version, "-p:PlayMapRun=17", "-p:PlayMapRunBase=0",
            "-p:PlayMapCommit=" + Sha);
        Assert.NotEqual(0, refused.Exit);
        Assert.Contains($"PlayMapVersion '{version}' has a major or minor above 255, the most an MSI's ProductVersion holds: no build may carry a version a release cannot install.",
            refused.Output, StringComparison.Ordinal);
        Assert.Equal("", refused.Properties["PlayMapMsiVersion"]);
        Assert.Equal("", refused.Properties["InformationalVersion"]);
    }

    [Fact]
    public async Task Version255IsTheLastAnInstallerHolds()
    {
        var last = await Evaluate("-t:PlayMapStampVersion", "-p:PlayMapVersion=255.255.0", "-p:PlayMapRun=17", "-p:PlayMapRunBase=0",
            "-p:PlayMapCommit=" + Sha);
        Assert.True(last.Exit == 0, last.Output);
        Assert.Equal("255.255.17", last.Properties["PlayMapMsiVersion"]);
    }

    private sealed record Evaluated(int Exit, string Output, IReadOnlyDictionary<string, string> Properties);

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Version.props")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("no Version.props above " + AppContext.BaseDirectory);
    }

    /// <summary>PlayMap.Core's project with these global properties (and a target, where given) run by the dotnet that runs these tests,
    /// nothing compiled; the installer's and the informational version read back.</summary>
    private static async Task<Evaluated> Evaluate(params string[] properties)
    {
        var root = RepoRoot();
        var host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } h && File.Exists(h) ? h : "dotnet";
        var start = new ProcessStartInfo(host)
        {
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in (string[])["msbuild", Path.Combine(root, "src", "PlayMap.Core", "PlayMap.Core.csproj"), "-nologo", "-nodeReuse:false",
                     "-getProperty:PlayMapMsiVersion", "-getProperty:InformationalVersion", .. properties])
            start.ArgumentList.Add(a);
        foreach (var name in new[] { "PlayMapRun", "PlayMapRunBase", "PlayMapCommit", "PlayMapVersion", "MSBuildExtensionsPath", "MSBuildSDKsPath", "MSBUILD_EXE_PATH" })
            start.Environment.Remove(name);
        start.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        start.Environment["DOTNET_NOLOGO"] = "1";
        using var process = Process.Start(start)!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
        await process.WaitForExitAsync(timeout.Token);
        var text = await stdout;
        var output = text + await stderr;
        // The properties are one JSON object, after any error lines.
        var at = text.IndexOf("\n{", StringComparison.Ordinal) + 1;
        if (at == 0 && text.StartsWith('{'))
            at = 0;
        else
            Assert.True(at > 0, output);
        using var doc = JsonDocument.Parse(text[at..]);
        var values = doc.RootElement.GetProperty("Properties").EnumerateObject()
            .ToDictionary(p => p.Name, p => p.Value.GetString() ?? "", StringComparer.Ordinal);
        return new Evaluated(process.ExitCode, output, values);
    }
}
