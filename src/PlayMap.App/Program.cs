namespace PlayMap.App;

/// <summary>The one exe. "PlayMap" opens the desk; "PlayMap engine [headless]" runs the node.</summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        return 0;
    }
}
