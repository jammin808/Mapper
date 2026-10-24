using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using System.ServiceProcess;
using System.Text;
using PlayMap.Core.Machine;
using PlayMap.Supervisor.Install;
using PlayMap.Supervisor.Windows;

namespace PlayMap.Supervisor;

/// <summary>"PlayMap.Supervisor &lt;command&gt;": the service (<c>run</c>, as Windows starts it) and the installer's and a person's
/// commands. Each answers in words on its standard output (and into <c>--out &lt;file&gt;</c> where the installer reads them) and with
/// its exit code: 0 done, 2 refused with words, 3 not on this OS, 64 a command line it does not take. The machine-level commands need
/// an elevated caller; the engine itself never runs elevated.</summary>
public static class Program
{
    public const int Done = 0;
    public const int Refused = 2;
    public const int NotHere = 3;
    public const int Usage = 64;

    /// <summary>The commands, as <c>help</c> lists them.</summary>
    public const string Words =
        "PlayMap.Supervisor run | status | resume | stop | preinstall-check [--out <file>] | install --dir <folder> [--show-user <account>] "
        + "[--previous-show-user <account>] [--public] [--out <file>] | "
        + "uninstall [--show-user <account>] [--out <file>] | upgrade-stop [--out <file>] | grant <account> | revoke <account> | "
        + "rights <account> | "
        + "version-check <next> <last> | previous-build <version> <build> | session-probe --out <file>";

    public static async Task<int> Main(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        await using var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
        return await RunAsync(args, stdout).ConfigureAwait(false);
    }

