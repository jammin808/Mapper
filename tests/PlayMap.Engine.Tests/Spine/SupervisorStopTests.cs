using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using PlayMap.Core.Bus;
using PlayMap.Engine.Host;
using PlayMap.Engine.Tests.Host;

namespace PlayMap.Engine.Tests.Spine;

/// <summary>The supervisor's stop event as the engine's entry waits on it. An engine started with --supervised opens it as it starts
/// and, once it is set — before the engine is up or after — stops as any asked stop does: exit 0, the recovery record written, its
/// journal saying the supervisor asked. An engine started without --supervised opens none; a supervised engine that finds none runs,
/// says so, and stops by its other signals. On Windows the event is a named one the engine opens with Synchronize alone, so it waits
/// on it and can neither set nor clear it.</summary>
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
            var opener = new Opener(null, @"Global\PlayMap-Engine-Stop does not exist");
            var log = new KeptLog();
            using var stop = new CancellationTokenSource();
            var run = EngineEntry.RunAsync(Args(home, supervised: true), EngineParts.Bare, stop.Token, log, opener.Open);
            Assert.True(await UpAsync(log));
            Assert.Equal(1, opener.Calls);
            Assert.Contains("The supervisor's stop event is not waited on (Global\\PlayMap-Engine-Stop does not exist): its stop ends this engine only by termination.",
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

    /// <summary>A named event as the supervisor makes it for the show user: this test's user may only wait on it.</summary>
    [SupportedOSPlatform("windows")]
    private static EventWaitHandle MadeToBeWaitedOn(string name)
    {
        var security = new EventWaitHandleSecurity();
        security.AddAccessRule(new EventWaitHandleAccessRule(WindowsIdentity.GetCurrent().User!, EventWaitHandleRights.Synchronize, AccessControlType.Allow));
        var made = EventWaitHandleAcl.Create(false, EventResetMode.ManualReset, name, out var created, security);
        Assert.True(created);
        return made;
    }

    private static string TestEventName() => @"Local\PlayMap-Engine-Stop-Test-" + Guid.NewGuid().ToString("N");

    [WindowsFact]
    [SupportedOSPlatform("windows")]
    public void TheEngineOpensTheNamedEventWithSynchronizeAloneAndCanNeitherSetNorClearIt()
    {
        var name = TestEventName();
        using var made = MadeToBeWaitedOn(name);
        // The event's ACL gives this user Synchronize alone: an open that asks for more is refused.
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
    public async Task ASupervisedEngineStopsInOrderWhenItsNamedEventIsSet()
    {
        var name = TestEventName();
        var home = Home("Named");
        try
        {
            using var made = MadeToBeWaitedOn(name);
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
