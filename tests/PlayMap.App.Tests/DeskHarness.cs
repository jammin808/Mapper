using Avalonia.Threading;
using PlayMap.App.Link;
using PlayMap.App.ViewModels;
using PlayMap.Core.Time;

namespace PlayMap.App.Tests;

/// <summary>A desk on the demo engine with a hand-moved clock: the link records every line sent, and
/// <see cref="Pump"/> runs the dispatcher so the mirror catches up with the engine's pushes.</summary>
public sealed class DeskHarness : IAsyncDisposable
{
    public DeskHarness()
    {
        Clock = new ManualClock(1_000_000_000);
        Link = new FakeEngineLink(clock: Clock);
        Session = new DeskSession(a => Dispatcher.UIThread.Post(a));
        Main = new MainViewModel(Session);
        Main.Attach(Link, "Demo node");
        Pump();
        Link.ClearSent();
    }

    public ManualClock Clock { get; }

    public FakeEngineLink Link { get; }

    public DeskSession Session { get; }

    public MainViewModel Main { get; }

    public static void Pump() => Dispatcher.UIThread.RunJobs();

    /// <summary>Waits for a command a press started, then lets the pushes land.</summary>
    public static async Task Settle(Task task)
    {
        await task;
        Pump();
    }

    /// <summary>Lets the throttle's interval pass and the coalesced lines go out.</summary>
    public async Task FlushThrottle()
    {
        await Session.Throttle.CommitAsync();
        Pump();
    }

    public async ValueTask DisposeAsync() => await Main.DisposeAsync();
}
