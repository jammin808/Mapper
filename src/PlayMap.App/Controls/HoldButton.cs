using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;

namespace PlayMap.App.Controls;

/// <summary>A button that acts only when held for <see cref="HoldMs"/> (600 ms by default), drawing a ring that fills
/// while held: for the few high-risk lines that touch many machines (promote, failover, retry a present path). A short press,
/// a release before the ring closes, a press dragged off it, or the keyboard's Enter or Space sends nothing. GO, PANIC and
/// BLACKOUT are never hold buttons. A subclass that holds only some presses says so with <see cref="Holds"/>; the rest are
/// an ordinary button's.</summary>
public class HoldButton : Button
{
    public static readonly StyledProperty<int> HoldMsProperty = AvaloniaProperty.Register<HoldButton, int>(nameof(HoldMs), 600);

    public static readonly StyledProperty<double> ProgressProperty = AvaloniaProperty.Register<HoldButton, double>(nameof(Progress));

    private DispatcherTimer? _timer;
    private long _pressedAt;
    private bool _held;

    static HoldButton()
    {
        AffectsRender<HoldButton>(ProgressProperty);
    }

    protected override Type StyleKeyOverride => typeof(Button);

    public int HoldMs
    {
        get => GetValue(HoldMsProperty);
        set => SetValue(HoldMsProperty, value);
    }

    /// <summary>True while a press is held and has not yet acted.</summary>
    public bool IsHeld => _held;

    /// <summary>How far the ring has closed, 0–1.</summary>
    public double Progress
    {
        get => GetValue(ProgressProperty);
        private set => SetValue(ProgressProperty, value);
    }

    /// <summary>True when a press must be held to act; false makes this an ordinary button.</summary>
    protected virtual bool Holds => true;

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (!Holds)
        {
            base.OnPointerPressed(e);
            return;
        }
        if (!IsEffectivelyEnabled || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;
        _held = true;
        _pressedAt = System.Diagnostics.Stopwatch.GetTimestamp();
        Progress = 0;
        e.Pointer.Capture(this);
        _timer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(30), DispatcherPriority.Input, (_, _) => Step());
        _timer.Start();
        e.Handled = true;
    }

    /// <summary>A press dragged off the button lets go: the hold is only a hold while the pointer stays on it.</summary>
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnPointerMoved(e);
        if (_held && !new Rect(Bounds.Size).Contains(e.GetPosition(this)))
            Release();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (!Holds && !_held)
        {
            base.OnPointerReleased(e);
            return;
        }
        Release();
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        Release();
        base.OnPointerCaptureLost(e);
    }

    /// <summary>A click that is not a hold (the keyboard's Enter or Space, an access key) acts only on a button that does not hold.</summary>
    protected override void OnClick()
    {
        if (!Holds)
            base.OnClick();
    }

    /// <summary>The hold reached its time: it acts once. Public so a keyboard path (Enter held) or a test can complete it with the
    /// elapsed time it measured.</summary>
    public bool Complete(double elapsedMs)
    {
        if (!_held || elapsedMs < HoldMs)
            return false;
        Release();
        OnHeld();
        return true;
    }

    /// <summary>What a completed hold does: the button's command.</summary>
    protected virtual void OnHeld()
    {
        if (Command is { } c && c.CanExecute(CommandParameter))
            c.Execute(CommandParameter);
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
        _timer?.Stop();
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
