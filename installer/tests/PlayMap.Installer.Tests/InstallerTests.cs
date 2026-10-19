using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using Microsoft.Win32;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace PlayMap.Installer.Tests;

/// <summary>The installer on the Windows runner, elevated (docs/round3/PLAN.md §3.5): the launch condition, the armed check against fake
/// engines on loopback, Smart App Control's warning, then the life cycle — the previous build installs and lays the files, the
/// service with its recovery actions, the firewall rules, WER's values and the install's key; this build upgrades it and keeps the
/// node's data; the previous build is refused as a downgrade with words; the uninstall removes all but the node's data. Every
/// assertion reads what Windows holds afterwards (the registry, the service manager, the firewall, the files), never the
/// installer's words alone. The installers come from the job: PLAYMAP_INSTALLER (this build) and PLAYMAP_INSTALLER_PREVIOUS (the
/// same files numbered one build lower).</summary>
[SupportedOSPlatform("windows")]
public sealed class InstallerTests
{
    private const string SacWords =
        "Smart App Control is evaluating this PC: once it enforces, Windows blocks this unsigned build and the node will not start. Turn it off in Windows Security > App & browser control before the show.";

    private static string This => Required("PLAYMAP_INSTALLER");

    private static string Previous => Required("PLAYMAP_INSTALLER_PREVIOUS");

    private static string ThisVersion => Required("PLAYMAP_INSTALLER_VERSION");

    private static string PreviousVersion => Required("PLAYMAP_INSTALLER_PREVIOUS_VERSION");

    private static string InstallDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PlayMap");

    private static string MachineFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PlayMap");

    private static string ShowUser => Environment.UserDomainName + "\\" + Environment.UserName;

