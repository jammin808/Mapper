using System.Diagnostics;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using PlayMap.App.ViewModels;
using PlayMap.Core;

namespace PlayMap.App.Tests;

/// <summary>The desk shows the build it is: its title starts "PlayMap 0.2.0" and the help's About line names the whole build; the
/// desk's assembly and, on Windows, PlayMap.exe carry the same stamp as their file and product versions, and so does every assembly
/// of PlayMap's own source the desk ships with; vendored code keeps its upstream's version.</summary>
public partial class VersionTests
{
    [AvaloniaFact]
    public async Task TheTitleAndTheHelpsAboutNameThisBuild()
    {
        await using var desk = new DeskHarness();
        var window = MainWindowTests.Open(desk);
        Assert.Equal(BuildInfo.Current.Short + " · Demo show", window.Title);
        Assert.StartsWith("PlayMap " + BuildInfo.Current.Version, window.Title, StringComparison.Ordinal);

        Avalonia.Headless.HeadlessWindowExtensions.KeyPress(window, Avalonia.Input.Key.F1, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.F1, "");
        DeskHarness.Pump();
        Assert.True(desk.Main.IsHelpOpen);
        var about = MainWindowTests.Find<SelectableTextBlock>(window, "AboutBuild");
        Assert.True(about.IsEffectivelyVisible);
        Assert.Equal(BuildInfo.Current.Display, about.Text);
        Assert.Matches(@"^PlayMap \d+\.\d+\.\d+( \(build \d+, [0-9a-f]{7}\)|-dev \(local build(, [0-9a-f]{7})?\))$", about.Text);
        Screens.Save(window, "help-about");
        window.Close();
    }

    [Fact]
    public void TheDeskAndItsExeCarryOneStamp()
    {
        var current = BuildInfo.Current;
        var app = typeof(DeskAbout).Assembly;
        var parts = current.Version.Split('.');
        Assert.Equal($"{parts[0]}.{parts[1]}.0.0", app.GetName().Version?.ToString());
        Assert.Equal(current.FileVersion, app.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version);
        Assert.Equal(current.Informational, app.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);

        // Windows' file properties: read from the version resource on Windows, from the same attributes elsewhere.
        var dll = FileVersionInfo.GetVersionInfo(app.Location);
        Assert.Equal(current.FileVersion, dll.FileVersion);
        Assert.Equal(current.Informational, dll.ProductVersion);
        if (OperatingSystem.IsWindows())
        {
            // The apphost a Windows build writes beside the desk carries the desk's resource: the exe a show machine runs.
            var exe = FileVersionInfo.GetVersionInfo(Path.Combine(Path.GetDirectoryName(app.Location)!, "PlayMap.exe"));
            Assert.Equal(current.FileVersion, exe.FileVersion);
            Assert.Equal(current.Informational, exe.ProductVersion);
            Assert.Equal(current.FileVersion, string.Join('.', exe.FileMajorPart, exe.FileMinorPart, exe.FileBuildPart, exe.FilePrivatePart));
        }
    }

    [Fact]
    public void EveryAssemblyOfPlayMapsOwnSourceCarriesTheStampAndVendoredCodeItsUpstreamsVersion()
    {
        var current = BuildInfo.Current;
        var folder = Path.GetDirectoryName(typeof(DeskAbout).Assembly.Location)!;
        var all = Directory.GetFiles(folder, "PlayMap*.dll");
        var vendored = all.Where(f => Path.GetFileName(f).StartsWith("PlayMap.ThirdParty.", StringComparison.Ordinal)).ToArray();
        var own = all.Except(vendored).ToArray();
        // The desk's folder holds what the exe ships: the desk, the engine, every edge and Core.
        foreach (var name in new[] { "PlayMap.dll", "PlayMap.Engine.dll", "PlayMap.Core.dll", "PlayMap.Net.dll", "PlayMap.Media.dll", "PlayMap.Render.dll" })
            Assert.Contains(own, f => Path.GetFileName(f) == name);
        foreach (var file in own)
        {
            var info = FileVersionInfo.GetVersionInfo(file);
            Assert.True(current.FileVersion == info.FileVersion, $"{Path.GetFileName(file)}'s file version is {info.FileVersion}, not {current.FileVersion}");
            Assert.True(current.Informational == info.ProductVersion,
                $"{Path.GetFileName(file)}'s product version is {info.ProductVersion}, not {current.Informational}");
            Assert.Equal("PlayMap", info.ProductName);
        }

        // Vendored source (src/ThirdParty) keeps its upstream's four-part version, with the upstream commit and PlayMap's patch level
        // in its product version (the SDK adds the commit built): its file version does not move with PlayMap's builds.
        Assert.NotEmpty(vendored);
        foreach (var file in vendored)
        {
            var info = FileVersionInfo.GetVersionInfo(file);
            Assert.Matches(VendoredProduct(), info.ProductVersion ?? "");
            Assert.StartsWith(info.FileVersion + "+", info.ProductVersion, StringComparison.Ordinal);
        }
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"^\d+\.\d+\.\d+\.\d+\+[0-9a-f]{7}\.playmap\.\d+(\.[0-9a-f]{40,64})?$", System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex VendoredProduct();
}