    /// <summary>Runs one command line; the words go to <paramref name="said"/> and, with <c>--out</c>, into that file too.</summary>
    public static async Task<int> RunAsync(IReadOnlyList<string> args, TextWriter said)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(said);
        var outFile = Option(args, "--out");
        var words = new StringBuilder();
        void Say(string line)
        {
            words.AppendLine(line);
            said.WriteLine(line);
        }
        int code;
        try
        {
            code = await DispatchAsync(WithoutOut(args), Say).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
                                       or System.ComponentModel.Win32Exception or System.Security.Principal.IdentityNotMappedException)
        {
            Say("refused: " + ex.Message);
            code = Refused;
        }
        if (outFile is { Length: > 0 })
            await File.WriteAllTextAsync(outFile, words.ToString(), new UTF8Encoding(false)).ConfigureAwait(false);
        return code;
    }

    private static async Task<int> DispatchAsync(IReadOnlyList<string> args, Action<string> say)
    {
        var command = args.Count > 0 ? args[0] : "";
        switch (command)
        {
            case "version-check" when args.Count == 3:
                var why = MsiVersionRule.Refusal(args[1], args[2]);
                say(why ?? $"{args[1]} follows the last release {(args[2].Length > 0 ? args[2] : "(none yet)")}");
                return why is null ? Done : Refused;
            case "previous-build" when args.Count == 3:
                if (!int.TryParse(args[2], NumberStyles.None, CultureInfo.InvariantCulture, out var build))
                    build = -1;
                if (MsiVersionRule.Previous(args[1], build, out var none) is { } previous)
                {
                    say(string.Create(CultureInfo.InvariantCulture, $"{previous.Version} {previous.Build}"));
                    return Done;
                }
                say(none);
                return Refused;
            case "preinstall-check":
                return await PreinstallCheckAsync(say).ConfigureAwait(false);
            case "help":
                say(Words);
                return Done;
        }
        if (!OperatingSystem.IsWindows())
        {
            if (command is "run" or "status" or "resume" or "stop" or "install" or "uninstall" or "upgrade-stop" or "grant" or "revoke" or "rights"
                or "session-probe" or "report-session")
            {
                say("the supervisor runs on Windows only: '" + command + "' changes nothing here");
                return NotHere;
            }
            say(Words);
            return Usage;
        }
        return await OnWindowsAsync(command, args, say).ConfigureAwait(false);
    }

    private static async Task<int> PreinstallCheckAsync(Action<string> say)
    {
        var look = ThisMachinesLook;
        var facts = await PreinstallCheck.LookAsync(look.Folder, look.FindEngine, look.IsEngine, look.AnswerTime).ConfigureAwait(false);
        var verdict = PreinstallCheck.Decide(facts, TimeZoneInfo.Local);
        say(verdict.Words);
        return verdict.GoOn ? Done : PreinstallVerdict.RefusedExit;
    }

    /// <summary>An engine running on this machine (a process named PlayMap whose command line runs it as the engine), with no port
    /// known: the check asks it on the default wire port.</summary>
    private static (int Pid, int Port)? FindEngine()
    {
        if (!OperatingSystem.IsWindows())
            return null;
        foreach (var p in Process.GetProcessesByName(EngineIdentity.ImageName))
        {
            using (p)
            {
                var facts = EngineProcess.ReadFacts(p.Id);
                // A process whose command line cannot be read may be an engine: the check fails closed.
                if (facts.Exists && (facts.CommandLine.Length == 0 || EngineIdentity.IsEngineLine(facts.CommandLine)))
                    return (p.Id, 0);
            }
        }
        return null;
    }

    /// <summary>Where this machine's armed check looks.</summary>
    private static ArmedLook ThisMachinesLook => new(EnginePidRecord.MachineFolder(), FindEngine, EngineIdentity.NamesRunningEngine, PreinstallCheck.AnswerTime);

    [SupportedOSPlatform("windows")]
    private static async Task<int> OnWindowsAsync(string command, IReadOnlyList<string> args, Action<string> say)
    {
        var showUser = Option(args, "--show-user") ?? "";
        switch (command)
        {
            case "run":
                ServiceBase.Run(new SupervisorService());
                return Done;
            case "install" when Option(args, "--dir") is { Length: > 0 } dir:
                MachineSetup.Install(dir, showUser, Option(args, "--previous-show-user") ?? "", args.Contains("--public"), say);
                return Done;
            case "uninstall":
                return await MachineCommands.UninstallAsync(new WindowsMachine(), ThisMachinesLook,
                    Option(args, "--show-user") ?? SupervisorHost.InstallValue("ShowUser"), TimeZoneInfo.Local, say).ConfigureAwait(false);
            case "upgrade-stop":
                return await MachineCommands.UpgradeStopAsync(new WindowsMachine(), ThisMachinesLook, TimeZoneInfo.Local, say).ConfigureAwait(false);
            case "grant" when args.Count == 2:
                MachineSetup.Grant(args[1]);
                say(MachinePlan.ShowUserRight + " granted to " + args[1]);
                return Done;
            case "revoke" when args.Count == 2:
                MachineSetup.Revoke(args[1]);
                say(MachinePlan.ShowUserRight + " taken back from " + args[1]);
                return Done;
            case "rights" when args.Count == 2:
                var held = MachineSetup.RightsOf(args[1]);
                say(held.Count == 0 ? args[1] + " holds no account right" : args[1] + " holds " + string.Join(", ", held));
                return Done;
            case "status" or "resume" or "stop":
                return PipeCommand(command, say);
            case "session-probe":
                return SessionProbe.Run(say);
            case "report-session":
                say(string.Create(CultureInfo.InvariantCulture, $"session {Process.GetCurrentProcess().SessionId}"));
                return Done;
            default:
                say(Words);
                return Usage;
        }
    }

    /// <summary>Asks the running service on its pipe, its server first held to <see cref="PipeServerRule"/>; with no service,
    /// <c>status</c> reads the status file and <c>stop</c> stops the engine itself.</summary>
    [SupportedOSPlatform("windows")]
    private static int PipeCommand(string word, Action<string> say)
    {
        var dir = SupervisorHost.InstallValue("InstallDir");
        var installed = dir.Length > 0 ? Path.Combine(dir, "Supervisor", "PlayMap.Supervisor.exe") : Environment.ProcessPath ?? "";
        if (SupervisorPipe.Ask(MachinePlan.PipeName, word, SupervisorPipe.ThisMachinesSupervisor(installed)) is { } answer)
        {
            if (!answer.FromSupervisor)
            {
                say("the supervisor's pipe is served by " + answer.Words + ": its answer is not taken");
                return Refused;
            }
            say(answer.Words);
            return answer.Words.StartsWith("ERR", StringComparison.Ordinal) ? Refused : Done;
        }
        switch (word)
        {
            case "status":
                var status = SupervisorStatus.Read(SupervisorStatus.MachineFolder());
                say(status is null ? "the supervisor is not running and has written no status" : "the supervisor is not running; its last status: " + status.ToJson());
                return status is null ? Refused : Done;
            case "stop":
                return StopEngine(say) is EngineStop.Failed or EngineStop.NotTheEngine ? Refused : Done;
            default:
                say("the supervisor is not running: there is nothing to resume (start the service PlayMapSupervisor)");
                return Refused;
        }
    }

    /// <summary>Stops the engine this machine's pid record names, when it is the installed engine (<see cref="EngineStopper"/>).</summary>
    [SupportedOSPlatform("windows")]
    private static EngineStop StopEngine(Action<string> say) =>
        EngineStopper.Stop(EnginePidRecord.MachineFolder(), SupervisorHost.InstallValue("InstallDir"), MachinePlan.StopEventPrefix, say);

    /// <summary>The command line less its <c>--out &lt;file&gt;</c>, which every command takes.</summary>
    private static List<string> WithoutOut(IReadOnlyList<string> args)
    {
        var rest = new List<string>(args.Count);
        for (var i = 0; i < args.Count; i++)
        {
            if (args[i] == "--out" && i + 1 < args.Count)
                i++;
            else
                rest.Add(args[i]);
        }
        return rest;
    }

    private static string? Option(IReadOnlyList<string> args, string name)
    {
        for (var i = 0; i < args.Count - 1; i++)
        {
            if (args[i] == name)
                return args[i + 1];
        }
        return null;
    }
}
