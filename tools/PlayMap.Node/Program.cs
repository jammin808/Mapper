using PlayMap.Engine.Host;

namespace PlayMap.Node;

/// <summary>"PlayMap.Node engine [headless] …" runs the node exactly as the desk's exe does, through the same entry
/// (<see cref="EngineEntry.TryDispatch"/>); a command line that names no entry exits <see cref="EngineEntry.BadArguments"/> and says
/// what it reads on the standard error.</summary>
public static class Program
{
    public static int Main(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (EngineEntry.TryDispatch(args, out var exit))
            return exit;
        Console.Error.WriteLine(args.Length == 0
            ? "PlayMap.Node needs an entry: " + EngineEntry.Usage
            : $"'{args[0]}' is not an entry of PlayMap.Node — it reads: {EngineEntry.Usage}");
        return EngineEntry.BadArguments;
    }
}
