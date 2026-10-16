using System.Diagnostics;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using PlayMap.App.ViewModels;
using PlayMap.Core;

namespace PlayMap.App.Tests;

/// <summary>The desk shows the build it is: its title starts "PlayMap 0.2.0" and the help's About line names the whole build; the
/// desk's assembly and, on Windows, PlayMap.exe carry the same stamp as their file and product versions.</summary>
public class VersionTests
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
}
