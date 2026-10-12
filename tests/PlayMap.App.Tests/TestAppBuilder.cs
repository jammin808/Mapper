using Avalonia;
using Avalonia.Headless;

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
            .WithInterFont();
}
