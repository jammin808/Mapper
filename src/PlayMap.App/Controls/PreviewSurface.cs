using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using PlayMap.App.State;

namespace PlayMap.App.Controls;

/// <summary>A picture from the engine: polls the view's preview source while it is on screen and draws the
/// newest frame, letterboxed; until a frame arrives, and while the source has nothing to read (no engine, a remote node, the demo,
/// a view the node names no ring for or one whose ring does not open) it draws a calm stand-in with the view's name and the words why,
/// so the layout never jumps. The source reads the ring the node names for the view (<see cref="NamesProperty"/>, inherited from the
/// window). Subclasses draw overlays on top in the picture's own coordinates.</summary>
public class PreviewSurface : Control
{
    /// <summary>The ring names of the desk's session (inherited: the main window sets it once for every preview in it).</summary>
    public static readonly AttachedProperty<PreviewNames?> NamesProperty =
        AvaloniaProperty.RegisterAttached<PreviewSurface, Control, PreviewNames?>("Names", inherits: true);

    /// <summary>Whether the desk's link to the node is live (inherited from the window): a frozen picture on a quiet link says the engine
    /// is quiet.</summary>
    public static readonly AttachedProperty<bool> LinkLiveProperty =
        AvaloniaProperty.RegisterAttached<PreviewSurface, Control, bool>("LinkLive", defaultValue: true, inherits: true);

    /// <summary>The desk's clock the previews' ages are read on (ms; tests set a manual one).</summary>
    internal static Func<long> Clock { get; set; } = () => System.Diagnostics.Stopwatch.GetTimestamp() * 1000 / System.Diagnostics.Stopwatch.Frequency;

    public static readonly StyledProperty<string?> NodeIdProperty = AvaloniaProperty.Register<PreviewSurface, string?>(nameof(NodeId));
    public static readonly StyledProperty<string?> ViewIdProperty = AvaloniaProperty.Register<PreviewSurface, string?>(nameof(ViewId));
    public static readonly StyledProperty<string?> LabelProperty = AvaloniaProperty.Register<PreviewSurface, string?>(nameof(Label));
    public static readonly StyledProperty<double> AspectProperty = AvaloniaProperty.Register<PreviewSurface, double>(nameof(Aspect), 16 / 9.0);
    public static readonly StyledProperty<string?> AccentProperty = AvaloniaProperty.Register<PreviewSurface, string?>(nameof(Accent));

    private static readonly IBrush Back = new SolidColorBrush(Color.Parse("#0C0E10")).ToImmutable();
    private static readonly IPen GridPen = new Pen(new SolidColorBrush(Color.Parse("#1B2025")).ToImmutable(), 1).ToImmutable();
    private static readonly IBrush LabelBrush = new SolidColorBrush(Color.Parse("#6F7882")).ToImmutable();
    private static readonly IBrush WordsBrush = new SolidColorBrush(Color.Parse("#8A939C")).ToImmutable();
    private IPreviewSource? _source;
    private DispatcherTimer? _timer;
    private WriteableBitmap? _frame;
    private string? _standInWords;
    private readonly PreviewAge _age = new();
    private (string? Badge, string? Frozen) _aged;
    private static readonly IBrush BadgeBrush = new SolidColorBrush(Color.Parse("#D29922")).ToImmutable();
    private static readonly IBrush FrozenVeil = new SolidColorBrush(Color.FromArgb(0xB0, 0x30, 0x33, 0x37)).ToImmutable();

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

    public static PreviewNames? GetNames(Control control)
    {
        ArgumentNullException.ThrowIfNull(control);
        return control.GetValue(NamesProperty);
    }

    public static void SetNames(Control control, PreviewNames? names)
    {
        ArgumentNullException.ThrowIfNull(control);
        control.SetValue(NamesProperty, names);
    }

    /// <summary>True while the engine's picture for this view is up.</summary>
    public bool HasFrame => _frame is not null;

    /// <summary>"stale 120 ms" while the picture is older than two frames of its own rate, else null.</summary>
    public string? AgeBadge => _aged.Badge;

    /// <summary>The reason a picture with no successor for a second is greyed, else null.</summary>
    public string? FrozenWords => _aged.Frozen;

    /// <summary>The picture's age model (its rate and age).</summary>
    internal PreviewAge Age => _age;

