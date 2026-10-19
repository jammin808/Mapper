using PlayMap.Core.Machine;
using PlayMap.Supervisor.Install;

namespace PlayMap.Supervisor.Tests.Install;

/// <summary>The installer's machine-level commands in their order, against a fake machine and fake engines on loopback: <c>uninstall</c>
/// runs the armed check first, so an armed or silent engine refuses it with nothing stopped and the service in place; an engine that
/// cannot be stopped starts the service again and removes nothing; every removal is tried and what remains is named. <c>upgrade-stop</c>
/// refuses while a program other than the supervised engine runs from the install's folder, before anything is stopped, and starts the
/// service again when something still runs after the engine stopped.</summary>
public sealed class MachineCommandsTests
{
    private static readonly TimeSpan Answer = TimeSpan.FromSeconds(5);

    /// <summary>A machine that records what was asked of it, in order.</summary>
    private sealed class FakeMachine : IMachineActions
    {
        public List<string> Asked { get; } = [];

        public EngineStop EngineStops { get; init; } = EngineStop.Stopped;

        public IReadOnlyList<string> Remains { get; init; } = [];

        public bool ServiceRefuses { get; init; }

        public List<IReadOnlyList<RunningProgram>> Running { get; init; } = [];

        public string InstallDir => @"C:\Program Files\PlayMap";

        public void StopService(Action<string> say)
        {
            Asked.Add("stop service");
            if (ServiceRefuses)
                throw new InvalidOperationException("sc.exe stop PlayMapSupervisor answered 5: Access is denied.");
        }

        public void StartService(Action<string> say) => Asked.Add("start service");

        public EngineStop StopEngine(Action<string> say)
        {
            Asked.Add("stop engine");
            return EngineStops;
        }

        public IReadOnlyList<string> Remove(string showUser, Action<string> say)
        {
            Asked.Add("remove " + showUser);
            return Remains;
        }

        public IReadOnlyList<RunningProgram> RunningFromInstall(bool exceptTheEngine)
        {
            Asked.Add(exceptTheEngine ? "running but the engine" : "running");
            if (Running.Count == 0)
                return [];
            var next = Running[0];
            if (Running.Count > 1)
                Running.RemoveAt(0);
            return next;
        }
    }

    private sealed class Folder : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("playmap-commands-").FullName;

