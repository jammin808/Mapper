using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace PlayMap.App.Controls;

/// <summary>A picture from the engine: polls the view's preview source while it is on screen and draws the
/// newest frame, letterboxed; until a frame arrives (no engine, a remote node, the demo) it draws a calm
/// placeholder with the view's name, so the layout never jumps. Subclasses draw overlays on top in the
/// picture's own coordinates.</summary>
public class PreviewSurface : Control
{
    public static readonly StyledProperty<string?> NodeIdProperty = AvaloniaProperty.Register<PreviewSurface, string?>(nameof(NodeId));
    public static readonly StyledProperty<string?> ViewIdProperty = AvaloniaProperty.Register<PreviewSurface, string?>(nameof(ViewId));
    public static readonly StyledProperty<string?> LabelProperty = AvaloniaProperty.Register<PreviewSurface, string?>(nameof(Label));
    public static readonly StyledProperty<double> AspectProperty = AvaloniaProperty.Register<PreviewSurface, double>(nameof(Aspect), 16 / 9.0);
    public static readonly StyledProperty<string?> AccentProperty = AvaloniaProperty.Register<PreviewSurface, string?>(nameof(Accent));

    private static readonly IBrush Back = new SolidColorBrush(Color.Parse("#0C0E10")).ToImmutable();
    private static readonly IPen GridPen = new Pen(new SolidColorBrush(Color.Parse("#1B2025")).ToImmutable(), 1).ToImmutable();
    private static readonly IBrush LabelBrush = new SolidColorBrush(Color.Parse("#6F7882")).ToImmutable();
    private IPreviewSource? _source;
    private DispatcherTimer? _timer;
    private WriteableBitmap? _frame;

    static PreviewSurface()
    {
        AffectsRender<PreviewSurface>(LabelProperty, AspectProperty, AccentProperty);
        ClipToBoundsProperty.OverrideDefaultValue<PreviewSurface>(true);
    }

    public string? NodeId
    {
        get => GetValue(NodeIdProperty);
        set => SetValue(NodeIdProperty, value);
    }

    public string? ViewId
    {
        get => GetValue(ViewIdProperty);
        set => SetValue(ViewIdProperty, value);
    }

    public string? Label
    {
        get => GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    /// <summary>Width ÷ height of the picture (the placeholder's shape until a frame says otherwise).</summary>
    public double Aspect
    {
        get => GetValue(AspectProperty);
        set => SetValue(AspectProperty, value);
    }

    /// <summary>A model colour for the frame's edge (a projector's), or null.</summary>
    public string? Accent
    {
        get => GetValue(AccentProperty);
        set => SetValue(AccentProperty, value);
    }

    /// <summary>True once the engine has sent a picture for this view.</summary>
    public bool HasFrame => _frame is not null;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == NodeIdProperty || change.Property == ViewIdProperty)
            Reopen();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Reopen();
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Render, (_, _) => PollFrame());
        _timer.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _timer?.Stop();
        _timer = null;
        _source?.Dispose();
        _source = null;
        _frame = null;
    }

    private void Reopen()
    {
        _source?.Dispose();
        _source = null;
        _frame = null;
        if (VisualRoot is not null && NodeId is { Length: > 0 } node && ViewId is { Length: > 0 } view)
            _source = PreviewFeeds.Resolve(node, view);
        InvalidateVisual();
    }

    private void PollFrame()
    {
        if (!IsEffectivelyVisible || _source is null)
            return;
        if (_source.Poll() is { } bitmap)
        {
            _frame = bitmap;
            InvalidateVisual();
        }
    }

    /// <summary>Where the picture sits in the control: the largest rectangle of its aspect that fits, centred.</summary>
    protected Rect PictureRect()
    {
        var aspect = _frame is { } f ? f.PixelSize.Width / (double)Math.Max(1, f.PixelSize.Height) : Math.Max(0.1, Aspect);
        var b = Bounds;
        if (b.Width <= 0 || b.Height <= 0)
            return default;
        var w = b.Width;
        var h = w / aspect;
        if (h > b.Height)
        {
            h = b.Height;
            w = h * aspect;
        }
        return new Rect((b.Width - w) / 2, (b.Height - h) / 2, w, h);
    }

    public override void Render(DrawingContext context)
    {
        var r = PictureRect();
        context.FillRectangle(Back, new Rect(Bounds.Size));
        if (_frame is { } frame)
        {
            context.DrawImage(frame, new Rect(0, 0, frame.PixelSize.Width, frame.PixelSize.Height), r);
        }
        else if (DrawFallback(context, r) == false)
        {
            DrawPlaceholder(context, r);
        }
        if (Accent is { } accent)
            context.DrawRectangle(new Pen(LightBrushConverter.Brushes.For(accent), 1.5), r.Deflate(0.75), 0);
        DrawOverlay(context, r);
    }

    /// <summary>What a subclass draws when no engine picture is up (the MAP views draw the stage themselves);
    /// false to draw the plain placeholder.</summary>
    protected virtual bool DrawFallback(DrawingContext context, Rect picture) => false;

    /// <summary>Overlays drawn on top of the picture.</summary>
    protected virtual void DrawOverlay(DrawingContext context, Rect picture)
    {
    }

    private void DrawPlaceholder(DrawingContext context, Rect r)
    {
        context.FillRectangle(new SolidColorBrush(Color.Parse("#111417")), r);
        var step = Math.Max(16, r.Width / 16);
        for (var x = r.X + step; x < r.Right; x += step)
            context.DrawLine(GridPen, new Point(x, r.Y), new Point(x, r.Bottom));
        for (var y = r.Y + step; y < r.Bottom; y += step)
            context.DrawLine(GridPen, new Point(r.X, y), new Point(r.Right, y));
        if (Label is { Length: > 0 } label)
        {
            var text = new FormattedText(label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, Math.Clamp(r.Height / 10, 10, 15), LabelBrush);
            context.DrawText(text, new Point(r.X + (r.Width - text.Width) / 2, r.Y + (r.Height - text.Height) / 2));
        }
    }
}
