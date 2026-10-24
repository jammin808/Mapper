using System.Globalization;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32;

namespace PlayMap.Bench.Probe;

/// <summary>The runner's answers to M7 and M14 (docs/round3/PLAN.md §1.5), as a Markdown section for the Windows job's summary.
///   PlayMap.Bench.Probe --out answers.md [--sdk 619] [--agility &lt;folder holding D3D12Core.dll&gt;]
///   PlayMap.Bench.Probe --session --out session.md      (M9's calls alone, for a run as LocalSystem)
/// Every question is asked and answered; an answer of no is a fact for the package that owns the question, never the probe's
/// failure. It exits 0 once it has written its answers, 2 when its command line is wrong.</summary>
internal static class Program
{
    // A lab bench is run from a console by the person reading its words: its answers go there.
#pragma warning disable RS0030
    private static readonly TextWriter Say = Console.Out;
    private static readonly TextWriter Complain = Console.Error;
#pragma warning restore RS0030

    private static int Main(string[] args)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19043))
        {
            Complain.WriteLine("the probe asks Windows 10 21H1 or later: it has nothing to ask here");
            return 2;
        }
        string? output = null;
        var session = false;
        var sdk = 0u;
        var agility = Path.Combine(AppContext.BaseDirectory, "D3D12");
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--out" when i + 1 < args.Length:
                    output = args[++i];
                    break;
                case "--sdk" when i + 1 < args.Length && uint.TryParse(args[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out var v):
                    sdk = v;
                    i++;
                    break;
                case "--agility" when i + 1 < args.Length:
                    agility = Path.GetFullPath(args[++i]);
                    break;
                case "--session":
                    session = true;
                    break;
                default:
                    Complain.WriteLine($"the probe does not know '{args[i]}': --out <file> [--sdk <n>] [--agility <folder>] [--session]");
                    return 2;
            }
        }
        if (output is null)
        {
            Complain.WriteLine("the probe writes its answers to --out <file>");
            return 2;
        }
        var text = Write(session ? SessionAnswers() : AllAnswers(sdk, agility), session);
        // Written whole, then moved into place, so a reader waiting for the file never reads half of it.
        var partial = output + ".partial";
        File.WriteAllText(partial, text, new UTF8Encoding(false));
        File.Move(partial, output, overwrite: true);
        Say.Write(text);
        return 0;
    }

    [SupportedOSPlatform("windows10.0.19043")]
    private static List<Answer> AllAnswers(uint sdk, string agility) =>
    [
        Questions.Ask("M7", "Does this .NET apphost load the Agility SDK's runtime through `ID3D12SDKConfiguration1::CreateDeviceFactory`?",
            () => Questions.Agility(sdk, agility)),
        Questions.Ask("M14", "MMCSS for a render thread (\"Games\") and an audio thread (\"Pro Audio\"), raised to high and reverted?", Questions.Mmcss),
        Questions.Ask("M14", "The power-throttling state (HighQoS, every timer honoured) set and read back?", Questions.Throttling),
        Questions.Ask("M14", "The GPU scheduling class raised to high and read back?", Questions.SchedulingClass),
        Questions.Ask("M14", "An HDR10 colour space on a display of the runner, as Windows reports each display now?", Questions.Displays),
        Questions.Ask("M14", "A flip swap chain on WARP that may present in HDR10?", Questions.Hdr10SwapChain),
        Questions.Ask("M14 (M9)", "This process's session calls: the console session's user token and a process started in that session?", Questions.Sessions),
    ];

    [SupportedOSPlatform("windows10.0.19043")]
    private static List<Answer> SessionAnswers() =>
    [
        Questions.Ask("M14 (M9)", "As LocalSystem: the console session's user token (WTSQueryUserToken) and a process started there (CreateProcessAsUser)?",
            Questions.Sessions),
    ];

    [SupportedOSPlatform("windows")]
    private static string Write(List<Answer> answers, bool session)
    {
        var text = new StringBuilder();
        if (session)
        {
            text.AppendLine("#### M9's calls as LocalSystem (a scheduled task, as the supervisor runs)").AppendLine();
        }
        else
        {
            text.AppendLine("### The runner's answers (M7, M14)").AppendLine();
            text.Append(CultureInfo.InvariantCulture, $"The runner: {Runner()}; the probe is `{Path.GetFileName(Environment.ProcessPath)}`, a .NET apphost, on .NET {Environment.Version}.")
                .AppendLine().AppendLine();
        }
        text.AppendLine("| # | Question | Answer | What Windows said |").AppendLine("|---|---|---|---|");
        foreach (var a in answers)
            text.AppendLine(CultureInfo.InvariantCulture, $"| {a.Number} | {Cell(a.Question)} | **{a.Verdict}** | {Cell(a.Said)} |");
        return text.AppendLine().ToString();
    }

    private static string Cell(string words) => words.Replace("|", "\\|", StringComparison.Ordinal).Replace("\r", "", StringComparison.Ordinal)
        .Replace("\n", " ", StringComparison.Ordinal);

    /// <summary>The Windows the runner is, as its registry names it: product, version, build and edition.</summary>
    [SupportedOSPlatform("windows")]
    private static string Runner()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        if (key is null)
            return $"Windows {Environment.OSVersion.Version}";
        string Read(string name) => Convert.ToString(key.GetValue(name), CultureInfo.InvariantCulture) ?? "";
        return $"{Read("ProductName")} {Read("DisplayVersion")} (build {Read("CurrentBuild")}.{Read("UBR")}), edition {Read("EditionID")}";
    }
}
