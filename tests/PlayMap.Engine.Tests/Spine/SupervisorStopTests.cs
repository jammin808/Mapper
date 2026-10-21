using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using PlayMap.Core.Bus;
using PlayMap.Core.Machine;
using PlayMap.Engine.Host;
using PlayMap.Engine.Tests.Host;

namespace PlayMap.Engine.Tests.Spine;

/// <summary>The supervisor's stop event as the engine's entry waits on it. An engine started with --supervised opens the one its
/// --stop-event names as it starts and, once it is set — while the engine starts, which then stops during its start, or once it is up —
/// stops as any asked stop does: exit 0, the recovery record written, its journal saying the supervisor asked. An engine started without
/// --supervised opens none; a supervised engine that finds none, or is given a name that is not a supervisor's stop event's, runs, says
/// so, and stops by its other signals. On Windows the event is a named one the engine opens with Synchronize and ReadPermissions alone,
/// so it waits on it and reads its owner and can neither set nor clear it, and waits on it only when SYSTEM or administrators made
/// it.</summary>
public sealed class SupervisorStopTests
{
    private static DirectoryInfo Home(string name)
    {
        var dir = Directory.CreateTempSubdirectory("playmap-stop-");
        new NodeHome(dir.FullName).SaveSettings(NodeSettings.CreateDefault(name) with
        {
            Bind = "127.0.0.1", WirePort = 0, HttpPort = 0, SyncPort = 0, Announce = false,
        });
        return dir;
    }

    private static string[] Args(DirectoryInfo home, bool supervised) =>
        supervised ? ["engine", "headless", "--home", home.FullName, "--supervised"] : ["engine", "headless", "--home", home.FullName];

    private static Task<bool> UpAsync(KeptLog log) =>
        Eventually.TrueAsync(() => log.Lines.Any(l => l.Contains(" is up", StringComparison.Ordinal)), 20000);

    private static List<string> Journal(DirectoryInfo home) =>
        File.ReadLines(new NodeHome(home.FullName).JournalPath).Select(row =>
        {
            using var doc = JsonDocument.Parse(row);
            return doc.RootElement.GetProperty("text").GetString() ?? "";
        }).ToList();

    /// <summary>An opener that hands the engine <paramref name="stopEvent"/> and counts its calls.</summary>
    private sealed class Opener(WaitHandle? stopEvent, string why = "")
    {
        public int Calls { get; private set; }

        public WaitHandle? Open(out string said)
        {
            Calls++;
            said = why;
            return stopEvent;
        }
    }

