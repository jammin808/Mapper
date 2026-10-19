using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Principal;
using PlayMap.Core.Machine;
using PlayMap.Supervisor.Install;
using PlayMap.Supervisor.Windows;

namespace PlayMap.Supervisor.Tests.Windows;

/// <summary>On Windows: the facts a process's handle gives (its image, start, command line, user, session and parent), and a pid record
/// that names a process other than PlayMap's engine — a helper this test starts — is neither adopted by the supervisor's step nor ended
/// by a stop: the helper still runs, and the words say why. The engine.pid file is one the show user may write, so this is the rule
/// that keeps a standard account from having SYSTEM or an administrator end a process it chose.</summary>
[SupportedOSPlatform("windows")]
public sealed class EngineIdentityWindowsTests
{
    [WindowsFact]
    public void AHandleGivesTheProcesssImageStartCommandLineUserSessionAndParent()
    {
        using var self = Process.GetCurrentProcess();
        var facts = EngineProcess.ReadFacts(Environment.ProcessId);
        Assert.True(facts.Exists);
        Assert.Equal(self.ProcessName, facts.Name, ignoreCase: true);
        Assert.Equal(Environment.ProcessPath, facts.ImagePath, ignoreCase: true);
        var started = new DateTimeOffset(self.StartTime.ToUniversalTime()).ToUnixTimeMilliseconds();
        Assert.InRange(facts.StartedUnixMs, started - 1000, started + 1000);
        Assert.NotEqual("", facts.CommandLine);
        Assert.Equal(WindowsIdentity.GetCurrent().User!.Value, facts.UserSid);
        Assert.Equal(self.SessionId, facts.Session);
        Assert.True(facts.ParentPid > 0);
        Assert.Equal(ProcessFacts.Gone, EngineProcess.ReadFacts(2_147_480_000));
    }

    [WindowsFact]
    public async Task ARecordNamingAHelperIsNotAdoptedByTheSupervisorsStepAndIsRemoved()
    {
        using var place = new Place();
        using var helper = Helper.Start();
        var record = new EnginePidRecord(helper.Pid, 9941, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        Assert.True(record.TryWrite(place.Folder));
        using var host = place.Host();
        host.Prepare();
        await host.StepAsync(CancellationToken.None);
        Assert.Equal(0, host.EnginePid);
        Assert.True(helper.Running);
        Assert.Null(EnginePidRecord.Named(place.Folder));
        var log = place.Log();
        Assert.Contains($"the pid record names pid {helper.Pid}, which is not the installed engine (pid {helper.Pid} is PING, not PlayMap.exe): not adopted, and the record is removed",
            log, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("terminated", log, StringComparison.Ordinal);
    }

    [WindowsFact]
    public void AStopOnARecordNamingAHelperEndsNothingAndSaysWhy()
    {
        using var place = new Place();
        using var helper = Helper.Start();
        Assert.True(new EnginePidRecord(helper.Pid, 9941, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()).TryWrite(place.Folder));
        var said = new List<string>();
        Assert.Equal(EngineStop.NotTheEngine, EngineStopper.Stop(place.Folder, place.InstallDir, place.StopEvent, said.Add));
        Assert.True(helper.Running);
        Assert.Equal([$"the pid record names pid {helper.Pid}, which is not the installed engine (pid {helper.Pid} is PING, not PlayMap.exe): not stopped"],
            said);
    }

    [WindowsFact]
    public void AStopOnARecordWhosePidWasTakenByAnotherProcessEndsNothing()
    {
        // The record is older than the helper: its pid now names a process that started after the record was written.
        using var place = new Place();
        using var helper = Helper.Start();
        Assert.True(new EnginePidRecord(helper.Pid, 9941, DateTimeOffset.UtcNow.AddMinutes(-10).ToUnixTimeMilliseconds()).TryWrite(place.Folder));
        var said = new List<string>();
        Assert.Equal(EngineStop.NotTheEngine, EngineStopper.Stop(place.Folder, place.InstallDir, place.StopEvent, said.Add));
        Assert.True(helper.Running);
        Assert.Contains("not stopped", Assert.Single(said), StringComparison.Ordinal);
    }

    /// <summary>A supervisor's places in a folder of the test's own, with names no other supervisor uses.</summary>
    internal sealed class Place : IDisposable
    {
        public Place()
        {
            Folder = Directory.CreateTempSubdirectory("playmap-supervisor-").FullName;
            InstallDir = Directory.CreateTempSubdirectory("playmap-install-").FullName;
            var id = Guid.NewGuid().ToString("N");
            PipeName = "PlayMap.Supervisor.Test." + id;
            StopEvent = @"Local\PlayMap-Engine-Stop-Test-" + id;
        }

        public string Folder { get; }

        public string InstallDir { get; }

        public string PipeName { get; }

        public string StopEvent { get; }

        public static string ThisUser => Environment.UserDomainName + "\\" + Environment.UserName;

        /// <summary>A supervisor whose console session is this test's, whose show user is this test's user, and which starts no engine
        /// (a start is refused with Windows' words).</summary>
        public SupervisorHost Host(Func<uint, string, string, EngineProcess>? launch = null)
        {
            using var self = Process.GetCurrentProcess();
            var session = (uint)self.SessionId;
            return new SupervisorHost(new SupervisorPlaces(Folder, PipeName, StopEvent, () => InstallDir, () => ThisUser, () => session,
                _ => Environment.UserName, launch ?? ((_, _, _) => throw new Win32Exception(5, "this test starts no engine"))));
        }

        public string Log()
        {
            var path = Path.Combine(Folder, "supervisor.log");
            return File.Exists(path) ? File.ReadAllText(path) : "";
        }

        public void Dispose()
        {
            Directory.Delete(Folder, true);
            Directory.Delete(InstallDir, true);
        }
    }

    /// <summary>A process that is not PlayMap's engine: Windows' ping, waiting a minute, ended when the test is done.</summary>
    internal sealed class Helper : IDisposable
    {
        private readonly Process _process;

        private Helper(Process process) => _process = process;

        public int Pid => _process.Id;

        public bool Running => !_process.HasExited;

        public static Helper Start()
        {
            var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "PING.EXE"))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var a in new[] { "-n", "60", "127.0.0.1" })
                start.ArgumentList.Add(a);
            return new Helper(Process.Start(start)!);
        }

        public void Dispose()
        {
            if (!_process.HasExited)
                _process.Kill();
            _process.WaitForExit();
            _process.Dispose();
        }
    }
}
