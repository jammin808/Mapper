using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
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
    /// <param name="setup">Prepares the fake engine before the desk attaches (a node without some verbs, a fixture).</param>
    /// <param name="nowMs">The desk's own clock for the Esc guard (null: the machine's).</param>
    /// <param name="project">The show the fake engine opens (the demo show when null).</param>
    /// <param name="prefs">The desk's preferences (in memory when null; a desk.json for a restart).</param>
    /// <param name="time">The clock the throttle's interval runs on (the machine's when null).</param>
    public DeskHarness(Action<FakeEngineLink>? setup = null, Func<long>? nowMs = null, PlayMap.Core.Model.Project? project = null,
        PlayMap.App.Infrastructure.DeskPrefs? prefs = null, TimeProvider? time = null)
    {
        Clock = new ManualClock(1_000_000_000);
        Link = new FakeEngineLink(project, clock: Clock);
        setup?.Invoke(Link);
        Session = new DeskSession(a => Dispatcher.UIThread.Post(a), time);
        Main = new MainViewModel(Session, prefs, nowMs);
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

    /// <summary>Clicks a control's centre with the headless mouse (the real input path, hit testing and all). The press is aimed at a
    /// frame the window has just drawn (<see cref="Aim"/>) and is raised on the control or a part of it, or the click fails naming
    /// what it landed on.</summary>
    public static void Click(TopLevel window, Control control, MouseButton button = MouseButton.Left)
    {
        var centre = Aim(window, control);
        Press(window, control, centre, () =>
        {
            HeadlessWindowExtensions.MouseDown(window, centre, button, RawInputModifiers.None);
            HeadlessWindowExtensions.MouseUp(window, centre, button, RawInputModifiers.None);
        });
        Pump();
    }

    /// <summary>Presses a held button with the headless mouse and keeps it down to the end of its ring (the hold completed at its own
    /// time, as its timer completes it), then lets go. The press is aimed and checked as <see cref="Click"/>'s is.</summary>
    public static void Hold(TopLevel window, PlayMap.App.Controls.HoldButton button)
    {
        var centre = Aim(window, button);
        Press(window, button, centre, () => HeadlessWindowExtensions.MouseDown(window, centre, MouseButton.Left, RawInputModifiers.None));
        Pump();
        if (!button.Complete(button.HoldMs))
            throw new InvalidOperationException("the button is not held by the press");
        HeadlessWindowExtensions.MouseUp(window, centre, MouseButton.Left, RawInputModifiers.None);
        Pump();
    }

    /// <summary>The window point at a control's centre, once the window has drawn the frame its hit test reads: the hit test there
    /// finds the control or a part of it, or this throws naming what covers it.</summary>
    public static Point Aim(TopLevel window, Control control)
    {
        using (HeadlessWindowExtensions.CaptureRenderedFrame(window))
            Pump();
        if (!control.IsEffectivelyVisible)
            throw new InvalidOperationException($"{Describe(control)} is not visible to press");
        var centre = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)
            ?? throw new InvalidOperationException($"{Describe(control)} is not in the window");
        var hit = window.InputHitTest(centre, enabledElementsOnly: false) as Visual;
        if (!Within(hit, control))
            throw new InvalidOperationException($"a press at {centre} meant for {Describe(control)} ({BoundsIn(window, control)}) lands on {Chain(hit)}");
        return centre;
    }

    /// <summary>Runs a press and checks the element it was raised on: the control or a part of it (the nearest enabled ancestor when
    /// the control is disabled, which is where a disabled control's press goes), or this throws naming the element that took it, in
    /// the window or out of it (an overlay that closes as it is pressed).</summary>
    private static void Press(TopLevel window, Control control, Point at, Action press)
    {
        Visual? landed = null;
        var seen = false;
        using (InputElement.PointerPressedEvent.AddClassHandler<InputElement>((_, e) =>
               {
                   if (seen)
                       return;
                   seen = true;
                   landed = e.Source as Visual;
               }, RoutingStrategies.Direct | RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true))
            press();
        var passedOn = control.IsEffectivelyEnabled ? null : control.GetVisualAncestors().OfType<InputElement>().FirstOrDefault(a => a.IsEffectivelyEnabled);
        if (!seen || !(Within(landed, control) || (passedOn is not null && ReferenceEquals(landed, passedOn))))
            throw new InvalidOperationException($"a press at {at} meant for {Describe(control)} ({BoundsIn(window, control)}) went to {(seen ? Chain(landed) : "no element")}");
    }

    private static bool Within(Visual? hit, Control control) =>
        hit is not null && (ReferenceEquals(hit, control) || control.IsVisualAncestorOf(hit));

    private static string BoundsIn(TopLevel window, Control control) =>
        control.TranslatePoint(default, window) is { } origin ? $"at {origin}, {control.Bounds.Size}" : "outside the window";

    private static string Describe(Visual? v) => v is null
        ? "nothing"
        : v.GetType().Name + (v.Name is { } n ? "#" + n : "") + (v.Classes.Count > 0 ? "." + string.Join(".", v.Classes) : "")
            + (v.DataContext is ViewModels.DeskAction a ? $" [{a.Verb}]" : "");

    /// <summary>A visual and its ancestors, innermost first, to the window.</summary>
    private static string Chain(Visual? v) => v is null ? "nothing" : string.Join(" in ", v.GetSelfAndVisualAncestors().Take(8).Select(Describe));

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
