using System.Diagnostics;
using System.Reflection;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using PlayMap.Core.Machine;
using PlayMap.Supervisor.Install;
using PlayMap.Supervisor.Windows;
using PlayMap.Tests;

namespace PlayMap.Supervisor.Tests.Windows;

/// <summary>The engine's stop event on Windows, with a supervisor this test runs under names of its own and a fake engine it starts (the
/// installer's fake, copied as the installed PlayMap.exe): each engine start gets an event of its own, made under a name drawn for that
/// start and given to the engine on its command line, so an event another account made beforehand — under any name — keeps no engine
/// from starting; a stop asked on the pipe sets the event and keeps it set through every step of the grace, so an engine that looks at
/// it between frames sees it and stops by itself — the log says it was asked and never that it was terminated; the show user may wait
/// on the event and read its owner, and nothing more; an engine the supervisor adopts, and an elevated stop, use the event the engine's
/// command line names only when SYSTEM or administrators made it.</summary>
[SupportedOSPlatform("windows")]
public sealed class SupervisorStopTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);

    private static bool Elevated => new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    [WindowsFact]
    public async Task AStopSetsTheEventAnEngineThatLooksBetweenFramesSeesItAndStopsWithinTheGrace()
    {
        using var place = new EngineIdentityWindowsTests.Place();
        CopyFakeEngine(place.InstallDir);
        Process? started = null;
        var portFile = Path.Combine(place.Folder, "fake-port.txt");
        EngineProcess Launch(uint session, string exe, string arguments)
        {
            // The fake waits on the event the supervisor named on the engine's command line.
            var name = SupervisorNames.StopEventIn(arguments, place.StopEventPrefix);
            Assert.NotNull(name);
            var start = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = place.InstallDir };
            foreach (var a in new[] { "engine", "--supervised", "--record", place.EngineFolder, SupervisorNames.StopEventOption, name, "--port-file", portFile })
                start.ArgumentList.Add(a);
            started = Process.Start(start)!;
            return EngineProcess.Open(started.Id, out _)!;
        }
        using var host = place.Host(Launch);
        host.Prepare();
        using var stop = new CancellationTokenSource();
        var serving = host.ServePipeAsync(stop.Token);
        try
        {
            // The first step starts the engine; it writes its record and opens the stop event.
            await host.StepAsync(CancellationToken.None);
            Assert.NotNull(started);
            var deadline = DateTime.UtcNow + Patience;
            while (!File.Exists(portFile) && DateTime.UtcNow < deadline)
                await Task.Delay(100);
            Assert.True(File.Exists(portFile), "the fake engine did not come up");
            Assert.Equal(started.Id, EnginePidRecord.Named(place.EngineFolder)?.Pid);
            await host.StepAsync(CancellationToken.None);

            var answer = SupervisorPipe.Ask(place.PipeName, "stop", (pid, _) => pid == Environment.ProcessId ? null : "not this test");
            Assert.NotNull(answer);
            Assert.StartsWith("OK held", answer.Words, StringComparison.Ordinal);
            // The supervisor steps on, every 100 ms here, through the grace; the event stays set at each step.
            while (host.EnginePid != 0 && DateTime.UtcNow < deadline)
            {
                await host.StepAsync(CancellationToken.None);
                await Task.Delay(100);
            }
            Assert.True(started.WaitForExit(Patience));
            Assert.Equal(0, started.ExitCode);
            Assert.Equal(0, host.EnginePid);
            Assert.Equal(PlayMap.Supervisor.Policy.SupervisorState.Held, host.State);
            var log = place.Log();
            Assert.Contains($"asked the engine (pid {started.Id}) to stop: stopped by a person (PlayMap.Supervisor.exe stop)", log, StringComparison.Ordinal);
            Assert.DoesNotContain("terminated", log, StringComparison.Ordinal);
            // The engine took its record back as it stopped.
            Assert.Null(EnginePidRecord.Named(place.EngineFolder));
        }
        finally
        {
            if (started is not null)
            {
                ChildProcess.Stop(started);
                started.Dispose();
            }
            await stop.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => serving);
        }
    }

    [WindowsFact]
    public async Task AnEngineTheStepStartsRunsOnlyOnceTheStatusNamingItIsWritten()
    {
        // The launch hands back a process held by its handle with what lets it run; the step lets it run only after the status file
        // names its pid, so the engine reads that status (and the cause before it) as it starts.
        using var place = new EngineIdentityWindowsTests.Place();
        var order = new List<string>();
        using var helper = EngineIdentityWindowsTests.Helper.Start();
        EngineProcess Launch(uint session, string exe, string arguments)
        {
            order.Add("launch");
            var handle = Native.OpenProcessHandle(0x1000 | 0x00100000 | 0x0001, false, helper.Pid);
            return EngineProcess.Started(handle, helper.Pid, () =>
                order.Add(SupervisorStatus.Read(place.Folder) is { State: "starting" } status && status.Pid == helper.Pid
                    ? "resume after the status names it"
                    : "resume before the status names it"));
        }
        using var host = place.Host(Launch);
        host.Prepare();
        await host.StepAsync(CancellationToken.None);
        Assert.Equal(["launch", "resume after the status names it"], order);
        Assert.Equal(helper.Pid, host.EnginePid);
        // Let go once: a later step resumes nothing again.
        await host.StepAsync(CancellationToken.None);
        Assert.Equal(2, order.Count);
    }

    [WindowsFact]
    public async Task TheShowUserMayOnlyWaitOnTheStopEventAndReadItsOwnerAndAdministratorsMaySetIt()
    {
        using var place = new EngineIdentityWindowsTests.Place();
        using var engines = new Helpers();
        using var host = place.Host(engines.Launch);
        host.Prepare();
        await host.StepAsync(CancellationToken.None);
        using var stopEvent = engines.StopEvent(0, place);
        var rules = RulesOf(stopEvent);
        var user = WindowsIdentity.GetCurrent().User!;
        var showUser = Assert.Single(rules, r => r.IdentityReference.Equals(user));
        Assert.Equal(AccessControlType.Allow, showUser.AccessControlType);
        Assert.Equal(EventWaitHandleRights.Synchronize | EventWaitHandleRights.ReadPermissions, showUser.EventWaitHandleRights);
        var administrators = Assert.Single(rules, r => r.IdentityReference.Equals(Administrators));
        Assert.Equal(EventWaitHandleRights.Modify | EventWaitHandleRights.Synchronize | EventWaitHandleRights.ReadPermissions,
            administrators.EventWaitHandleRights);
        var system = Assert.Single(rules, r => r.IdentityReference.Equals(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null)));
        Assert.Equal(EventWaitHandleRights.FullControl, system.EventWaitHandleRights);
        Assert.Equal(3, rules.Count);
    }

    [WindowsFact]
    public async Task WithNoShowUserNamedAnySignedInUserMayOnlyWaitOnTheStopEventAndReadItsOwner()
    {
        // The console's user the supervisor starts the engine as is signed in to a session, so INTERACTIVE is among its token's groups.
        using var place = new EngineIdentityWindowsTests.Place();
        using var engines = new Helpers();
        using var host = place.Host(engines.Launch, showUser: "");
        host.Prepare();
        await host.StepAsync(CancellationToken.None);
        using var stopEvent = engines.StopEvent(0, place);
        var rules = RulesOf(stopEvent);
        var interactive = Assert.Single(rules, r => r.IdentityReference.Equals(new SecurityIdentifier(MachinePlan.AnyInteractiveUserSid)));
        Assert.Equal(AccessControlType.Allow, interactive.AccessControlType);
        Assert.Equal(EventWaitHandleRights.Synchronize | EventWaitHandleRights.ReadPermissions, interactive.EventWaitHandleRights);
        Assert.DoesNotContain(rules, r => r.IdentityReference.Equals(WindowsIdentity.GetCurrent().User!));
        Assert.Equal(3, rules.Count);
    }

    [WindowsFact]
    public async Task AStopEventAnotherAccountMadeBeforehandKeepsNoEngineFromStarting()
    {
        // An account that is not SYSTEM or administrators makes events where a supervisor's would be — under its prefix alone, and
        // under the name a supervisor without drawn names would use — and keeps them open, as a standard user may while no process holds
        // a name (an upgrade, a service restart). The supervisor still prepares, starts the engine with an event of its own, and never
        // touches theirs.
        using var place = new EngineIdentityWindowsTests.Place();
        var user = WindowsIdentity.GetCurrent().User!;
        using var squatted = MadeBy(place.StopEventPrefix, user);
        using var bare = MadeBy(place.StopEventPrefix.TrimEnd('-'), user);
        using var engines = new Helpers();
        using var host = place.Host(engines.Launch);

        host.Prepare();
        await host.StepAsync(CancellationToken.None);

        Assert.NotEqual(0, host.EnginePid);
        var name = engines.StopEventName(0, place);
        Assert.NotEqual(place.StopEventPrefix, name);
        using (var own = EventWaitHandleAcl.OpenExisting(name, EventWaitHandleRights.Synchronize | EventWaitHandleRights.ReadPermissions))
            Assert.Equal(3, RulesOf(own).Count);
        Assert.Contains("started the engine", place.Log(), StringComparison.Ordinal);
        Assert.False(squatted.WaitOne(0));
        Assert.False(bare.WaitOne(0));
    }

    [WindowsFact]
    public async Task EachEngineStartGetsAStopEventOfItsOwnUnderANameDrawnForIt()
    {
        // The event of an engine that has gone is let go of; the next start draws another name, so no engine ever waits on one that
        // was set for another, and no name is ever used twice.
        using var place = new EngineIdentityWindowsTests.Place();
        using var engines = new Helpers();
        using var host = place.Host(engines.Launch);
        host.Prepare();
        await host.StepAsync(CancellationToken.None);
        var first = engines.StopEventName(0, place);
        Assert.True(EventWaitHandleAcl.TryOpenExisting(first, EventWaitHandleRights.Synchronize, out var held));
        held.Dispose();

        engines.End(0);
        var deadline = DateTime.UtcNow + Patience;
        while (engines.Count < 2 && DateTime.UtcNow < deadline)
        {
            await host.StepAsync(CancellationToken.None);
            await Task.Delay(200);
        }
        Assert.Equal(2, engines.Count);
        var second = engines.StopEventName(1, place);
        Assert.NotEqual(first, second);
        Assert.True(SupervisorNames.IsStopEvent(second, place.StopEventPrefix));
        Assert.False(EventWaitHandleAcl.TryOpenExisting(first, EventWaitHandleRights.Synchronize, out _), "the gone engine's event is still held");
        Assert.True(EventWaitHandleAcl.TryOpenExisting(second, EventWaitHandleRights.Synchronize, out var next));
        next.Dispose();
    }

    [WindowsFact]
    public async Task AnAdoptedEnginesStopEventIsUsedOnlyWhenSystemOrAdministratorsMadeIt()
    {
        // A supervisor that did not start the engine (it restarted) adopts it by its record and takes the event its command line names:
        // one this account made under its own name is refused, said in the log; one administrators made is kept, and a stop asked on
        // the pipe sets it, so the adopted engine stops by itself.
        using var place = new EngineIdentityWindowsTests.Place();
        CopyFakeEngine(place.InstallDir);
        var owners = Elevated ? new[] { WindowsIdentity.GetCurrent().User!, Administrators } : [WindowsIdentity.GetCurrent().User!];
        foreach (var owner in owners)
        {
            var name = SupervisorNames.NewStopEvent(place.StopEventPrefix);
            using var made = MadeBy(name, owner);
            using var engine = StartFake(place, name);
            using var host = place.Host();
            host.Prepare();
            using var stop = new CancellationTokenSource();
            var serving = host.ServePipeAsync(stop.Token);
            try
            {
                await host.StepAsync(CancellationToken.None);
                Assert.Equal(engine.Id, host.EnginePid);
                var log = place.Log();
                if (!MachinePlan.StopEventOwnerTrusted(owner.Value))
                {
                    Assert.Contains($"the adopted engine (pid {engine.Id}) has no stop event this supervisor may set ({name} was made by {owner.Value}, not by SYSTEM or administrators)",
                        log, StringComparison.Ordinal);
                    var asked = SupervisorPipe.Ask(place.PipeName, "stop", (pid, _) => pid == Environment.ProcessId ? null : "not this test");
                    Assert.NotNull(asked);
                    Assert.False(made.WaitOne(0), "the supervisor set an event another account made");
                    Assert.Contains("it has no stop event this supervisor may set", place.Log(), StringComparison.Ordinal);
                    continue;
                }
                Assert.Contains($"kept the adopted engine's (pid {engine.Id}) stop event {name}", log, StringComparison.Ordinal);
                var answer = SupervisorPipe.Ask(place.PipeName, "stop", (pid, _) => pid == Environment.ProcessId ? null : "not this test");
                Assert.NotNull(answer);
                Assert.True(made.WaitOne(0));
                Assert.True(engine.WaitForExit(Patience));
                Assert.Equal(0, engine.ExitCode);
            }
            finally
            {
                ChildProcess.Stop(engine);
                await stop.CancelAsync();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => serving);
                File.Delete(Path.Combine(place.Folder, "supervisor.log"));
                File.Delete(Path.Combine(place.EngineFolder, EnginePidRecord.FileName));
            }
        }
    }

    [WindowsFact]
    public async Task AnElevatedStopSetsOnlyAStopEventSystemOrAdministratorsMade()
    {
        // The elevated stop (no service, an uninstall, an upgrade) reads the event's name from the installed engine's command line and
        // sets it only when SYSTEM or administrators made it; otherwise the grace and the termination follow, the event untouched.
        using var place = new EngineIdentityWindowsTests.Place();
        CopyFakeEngine(place.InstallDir);
        var owners = Elevated ? new[] { WindowsIdentity.GetCurrent().User!, Administrators } : [WindowsIdentity.GetCurrent().User!];
        foreach (var owner in owners)
        {
            var name = SupervisorNames.NewStopEvent(place.StopEventPrefix);
            using var made = MadeBy(name, owner);
            using var engine = StartFake(place, name);
            try
            {
                var said = new List<string>();
                var outcome = await Task.Run(() => EngineStopper.Stop(place.EngineFolder, place.InstallDir, place.StopEventPrefix, said.Add));
                if (MachinePlan.StopEventOwnerTrusted(owner.Value))
                {
                    Assert.Equal(EngineStop.Stopped, outcome);
                    Assert.True(made.WaitOne(0));
                    Assert.True(engine.WaitForExit(Patience));
                    Assert.Equal(0, engine.ExitCode);
                }
                else
                {
                    Assert.Equal(EngineStop.Terminated, outcome);
                    Assert.False(made.WaitOne(0), "the elevated stop set an event another account made");
                    Assert.Contains(said, s => s.Contains($"{name} was made by {owner.Value}, not by SYSTEM or administrators: it is not set", StringComparison.Ordinal));
                }
            }
            finally
            {
                ChildProcess.Stop(engine);
                File.Delete(Path.Combine(place.EngineFolder, EnginePidRecord.FileName));
            }
        }
    }

    /// <summary>The installer's fake, as the installed PlayMap.exe, run as the supervised engine waiting on <paramref name="stopEvent"/>,
    /// once it has written its record and its port.</summary>
    private static Process StartFake(EngineIdentityWindowsTests.Place place, string stopEvent)
    {
        var portFile = Path.Combine(place.Folder, "fake-port-" + Guid.NewGuid().ToString("N") + ".txt");
        var start = new ProcessStartInfo(Path.Combine(place.InstallDir, "PlayMap.exe")) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = place.InstallDir };
        foreach (var a in new[] { "engine", "--supervised", "--record", place.EngineFolder, SupervisorNames.StopEventOption, stopEvent, "--port-file", portFile })
            start.ArgumentList.Add(a);
        var process = Process.Start(start)!;
        var deadline = DateTime.UtcNow + Patience;
        while (!File.Exists(portFile) && !process.HasExited && DateTime.UtcNow < deadline)
            process.WaitForExit(100);
        Assert.True(File.Exists(portFile), "the fake engine did not come up");
        return process;
    }

    /// <summary>An event made by this test with <paramref name="owner"/> as its owner and full control for this test's user.</summary>
    private static EventWaitHandle MadeBy(string name, SecurityIdentifier owner)
    {
        var security = new EventWaitHandleSecurity();
        security.SetOwner(owner);
        security.AddAccessRule(new EventWaitHandleAccessRule(WindowsIdentity.GetCurrent().User!, EventWaitHandleRights.FullControl, AccessControlType.Allow));
        var made = EventWaitHandleAcl.Create(false, EventResetMode.ManualReset, name, out var created, security);
        Assert.True(created, name + " existed already");
        return made;
    }

    private static List<EventWaitHandleAccessRule> RulesOf(EventWaitHandle stop) =>
        stop.GetAccessControl().GetAccessRules(true, false, typeof(SecurityIdentifier)).Cast<EventWaitHandleAccessRule>().ToList();

    /// <summary>A launcher that starts Windows' ping in the engine's place, keeping the arguments each start was given.</summary>
    private sealed class Helpers : IDisposable
    {
        private readonly List<(string Arguments, EngineIdentityWindowsTests.Helper Helper)> _started = [];
        private readonly HashSet<int> _ended = [];

        public int Count => _started.Count;

        public EngineProcess Launch(uint session, string exe, string arguments)
        {
            var helper = EngineIdentityWindowsTests.Helper.Start();
            _started.Add((arguments, helper));
            return EngineProcess.Open(helper.Pid, out _)!;
        }

        /// <summary>The stop event the <paramref name="index"/>th start named on its command line, a name drawn under the place's prefix.</summary>
        public string StopEventName(int index, EngineIdentityWindowsTests.Place place)
        {
            var name = SupervisorNames.StopEventIn(_started[index].Arguments, place.StopEventPrefix);
            Assert.NotNull(name);
            Assert.StartsWith("engine --supervised " + SupervisorNames.StopEventOption + " ", _started[index].Arguments, StringComparison.Ordinal);
            return name;
        }

        /// <summary>That event, opened as the engine opens it.</summary>
        public EventWaitHandle StopEvent(int index, EngineIdentityWindowsTests.Place place) =>
            EventWaitHandleAcl.OpenExisting(StopEventName(index, place), EventWaitHandleRights.Synchronize | EventWaitHandleRights.ReadPermissions);

        /// <summary>Ends the <paramref name="index"/>th start's process, as an engine that exits.</summary>
        public void End(int index)
        {
            if (_ended.Add(index))
                _started[index].Helper.Dispose();
        }

        public void Dispose()
        {
            for (var i = 0; i < _started.Count; i++)
                End(i);
        }
    }

    /// <summary>The installer's fake engine copied into <paramref name="folder"/> as PlayMap.exe (its apphost finds its assembly by the
    /// name built into it). It is built as this project's reference: in this test's configuration, or, when the solution built it (a
    /// project outside the solution builds in its own default configuration), in the newest of its configurations' folders.</summary>
    internal static void CopyFakeEngine(string folder)
    {
        var configuration = typeof(SupervisorStopTests).Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration ?? "Release";
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PlayMap.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        var bin = Path.Combine(dir.FullName, "installer", "tests", "PlayMap.FakeEngine", "bin");
        var own = Path.Combine(bin, configuration, "net10.0");
        var built = File.Exists(Path.Combine(own, "PlayMap.FakeEngine.exe"))
            ? own
            : Directory.Exists(bin)
                ? Directory.GetDirectories(bin).Select(c => Path.Combine(c, "net10.0")).Where(d => File.Exists(Path.Combine(d, "PlayMap.FakeEngine.exe")))
                    .OrderByDescending(d => File.GetLastWriteTimeUtc(Path.Combine(d, "PlayMap.FakeEngine.exe"))).FirstOrDefault() ?? own
                : own;
        Assert.True(File.Exists(Path.Combine(built, "PlayMap.FakeEngine.exe")), "the fake engine is not built in " + bin);
        foreach (var file in Directory.GetFiles(built))
            File.Copy(file, Path.Combine(folder, Path.GetFileName(file)));
        File.Move(Path.Combine(folder, "PlayMap.FakeEngine.exe"), Path.Combine(folder, "PlayMap.exe"));
    }
}
