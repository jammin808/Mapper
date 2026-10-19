using System.Globalization;

namespace PlayMap.Engine;

/// <summary>How an engine process was asked to run: "PlayMap engine [headless] [--home &lt;folder&gt;]
/// [--project &lt;file&gt;] [--name &lt;node name&gt;] [--http &lt;port&gt;] [--wire &lt;port&gt;] [--no-recover]
/// [--desk-pid &lt;pid&gt;] [--supervised]".</summary>
public sealed record EngineOptions(
    bool Headless,
    string? Home,
    string? Project,
    string? Name,
    int? HttpPort,
    int? WirePort,
    bool Recover,
    int? DeskPid)
{
    /// <summary>The supervisor started this engine ("--supervised", on the supervisor's command line only): it writes the machine's
    /// pid record. Any other run (the desk's, a portable exe's, a test's) writes none.</summary>
    public bool Supervised { get; init; }

    public static EngineOptions Parse(IReadOnlyList<string> args, out string? error)
    {
        ArgumentNullException.ThrowIfNull(args);
        error = null;
        var o = new EngineOptions(false, null, null, null, null, null, true, null);
        for (var i = 0; i < args.Count; i++)
        {
            var a = args[i];
            string? Next()
            {
                if (i + 1 >= args.Count)
                    return null;
                i++;
                return args[i];
            }

            switch (a)
            {
                case "engine":
                    break;
                case "headless":
                case "--headless":
                    o = o with { Headless = true };
                    break;
                case "--home":
                    o = o with { Home = Next() };
                    break;
                case "--project":
                    o = o with { Project = Next() };
                    break;
                case "--name":
                    o = o with { Name = Next() };
                    break;
                case "--http":
                case "--wire":
                case "--desk-pid":
                    var v = Next();
                    if (!int.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n is < 1 or > 65535 && a != "--desk-pid")
                    {
                        error = $"{a} takes a number";
                        return o;
                    }
                    o = a switch
                    {
                        "--http" => o with { HttpPort = n },
                        "--wire" => o with { WirePort = n },
                        _ => o with { DeskPid = n },
                    };
                    break;
                case "--no-recover":
                    o = o with { Recover = false };
                    break;
                case "--supervised":
                    o = o with { Supervised = true };
                    break;
                default:
                    error = $"unknown option {a}";
                    return o;
            }
        }
        return o;
    }
}