    private static string Required(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : throw new InvalidOperationException(name + " is not set: the installer job sets it");

    [Fact]
    public void AnOlderWindowsBuildIsRefusedByTheLaunchConditionWithWords()
    {
        EnsureUninstalled();
        var run = Install(This, "/PLAYMAPTESTBUILD=22631");
        Assert.NotEqual(0, run.Exit);
        Assert.Contains("PlayMap needs Windows 11 24H2 (build 26100) or later; this machine runs build 22631.", run.Log, StringComparison.Ordinal);
        Assert.Null(InstallKey());
        Assert.False(Directory.Exists(InstallDir));
    }

    [Fact]
    public void AnEngineAnsweringArmedStopsTheInstallWithItsWords()
    {
        EnsureUninstalled();
        var since = DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeMilliseconds();
        using var engine = FakeEngineProcess.Start("--answer", $"{{\"armed\":true,\"by\":\"Desk\",\"since\":{since},\"words\":\"armed\"}}",
            "--record", SupervisorFolder());
        var run = Install(This);
        Assert.NotEqual(0, run.Exit);
        var at = TimeZoneInfo.ConvertTimeFromUtc(DateTimeOffset.FromUnixTimeMilliseconds(since).UtcDateTime, TimeZoneInfo.Local)
            .ToString("HH:mm", CultureInfo.InvariantCulture);
        Assert.Contains($"the show is armed on this node since {at} by Desk: disarm first", run.Log, StringComparison.Ordinal);
        Assert.Contains("@pmsup SHOW STATUS", engine.Asked);
        Assert.Null(InstallKey());
    }

    [Fact]
    public void AnEngineProcessThatNeverAnswersStopsTheInstallWithDoesNotAnswer()
    {
        EnsureUninstalled();
        using var engine = FakeEngineProcess.Start("--silent", "--record", SupervisorFolder());
        var run = Install(This);
        Assert.NotEqual(0, run.Exit);
        Assert.Contains($"an engine is running here (pid {engine.Pid}) but does not answer", run.Log, StringComparison.Ordinal);
        Assert.Null(InstallKey());
    }

    [Fact]
    public void TheLifeCycleInstallsUpgradesRefusesADowngradeAndUninstalls()
    {
        EnsureUninstalled();
        var nodeData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PlayMap", "installer-test-settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(nodeData)!);
        File.WriteAllText(nodeData, "{\"kept\":true}");
        try
        {
            // The previous build installs and lays everything down.
            var first = Install(Previous, "/SHOWUSER=" + ShowUser);
            Assert.True(first.Exit == 0, first.Log);
            AssertInstalled(PreviousVersion);
            var dump = Path.Combine(MachineFolder, "dumps", "installer-test.dmp");
            File.WriteAllText(dump, "a dump the node keeps");

            // This build upgrades it, with Smart App Control evaluating: it completes and its log carries the warning.
            var upgrade = Install(This, "/PLAYMAPTESTSAC=2");
            Assert.True(upgrade.Exit == 0, upgrade.Log);
            Assert.Contains(SacWords, upgrade.Log, StringComparison.Ordinal);
            AssertInstalled(ThisVersion);
            Assert.Equal(ShowUser, InstallKey()!.GetValue("ShowUser"));
            Assert.Equal("{\"kept\":true}", File.ReadAllText(nodeData));
            Assert.True(File.Exists(dump));

            // The previous build again is a downgrade, refused with words, and changes nothing.
            var downgrade = Install(Previous);
            Assert.NotEqual(0, downgrade.Exit);
            Assert.Contains($"PlayMap {ThisVersion} is newer than this installer's {PreviousVersion}: uninstall it first to go back.", downgrade.Log,
                StringComparison.Ordinal);
            Assert.Equal(ThisVersion, InstallKey()!.GetValue("Version"));

            // The same build again is refused too: an installer upgrades only an older build.
            var same = Install(This);
            Assert.NotEqual(0, same.Exit);
            Assert.Contains($"PlayMap {ThisVersion} is installed already", same.Log, StringComparison.Ordinal);

            // The uninstall removes all but the node's data.
            var gone = Uninstall();
            Assert.True(gone.Exit == 0, gone.Log);
            Assert.Null(InstallKey());
            Assert.False(File.Exists(Path.Combine(InstallDir, "PlayMap.exe")));
            Assert.False(ServiceExists());
            Assert.DoesNotContain("9940", Tool("netsh.exe", "advfirewall", "firewall", "show", "rule", "name=PlayMap (TCP in)").Output, StringComparison.Ordinal);
            Assert.Null(Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\Windows Error Reporting\LocalDumps\PlayMap.exe"));
            Assert.Null(Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\EventLog\Application\PlayMap"));
            Assert.Equal("{\"kept\":true}", File.ReadAllText(nodeData));
            Assert.True(File.Exists(dump));
        }
        finally
        {
            File.Delete(nodeData);
            EnsureUninstalled();
        }
    }

    [Fact]
    public void AFolderOtherThanProgramFilesIsRefusedAndNothingIsLaidDown()
    {
        EnsureUninstalled();
        var elsewhere = @"C:\PlayMapElsewhere";
        var run = Install(This, "/DIR=" + elsewhere);
        Assert.Equal(7, run.Exit);
        Assert.Contains($"PlayMap installs only in {InstallDir}, which only administrators may change, since its service runs as SYSTEM; {elsewhere} was asked: nothing was changed.",
            run.Log, StringComparison.Ordinal);
        Assert.False(Directory.Exists(elsewhere) && Directory.EnumerateFileSystemEntries(elsewhere).Any(), elsewhere + " holds files");
        Assert.Null(InstallKey());
        Assert.False(ServiceExists());
    }

    [Fact]
    public void AShowUserWindowsCannotFindFailsTheMachineSetupAndLeavesTheMachineUnsupervised()
    {
        EnsureUninstalled();
        try
        {
            var run = Install(This, "/SHOWUSER=NoSuchAccount");
            Assert.Equal(3, run.Exit);
            Assert.Contains("the machine-level setup did not finish", run.Log, StringComparison.Ordinal);
            using var key = InstallKey();
            Assert.NotNull(key);
            Assert.Null(key.GetValue("Supervised"));
        }
        finally
        {
            EnsureUninstalled();
        }
    }

    [Fact]
    public void AnUpgradeWhileAProgramRunsFromTheInstallIsRefusedAndTheEarlierServiceRunsOn()
    {
        EnsureUninstalled();
        try
        {
            var first = Install(Previous, "/SHOWUSER=" + ShowUser);
            Assert.True(first.Exit == 0, first.Log);
            using (var holder = Holder.StartIn(InstallDir))
            {
                var upgrade = Install(This);
                Assert.Equal(7, upgrade.Exit);
                Assert.Contains($"a program runs from {InstallDir} (pid {holder.Pid}, {Holder.Name}): close it (the desk, or an engine run by hand), then install again",
                    upgrade.Log, StringComparison.Ordinal);
                Assert.True(holder.Running);
            }
            Assert.Equal(PreviousVersion, InstallKey()!.GetValue("Version"));
            Assert.Equal(1, InstallKey()!.GetValue("Supervised"));
            Assert.Equal("RUNNING", WaitForService("RUNNING"));
        }
        finally
        {
            EnsureUninstalled();
        }
    }

    [Fact]
    public void AnUpgradeThatCannotReplaceAFileIsRolledBackAndTheEarlierServiceStartsAgain()
    {
        EnsureUninstalled();
        try
        {
            var first = Install(Previous, "/SHOWUSER=" + ShowUser);
            Assert.True(first.Exit == 0, first.Log);
            // The engine held, so no process but this test's holds the install's files; then one of them held open with no sharing:
            // the upgrade cannot replace it, and a quiet install aborts.
            HoldTheEngine();
            Run upgrade;
            using (new FileStream(Path.Combine(InstallDir, "PlayMap.Core.dll"), FileMode.Open, FileAccess.Read, FileShare.None))
                upgrade = Install(This);
            Assert.NotEqual(0, upgrade.Exit);
            Assert.Contains("the install did not finish: the earlier install's service is started again", upgrade.Log, StringComparison.Ordinal);
            Assert.Equal(PreviousVersion, InstallKey()!.GetValue("Version"));
            Assert.Equal(1, InstallKey()!.GetValue("Supervised"));
            Assert.Equal("RUNNING", WaitForService("RUNNING"));
            Assert.True(File.Exists(Path.Combine(InstallDir, "PlayMap.exe")));
        }
        finally
        {
            EnsureUninstalled();
        }
    }

    [Fact]
    public void AnEngineArmedWhenTheUninstallIsAskedStopsItAndLeavesTheServiceInPlace()
    {
        EnsureUninstalled();
        try
        {
            var first = Install(This, "/SHOWUSER=" + ShowUser);
            Assert.True(first.Exit == 0, first.Log);
            // The supervisor holds its engine and is stopped, so the armed fake is the only engine the check finds.
            HoldTheEngine();
            Tool("sc.exe", "stop", "PlayMapSupervisor");
            Assert.Equal("STOPPED", WaitForService("STOPPED"));
            using (var engine = FakeEngineProcess.Start("--answer", "{\"armed\":true,\"by\":\"Desk\",\"since\":0,\"words\":\"armed\"}",
                       "--record", SupervisorFolder()))
            {
                var refused = Uninstall();
                Assert.Contains("the show is armed on this node by Desk: disarm first", refused.Log, StringComparison.Ordinal);
                Assert.NotNull(InstallKey());
                Assert.True(ServiceExists());
                Assert.True(File.Exists(Path.Combine(InstallDir, "PlayMap.exe")));
                Assert.True(engine.Running);
            }
            Tool("sc.exe", "start", "PlayMapSupervisor");
        }
        finally
        {
            EnsureUninstalled();
        }
    }

    /// <summary>What an installed build holds, read from Windows.</summary>
    private static void AssertInstalled(string version)
    {
        using var key = InstallKey();
        Assert.NotNull(key);
        Assert.Equal(version, key.GetValue("Version"));
        Assert.Equal(InstallDir, ((string)key.GetValue("InstallDir")!).TrimEnd('\\'));
        Assert.Equal(1, key.GetValue("Supervised"));
        Assert.Equal(0, key.GetValue("Signed"));

        foreach (var file in new[] { "PlayMap.exe", @"Supervisor\PlayMap.Supervisor.exe", @"D3D12\D3D12Core.dll", "libvmx.dll", @"ffmpeg\avcodec-63.dll" })
            Assert.True(File.Exists(Path.Combine(InstallDir, file)), file + " is not installed");
        // The notices and the licence texts the shipped third-party files ask for, beside them.
        foreach (var file in new[] { "THIRD-PARTY-NOTICES.md", @"ffmpeg\LICENSE.txt", @"ffmpeg\GPL-3.0.txt", "libvmx-LICENSE.txt", "soft_oal-LICENSE.txt" })
            Assert.True(File.Exists(Path.Combine(InstallDir, file)), file + " is not installed");
        Assert.Contains("GNU LESSER GENERAL PUBLIC LICENSE", File.ReadAllText(Path.Combine(InstallDir, "ffmpeg", "LICENSE.txt")), StringComparison.Ordinal);
        AssertEveryFileMatchesItsSum();
        Assert.Empty(Directory.GetFiles(InstallDir, "d3d12SDKLayers.dll", SearchOption.AllDirectories));
        // FFmpeg is the LGPL build every decode test on CI loads: each library hashes to the pinned build's.
        var pinned = Required("PLAYMAP_FFMPEG_DIR");
        var libraries = Directory.GetFiles(pinned, "*.dll");
        Assert.NotEmpty(libraries);
        foreach (var lib in libraries)
            Assert.Equal(Sha256(lib), Sha256(Path.Combine(InstallDir, "ffmpeg", Path.GetFileName(lib))));

        // The service: LocalSystem, automatic, restarting after 5 s and 30 s, non-crash failures too, running.
        var config = Tool("sc.exe", "qc", "PlayMapSupervisor").Output;
        Assert.Contains("AUTO_START", config, StringComparison.Ordinal);
        Assert.Contains("LocalSystem", config, StringComparison.Ordinal);
        Assert.Contains(@"Supervisor\PlayMap.Supervisor.exe"" run", config, StringComparison.Ordinal);
        var failure = Tool("sc.exe", "qfailure", "PlayMapSupervisor").Output;
        Assert.Contains("86400", failure, StringComparison.Ordinal);
        Assert.Contains("RESTART -- Delay = 5000", failure, StringComparison.Ordinal);
        Assert.Contains("RESTART -- Delay = 30000", failure, StringComparison.Ordinal);
        Assert.Contains("TRUE", Tool("sc.exe", "qfailureflag", "PlayMapSupervisor").Output, StringComparison.Ordinal);
        Assert.Contains("RUNNING", Tool("sc.exe", "query", "PlayMapSupervisor").Output, StringComparison.Ordinal);
        var status = Tool(Path.Combine(InstallDir, "Supervisor", "PlayMap.Supervisor.exe"), "status");
        Assert.True(status.Exit == 0, status.Output);
        Assert.Contains("\"state\":", status.Output, StringComparison.Ordinal);

        // The firewall: TCP 9940 and 9941, UDP 9928, 9942, 9943, 6454, 5568 and 5353, for the installed exe.
        var tcp = Tool("netsh.exe", "advfirewall", "firewall", "show", "rule", "name=PlayMap (TCP in)", "verbose").Output;
        Assert.Contains("9940,9941", tcp, StringComparison.Ordinal);
        Assert.Contains(Path.Combine(InstallDir, "PlayMap.exe"), tcp, StringComparison.OrdinalIgnoreCase);
        var udp = Tool("netsh.exe", "advfirewall", "firewall", "show", "rule", "name=PlayMap (UDP in)", "verbose").Output;
        Assert.Contains("9928,9942,9943,6454,5568,5353", udp, StringComparison.Ordinal);

        // WER keeps five full dumps of the engine in the machine's folder.
        using var wer = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\Windows Error Reporting\LocalDumps\PlayMap.exe");
        Assert.NotNull(wer);
        Assert.Equal(@"%ProgramData%\PlayMap\dumps", wer.GetValue("DumpFolder", null, RegistryValueOptions.DoNotExpandEnvironmentNames));
        Assert.Equal(2, wer.GetValue("DumpType"));
        Assert.Equal(5, wer.GetValue("DumpCount"));
        Assert.NotNull(Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\EventLog\Application\PlayMap"));
    }

    /// <summary>{app}\SHA256SUMS lists every file the install laid down, each with the hash it has, and no file is missing from it but
    /// the uninstaller Inno Setup writes there and the sums themselves.</summary>
    private static void AssertEveryFileMatchesItsSum()
    {
        var sums = Path.Combine(InstallDir, "SHA256SUMS");
        Assert.True(File.Exists(sums), "the install laid down no SHA256SUMS");
        var listed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in File.ReadAllLines(sums).Where(l => l.Length > 0))
        {
            Assert.Matches("^[0-9a-f]{64}  [^ ].*$", line);
            listed.Add(line[66..].Replace('/', '\\'), line[..64]);
        }
        foreach (var (name, hash) in listed)
        {
            var file = Path.Combine(InstallDir, name);
            Assert.True(File.Exists(file), name + " is listed and not installed");
            Assert.Equal(hash, Sha256(file).ToLowerInvariant());
        }
        var laid = Directory.GetFiles(InstallDir, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(InstallDir, f))
            .Where(f => !string.Equals(f, "SHA256SUMS", StringComparison.OrdinalIgnoreCase) && !f.StartsWith("unins", StringComparison.OrdinalIgnoreCase));
        Assert.All(laid, f => Assert.True(listed.ContainsKey(f), f + " is installed and not in SHA256SUMS"));
    }

    private static RegistryKey? InstallKey() => Registry.LocalMachine.OpenSubKey(@"SOFTWARE\PlayMap");

    /// <summary>Asks the supervisor to stop its engine and hold it (<c>PlayMap.Supervisor.exe stop</c>) and waits until its status says
    /// held with no engine, the engine gone.</summary>
    private static void HoldTheEngine()
    {
        var supervisor = Path.Combine(InstallDir, "Supervisor", "PlayMap.Supervisor.exe");
        var stop = Tool(supervisor, "stop");
        Assert.True(stop.Exit == 0, stop.Output);
        var deadline = Stopwatch.StartNew();
        var status = Tool(supervisor, "status").Output;
        while (!(status.Contains("\"state\":\"held\"", StringComparison.Ordinal) && status.Contains("\"pid\":0", StringComparison.Ordinal))
               && deadline.Elapsed < TimeSpan.FromMinutes(1))
        {
            Wait("cmd.exe", ["/c", "ping", "-n", "2", "127.0.0.1"]);
            status = Tool(supervisor, "status").Output;
        }
        Assert.True(status.Contains("\"state\":\"held\"", StringComparison.Ordinal) && status.Contains("\"pid\":0", StringComparison.Ordinal), status);
    }

    /// <summary>The service's state as sc.exe says it ("RUNNING", "STOPPED", …; "" when it is not installed): the word after the
    /// state's number on its STATE line ("        STATE              : 4  RUNNING ").</summary>
    private static string ServiceState()
    {
        var query = Tool("sc.exe", "query", "PlayMapSupervisor");
        var state = System.Text.RegularExpressions.Regex.Match(query.Output, @"STATE\s*:\s*\d+\s+([A-Z_]+)");
        return state.Success ? state.Groups[1].Value : "";
    }

    /// <summary>The service's state once it is <paramref name="state"/>, or as it stands after a minute.</summary>
    private static string WaitForService(string state)
    {
        var deadline = Stopwatch.StartNew();
        var now = ServiceState();
        while (now != state && deadline.Elapsed < TimeSpan.FromMinutes(1))
        {
            Wait("cmd.exe", ["/c", "ping", "-n", "2", "127.0.0.1"]);
            now = ServiceState();
        }
        return now;
    }

    private static bool ServiceExists() => Tool("sc.exe", "query", "PlayMapSupervisor").Exit != 1060;

    private sealed record Run(int Exit, string Log);

    private static Run Install(string installer, params string[] extra)
    {
        var log = Path.Combine(Path.GetTempPath(), "playmap-install-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture) + ".log");
        var exit = Wait(installer, ["/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/LOG=" + log, .. extra]);
        return new Run(exit, ReadShared(log));
    }

    private static Run Uninstall()
    {
        var log = Path.Combine(Path.GetTempPath(), "playmap-uninstall-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture) + ".log");
        var uninstaller = Path.Combine(InstallDir, "unins000.exe");
        var exit = Wait(uninstaller, ["/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/LOG=" + log]);
        // The uninstaller hands the work to a copy of itself and returns: wait until its copy has ended (no process runs from the
        // temp folder it copied itself to) and the install's key is gone, or two minutes pass.
        var deadline = Stopwatch.StartNew();
        while ((InstallKey() is not null || UninstallerRuns()) && deadline.Elapsed < TimeSpan.FromMinutes(2))
            Wait("cmd.exe", ["/c", "ping", "-n", "2", "127.0.0.1"]);
        return new Run(exit, ReadShared(log));
    }

    /// <summary>A log another process may still hold open for writing (the uninstaller's copy of itself), read as it stands.</summary>
    private static string ReadShared(string path)
    {
        if (!File.Exists(path))
            return "";
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>Whether Inno Setup's uninstaller copy (_iu14D2N.tmp and its kin) still runs.</summary>
    private static bool UninstallerRuns() =>
        Process.GetProcesses().Any(p =>
        {
            using (p)
                return p.ProcessName.StartsWith("_iu", StringComparison.OrdinalIgnoreCase) || p.ProcessName.StartsWith("unins0", StringComparison.OrdinalIgnoreCase);
        });

    private static void EnsureUninstalled()
    {
        if (InstallKey() is null)
            return;
        var gone = Uninstall();
        Assert.True(InstallKey() is null, "the previous test's install could not be removed: " + gone.Log);
    }

    private static int Wait(string exe, IReadOnlyList<string> args)
    {
        var start = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args)
            start.ArgumentList.Add(a);
        using var p = Process.Start(start)!;
        Assert.True(p.WaitForExit(TimeSpan.FromMinutes(10)), exe + " did not finish within 10 minutes");
        return p.ExitCode;
    }

    private static (int Exit, string Output) Tool(string exe, params string[] args)
    {
        var path = Path.IsPathRooted(exe) ? exe : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), exe);
        var start = new ProcessStartInfo(path) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args)
            start.ArgumentList.Add(a);
        using var p = Process.Start(start)!;
        var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, output);
    }

