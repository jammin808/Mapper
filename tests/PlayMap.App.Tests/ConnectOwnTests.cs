using PlayMap.App.Link;
using PlayMap.App.Tests.Quad;
using PlayMap.App.ViewModels;
using PlayMap.Core.Wire;

namespace PlayMap.App.Tests;

/// <summary>Connect's "This machine's engine", through the connection manager's own seams (the link it makes for an endpoint, the
/// supervisor it makes for this machine's engine, the clock its waits run on): an engine already live on loopback is attached and none
/// is started; one that does not answer within 1.5 s is started once, watched, and attached when it answers. A network node is reached
/// at the endpoint the person typed and never started.</summary>
public sealed class ConnectOwnTests
{
    /// <summary>A started engine that runs until the desk stops watching it.</summary>
    private sealed class RunningEngine : IEngineProcess
    {
        public int Id => 4120;

        public async Task<int> WaitForExitAsync(CancellationToken ct)
        {
            await Task.Delay(Timeout.Infinite, ct);
            return 0;
        }

        public void Dispose()
        {
            // Nothing is held: the engine is the test's.
        }
    }

    private sealed class Rig : IAsyncDisposable
    {
        public Rig(LinkStatus status)
        {
            Link = new FakeEngineLink(clock: new PlayMap.Core.Time.ManualClock());
            Link.SetStatus(status);
            Session = new DeskSession(a => a());
            Connections = new ConnectionManager(Session,
                links: endpoint =>
                {
                    Endpoints.Add(endpoint);
                    return Link;
                },
                supervisor: () =>
                {
                    Supervisors++;
                    Supervisor = new EngineSupervisor(() =>
                    {
                        Started.TrySetResult();
                        return new RunningEngine();
                    });
                    return Supervisor;
                },
                time: Time);
            Connections.Note += Notes.Add;
        }

        /// <summary>The link the manager is handed for the next endpoint it asks for (it disposes the one it lets go).</summary>
        public FakeEngineLink Link { get; set; }

        public DeskSession Session { get; }

        public ConnectionManager Connections { get; }

        public ManualTime Time { get; } = new();

        public List<EngineEndpoint> Endpoints { get; } = [];

        public List<string> Notes { get; } = [];

        public int Supervisors { get; private set; }

        public EngineSupervisor? Supervisor { get; private set; }

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask DisposeAsync()
        {
            await Connections.DisposeAsync();
            await Session.DisposeAsync();
        }
    }

    [Fact]
    public async Task AnEngineAlreadyLiveOnLoopbackIsAttachedAndNoneIsStarted()
    {
        await using var rig = new Rig(LinkStatus.Live);
        Assert.True(await rig.Connections.ConnectOwnAsync(TestCt.Ct));
        Assert.Equal([EngineEndpoint.Loopback], rig.Endpoints);
        Assert.Same(rig.Link, rig.Session.Link);
        Assert.Equal(LinkStatus.Live, rig.Session.Link!.Status);
        Assert.Equal(0, rig.Supervisors);
        Assert.Empty(rig.Notes);
    }

    [Fact]
    public async Task AnEngineThatDoesNotAnswerIsStartedOnceAndAttachedWhenItAnswers()
    {
        await using var rig = new Rig(LinkStatus.Down);
        var connecting = rig.Connections.ConnectOwnAsync(TestCt.Ct);
        Assert.Same(rig.Link, rig.Session.Link);
        Assert.Equal(1, rig.Time.Waiting);

        // 1.5 s on the manager's clock: until then nothing is started.
        rig.Time.Advance(TimeSpan.FromMilliseconds(1_499));
        Assert.False(connecting.IsCompleted);
        Assert.Equal(0, rig.Supervisors);
        rig.Time.Advance(TimeSpan.FromMilliseconds(1));
        await rig.Started.Task.WaitAsync(TimeSpan.FromSeconds(30), TestCt.Ct);
        Assert.Equal(1, rig.Supervisors);
        Assert.Equal(1, rig.Supervisor!.Starts);
        Assert.True(rig.Supervisor.IsWatching);

        // The engine answers: the desk is on it, and no second engine is started.
        rig.Link.SetStatus(LinkStatus.Live);
        Assert.True(await connecting.WaitAsync(TimeSpan.FromSeconds(30), TestCt.Ct));
        Assert.Same(rig.Link, rig.Session.Link);
        Assert.Equal(1, rig.Supervisor.Starts);
        Assert.Contains("Starting this machine's engine…", rig.Notes);
        Assert.Equal([EngineEndpoint.Loopback], rig.Endpoints);
    }

    /// <summary>Until the manager's clock holds this many waits (the connect runs on another thread up to its next wait).</summary>
    private static async Task Waits(ManualTime time, int count)
    {
        while (time.Waiting < count)
            await Task.Delay(1, TestCt.Ct);
    }

    /// <summary>This machine's engine stops answering after the desk started it: Connect ▸ This machine again finds its supervisor still
    /// watching, starts no second engine and notes that the desk waits for it, never that it starts one; the engine answers and the desk
    /// is on it.</summary>
    [Fact]
    public async Task ASecondConnectWhileTheSupervisorWatchesStartsNothingAndSaysItWaits()
    {
        await using var rig = new Rig(LinkStatus.Down);
        var first = rig.Connections.ConnectOwnAsync(TestCt.Ct);
        rig.Time.Advance(TimeSpan.FromMilliseconds(1_500));
        await rig.Started.Task.WaitAsync(TimeSpan.FromSeconds(30), TestCt.Ct);
        rig.Link.SetStatus(LinkStatus.Live);
        Assert.True(await first.WaitAsync(TimeSpan.FromSeconds(30), TestCt.Ct));
        Assert.Equal([EngineSupervisor.StartingWords], rig.Notes);

        // The engine goes quiet; the desk connects to it again on a new link.
        rig.Link = new FakeEngineLink(clock: new PlayMap.Core.Time.ManualClock());
        rig.Link.SetStatus(LinkStatus.Down);
        var second = rig.Connections.ConnectOwnAsync(TestCt.Ct);
        await Waits(rig.Time, 1);
        rig.Time.Advance(TimeSpan.FromMilliseconds(1_500));
        await Waits(rig.Time, 1);
        Assert.Equal([EngineSupervisor.StartingWords, ConnectionManager.WaitingWords], rig.Notes);
        Assert.Equal(1, rig.Supervisors);
        Assert.Equal(1, rig.Supervisor!.Starts);
        Assert.True(rig.Supervisor.IsWatching);

        rig.Link.SetStatus(LinkStatus.Live);
        Assert.True(await second.WaitAsync(TimeSpan.FromSeconds(30), TestCt.Ct));
        Assert.Same(rig.Link, rig.Session.Link);
        Assert.Equal(1, rig.Supervisor.Starts);
        Assert.Equal([EngineEndpoint.Loopback, EngineEndpoint.Loopback], rig.Endpoints);
    }

    [Fact]
    public async Task ANetworkNodeIsReachedAtItsEndpointAndNeverStarted()
    {
        await using var rig = new Rig(LinkStatus.Live);
        Assert.True(EngineEndpoint.TryParse("10.0.0.7:9941", "token", out var endpoint));
        Assert.True(await rig.Connections.ConnectNetworkAsync(endpoint, TestCt.Ct));
        Assert.Equal([endpoint], rig.Endpoints);
        Assert.Same(rig.Link, rig.Session.Link);
        Assert.Equal(0, rig.Supervisors);
    }
}
