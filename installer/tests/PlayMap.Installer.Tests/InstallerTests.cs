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

    private static RegistryKey? InstallKey() => Registry.LocalMachine.OpenSubKey(@"SOFTWARE\PlayMap");

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

    /// <summary>The machine's supervisor folder, where a supervised engine writes its pid record (made when it is missing, as the
    /// installer makes it).</summary>
    private static string SupervisorFolder()
    {
        var folder = Path.Combine(MachineFolder, "supervisor");
        Directory.CreateDirectory(folder);
        return folder;
    }
}
