using Avalonia;

namespace PlayMap.App;

/// <summary>The one exe. "PlayMap" opens the desk; "PlayMap engine [headless]" runs the node (the engine
/// module's entry, routed through <see cref="Link.EngineEntry"/>).</summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Length > 0 && string.Equals(args[0], "engine", StringComparison.OrdinalIgnoreCase))
            return Link.EngineEntry.Run(args);
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    /// <summary>The desk's Avalonia set-up (also what the designer and the headless tests start from).</summary>
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<DeskApp>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