    public static bool GetLinkLive(Control control)
    {
        ArgumentNullException.ThrowIfNull(control);
        return control.GetValue(LinkLiveProperty);
    }

    public static void SetLinkLive(Control control, bool live)
    {
        ArgumentNullException.ThrowIfNull(control);
        control.SetValue(LinkLiveProperty, live);
    }

    /// <summary>The words the stand-in shows now (the node's, or why its ring does not open); null while a picture is up or no words
    /// are due.</summary>
    public string? StandInWords => _standInWords;

    /// <summary>The source this view polls (null while it is off screen).</summary>
    internal IPreviewSource? Source => _source;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == NodeIdProperty || change.Property == ViewIdProperty || change.Property == NamesProperty)
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
        _standInWords = null;
    }

    private void Reopen()
    {
        _source?.Dispose();
        _source = null;
        _frame = null;
        _standInWords = null;
        _age.Reset();
        _aged = (null, null);
        if (VisualRoot is not null && NodeId is { Length: > 0 } node && ViewId is { Length: > 0 } view)
            _source = PreviewFeeds.Resolve(node, view, GetNames(this));
        InvalidateVisual();
    }

    /// <summary>One tick of the view's timer. On screen: a new picture is drawn, and a source with nothing to read takes the picture
    /// down for the stand-in and its words. Off screen (attached, hidden): nothing is read, but the source lets go of a ring the node no
    /// longer names at once.</summary>
    internal void PollFrame()
    {
        if (_source is null)
            return;
        if (!IsEffectivelyVisible)
        {
            _source.Idle();
            return;
        }
        if (_source.Poll() is { } bitmap)
        {
            _frame = bitmap;
            _age.Arrived(_source.LastStampNs, Clock());
            InvalidateVisual();
        }
        else if (_frame is not null && !_source.Live)
        {
            _frame = null;
            InvalidateVisual();
        }
        var now = Clock();
        var aged = _frame is null ? (null, null) : (_age.Badge(now), _age.FrozenWords(now, _source.Live, GetLinkLive(this)));
        if (aged != _aged)
        {
            _aged = aged;
            InvalidateVisual();
        }
        var words = _frame is null ? _source.Words : null;
        if (!string.Equals(words, _standInWords, StringComparison.Ordinal))
        {
            _standInWords = words;
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
            DrawAge(context, r);
        }
        else
        {
            if (DrawFallback(context, r) == false)
                DrawPlaceholder(context, r);
            DrawWords(context, r);
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

    /// <summary>A frozen picture greyed with its reason; a stale one's badge with its age, in the picture's top right corner.</summary>
    private void DrawAge(DrawingContext context, Rect r)
    {
        if (_aged.Frozen is { } frozen)
        {
            context.FillRectangle(FrozenVeil, r);
            if (r.Width >= 40 && r.Height >= 30)
            {
                var text = new FormattedText(frozen, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, Math.Clamp(r.Height / 16, 9, 12), WordsBrush)
                {
                    MaxTextWidth = Math.Max(1, r.Width - 16),
                    MaxLineCount = 2,
                    Trimming = TextTrimming.CharacterEllipsis,
                };
                context.DrawText(text, new Point(r.X + 8, r.Bottom - text.Height - 6));
            }
        }
        if (_aged.Badge is { } badge && r.Width >= 60)
        {
            var text = new FormattedText("△ " + badge, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, 11, BadgeBrush);
            context.FillRectangle(Back, new Rect(r.Right - text.Width - 10, r.Y + 4, text.Width + 6, text.Height + 2));
            context.DrawText(text, new Point(r.Right - text.Width - 7, r.Y + 5));
        }
    }

    /// <summary>The stand-in's words, along the picture's foot.</summary>
    private void DrawWords(DrawingContext context, Rect r)
    {
        if (_standInWords is not { Length: > 0 } words || r.Width < 40 || r.Height < 30)
            return;
        var text = new FormattedText(words, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, Math.Clamp(r.Height / 16, 9, 12), WordsBrush)
        {
            MaxTextWidth = Math.Max(1, r.Width - 16),
            MaxLineCount = 2,
            Trimming = TextTrimming.CharacterEllipsis,
        };
        context.DrawText(text, new Point(r.X + 8, r.Bottom - text.Height - 6));
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
