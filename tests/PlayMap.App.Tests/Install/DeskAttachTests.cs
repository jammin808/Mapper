using PlayMap.App.Link;
using PlayMap.App.Tests.Quad;
using PlayMap.App.ViewModels;
using PlayMap.Core.Wire;

namespace PlayMap.App.Tests.Install;

/// <summary>On a supervised machine the desk attaches to the engine the supervisor keeps and never starts its own: no start is asked,
/// nothing is watched, the start answers that it began nothing, and the desk says why; Connect ▸ This machine there makes the
/// supervisor once, starts nothing, notes that the supervisor keeps the engine and that the desk waits for it, and attaches when the
/// engine answers on loopback. On any other machine the desk starts and watches its own as before.</summary>
public sealed class DeskAttachTests
{
    private sealed class FakeEngine : IEngineProcess
    {
        public int Id => 4120;

        public Task<int> WaitForExitAsync(CancellationToken ct) => Task.Delay(Timeout.Infinite, ct).ContinueWith(_ => 0, TaskScheduler.Default);

        public void Dispose()
        {
            // Nothing is held.
        }
    }

    [Fact]
    public async Task ASupervisedMachinesDeskNeverStartsAnEngine()
    {
        var asked = 0;
        var notes = new List<string>();
        await using var supervisor = new EngineSupervisor(() =>
        {
            asked++;
            return new FakeEngine();
        }, supervisedMachine: () => true);
        supervisor.Note += notes.Add;
        Assert.False(supervisor.Start());
        Assert.False(supervisor.Start());
        Assert.False(supervisor.IsWatching);
        Assert.Equal(0, asked);
        Assert.Equal(0, supervisor.Starts);
        Assert.Equal([EngineSupervisor.SupervisedWords, EngineSupervisor.SupervisedWords], notes);
        Assert.Equal("This machine's engine is kept by its supervisor — the desk attaches to it.", EngineSupervisor.SupervisedWords);
    }

    [Fact]
    public async Task AnUnsupervisedMachinesDeskStartsItsOwn()
    {
        var started = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var notes = new List<string>();
        await using var supervisor = new EngineSupervisor(() => new FakeEngine(), supervisedMachine: () => false);
        supervisor.Started += started.SetResult;
        supervisor.Note += notes.Add;
        Assert.True(supervisor.Start());
        Assert.Equal(4120, await started.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
        Assert.True(supervisor.IsWatching);
        Assert.Equal(1, supervisor.Starts);
        Assert.Equal([EngineSupervisor.StartingWords], notes);
    }

    /// <summary>Connect's own-engine path on a supervised machine, through the connection manager's seams (the link it makes for
    /// loopback, the supervisor it makes, the clock its waits run on).</summary>
    private sealed class SupervisedRig : IAsyncDisposable
    {
        public SupervisedRig(LinkStatus status)
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
                        Asked++;
                        return new FakeEngine();
                    }, supervisedMachine: () => true);
                    return Supervisor;
                },
                time: Time);
            Connections.Note += Notes.Add;
        }

        public FakeEngineLink Link { get; }

        public DeskSession Session { get; }

        public ConnectionManager Connections { get; }

        public ManualTime Time { get; } = new();

        public List<EngineEndpoint> Endpoints { get; } = [];

        public List<string> Notes { get; } = [];

        public int Supervisors { get; private set; }

        public int Asked { get; private set; }

        public EngineSupervisor? Supervisor { get; private set; }

        public async ValueTask DisposeAsync()
        {
            await Connections.DisposeAsync();
            await Session.DisposeAsync();
        }
    }

    /// <summary>Until the manager's clock holds this many waits (the connect runs on another thread up to its next wait).</summary>
    private static async Task Waits(ManualTime time, int count)
    {
        while (time.Waiting < count)
            await Task.Delay(1, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ConnectOnASupervisedMachineStartsNoEngineAndAttachesWhenTheSupervisorsEngineAnswers()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var rig = new SupervisedRig(LinkStatus.Down);
        var connecting = rig.Connections.ConnectOwnAsync(ct);
        Assert.Same(rig.Link, rig.Session.Link);
        await Waits(rig.Time, 1);

        // 1.5 s on the manager's clock with no answer on loopback: the supervisor is made, starts nothing and answers so, and the desk
        // waits for the engine this machine's supervisor keeps.
        rig.Time.Advance(TimeSpan.FromMilliseconds(1_500));
        await Waits(rig.Time, 1);
        Assert.False(connecting.IsCompleted);
        Assert.Equal(1, rig.Supervisors);
        Assert.Equal(0, rig.Asked);
        Assert.Equal(0, rig.Supervisor!.Starts);
        Assert.False(rig.Supervisor.IsWatching);
        Assert.Equal([EngineSupervisor.SupervisedWords, ConnectionManager.WaitingWords], rig.Notes);

        // The supervisor's engine answers on loopback: the desk is on it, and still nothing was started.
        rig.Link.SetStatus(LinkStatus.Live);
        Assert.True(await connecting.WaitAsync(TimeSpan.FromSeconds(30), ct));
        Assert.Same(rig.Link, rig.Session.Link);
        Assert.Equal(LinkStatus.Live, rig.Session.Link!.Status);
        Assert.Equal([EngineEndpoint.Loopback], rig.Endpoints);
        Assert.Equal(1, rig.Supervisors);
        Assert.Equal(0, rig.Asked);
        Assert.Equal(0, rig.Supervisor.Starts);
        Assert.DoesNotContain(EngineSupervisor.StartingWords, rig.Notes);
    }

    [Fact]
    public async Task ConnectOnASupervisedMachineWhoseEngineAnswersMakesNoSupervisor()
    {
        await using var rig = new SupervisedRig(LinkStatus.Live);
        Assert.True(await rig.Connections.ConnectOwnAsync(TestContext.Current.CancellationToken));
        Assert.Same(rig.Link, rig.Session.Link);
        Assert.Equal([EngineEndpoint.Loopback], rig.Endpoints);
        Assert.Equal(0, rig.Supervisors);
        Assert.Equal(0, rig.Asked);
        Assert.Empty(rig.Notes);
    }

    [Fact]
    public void ATestMachineIsNotSupervised()
    {
        // No test machine holds the install's key: Linux has no registry, and the Windows test job installs nothing.
        Assert.False(Engine.Install.InstallFacts.SupervisedMachine());
    }
}
