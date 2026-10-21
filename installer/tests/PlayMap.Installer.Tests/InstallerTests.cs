using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using PlayMap.Core.Machine;
using PlayMap.Engine.ShaderWorker;

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

    /// <summary>What the installer reads back from its licence page into its log (PlayMap.iss, LogLicencePage).</summary>
    private const string LicencePage = "the licence page: heading yes; Microsoft's distributable-code terms yes; read as UTF-8 yes";

    /// <summary>The repository the tests are built from: the folder above them that holds PlayMap.sln.</summary>
    private static string Repository
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PlayMap.sln")))
                dir = dir.Parent;
            return dir?.FullName ?? throw new InvalidOperationException("no PlayMap.sln above " + AppContext.BaseDirectory);
        }
    }

    /// <summary>The terms the licence page shows, as the repository holds them (installer/licence.txt).</summary>
    private static string RepositoryTerms => Path.Combine(Repository, "installer", "licence.txt");

    /// <summary>The shader worker's Windows natives, each with the SHA-256 the worker holds it to when it loads it, as the engine pins
    /// them (CompilerNatives, its win-x64 rows): shaderc, SPIRV-Tools, SPIRV-Cross and DXC's compiler, dxcompiler.</summary>
    private static List<(string File, string Sha256)> WorkerPins()
    {
        var pins = CompilerNatives.PinsFor("win-x64").Select(p => (p.File, p.Sha256)).ToList();
        Assert.Equal(["dxcompiler.dll", "shaderc_shared.dll", "spirv-cross.dll", "SPIRV-Tools-shared.dll"],
            pins.Select(p => p.File).Order(StringComparer.OrdinalIgnoreCase));
        return pins;
    }

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
        Assert.Empty(Left());
    }

    [Fact]
    public void TheLicencePageHoldsTheTermsBeforeAnythingIsLaidDownAndTheInstallKeepsThem()
    {
        EnsureUninstalled();
        try
        {
            var run = Install(This, "/SHOWUSER=" + ShowUser);
            Assert.True(run.Exit == 0, run.Log);
            // The page as the built installer holds it: the terms' heading first, Microsoft's DISTRIBUTABLE CODE section on it, decoded
            // as UTF-8; a quiet install, which shows no page, goes on only with /ACCEPTTERMS=yes, and Inno Setup lays nothing down
            // before.
            var page = run.Log.IndexOf(LicencePage, StringComparison.Ordinal);
            Assert.True(page >= 0, "the installer's log reads back no licence page showing the terms whole: " + run.Log);
            var laying = run.Log.IndexOf("Starting the installation process.", StringComparison.Ordinal);
            Assert.True(laying > page, "the installation began before the licence page was read: " + run.Log);
            // The install keeps the terms it showed, byte for byte the repository's.
            Assert.Equal(File.ReadAllBytes(RepositoryTerms), File.ReadAllBytes(Path.Combine(InstallDir, "licence.txt")));
        }
        finally
        {
            EnsureUninstalled();
        }
    }

    /// <summary>A quiet install shows no licence page, so one whose command line does not carry /ACCEPTTERMS=yes is refused with
    /// words before anything else runs: /VERYSILENT, /SILENT, and any other value of the word.</summary>
    [Fact]
    public void AQuietInstallWithoutAcceptTermsIsRefusedAndLaysNothingDown()
    {
        EnsureUninstalled();
        foreach (var quiet in new[] { new[] { "/VERYSILENT" }, ["/SILENT"], ["/VERYSILENT", "/ACCEPTTERMS=no"] })
        {
            var log = Path.Combine(Path.GetTempPath(), "playmap-install-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture) + ".log");
            var exit = Wait(This, [.. quiet, "/SUPPRESSMSGBOXES", "/NORESTART", "/SHOWUSER=" + ShowUser, "/LOG=" + log]);
            var said = ReadShared(log);
            var asked = string.Join(' ', quiet);
            Assert.True(exit != 0, asked + " installed: " + said);
            Assert.Contains("refused: A quiet install shows no licence page, so it lays nothing down unless its command line carries /ACCEPTTERMS=yes",
                said, StringComparison.Ordinal);
            Assert.DoesNotContain("preinstall-check", said, StringComparison.Ordinal);
            Assert.Null(InstallKey());
            Assert.False(ServiceExists(), asked + " left a service");
            Assert.Empty(Left());
        }
    }

    [Fact]
    public void AnEngineAnsweringArmedStopsTheInstallWithItsWords()
    {
        EnsureUninstalled();
        var since = DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeMilliseconds();
        using var engine = FakeEngineProcess.Start("--answer", $"{{\"armed\":true,\"by\":\"Desk\",\"since\":{since},\"words\":\"armed\"}}",
            "--record", EngineFolder());
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
        using var engine = FakeEngineProcess.Start("--silent", "--record", EngineFolder());
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
        // A copy of the supervisor outside the install, to read the account right back once the uninstall has removed the install.
        var supervisor = SupervisorCopy();
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
            File.Copy(SupervisorExe, supervisor, overwrite: true);

            // The uninstall removes all but the node's data. A pid record naming a process that is not the engine (Windows' ping,
            // written in the engine's folder, which a show user may write) ends nothing: the uninstall goes on and the process runs on.
            HoldTheEngine();
            using var helper = Holder.Ping();
            Assert.True(new EnginePidRecord(helper.Pid, 9941, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()).TryWrite(EngineFolder()));
            var gone = Uninstall();
            Assert.True(gone.Exit == 0, gone.Log);
            Assert.True(helper.Running, "the uninstall ended the process a pid record named");
            AssertTakenBack(supervisor);
            Assert.False(File.Exists(Path.Combine(InstallDir, "PlayMap.exe")));
            Assert.Equal("{\"kept\":true}", File.ReadAllText(nodeData));
            Assert.True(File.Exists(dump));
        }
        finally
        {
            File.Delete(nodeData);
            File.Delete(supervisor);
            EnsureUninstalled();
        }
    }

    [Fact]
    public void APublicFirewallInstallOpensThePublicProfileToo()
    {
        EnsureUninstalled();
        try
        {
            var run = Install(This, "/SHOWUSER=" + ShowUser, "/PUBLICFIREWALL=1");
            Assert.True(run.Exit == 0, run.Log);
            AssertInstalled(ThisVersion, publicProfile: true);
        }
        finally
        {
            EnsureUninstalled();
        }
    }

    [Fact]
    public void StopHoldsTheEngineUntilResumeAndTheShowUserMayOnlyWaitOnTheStopEvent()
    {
        EnsureUninstalled();
        try
        {
            var first = Install(This, "/SHOWUSER=" + ShowUser);
            Assert.True(first.Exit == 0, first.Log);
            WaitForEngine();
            // The engine's stop event, made by the service: the show user may wait on it and nothing more.
            using (var stopEvent = EventWaitHandleAcl.OpenExisting(@"Global\PlayMap-Engine-Stop", EventWaitHandleRights.Synchronize | EventWaitHandleRights.ReadPermissions))
            {
                var rules = stopEvent.GetAccessControl().GetAccessRules(true, false, typeof(SecurityIdentifier)).Cast<EventWaitHandleAccessRule>().ToList();
                var showUser = Assert.Single(rules, r => r.IdentityReference.Equals(WindowsIdentity.GetCurrent().User));
                Assert.Equal(AccessControlType.Allow, showUser.AccessControlType);
                Assert.Equal(EventWaitHandleRights.Synchronize, showUser.EventWaitHandleRights);
            }
            // stop answers held, and the status file says held with no engine; resume leaves held and the engine starts again.
            HoldTheEngine();
            var file = File.ReadAllText(Path.Combine(MachineFolder, "supervisor", "status.json"));
            Assert.Contains("\"state\":\"held\"", file, StringComparison.Ordinal);
            var resume = Tool(SupervisorExe, "resume");
            Assert.True(resume.Exit == 0, resume.Output);
            Assert.StartsWith("OK resumed", resume.Output.Trim(), StringComparison.Ordinal);
            Assert.NotEqual(0, WaitForEngine());
            Assert.DoesNotContain("\"state\":\"held\"", Tool(SupervisorExe, "status").Output, StringComparison.Ordinal);
        }
        finally
        {
            EnsureUninstalled();
        }
    }

    [Fact]
    public void AnUninstallRightAfterTheServiceStopsEndsItsEngineAndTakesEveryActionBack()
    {
        EnsureUninstalled();
        var supervisor = SupervisorCopy();
        try
        {
            var first = Install(This, "/SHOWUSER=" + ShowUser);
            Assert.True(first.Exit == 0, first.Log);
            var engine = WaitForEngine();
            File.Copy(SupervisorExe, supervisor, overwrite: true);
            // The service asked to stop and not waited for: it leaves its engine running, and the uninstall comes straight after.
            Tool("sc.exe", "stop", "PlayMapSupervisor");
            var gone = Uninstall();
            Assert.True(gone.Exit == 0, gone.Log);
            AssertTakenBack(supervisor);
            Assert.False(IsEngine(engine), $"the engine (pid {engine}) runs on after the uninstall");
        }
        finally
        {
            File.Delete(supervisor);
            EnsureUninstalled();
        }
    }

    [Fact]
    public async Task AStandardUserServesTheInstalledEngineOn9940WithNoUrlReservation()
    {
        EnsureUninstalled();
        var account = "pmtest" + RandomNumberGenerator.GetHexString(6, lowercase: true);
        // Fourteen characters (net.exe asks a question of a longer one) with upper and lower case, a digit and a sign.
        var password = "Pm!7" + RandomNumberGenerator.GetString("ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789", 10);
        var home = Directory.CreateTempSubdirectory("playmap-standard-user-").FullName;
        Process? engine = null;
        try
        {
            var first = Install(This, "/SHOWUSER=" + ShowUser);
            Assert.True(first.Exit == 0, first.Log);
            // The supervised engine held, so 9940 is free for the one this test runs.
            HoldTheEngine();
            var added = Tool("net.exe", "user", account, password, "/add");
            Assert.True(added.Exit == 0, added.Output);
            Assert.DoesNotContain(account, Tool("net.exe", "localgroup", "Administrators").Output, StringComparison.OrdinalIgnoreCase);
            var security = new DirectoryInfo(home).GetAccessControl();
            security.AddAccessRule(new FileSystemAccessRule(new NTAccount(Environment.MachineName, account), FileSystemRights.Modify,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(home).SetAccessControl(security);

            var start = new ProcessStartInfo(Path.Combine(InstallDir, "PlayMap.exe"))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = InstallDir,
                UserName = account,
                Domain = Environment.MachineName,
                PasswordInClearText = password,
                LoadUserProfile = true,
            };
            foreach (var a in new[] { "engine", "headless", "--home", home, "--http", "9940" })
                start.ArgumentList.Add(a);
            engine = Process.Start(start)!;

            // A TCP connection to 9940 and an HTTP answer from the node on it: the standard user bound the port with no reservation.
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var deadline = Stopwatch.StartNew();
            HttpResponseMessage? answer = null;
            var tried = "";
            while (answer is null && !engine.HasExited && deadline.Elapsed < TimeSpan.FromSeconds(120))
            {
                try
                {
                    using (var tcp = new TcpClient())
                        await tcp.ConnectAsync(IPAddress.Loopback, 9940);
                    answer = await http.GetAsync(new Uri("http://127.0.0.1:9940/health"));
                }
                catch (Exception ex) when (ex is SocketException or HttpRequestException or TaskCanceledException)
                {
                    tried = ex.Message;
                    await Task.Delay(500);
                }
            }
            Assert.True(answer is not null, $"no answer on 9940 from the engine run as {account} (exited: {engine.HasExited}; last: {tried}): {EngineLog(home)}");
            using (answer)
            {
                Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
                Assert.StartsWith("{", (await answer.Content.ReadAsStringAsync()).TrimStart(), StringComparison.Ordinal);
            }
            Assert.Equal(account, OwnerOf(engine), ignoreCase: true);
            Assert.DoesNotContain(":9940/", Tool("netsh.exe", "http", "show", "urlacl").Output, StringComparison.Ordinal);
        }
        finally
        {
            if (engine is not null)
            {
                if (!engine.HasExited)
                    engine.Kill(entireProcessTree: true);
                await engine.WaitForExitAsync();
                engine.Dispose();
            }
            Tool("net.exe", "user", account, "/delete");
            RemoveFolder(home);
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
    public void AMachineFolderAStandardUserMadeAJunctionGetsNothingMadeThroughIt()
    {
        // %ProgramData% lets every account make a folder in it, and turning a folder of one's own into a junction needs no privilege:
        // a standard user may put %ProgramData%\PlayMap as a junction before a first install. The installer makes nothing under
        // %ProgramData% itself; the supervisor's install refuses the link before it makes anything inside it, so nothing appears at
        // the junction's target and the machine is left unsupervised (exit 3, the words in the log).
        EnsureUninstalled();
        var target = Directory.CreateTempSubdirectory("playmap-junction-target-").FullName;
        var aside = MachineFolder + "-aside-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        try
        {
            if (Directory.Exists(MachineFolder))
                Directory.Move(MachineFolder, aside);
            var junction = Tool("cmd.exe", "/c", "mklink", "/J", MachineFolder, target);
            Assert.True(junction.Exit == 0, junction.Output);
            Assert.True((File.GetAttributes(MachineFolder) & FileAttributes.ReparsePoint) != 0, MachineFolder + " is no junction");

            var run = Install(This, "/SHOWUSER=" + ShowUser);
            Assert.Equal(3, run.Exit);
            Assert.Contains("is a link (a junction or a symbolic link): the install makes and grants nothing through it", run.Log, StringComparison.Ordinal);
            Assert.Empty(Directory.EnumerateFileSystemEntries(target));
            using var key = InstallKey();
            Assert.NotNull(key);
            Assert.Null(key.GetValue("Supervised"));
        }
        finally
        {
            // The junction alone is removed (RemoveDirectory never follows it), then the earlier tests' folder is put back.
            if (Directory.Exists(MachineFolder) && (File.GetAttributes(MachineFolder) & FileAttributes.ReparsePoint) != 0)
                Directory.Delete(MachineFolder, recursive: false);
            if (Directory.Exists(aside))
                Directory.Move(aside, MachineFolder);
            RemoveFolder(target);
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
                       "--record", EngineFolder()))
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
    private static void AssertInstalled(string version, bool publicProfile = false)
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
        foreach (var file in new[]
                 {
                     "THIRD-PARTY-NOTICES.md", "licence.txt", @"ffmpeg\LICENSE.txt", @"ffmpeg\GPL-3.0.txt", "libvmx-LICENSE.txt", "soft_oal-LICENSE.txt",
                     @"licenses\Apache-2.0.txt", @"licenses\Inter-OFL.txt", @"licenses\Microsoft.NETCore.App.Runtime.win-x64\THIRD-PARTY-NOTICES.TXT",
                     @"licenses\Microsoft.AspNetCore.App.Runtime.win-x64\THIRD-PARTY-NOTICES.TXT", @"licenses\SkiaSharp.NativeAssets.Win32\THIRD-PARTY-NOTICES.txt",
                     @"licenses\Microsoft.ML.OnnxRuntime\ThirdPartyNotices.txt",
                     // The packages inside the supervisor's single-file exe, read from its restore.
                     @"licenses\System.ServiceProcess.ServiceController\THIRD-PARTY-NOTICES.TXT", @"licenses\System.Diagnostics.EventLog\THIRD-PARTY-NOTICES.TXT",
                 })
            Assert.True(File.Exists(Path.Combine(InstallDir, file)), file + " is not installed");
        Assert.Equal(File.ReadAllBytes(RepositoryTerms), File.ReadAllBytes(Path.Combine(InstallDir, "licence.txt")));
        Assert.Contains("GNU LESSER GENERAL PUBLIC LICENSE", File.ReadAllText(Path.Combine(InstallDir, "ffmpeg", "LICENSE.txt")), StringComparison.Ordinal);
        AssertEveryFileMatchesItsSum();
        Assert.Empty(Directory.GetFiles(InstallDir, "d3d12SDKLayers.dll", SearchOption.AllDirectories));
        AssertTheShaderWorkersNatives();
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
        // On the private and domain profiles, and Public only when the install asked for it.
        var profiles = publicProfile ? "Domain,Private,Public" : "Domain,Private";
        Assert.Equal(profiles, ProfilesOf(tcp));
        Assert.Equal(profiles, ProfilesOf(udp));

        // The show user may write the dumps and the engine's folder (an allow rule of its own, Modify) and nothing else of the machine's
        // folders: the PlayMap folder's access is its own (SYSTEM and administrators full control, every account read), and the
        // supervisor's folder, whose files SYSTEM writes, has no rule of its own. The show user holds its right.
        var user = WindowsIdentity.GetCurrent().User!;
        foreach (var folder in new[] { "dumps", @"supervisor\engine" })
        {
            var rules = ExplicitRules(Path.Combine(MachineFolder, folder));
            Assert.Contains(rules, r => r.IdentityReference.Equals(user) && r.AccessControlType == AccessControlType.Allow
                && (r.FileSystemRights & FileSystemRights.Modify) == FileSystemRights.Modify);
        }
        Assert.Empty(ExplicitRules(Path.Combine(MachineFolder, "supervisor")));
        Assert.True(new DirectoryInfo(MachineFolder).GetAccessControl().AreAccessRulesProtected);
        Assert.DoesNotContain(ExplicitRules(MachineFolder), r => r.IdentityReference.Equals(user));
        var rights = Tool(SupervisorExe, "rights", ShowUser);
        Assert.True(rights.Exit == 0, rights.Output);
        Assert.Contains("SeIncreaseBasePriorityPrivilege", rights.Output, StringComparison.Ordinal);

        // WER keeps five full dumps of the engine in the machine's folder.
        using var wer = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\Windows Error Reporting\LocalDumps\PlayMap.exe");
        Assert.NotNull(wer);
        Assert.Equal(@"%ProgramData%\PlayMap\dumps", wer.GetValue("DumpFolder", null, RegistryValueOptions.DoNotExpandEnvironmentNames));
        Assert.Equal(2, wer.GetValue("DumpType"));
        Assert.Equal(5, wer.GetValue("DumpCount"));
        Assert.NotNull(Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\EventLog\Application\PlayMap"));
    }

    /// <summary>The shader worker's natives beside the exe, each the file the worker's pin names (round 3 §9 decision 24 for DXC's):
    /// of DXC, its compiler alone, dxcompiler.dll, with the text its licence asks to travel with it in licenses\DXC\ beside it — the
    /// package's LICENSE-LLVM.txt and DXC's own LICENSE.TXT and ThirdPartyNotices.txt as the engine carries them — and no validator
    /// library (dxil.dll, libdxil.so) and no LICENSE-MS.txt anywhere in the install.</summary>
    private static void AssertTheShaderWorkersNatives()
    {
        foreach (var (file, sha256) in WorkerPins())
        {
            var installed = Path.Combine(InstallDir, file);
            Assert.True(File.Exists(installed), file + " is not installed beside the exe");
            Assert.Equal(sha256, Sha256(installed).ToLowerInvariant());
        }
        var notices = Path.Combine(InstallDir, "licenses", "DXC");
        Assert.Contains("LLVM Release License", File.ReadAllText(Path.Combine(notices, "LICENSE-LLVM.txt")), StringComparison.Ordinal);
        foreach (var file in new[] { "LICENSE.TXT", "ThirdPartyNotices.txt" })
            Assert.Equal(File.ReadAllBytes(Path.Combine(Repository, "src", "PlayMap.Engine", "ShaderWorker", "DxcNotices", file)),
                File.ReadAllBytes(Path.Combine(notices, file)));
        Assert.Equal(["LICENSE-LLVM.txt", "LICENSE.TXT", "ThirdPartyNotices.txt"],
            Directory.GetFiles(notices).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        foreach (var never in new[] { "dxil.dll", "libdxil.so", "LICENSE-MS.txt" })
            Assert.Empty(Directory.GetFiles(InstallDir, never, SearchOption.AllDirectories));
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

    /// <summary>A folder's own access rules (none inherited).</summary>
    private static List<FileSystemAccessRule> ExplicitRules(string folder) => new DirectoryInfo(folder).GetAccessControl()
        .GetAccessRules(true, false, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToList();

    /// <summary>What an uninstall takes back, read from Windows: the install's key, the service, both firewall rules, WER's values,
    /// the event source, the show user's right (read by <paramref name="supervisor"/>, a copy outside the removed install) and the show
    /// user's write access to the dumps and the engine's folder, which stay.</summary>
    private static void AssertTakenBack(string supervisor)
    {
        foreach (var folder in new[] { "dumps", @"supervisor\engine" })
        {
            var path = Path.Combine(MachineFolder, folder);
            Assert.True(Directory.Exists(path), path + " was removed");
            Assert.Empty(ExplicitRules(path));
        }
        Assert.Null(InstallKey());
        Assert.True(Left().Count == 0, "the uninstall left in the install's folder: " + string.Join(", ", Left()));
        Assert.False(ServiceExists());
        foreach (var rule in new[] { "PlayMap (TCP in)", "PlayMap (UDP in)" })
            Assert.NotEqual(0, Tool("netsh.exe", "advfirewall", "firewall", "show", "rule", "name=" + rule).Exit);
        Assert.Null(Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\Windows Error Reporting\LocalDumps\PlayMap.exe"));
        Assert.Null(Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\EventLog\Application\PlayMap"));
        var rights = Tool(supervisor, "rights", ShowUser);
        Assert.True(rights.Exit == 0, rights.Output);
        Assert.DoesNotContain("SeIncreaseBasePriorityPrivilege", rights.Output, StringComparison.Ordinal);
    }

    /// <summary>The profiles netsh's verbose listing names for a rule ("Domain,Private").</summary>
    private static string ProfilesOf(string verbose) =>
        Regex.Match(verbose, @"^Profiles:\s*(\S+)", RegexOptions.Multiline, TimeSpan.FromSeconds(1)).Groups[1].Value;

    private static string SupervisorExe => Path.Combine(InstallDir, "Supervisor", "PlayMap.Supervisor.exe");

    /// <summary>A path outside the install for a copy of its supervisor.</summary>
    private static string SupervisorCopy() =>
        Path.Combine(Path.GetTempPath(), "playmap-supervisor-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture) + ".exe");

    /// <summary>Waits until the supervisor's status names a running engine; its pid (0 after two minutes without one).</summary>
    private static int WaitForEngine()
    {
        var deadline = Stopwatch.StartNew();
        var pid = 0;
        while (deadline.Elapsed < TimeSpan.FromMinutes(2))
        {
            var status = Tool(SupervisorExe, "status").Output;
            var named = Regex.Match(status, "\"pid\":(\\d+)", RegexOptions.None, TimeSpan.FromSeconds(1));
            if (named.Success && (pid = int.Parse(named.Groups[1].Value, CultureInfo.InvariantCulture)) != 0)
                return pid;
            Wait("cmd.exe", ["/c", "ping", "-n", "2", "127.0.0.1"]);
        }
        Assert.Fail("no engine within two minutes: " + Tool(SupervisorExe, "status").Output);
        return pid;
    }

    /// <summary>Whether a process of that id runs as PlayMap.exe.</summary>
    private static bool IsEngine(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited && string.Equals(process.ProcessName, "PlayMap", StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            // No process has that id.
            return false;
        }
    }

    /// <summary>The account a process runs as (its user name), as Windows' tasklist says it.</summary>
    private static string OwnerOf(Process process)
    {
        var listed = Tool("tasklist.exe", "/v", "/fo", "csv", "/nh", "/fi", "PID eq " + process.Id.ToString(CultureInfo.InvariantCulture)).Output;
        var fields = Regex.Matches(listed, "\"([^\"]*)\"", RegexOptions.None, TimeSpan.FromSeconds(1)).Select(m => m.Groups[1].Value).ToList();
        // Image name, PID, session name, session, memory, status, user name, …: the user name is DOMAIN\name.
        return fields.Count > 6 ? fields[6][(fields[6].IndexOf('\\', StringComparison.Ordinal) + 1)..] : listed;
    }

    /// <summary>Removes a folder a process just ended may still hold a file of: tried for up to ten seconds, then left with the reason
    /// in the test's trace (the runner is discarded after the job).</summary>
    private static void RemoveFolder(string folder)
    {
        var deadline = Stopwatch.StartNew();
        while (Directory.Exists(folder))
        {
            try
            {
                Directory.Delete(folder, true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (deadline.Elapsed > TimeSpan.FromSeconds(10))
                {
                    Trace.WriteLine("PlayMap.Installer.Tests: " + folder + " is left: " + ex.Message);
                    return;
                }
                Wait("cmd.exe", ["/c", "ping", "-n", "2", "127.0.0.1"]);
            }
        }
    }

    /// <summary>The last lines of the engine's log under its home, for a failure's words.</summary>
    private static string EngineLog(string home)
    {
        var logs = Directory.Exists(home) ? Directory.GetFiles(home, "*.log", SearchOption.AllDirectories) : [];
        return logs.Length == 0 ? "no log in " + home : string.Join(" | ", logs.SelectMany(l => ReadShared(l).Split('\n').TakeLast(20)));
    }

    private static RegistryKey? InstallKey() => Registry.LocalMachine.OpenSubKey(@"SOFTWARE\PlayMap");

    /// <summary>Asks the supervisor to stop its engine and hold it (<c>PlayMap.Supervisor.exe stop</c>) and waits until its status says
    /// held with no engine, the engine gone.</summary>
    private static void HoldTheEngine()
    {
        var (stopped, status) = TryHoldTheEngine();
        Assert.True(stopped.Exit == 0, stopped.Output);
        Assert.True(Held(status), status);
    }

    /// <summary>Asks the supervisor to hold its engine and waits up to a minute for its status to say so: <c>stop</c>'s answer and the
    /// last status read.</summary>
    private static ((int Exit, string Output) Stop, string Status) TryHoldTheEngine()
    {
        var stop = Tool(SupervisorExe, "stop");
        var deadline = Stopwatch.StartNew();
        var status = Tool(SupervisorExe, "status").Output;
        while (!Held(status) && deadline.Elapsed < TimeSpan.FromMinutes(1))
        {
            Wait("cmd.exe", ["/c", "ping", "-n", "2", "127.0.0.1"]);
            status = Tool(SupervisorExe, "status").Output;
        }
        return (stop, status);
    }

    private static bool Held(string status) =>
        status.Contains("\"state\":\"held\"", StringComparison.Ordinal) && status.Contains("\"pid\":0", StringComparison.Ordinal);

    /// <summary>The service's state as sc.exe says it ("RUNNING", "STOPPED", …; "" when it is not installed): the word after the
    /// state's number on its STATE line ("        STATE              : 4  RUNNING ").</summary>
    private static string ServiceState()
    {
        var query = Tool("sc.exe", "query", "PlayMapSupervisor");
        var state = Regex.Match(query.Output, @"STATE\s*:\s*\d+\s+([A-Z_]+)", RegexOptions.None, TimeSpan.FromSeconds(1));
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
        var exit = Wait(installer, ["/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/ACCEPTTERMS=yes", "/LOG=" + log, .. extra]);
        return new Run(exit, ReadShared(log));
    }

    private static Run Uninstall()
    {
        var log = Path.Combine(Path.GetTempPath(), "playmap-uninstall-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture) + ".log");
        var uninstaller = Path.Combine(InstallDir, "unins000.exe");
        var exit = Wait(uninstaller, ["/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/LOG=" + log]);
        // The uninstaller hands the work to a copy of itself in the temp folder, which removes the uninstaller and the folder last:
        // wait until no part of it runs (the uninstall done, or refused), or two minutes pass.
        var deadline = Stopwatch.StartNew();
        while (UninstallerRuns() && deadline.Elapsed < TimeSpan.FromMinutes(2))
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

    /// <summary>Whether Inno Setup's uninstaller still runs: the install's <c>unins000.exe</c>, which waits for its copy, or the copy in
    /// the temp folder (<c>_unins.tmp</c>, which removes the uninstaller and the folder once the first has ended; <c>_iu*.tmp</c> in
    /// earlier versions).</summary>
    private static bool UninstallerRuns() =>
        Process.GetProcesses().Any(p =>
        {
            using (p)
                return p.ProcessName.StartsWith("_unins", StringComparison.OrdinalIgnoreCase) || p.ProcessName.StartsWith("_iu", StringComparison.OrdinalIgnoreCase)
                    || p.ProcessName.StartsWith("unins0", StringComparison.OrdinalIgnoreCase);
        });

    /// <summary>Each test starts from a machine with no install: an earlier test's install is uninstalled, and anything an earlier
    /// test left in the install's folder (named in the job's summary, so it is seen) is removed.</summary>
    private static void EnsureUninstalled()
    {
        if (InstallKey() is not null)
        {
            // The earlier test's engine held first: one its service started moments ago does not answer the uninstall's armed check
            // yet, which refuses it (as it would refuse a person).
            if (File.Exists(SupervisorExe) && ServiceState() == "RUNNING")
                TryHoldTheEngine();
            var gone = Uninstall();
            Assert.True(InstallKey() is null, "the previous test's install could not be removed: " + gone.Log);
        }
        if (Left() is { Count: > 0 } left)
        {
            if (Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY") is { Length: > 0 } summary)
                File.AppendAllText(summary, "- InstallerTests: an earlier test left in " + InstallDir + ": " + string.Join(", ", left) + Environment.NewLine);
            RemoveFolder(InstallDir);
        }
    }

    /// <summary>What is in the install's folder, by path under it (none when there is no folder).</summary>
    private static List<string> Left() =>
        Directory.Exists(InstallDir)
            ? Directory.EnumerateFileSystemEntries(InstallDir, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(InstallDir, f)).ToList()
            : [];

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

    /// <summary>A program that is not PlayMap: Windows' ping, waiting ten minutes — copied into a folder and run from there, or run where
    /// Windows keeps it — ended (and its copy removed) when the test is done.</summary>
    private sealed class Holder : IDisposable
    {
        public const string Name = "ping-holder.exe";

        private readonly Process _process;
        private readonly string? _copy;

        private Holder(Process process, string? copy)
        {
            _process = process;
            _copy = copy;
        }

        public int Pid => _process.Id;

        public bool Running => !_process.HasExited;

        private static string PingExe => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "PING.EXE");

        public static Holder StartIn(string folder)
        {
            var path = Path.Combine(folder, Name);
            File.Copy(PingExe, path, overwrite: true);
            return new Holder(Run(path), path);
        }

        public static Holder Ping() => new(Run(PingExe), null);

        private static Process Run(string path)
        {
            var start = new ProcessStartInfo(path) { UseShellExecute = false, CreateNoWindow = true };
            foreach (var a in new[] { "-n", "600", "127.0.0.1" })
                start.ArgumentList.Add(a);
            return Process.Start(start)!;
        }

        public void Dispose()
        {
            if (!_process.HasExited)
                _process.Kill();
            _process.WaitForExit();
            _process.Dispose();
            if (_copy is not null)
                File.Delete(_copy);
        }
    }

    /// <summary>The machine's engine folder inside the supervisor's, where a supervised engine writes its pid record (made when it is
    /// missing, as the installer makes it).</summary>
    private static string EngineFolder()
    {
        var folder = Path.Combine(MachineFolder, "supervisor", "engine");
        Directory.CreateDirectory(folder);
        return folder;
    }
}