        public void Dispose() => Directory.Delete(Path, true);
    }

    /// <summary>The look at a folder whose record names this test process, which the look takes for the engine.</summary>
    private static ArmedLook Look(Folder folder) => new(folder.Path, () => null, r => r.Pid == Environment.ProcessId, Answer);

    private static async Task<(int Exit, List<string> Said)> Uninstall(FakeMachine machine, Folder folder)
    {
        var said = new List<string>();
        var exit = await MachineCommands.UninstallAsync(machine, Look(folder), "MACHINE\\show", TimeZoneInfo.Utc, said.Add);
        return (exit, said);
    }

    [Fact]
    public async Task AnArmedEngineRefusesTheUninstallBeforeAnythingIsStopped()
    {
        using var folder = new Folder();
        var since = new DateTimeOffset(2026, 10, 19, 20, 31, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        await using var engine = FakeEngine.Answering($"{{\"armed\":true,\"by\":\"Desk\",\"since\":{since},\"words\":\"armed\"}}");
        Assert.True(new EnginePidRecord(Environment.ProcessId, engine.Port, 1).TryWrite(folder.Path));
        var machine = new FakeMachine();
        var (exit, said) = await Uninstall(machine, folder);
        Assert.Equal(Program.Refused, exit);
        Assert.Equal(["the show is armed on this node since 20:31 by Desk: disarm first"], said);
        Assert.Empty(machine.Asked);
    }

    [Fact]
    public async Task AnEngineThatDoesNotAnswerRefusesTheUninstallBeforeAnythingIsStopped()
    {
        using var folder = new Folder();
        await using var engine = FakeEngine.Silent();
        Assert.True(new EnginePidRecord(Environment.ProcessId, engine.Port, 1).TryWrite(folder.Path));
        var machine = new FakeMachine();
        var (exit, said) = await Uninstall(machine, folder);
        Assert.Equal(Program.Refused, exit);
        Assert.Contains($"(pid {Environment.ProcessId}) but does not answer", Assert.Single(said), StringComparison.Ordinal);
        Assert.Empty(machine.Asked);
    }

    [Fact]
    public async Task WithNoEngineTheServiceTheEngineAndEveryActionAreTakenBackInOrder()
    {
        using var folder = new Folder();
        var machine = new FakeMachine { EngineStops = EngineStop.NoneRan };
        var (exit, _) = await Uninstall(machine, folder);
        Assert.Equal(Program.Done, exit);
        Assert.Equal(["stop service", "stop engine", "remove MACHINE\\show"], machine.Asked);
    }

    [Fact]
    public async Task WhatCouldNotBeTakenBackIsNamedAndRefusesTheUninstall()
    {
        using var folder = new Folder();
        var machine = new FakeMachine { Remains = ["the firewall rule 'PlayMap (UDP in)'", "WER LocalDumps for PlayMap.exe"] };
        var (exit, said) = await Uninstall(machine, folder);
        Assert.Equal(Program.Refused, exit);
        Assert.Equal("not taken back: the firewall rule 'PlayMap (UDP in)'; WER LocalDumps for PlayMap.exe", said[^1]);
        Assert.Equal(["stop service", "stop engine", "remove MACHINE\\show"], machine.Asked);
    }

    [Fact]
    public async Task AnEngineThatCannotBeStoppedStartsTheServiceAgainAndRemovesNothing()
    {
        using var folder = new Folder();
        var machine = new FakeMachine { EngineStops = EngineStop.Failed };
        var (exit, said) = await Uninstall(machine, folder);
        Assert.Equal(Program.Refused, exit);
        Assert.Equal(["stop service", "stop engine", "start service"], machine.Asked);
        Assert.Equal("the engine still runs: the service is started again and nothing is removed", said[^1]);
    }

    [Fact]
    public async Task AServiceThatCannotBeStoppedRefusesWithNothingRemoved()
    {
        using var folder = new Folder();
        var machine = new FakeMachine { ServiceRefuses = true };
        var (exit, said) = await Uninstall(machine, folder);
        Assert.Equal(Program.Refused, exit);
        Assert.Equal(["stop service"], machine.Asked);
        Assert.Equal("the service could not be stopped (sc.exe stop PlayMapSupervisor answered 5: Access is denied.): nothing was removed", said[^1]);
    }

    [Fact]
    public async Task AnUpgradeIsRefusedWhileAProgramRunsFromTheInstallBeforeAnythingIsStopped()
    {
        using var folder = new Folder();
        var machine = new FakeMachine { Running = [[new RunningProgram(77, @"C:\Program Files\PlayMap\PlayMap.exe")]] };
        var said = new List<string>();
        Assert.Equal(Program.Refused, await MachineCommands.UpgradeStopAsync(machine, Look(folder), TimeZoneInfo.Utc, said.Add));
        Assert.Equal(["running but the engine"], machine.Asked);
        Assert.Equal(@"a program runs from C:\Program Files\PlayMap (pid 77, PlayMap.exe): close it (the desk, or an engine run by hand), then install again",
            said[^1]);
    }

    [Fact]
    public async Task AnUpgradeStopsTheServiceAndTheEngineAndLeavesTheMachineActions()
    {
        using var folder = new Folder();
        var machine = new FakeMachine();
        var said = new List<string>();
        Assert.Equal(Program.Done, await MachineCommands.UpgradeStopAsync(machine, Look(folder), TimeZoneInfo.Utc, said.Add));
        Assert.Equal(["running but the engine", "stop service", "stop engine", "running"], machine.Asked);
        Assert.Equal("the earlier install's service and engine are stopped: its files may be replaced", said[^1]);
    }

    [Fact]
    public async Task AnUpgradeWithAProgramLeftRunningAfterTheEngineStoppedStartsTheServiceAgain()
    {
        using var folder = new Folder();
        var machine = new FakeMachine { Running = [[], [new RunningProgram(91, @"C:\Program Files\PlayMap\PlayMap.exe")]] };
        var said = new List<string>();
        Assert.Equal(Program.Refused, await MachineCommands.UpgradeStopAsync(machine, Look(folder), TimeZoneInfo.Utc, said.Add, TimeSpan.Zero));
        Assert.Equal(["running but the engine", "stop service", "stop engine", "running", "start service"], machine.Asked);
        Assert.Equal(@"a program runs from C:\Program Files\PlayMap (pid 91, PlayMap.exe) after the engine stopped: the service is started again and nothing is replaced",
            said[^1]);
    }

    [Fact]
    public async Task AnUpgradeWhoseEngineCannotBeStoppedStartsTheServiceAgain()
    {
        using var folder = new Folder();
        var machine = new FakeMachine { EngineStops = EngineStop.Failed };
        var said = new List<string>();
        Assert.Equal(Program.Refused, await MachineCommands.UpgradeStopAsync(machine, Look(folder), TimeZoneInfo.Utc, said.Add));
        Assert.Equal(["running but the engine", "stop service", "stop engine", "start service"], machine.Asked);
    }

    [Fact]
    public async Task AnUpgradeIsRefusedArmedWithNothingStopped()
    {
        using var folder = new Folder();
        await using var engine = FakeEngine.Answering("{\"armed\":true,\"by\":\"Desk\",\"since\":0,\"words\":\"armed\"}");
        Assert.True(new EnginePidRecord(Environment.ProcessId, engine.Port, 1).TryWrite(folder.Path));
        var machine = new FakeMachine();
        var said = new List<string>();
        Assert.Equal(Program.Refused, await MachineCommands.UpgradeStopAsync(machine, Look(folder), TimeZoneInfo.Utc, said.Add));
        Assert.Empty(machine.Asked);
        Assert.Equal(["the show is armed on this node by Desk: disarm first"], said);
    }
}
