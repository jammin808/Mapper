using PlayMap.App.Link;

namespace PlayMap.App.Tests.Install;

/// <summary>On a supervised machine the desk attaches to the engine the supervisor keeps and never starts its own: no start is asked,
/// nothing is watched, and the desk says why; on any other machine it starts and watches its own as before.</summary>
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
        supervisor.Start();
        supervisor.Start();
        Assert.False(supervisor.IsWatching);
        Assert.Equal(0, asked);
        Assert.Equal(0, supervisor.Starts);
        Assert.Equal(["This machine's engine is kept by its supervisor — the desk attaches to it.",
            "This machine's engine is kept by its supervisor — the desk attaches to it."], notes);
    }

    [Fact]
    public async Task AnUnsupervisedMachinesDeskStartsItsOwn()
    {
        var started = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var supervisor = new EngineSupervisor(() => new FakeEngine(), supervisedMachine: () => false);
        supervisor.Started += started.SetResult;
        supervisor.Start();
        Assert.Equal(4120, await started.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
        Assert.True(supervisor.IsWatching);
        Assert.Equal(1, supervisor.Starts);
    }

    [Fact]
    public void ATestMachineIsNotSupervised()
    {
        // No test machine holds the install's key: Linux has no registry, and the Windows test job installs nothing.
        Assert.False(EngineSupervisor.IsSupervisedMachine());
    }
}
