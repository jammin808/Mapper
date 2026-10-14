using Avalonia;

namespace PlayMap.App;

/// <summary>The one exe. "PlayMap" opens the desk; "PlayMap engine [headless]" runs the node, and each package's own process
/// word runs its process — every entry the engine's <see cref="Engine.Host.EngineEntry.TryDispatch"/> reads, before Avalonia.</summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (Engine.Host.EngineEntry.TryDispatch(args, out var exit))
            return exit;
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    /// <summary>The desk's Avalonia set-up (also what the designer and the headless tests start from).</summary>
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<DeskApp>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