    private static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    /// <summary>A program that runs from a folder and is not PlayMap: Windows' ping copied there, waiting ten minutes, ended and removed
    /// when the test is done.</summary>
    private sealed class Holder : IDisposable
    {
        public const string Name = "ping-holder.exe";

        private readonly Process _process;
        private readonly string _path;

        private Holder(Process process, string path)
        {
            _process = process;
            _path = path;
        }

        public int Pid => _process.Id;

        public bool Running => !_process.HasExited;

        public static Holder StartIn(string folder)
        {
            var path = Path.Combine(folder, Name);
            File.Copy(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "PING.EXE"), path, overwrite: true);
            var start = new ProcessStartInfo(path) { UseShellExecute = false, CreateNoWindow = true };
            foreach (var a in new[] { "-n", "600", "127.0.0.1" })
                start.ArgumentList.Add(a);
            return new Holder(Process.Start(start)!, path);
        }

        public void Dispose()
        {
            if (!_process.HasExited)
                _process.Kill();
            _process.WaitForExit();
            _process.Dispose();
            File.Delete(_path);
        }
    }

    /// <summary>The machine's supervisor folder, where a supervised engine writes its pid record (made when it is missing, as the
    /// installer makes it).</summary>
    private static string SupervisorFolder()
    {
        var folder = Path.Combine(MachineFolder, "supervisor");
        Directory.CreateDirectory(folder);
        return folder;
    }
}
