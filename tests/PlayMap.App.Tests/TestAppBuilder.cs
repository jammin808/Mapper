using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Styling;

[assembly: AvaloniaTestApplication(typeof(PlayMap.App.Tests.TestAppBuilder))]

namespace PlayMap.App.Tests;

/// <summary>The headless desk the UI tests run on: the real application (theme, styles, fonts) drawn by Skia
/// into memory, so a test can click, read the visual tree and capture a frame.</summary>
public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<DeskApp>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .WithInterFont()
            .AfterSetup(b => b.Instance!.Styles.Add(NoHoverTips()));

    /// <summary>No window of the headless desk opens a tooltip on hover. The tooltip service opens one once its show delay has passed
    /// on the machine's clock, which no test drives, in the window's own overlay, where it takes a press aimed at what it covers: the
    /// headless pointer rests where it last pressed, so whether the next press lands would turn on how fast the runner is. A tip is
    /// read where the desk sets it (<see cref="ToolTip.GetTip"/>, <c>DeskAction.Tip</c>).</summary>
    private static Style NoHoverTips() => new(x => x.Is<TopLevel>()) { Setters = { new Setter(ToolTip.ServiceEnabledProperty, false) } };
}
