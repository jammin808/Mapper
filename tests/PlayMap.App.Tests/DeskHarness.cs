using Avalonia;
using Avalonia.Threading;
using Avalonia.VisualTree;
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

    /// <summary>The verbs the desk sent, without its preview requests and its availability queries.</summary>
    public IReadOnlyList<string> Verbs() =>
        Link.SentLines.Where(l => !l.StartsWith("VIEW ", StringComparison.Ordinal) && l is not ("MEDIA LIST" or "DISPLAYS")).ToList();

    /// <summary>Clicks a control's centre with the headless mouse (the real input path, hit testing and all).</summary>
    public static void Click(Avalonia.Controls.TopLevel window, Avalonia.Controls.Control control, Avalonia.Input.MouseButton button = Avalonia.Input.MouseButton.Left)
    {
        var centre = control.TranslatePoint(new Avalonia.Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)
            ?? throw new InvalidOperationException("the control is not in the window");
        Avalonia.Headless.HeadlessWindowExtensions.MouseDown(window, centre, button, Avalonia.Input.RawInputModifiers.None);
        Avalonia.Headless.HeadlessWindowExtensions.MouseUp(window, centre, button, Avalonia.Input.RawInputModifiers.None);
        Pump();
    }

    /// <summary>Waits for every command a press started, then lets the pushes land.</summary>
    public static async Task Drain(IEnumerable<ViewModels.DeskAction> actions)
    {
        foreach (var a in actions)
            await a.Command.Last;
        Pump();
    }

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

    public async ValueTask DisposeAsync()
    {
        await Main.DisposeAsync();
        await Link.DisposeAsync();
    }
}
