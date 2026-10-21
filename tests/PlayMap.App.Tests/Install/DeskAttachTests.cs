using PlayMap.App.Link;
using PlayMap.App.Tests.Quad;
using PlayMap.App.ViewModels;
using PlayMap.Core.Machine;
using PlayMap.Core.Wire;

namespace PlayMap.App.Tests.Install;

/// <summary>On a supervised machine the desk attaches to the engine the supervisor keeps and never starts its own: no start is asked,
/// nothing is watched, the start answers that it began nothing, and the desk says why; Connect ▸ This machine there says it attaches
/// to that engine, makes the supervisor once, starts nothing, notes that the supervisor keeps the engine, what the supervisor's status
/// file says of it (its state and cause, and the line that resumes it when it holds the engine or has stopped trying) and that the
/// desk waits for it, attaches when the engine answers on loopback, and, when it does not, fails with the supervisor's words. On any
/// other machine the desk starts and watches its own as before.</summary>
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
        /// <param name="status">The link's status as Connect begins.</param>
        /// <param name="supervisorStatus">The supervisor's status file as the desk reads it (null: none).</param>
        public SupervisedRig(LinkStatus status, SupervisorStatus? supervisorStatus = null)
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
                time: Time,
                supervisedMachine: () => true,
                supervisorStatus: () => supervisorStatus);
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
        Assert.Equal([EngineSupervisor.SupervisedWords, ConnectionManager.SupervisorSays(null), ConnectionManager.WaitingWords], rig.Notes);

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
    public async Task ConnectOnASupervisedMachineWhoseSupervisorHoldsTheEngineSaysItsStateCauseAndTheLineThatResumesIt()
    {
        var ct = TestContext.Current.CancellationToken;
        var held = new SupervisorStatus("held", 0, 1, "stopped by a person (PlayMap.Supervisor.exe stop)", "", 1_760_000_000_000);
        await using var rig = new SupervisedRig(LinkStatus.Down, held);
        var connect = new ConnectViewModel(rig.Connections) { Choice = ConnectChoice.OwnEngine };
        // What Connect is about to do on a supervised machine: attach to the engine its supervisor keeps, starting none.
        Assert.Equal(ConnectViewModel.SupervisedOwnSentence, connect.Sentence);
        Assert.DoesNotContain("starting it", connect.Sentence, StringComparison.Ordinal);

        connect.ConnectCommand.Execute(null);
        await Waits(rig.Time, 1);
        rig.Time.Advance(TimeSpan.FromMilliseconds(1_500));
        await Waits(rig.Time, 1);
        rig.Time.Advance(TimeSpan.FromSeconds(15));
        await connect.ConnectCommand.Last.WaitAsync(TimeSpan.FromSeconds(30), ct);

        // Nothing was started, and the desk says what the supervisor's status file says: the state, its cause, the line that resumes it
        // and when the file was written.
        Assert.Equal(0, rig.Asked);
        var said = ConnectionManager.SupervisorSays(held);
        Assert.Equal([EngineSupervisor.SupervisedWords, said, ConnectionManager.WaitingWords], rig.Notes);
        Assert.Equal(said, rig.Connections.SupervisorWords);
        Assert.Contains("holds the engine", said, StringComparison.Ordinal);
        Assert.Contains("stopped by a person (PlayMap.Supervisor.exe stop)", said, StringComparison.Ordinal);
        Assert.Contains("PlayMap.Supervisor.exe resume", said, StringComparison.Ordinal);
        Assert.Contains("2025-10-09 08:53:20 UTC", said, StringComparison.Ordinal);
        Assert.True(connect.IsOpen);
        Assert.Equal("This machine's engine did not answer. " + said, connect.Error);
    }

    [Fact]
    public void TheSupervisorsStatusIsSaidForEachStateWithTheResumeLineWhereOneIsNeeded()
    {
        static SupervisorStatus In(string state, string cause = "") => new(state, 0, 5, cause, "", 1_760_000_000_000);
        var stopped = ConnectionManager.SupervisorSays(In("stopped-too-many-restarts", "the engine exited (code 3)"));
        Assert.Contains("stopped after too many restarts (the engine exited (code 3))", stopped, StringComparison.Ordinal);
        Assert.Contains("PlayMap.Supervisor.exe resume", stopped, StringComparison.Ordinal);
        foreach (var state in new[] { "waiting-for-session", "starting", "running", "stopping", "backoff" })
            Assert.DoesNotContain("resume", ConnectionManager.SupervisorSays(In(state)), StringComparison.Ordinal);
        Assert.Contains("waits for the show user's session", ConnectionManager.SupervisorSays(In("waiting-for-session")), StringComparison.Ordinal);
        Assert.Contains("backoff (last cause: the engine exited)", ConnectionManager.SupervisorSays(In("backoff", "the engine exited")), StringComparison.Ordinal);
        Assert.Contains("PlayMap.Supervisor.exe status", ConnectionManager.SupervisorSays(null), StringComparison.Ordinal);
    }

    [Fact]
    public async Task OnAMachineNoSupervisorKeepsConnectSaysItStartsTheEngine()
    {
        await using var connections = new ConnectionManager(new DeskSession(a => a()), links: _ => null, supervisedMachine: () => false,
            supervisorStatus: () => throw new InvalidOperationException("an unsupervised machine's desk reads no supervisor's status"));
        var connect = new ConnectViewModel(connections) { Choice = ConnectChoice.OwnEngine };
        Assert.Equal(ConnectViewModel.OwnSentence, connect.Sentence);
        Assert.Contains("starting it", connect.Sentence, StringComparison.Ordinal);
    }

    [Fact]
    public void ATestMachineIsNotSupervised()
    {
        // No test machine holds the install's key: Linux has no registry, and the Windows test job installs nothing.
        Assert.False(Engine.Install.InstallFacts.SupervisedMachine());
    }
}
