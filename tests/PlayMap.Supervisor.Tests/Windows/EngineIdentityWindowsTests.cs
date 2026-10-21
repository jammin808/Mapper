using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Principal;
using PlayMap.Core.Machine;
using PlayMap.Supervisor.Install;
using PlayMap.Supervisor.Windows;

namespace PlayMap.Supervisor.Tests.Windows;

/// <summary>On Windows: the facts a process's handle gives (its image, start, command line, user, session and parent), Core's reader
/// giving the same bar the user, session and parent, and a pid record that names a process other than PlayMap's engine — a helper this
/// test starts — is neither adopted by the supervisor's step nor ended by a stop: the helper still runs, and the words say why. The
/// engine.pid file is one the show user may write, so this is the rule that keeps a standard account from having SYSTEM or an
/// administrator end a process it chose. The engine's own write holds the record to the same rule: a running engine's record (the
/// installer's fake, copied as PlayMap.exe and run as the engine) is kept from another engine until it has gone, and a record whose
/// pid a helper holds is replaced. An end Windows refuses is said in the refusal's own words.</summary>
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
    public void CoresReaderGivesWhatTheHandleGivesButTheUserSessionAndParent()
    {
        var full = EngineProcess.ReadFacts(Environment.ProcessId);
        Assert.True(full.Exists);
        Assert.Equal(full with { UserSid = "", Session = -1, ParentPid = 0 }, ProcessReader.Read(Environment.ProcessId));
        Assert.Equal(ProcessFacts.Gone, ProcessReader.Read(2_147_480_000));
    }

    [WindowsFact]
    public async Task ARunningEnginesRecordIsKeptFromAnotherEngineUntilItHasGone()
    {
        using var place = new Place();
        SupervisorStopTests.CopyFakeEngine(place.InstallDir);
        var portFile = Path.Combine(place.Folder, "fake-port.txt");
        var start = new ProcessStartInfo(Path.Combine(place.InstallDir, "PlayMap.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = place.InstallDir,
        };
        foreach (var a in new[] { "engine", "--record", place.EngineFolder, "--port-file", portFile })
            start.ArgumentList.Add(a);
        using var engine = Process.Start(start)!;
        try
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
            while (!File.Exists(portFile) && DateTime.UtcNow < deadline)
                await Task.Delay(100);
            Assert.True(File.Exists(portFile), "the fake engine did not come up");
            var record = EnginePidRecord.Named(place.EngineFolder);
            Assert.NotNull(record);
            Assert.Equal(engine.Id, record.Pid);
            var facts = ProcessReader.Read(engine.Id);
            Assert.Equal(EngineIdentity.ImageName, facts.Name, ignoreCase: true);
            Assert.True(EngineIdentity.IsEngineLine(facts.CommandLine), facts.CommandLine);
            Assert.True(EngineIdentity.NamesRunningEngine(record));

            var other = new EnginePidRecord(Environment.ProcessId, 9942, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            Assert.False(other.TryWrite(place.EngineFolder, out var holder));
            Assert.Equal(record, holder);
            Assert.Equal(record, EnginePidRecord.Named(place.EngineFolder));

            engine.Kill();
            Assert.True(engine.WaitForExit(TimeSpan.FromSeconds(30)));
            Assert.True(other.TryWrite(place.EngineFolder, out holder));
            Assert.Null(holder);
            Assert.Equal(other, EnginePidRecord.Named(place.EngineFolder));
        }
        finally
        {
            if (!engine.HasExited)
                engine.Kill();
        }
    }

    [WindowsFact]
    public void ARecordWhosePidAHelperHoldsIsReplacedByAnEngine()
    {
        using var place = new Place();
        using var helper = Helper.Start();
        Assert.True(new EnginePidRecord(helper.Pid, 9941, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()).TryWrite(place.EngineFolder));
        var engine = new EnginePidRecord(Environment.ProcessId, 9942, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        Assert.True(engine.TryWrite(place.EngineFolder, out var holder));
        Assert.Null(holder);
        Assert.Equal(engine, EnginePidRecord.Named(place.EngineFolder));
        Assert.True(helper.Running);
    }

    [WindowsFact]
    public async Task ARecordNamingAHelperIsNotAdoptedByTheSupervisorsStepAndIsLeftForTheEnginesOwnWrite()
    {
        using var place = new Place();
        using var helper = Helper.Start();
        var record = new EnginePidRecord(helper.Pid, 9941, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        Assert.True(record.TryWrite(place.EngineFolder));
        var before = File.GetLastWriteTimeUtc(Path.Combine(place.EngineFolder, EnginePidRecord.FileName));
        using var host = place.Host();
        host.Prepare();
        await host.StepAsync(CancellationToken.None);
        await host.StepAsync(CancellationToken.None);
        Assert.Equal(0, host.EnginePid);
        Assert.True(helper.Running);
        // The supervisor writes nothing in the engine's folder: the record stays as it was, for the next engine's write to replace.
        Assert.Equal(record, EnginePidRecord.Named(place.EngineFolder));
        Assert.Equal(before, File.GetLastWriteTimeUtc(Path.Combine(place.EngineFolder, EnginePidRecord.FileName)));
        Assert.Equal([EnginePidRecord.FileName], Directory.GetFiles(place.EngineFolder).Select(Path.GetFileName));
        var log = place.Log();
        var said = $"the pid record names pid {helper.Pid}, which is not the installed engine (pid {helper.Pid} is PING, not PlayMap.exe): not adopted, and the next engine's record replaces it";
        Assert.Contains(said, log, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, log.Split('\n').Count(l => l.Contains(said, StringComparison.OrdinalIgnoreCase)));
        Assert.DoesNotContain("terminated", log, StringComparison.Ordinal);

        var engine = new EnginePidRecord(Environment.ProcessId, 9942, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        Assert.True(engine.TryWrite(place.EngineFolder));
        Assert.Equal(engine, EnginePidRecord.Named(place.EngineFolder));
    }

    [WindowsFact]
    public void AStopOnARecordNamingAHelperEndsNothingAndSaysWhy()
    {
        using var place = new Place();
        using var helper = Helper.Start();
        Assert.True(new EnginePidRecord(helper.Pid, 9941, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()).TryWrite(place.EngineFolder));
        var said = new List<string>();
        Assert.Equal(EngineStop.NotTheEngine, EngineStopper.Stop(place.EngineFolder, place.InstallDir, place.StopEvent, said.Add));
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
        Assert.True(new EnginePidRecord(helper.Pid, 9941, DateTimeOffset.UtcNow.AddMinutes(-10).ToUnixTimeMilliseconds()).TryWrite(place.EngineFolder));
        var said = new List<string>();
        Assert.Equal(EngineStop.NotTheEngine, EngineStopper.Stop(place.EngineFolder, place.InstallDir, place.StopEvent, said.Add));
        Assert.True(helper.Running);
        Assert.Contains("not stopped", Assert.Single(said), StringComparison.Ordinal);
    }

    [WindowsFact]
    public void AnEndWindowsRefusesIsSaidInTheRefusalsOwnWords()
    {
        // A handle that may read and wait on the helper but not end it: Windows refuses the termination (access denied) while the
        // helper runs, and End says that refusal — not the words of the wait that then finds the helper still running.
        const int accessDenied = 5;
        using var helper = Helper.Start();
        var handle = Native.OpenProcessHandle(0x1000 | 0x00100000, false, helper.Pid);
        Assert.False(handle.IsInvalid);
        using var process = EngineProcess.Started(handle, helper.Pid);
        Assert.False(process.End(out var why));
        Assert.True(helper.Running);
        Assert.Equal(new Win32Exception(accessDenied).Message, why);
    }

    /// <summary>A supervisor's places in a folder of the test's own, with names no other supervisor uses: its folder, and the engine's
    /// folder inside it.</summary>
    internal sealed class Place : IDisposable
    {
        public Place()
        {
            Folder = Directory.CreateTempSubdirectory("playmap-supervisor-").FullName;
            EngineFolder = Directory.CreateDirectory(Path.Combine(Folder, "engine")).FullName;
            InstallDir = Directory.CreateTempSubdirectory("playmap-install-").FullName;
            var id = Guid.NewGuid().ToString("N");
            PipeName = "PlayMap.Supervisor.Test." + id;
            StopEvent = @"Local\PlayMap-Engine-Stop-Test-" + id;
        }

        public string Folder { get; }

        public string EngineFolder { get; }

        public string InstallDir { get; }

        public string PipeName { get; }

        public string StopEvent { get; }

        public static string ThisUser => Environment.UserDomainName + "\\" + Environment.UserName;

        /// <summary>A supervisor whose console session is this test's, whose show user is this test's user (or <paramref name="showUser"/>,
        /// "" for an install that names none), and which starts no engine (a start is refused with Windows' words).</summary>
        public SupervisorHost Host(Func<uint, string, string, EngineProcess>? launch = null, string? showUser = null)
        {
            using var self = Process.GetCurrentProcess();
            var session = (uint)self.SessionId;
            var shows = showUser ?? ThisUser;
            return new SupervisorHost(new SupervisorPlaces(Folder, EngineFolder, PipeName, StopEvent, () => InstallDir, () => shows, () => session,
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
