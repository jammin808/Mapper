using System.Diagnostics;
using System.Reflection;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using PlayMap.Core.Machine;
using PlayMap.Supervisor.Install;
using PlayMap.Supervisor.Windows;

namespace PlayMap.Supervisor.Tests.Windows;

/// <summary>The engine's stop event on Windows, with a supervisor this test runs under names of its own and a fake engine it starts (the
/// installer's fake, copied as the installed PlayMap.exe): a stop asked on the pipe sets the event and keeps it set through every step
/// of the grace, so an engine that looks at it between frames sees it and stops by itself — the log says it was asked and never that
/// it was terminated; the show user may wait on the event and nothing more.</summary>
[SupportedOSPlatform("windows")]
public sealed class SupervisorStopTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    [WindowsFact]
    public async Task AStopSetsTheEventAnEngineThatLooksBetweenFramesSeesItAndStopsWithinTheGrace()
    {
        using var place = new EngineIdentityWindowsTests.Place();
        CopyFakeEngine(place.InstallDir);
        Process? started = null;
        var portFile = Path.Combine(place.Folder, "fake-port.txt");
        EngineProcess Launch(uint session, string exe, string arguments)
        {
            var start = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = place.InstallDir };
            foreach (var a in new[] { "engine", "--supervised", "--record", place.EngineFolder, "--stop-event", place.StopEvent, "--port-file", portFile })
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
            if (started is { HasExited: false })
                started.Kill();
            started?.Dispose();
            await stop.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => serving);
        }
    }

    [WindowsFact]
    public void TheShowUserMayOnlyWaitOnTheStopEventAndAdministratorsMaySetIt()
    {
        using var place = new EngineIdentityWindowsTests.Place();
        using var host = place.Host();
        host.Prepare();
        using var stop = EventWaitHandleAcl.OpenExisting(place.StopEvent, EventWaitHandleRights.Synchronize | EventWaitHandleRights.ReadPermissions);
        var rules = stop.GetAccessControl().GetAccessRules(true, false, typeof(SecurityIdentifier)).Cast<EventWaitHandleAccessRule>().ToList();
        var user = WindowsIdentity.GetCurrent().User!;
        var showUser = Assert.Single(rules, r => r.IdentityReference.Equals(user));
        Assert.Equal(AccessControlType.Allow, showUser.AccessControlType);
        Assert.Equal(EventWaitHandleRights.Synchronize, showUser.EventWaitHandleRights);
        var administrators = Assert.Single(rules, r => r.IdentityReference.Equals(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null)));
        Assert.Equal(EventWaitHandleRights.Modify | EventWaitHandleRights.Synchronize | EventWaitHandleRights.ReadPermissions,
            administrators.EventWaitHandleRights);
        var system = Assert.Single(rules, r => r.IdentityReference.Equals(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null)));
        Assert.Equal(EventWaitHandleRights.FullControl, system.EventWaitHandleRights);
        Assert.Equal(3, rules.Count);
    }

    [WindowsFact]
    public void AStopEventThatExistsAlreadyIsKeptOnlyFromSystemOrAdministrators()
    {
        // The event exists already, made by this test's account (as an engine waiting on a supervisor's holds it open across a restart):
        // a second supervisor keeps it when its owner is SYSTEM or administrators, and otherwise refuses to start, naming the owner.
        using var place = new EngineIdentityWindowsTests.Place();
        using var first = place.Host();
        first.Prepare();
        using var made = EventWaitHandleAcl.OpenExisting(place.StopEvent, EventWaitHandleRights.Synchronize | EventWaitHandleRights.ReadPermissions);
        var owner = ((SecurityIdentifier)made.GetAccessControl().GetOwner(typeof(SecurityIdentifier))!).Value;
        using var again = place.Host();
        if (MachinePlan.StopEventOwnerTrusted(owner))
        {
            again.Prepare();
            Assert.Contains("is held open already (by an engine waiting on it): kept", place.Log(), StringComparison.Ordinal);
        }
        else
        {
            var refused = Assert.Throws<InvalidOperationException>(again.Prepare);
            Assert.Equal($"the engine's stop event {place.StopEvent} exists already, made by {owner}, which could set or clear it: the supervisor does not start beside it",
                refused.Message);
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