    private static async Task AssertStoppedBySupervisorAsync(Task<int> run, KeptLog log, DirectoryInfo home)
    {
        Assert.Equal(EngineEntry.Stopped, await run.WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.True(File.Exists(Path.Combine(home.FullName, "playmap.recovery.json")));
        Assert.Contains("The engine waits on the supervisor's stop event.", log.Lines);
        Assert.Contains("The engine was asked to stop by the supervisor.", log.Lines);
        var journal = Journal(home);
        var asked = journal.IndexOf("the supervisor asked the engine to stop");
        Assert.True(asked >= 0, string.Join("\n", journal));
        Assert.Contains(journal.Skip(asked), t => t.EndsWith(" stopped; the recovery record holds the show", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ASupervisedEngineStopsInOrderOnceTheSupervisorSetsItsEvent()
    {
        var home = Home("Supervised");
        try
        {
            using var stopEvent = new EventWaitHandle(false, EventResetMode.ManualReset);
            var opener = new Opener(stopEvent);
            var log = new KeptLog();
            var run = EngineEntry.RunAsync(Args(home, supervised: true), EngineParts.Bare, CancellationToken.None, log, opener.Open);
            Assert.True(await UpAsync(log));
            Assert.Equal(1, opener.Calls);
            Assert.False(run.IsCompleted);

            stopEvent.Set();
            await AssertStoppedBySupervisorAsync(run, log, home);
        }
        finally
        {
            home.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task AStopSetBeforeTheEngineIsUpStillStopsIt()
    {
        // The supervisor keeps its event set until the engine has gone: an engine that opens it set never stays.
        var home = Home("Early");
        try
        {
            using var stopEvent = new EventWaitHandle(true, EventResetMode.ManualReset);
            var log = new KeptLog();
            var run = EngineEntry.RunAsync(Args(home, supervised: true), EngineParts.Bare, CancellationToken.None, log, new Opener(stopEvent).Open);
            Assert.Equal(EngineEntry.Stopped, await run.WaitAsync(TimeSpan.FromSeconds(30)));
            Assert.Contains(log.Lines, l => l.Contains("asked to stop", StringComparison.Ordinal) && l.Contains("supervisor", StringComparison.Ordinal));
            // Stopped during its start or once up, the show's record says why: the recovery record is written and the journal says
            // the supervisor asked, before the row that says the engine stopped.
            Assert.True(File.Exists(Path.Combine(home.FullName, "playmap.recovery.json")));
            var journal = Journal(home);
            var asked = journal.IndexOf("the supervisor asked the engine to stop");
            Assert.True(asked >= 0, string.Join("\n", journal));
            Assert.Contains(journal.Skip(asked), t => t.EndsWith(" stopped; the recovery record holds the show", StringComparison.Ordinal));
        }
        finally
        {
            home.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task AnEngineTheSupervisorDidNotStartOpensNoStopEvent()
    {
        var home = Home("Desk's own");
        try
        {
            using var stopEvent = new EventWaitHandle(false, EventResetMode.ManualReset);
            var opener = new Opener(stopEvent);
            var log = new KeptLog();
            using var stop = new CancellationTokenSource();
            var run = EngineEntry.RunAsync(Args(home, supervised: false), EngineParts.Bare, stop.Token, log, opener.Open);
            Assert.True(await UpAsync(log));
            Assert.Equal(0, opener.Calls);

            await stop.CancelAsync();
            Assert.Equal(EngineEntry.Stopped, await run.WaitAsync(TimeSpan.FromSeconds(30)));
            Assert.Equal(0, opener.Calls);
            Assert.Contains("The engine was asked to stop.", log.Lines);
            Assert.DoesNotContain("The engine waits on the supervisor's stop event.", log.Lines);
            Assert.DoesNotContain("The engine was asked to stop by the supervisor.", log.Lines);
            Assert.DoesNotContain("the supervisor asked the engine to stop", Journal(home));
        }
        finally
        {
            home.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task ASupervisedEngineThatFindsNoStopEventRunsSaysSoAndStopsByItsSignals()
    {
        var home = Home("No event");
        try
        {
            var opener = new Opener(null, @"Global\PlayMap-Engine-Stop-0123456789abcdef0123456789abcdef does not exist");
            var log = new KeptLog();
            using var stop = new CancellationTokenSource();
            var run = EngineEntry.RunAsync(Args(home, supervised: true), EngineParts.Bare, stop.Token, log, opener.Open);
            Assert.True(await UpAsync(log));
            Assert.Equal(1, opener.Calls);
            Assert.Contains("The supervisor's stop event is not waited on (Global\\PlayMap-Engine-Stop-0123456789abcdef0123456789abcdef does not exist): its stop ends this engine only by termination.",
                log.Lines);

            await stop.CancelAsync();
            Assert.Equal(EngineEntry.Stopped, await run.WaitAsync(TimeSpan.FromSeconds(30)));
            Assert.Contains("The engine was asked to stop.", log.Lines);
        }
        finally
        {
            home.Delete(recursive: true);
        }
    }

    [Fact]
    public void TheEngineTakesTheStopEventItsSupervisorNamesAndOnlyASupervisorsName()
    {
        var name = SupervisorNames.NewStopEvent(SupervisorNames.StopEventPrefix);
        var options = EngineOptions.Parse(["engine", "--supervised", "--stop-event", name], out var error);
        Assert.Null(error);
        Assert.True(options.Supervised);
        Assert.Equal(name, options.StopEvent);
        EngineOptions.Parse(["engine", "--supervised", "--stop-event"], out error);
        Assert.Equal("--stop-event takes a name", error);
        Assert.Contains("[--stop-event <name>]", EngineEntry.Usage, StringComparison.Ordinal);

        Assert.Null(EngineEntry.OpenThisMachinesStopEvent(null, out var why));
        Assert.Equal("no stop event is named (--stop-event)", why);
        // Another program's event, a fixed name, or a supervisor's prefix with anything but its drawn digits: never opened.
        foreach (var other in new[] { @"Global\SomeService-Shutdown", @"Global\PlayMap-Engine-Stop", SupervisorNames.StopEventPrefix, name.ToUpperInvariant(), name + "0" })
        {
            Assert.Null(EngineEntry.OpenThisMachinesStopEvent(other, out why));
            Assert.Equal(other + " is not the name of a supervisor's stop event", why);
        }
    }

    [Fact]
    public async Task ASupervisedEngineGivenANameThatIsNoSupervisorsWaitsOnNothingAndSaysWhy()
    {
        var home = Home("Not a supervisor's");
        try
        {
            var log = new KeptLog();
            using var stop = new CancellationTokenSource();
            var run = EngineEntry.RunAsync([.. Args(home, supervised: true), "--stop-event", @"Global\SomeService-Shutdown"], EngineParts.Bare, stop.Token, log);
            Assert.True(await UpAsync(log));
            Assert.Contains("The supervisor's stop event is not waited on (Global\\SomeService-Shutdown is not the name of a supervisor's stop event): its stop ends this engine only by termination.",
                log.Lines);
            await stop.CancelAsync();
            Assert.Equal(EngineEntry.Stopped, await run.WaitAsync(TimeSpan.FromSeconds(30)));
        }
        finally
        {
            home.Delete(recursive: true);
        }
    }

    private static bool Elevated => OperatingSystem.IsWindows() && new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    /// <summary>A named event as the supervisor makes it for the show user — this test's user may only wait on it and read its owner —
    /// owned by <paramref name="owner"/>.</summary>
    [SupportedOSPlatform("windows")]
    private static EventWaitHandle MadeToBeWaitedOn(string name, SecurityIdentifier owner)
    {
        var security = new EventWaitHandleSecurity();
        security.SetOwner(owner);
        security.AddAccessRule(new EventWaitHandleAccessRule(WindowsIdentity.GetCurrent().User!, EventWaitHandleRights.Synchronize | EventWaitHandleRights.ReadPermissions,
            AccessControlType.Allow));
        var made = EventWaitHandleAcl.Create(false, EventResetMode.ManualReset, name, out var created, security);
        Assert.True(created);
        return made;
    }

    [SupportedOSPlatform("windows")]
    private static SecurityIdentifier Administrators => new(WellKnownSidType.BuiltinAdministratorsSid, null);

    private static string TestEventName() => @"Local\PlayMap-Engine-Stop-Test-" + Guid.NewGuid().ToString("N");

    [WindowsFact]
    [SupportedOSPlatform("windows")]
    public void TheEngineOpensTheNamedEventWithSynchronizeAndReadPermissionsAloneAndCanNeitherSetNorClearIt()
    {
        // Only an elevated caller may make an event administrators own, as the supervisor's are SYSTEM's.
        if (!Elevated)
            return;
        var name = TestEventName();
        using var made = MadeToBeWaitedOn(name, Administrators);
        // The event's ACL gives this user Synchronize and ReadPermissions alone: an open that asks for more is refused.
        Assert.Throws<UnauthorizedAccessException>(() => EventWaitHandle.OpenExisting(name));

        using var opened = EngineEntry.OpenStopEvent(name, out var why);
        Assert.Equal("", why);
        var engines = Assert.IsType<EventWaitHandle>(opened);
        Assert.False(engines.WaitOne(0));
        made.Set();
        Assert.True(engines.WaitOne(0));
        Assert.Throws<UnauthorizedAccessException>(() => engines.Reset());
        Assert.True(engines.WaitOne(0));
        made.Reset();
        Assert.Throws<UnauthorizedAccessException>(() => engines.Set());
        Assert.False(engines.WaitOne(0));

        Assert.Null(EngineEntry.OpenStopEvent(name + "-none", out var none));
        Assert.Equal(name + "-none does not exist", none);
    }

    [WindowsFact]
    [SupportedOSPlatform("windows")]
    public void AnEventAnotherAccountMadeIsNotWaitedOn()
    {
        // An event of the stop event's name and rights that an account other than SYSTEM or administrators owns (as a standard user's
        // would be): that account could set or clear it, so the engine leaves it and stops by its signals and termination alone.
        var name = TestEventName();
        var user = WindowsIdentity.GetCurrent().User!;
        using var made = MadeToBeWaitedOn(name, user);
        Assert.Null(EngineEntry.OpenStopEvent(name, out var why));
        Assert.Equal($"{name} was made by {user.Value}, not by SYSTEM or administrators", why);
    }

    [WindowsFact]
    [SupportedOSPlatform("windows")]
    public void ThisMachinesStopEventIsTheOneItsSupervisorNamedUnderTheGlobalPrefix()
    {
        if (!Elevated)
            return;
        var name = SupervisorNames.NewStopEvent(SupervisorNames.StopEventPrefix);
        using var made = MadeToBeWaitedOn(name, Administrators);
        using var opened = EngineEntry.OpenThisMachinesStopEvent(name, out var why);
        Assert.Equal("", why);
        Assert.NotNull(opened);
        made.Set();
        Assert.True(opened.WaitOne(0));
    }

    [WindowsFact]
    [SupportedOSPlatform("windows")]
    public async Task ASupervisedEngineStopsInOrderWhenItsNamedEventIsSet()
    {
        if (!Elevated)
            return;
        var name = TestEventName();
        var home = Home("Named");
        try
        {
            using var made = MadeToBeWaitedOn(name, Administrators);
            var log = new KeptLog();
            var run = EngineEntry.RunAsync(Args(home, supervised: true), EngineParts.Bare, CancellationToken.None, log,
                (out string why) => EngineEntry.OpenStopEvent(name, out why));
            Assert.True(await UpAsync(log));
            Assert.False(run.IsCompleted);

            made.Set();
            await AssertStoppedBySupervisorAsync(run, log, home);
        }
        finally
        {
            home.Delete(recursive: true);
        }
    }
}
