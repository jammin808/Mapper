using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;

namespace PlayMap.App.Controls;

/// <summary>A button that acts only when held for <see cref="HoldMs"/> (600 ms by default), drawing a ring that fills
/// while held: for the few high-risk lines that touch many machines (promote, retry a present path). A short press, or a
/// release before the ring closes, sends nothing. GO, PANIC and BLACKOUT are never hold buttons.</summary>
public class HoldButton : Button
{
    public static readonly StyledProperty<int> HoldMsProperty = AvaloniaProperty.Register<HoldButton, int>(nameof(HoldMs), 600);

    public static readonly StyledProperty<double> ProgressProperty = AvaloniaProperty.Register<HoldButton, double>(nameof(Progress));

    private readonly DispatcherTimer _timer;
    private long _pressedAt;
    private bool _held;

    static HoldButton()
    {
        AffectsRender<HoldButton>(ProgressProperty);
    }

    public HoldButton()
    {
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(30), DispatcherPriority.Input, (_, _) => Step());
    }

    protected override Type StyleKeyOverride => typeof(Button);

    public int HoldMs
    {
        get => GetValue(HoldMsProperty);
        set => SetValue(HoldMsProperty, value);
    }

    /// <summary>How far the ring has closed, 0–1.</summary>
    public double Progress
    {
        get => GetValue(ProgressProperty);
        private set => SetValue(ProgressProperty, value);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (!IsEffectivelyEnabled || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;
        _held = true;
        _pressedAt = System.Diagnostics.Stopwatch.GetTimestamp();
        Progress = 0;
        _timer.Start();
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        Release();
        e.Handled = true;
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        Release();
        base.OnPointerCaptureLost(e);
    }

    /// <summary>The hold reached its time: the command runs once. Public so a keyboard path (Enter held) or a test can
    /// complete it with the elapsed time it measured.</summary>
    public bool Complete(double elapsedMs)
    {
        if (!_held || elapsedMs < HoldMs)
            return false;
        Release();
        if (Command is { } c && c.CanExecute(CommandParameter))
            c.Execute(CommandParameter);
        return true;
    }

    private void Step()
    {
        var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(_pressedAt).TotalMilliseconds;
        Progress = Math.Clamp(elapsed / Math.Max(1, HoldMs), 0, 1);
        Complete(elapsed);
    }

    private void Release()
    {
        _held = false;
        _timer.Stop();
        Progress = 0;
    }

    public override void Render(DrawingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        base.Render(context);
        var p = Progress;
        if (p <= 0)
            return;
        var r = Math.Min(Bounds.Width, Bounds.Height) / 2 - 2;
        var c = new Point(Bounds.Width / 2, Bounds.Height / 2);
        ScoreRing.DrawArc(context, c, r, p, new Pen(Brushes.White, 2));
    }
}
