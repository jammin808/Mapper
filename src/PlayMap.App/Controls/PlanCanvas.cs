using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using PlayMap.App.ViewModels;
using PlayMap.Core.Model;

namespace PlayMap.App.Controls;

/// <summary>The multi-screen plan: every output as a rectangle where it sits, scaled to fit. Click selects;
/// dragging moves it (snapped to 10 px and to its neighbours' edges), with SET &lt;output&gt; plan going out
/// through the throttle and the last position landing on release. A right click opens the output's menu.</summary>
public sealed class PlanCanvas : Control
{
    public static readonly StyledProperty<OutputsDrawerViewModel?> DrawerProperty = AvaloniaProperty.Register<PlanCanvas, OutputsDrawerViewModel?>(nameof(Drawer));

    private static readonly IBrush Back = new SolidColorBrush(Color.Parse("#0E1012")).ToImmutable();
    private static readonly IPen GridPen = new Pen(new SolidColorBrush(Color.Parse("#1A1F24")).ToImmutable(), 1).ToImmutable();
    private PlanTile? _dragging;
    private Point _press;
    private RectD _start;
    private bool _moved;
    private (double Scale, double Ox, double Oy, double MinX, double MinY) _fit;

    public PlanCanvas()
    {
        ClipToBounds = true;
        Focusable = true;
    }

    public OutputsDrawerViewModel? Drawer
    {
        get => GetValue(DrawerProperty);
        set => SetValue(DrawerProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != DrawerProperty)
            return;
        if (change.OldValue is OutputsDrawerViewModel old)
        {
            old.Tiles.CollectionChanged -= OnTiles;
            foreach (var t in old.Tiles)
                t.PropertyChanged -= OnTile;
        }
        if (change.NewValue is OutputsDrawerViewModel d)
        {
            d.Tiles.CollectionChanged += OnTiles;
            foreach (var t in d.Tiles)
                t.PropertyChanged += OnTile;
        }
        InvalidateVisual();
    }

    private void OnTiles(object? sender, NotifyCollectionChangedEventArgs e)
    {
        foreach (var t in e.OldItems?.OfType<PlanTile>() ?? [])
            t.PropertyChanged -= OnTile;
        foreach (var t in e.NewItems?.OfType<PlanTile>() ?? [])
            t.PropertyChanged += OnTile;
        InvalidateVisual();
    }

    private void OnTile(object? sender, PropertyChangedEventArgs e) => InvalidateVisual();

    private void Fit()
    {
        var tiles = Drawer?.Tiles;
        if (tiles is null || tiles.Count == 0 || Bounds.Width < 10)
        {
            _fit = (0.1, 20, 20, 0, 0);
            return;
        }
        var minX = tiles.Min(t => t.Plan.X);
        var minY = tiles.Min(t => t.Plan.Y);
        var maxX = tiles.Max(t => t.Plan.Right);
        var maxY = tiles.Max(t => t.Plan.Bottom);
        var w = Math.Max(1, maxX - minX);
        var h = Math.Max(1, maxY - minY);
        var pad = 28;
        var scale = Math.Min((Bounds.Width - 2 * pad) / w, (Bounds.Height - 2 * pad) / h);
        _fit = (scale, (Bounds.Width - w * scale) / 2, (Bounds.Height - h * scale) / 2, minX, minY);
    }

    private Rect ToScreen(RectD p) => new(_fit.Ox + (p.X - _fit.MinX) * _fit.Scale, _fit.Oy + (p.Y - _fit.MinY) * _fit.Scale, p.W * _fit.Scale, p.H * _fit.Scale);

    public override void Render(DrawingContext context)
    {
        context.FillRectangle(Back, new Rect(Bounds.Size), 8);
        for (var x = 0.0; x < Bounds.Width; x += 24)
            context.DrawLine(GridPen, new Point(x, 0), new Point(x, Bounds.Height));
        for (var y = 0.0; y < Bounds.Height; y += 24)
            context.DrawLine(GridPen, new Point(0, y), new Point(Bounds.Width, y));
        if (Drawer is not { } d)
            return;
        if (_dragging is null)
            Fit();
        foreach (var t in d.Tiles)
        {
            var r = ToScreen(t.Plan);
            var color = StageDrawing.ColorOf(t.Color);
            context.DrawRectangle(new SolidColorBrush(Color.FromArgb(t.Enabled ? (byte)60 : (byte)25, color.R, color.G, color.B)),
                new Pen(t.IsSelected ? StageDrawing.Accent : new SolidColorBrush(color), t.IsSelected ? 2.5 : 1.4, dashStyle: t.Enabled ? null : DashStyle.Dash),
                r, 4, 4);
            var name = new FormattedText(t.Name, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, 12.5, StageDrawing.TextBrush);
            var detail = new FormattedText(t.Detail, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, 10.5, StageDrawing.DimText);
            context.DrawEllipse(LightBrushConverter.Brushes.For(t.Light), null, new Point(r.X + 12, r.Y + 14), 4, 4);
            context.DrawText(name, new Point(r.X + 22, r.Y + 6));
            if (r.Height > 44)
                context.DrawText(detail, new Point(r.X + 10, r.Y + 26));
        }
    }

    private PlanTile? HitTile(Point p) => Drawer?.Tiles.LastOrDefault(t => ToScreen(t.Plan).Contains(p));

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (Drawer is not { } d)
            return;
        var pt = e.GetCurrentPoint(this);
        var tile = HitTile(pt.Position);
        d.Selected = tile;
        if (tile is null)
            return;
        if (pt.Properties.IsRightButtonPressed)
        {
            if (d.Inspector.Current is { } insp)
            {
                var menu = new ContextMenu();
                DeskMenu.Fill(menu.Items, insp.Actions);
                ContextMenu = menu;
                menu.Open(this);
            }
            e.Handled = true;
            return;
        }
        _dragging = tile;
        _press = pt.Position;
        _start = tile.Plan;
        _moved = false;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_dragging is not { } tile || Drawer is not { } d)
            return;
        var p = e.GetPosition(this);
        if (!_moved && Point.Distance(p, _press) < 3)
            return;
        _moved = true;
        var dx = (p.X - _press.X) / _fit.Scale;
        var dy = (p.Y - _press.Y) / _fit.Scale;
        var x = Snap(_start.X + dx, _start.W, d.Tiles.Where(t => t != tile).SelectMany(t => new[] { t.Plan.X, t.Plan.Right }));
        var y = Snap(_start.Y + dy, _start.H, d.Tiles.Where(t => t != tile).SelectMany(t => new[] { t.Plan.Y, t.Plan.Bottom }));
        d.MoveTile(tile, _start with { X = x, Y = y }, commit: false);
        InvalidateVisual();
    }

    /// <summary>To a neighbour's edge when within 24 plan pixels (either side of the moving tile), else to 10 px.</summary>
    private static double Snap(double v, double size, IEnumerable<double> edges)
    {
        foreach (var edge in edges)
        {
            if (Math.Abs(v - edge) < 24)
                return edge;
            if (Math.Abs(v + size - edge) < 24)
                return edge - size;
        }
        return Math.Round(v / 10) * 10;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_dragging is not { } tile || Drawer is not { } d)
            return;
        e.Pointer.Capture(null);
        if (_moved)
            d.MoveTile(tile, tile.Plan, commit: true);
        _dragging = null;
        InvalidateVisual();
    }
}

/// <summary>The scan review: the smart-scan picture (or the confidence heatmap) from the engine — the desk
/// draws the regions the scan found over it, and a click picks the smallest region under the cursor.</summary>
public sealed class ScanReviewCanvas : PreviewSurface
{
    public static readonly StyledProperty<ScanWizardViewModel?> WizardProperty = AvaloniaProperty.Register<ScanReviewCanvas, ScanWizardViewModel?>(nameof(Wizard));

    private static readonly string[] RegionColors = ["#4FC3F7", "#FFB74D", "#A5D6A7", "#CE93D8", "#90CAF9", "#FFCC80"];

    public ScanWizardViewModel? Wizard
    {
        get => GetValue(WizardProperty);
        set => SetValue(WizardProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != WizardProperty)
            return;
        if (change.OldValue is ScanWizardViewModel old)
        {
            old.PropertyChanged -= OnWizard;
            old.Regions.CollectionChanged -= OnRegions;
        }
        if (change.NewValue is ScanWizardViewModel w)
        {
            w.PropertyChanged += OnWizard;
            w.Regions.CollectionChanged += OnRegions;
        }
        InvalidateVisual();
    }

    private void OnWizard(object? sender, PropertyChangedEventArgs e) => InvalidateVisual();

    private void OnRegions(object? sender, NotifyCollectionChangedEventArgs e) => InvalidateVisual();

    protected override bool DrawFallback(DrawingContext context, Rect picture)
    {
        // Without the engine's picture: a quiet stand-in in the regions' shapes, so the review still reads.
        context.FillRectangle(StageDrawing.Background, picture);
        if (Wizard is not { } w)
            return true;
        var i = 0;
        foreach (var r in w.Regions)
        {
            var color = StageDrawing.ColorOf(RegionColors[i++ % RegionColors.Length], w.ShowConfidence ? (byte)(40 + 180 * r.Confidence) : (byte)55);
            context.FillRectangle(new SolidColorBrush(color), Map(picture, r));
        }
        return true;
    }

    private static Rect Map(Rect p, State.ScanRegion r) => new(p.X + r.X * p.Width, p.Y + r.Y * p.Height, r.W * p.Width, r.H * p.Height);

    protected override void DrawOverlay(DrawingContext context, Rect picture)
    {
        if (Wizard is not { } w)
            return;
        var i = 0;
        foreach (var r in w.Regions)
        {
            var selected = r == w.SelectedRegion;
            var color = StageDrawing.ColorOf(RegionColors[i++ % RegionColors.Length]);
            var rect = Map(picture, r);
            context.DrawRectangle(null, selected ? StageDrawing.AccentPen : new Pen(new SolidColorBrush(color), 1.4), rect, 4, 4);
            StageDrawing.Label(context, string.Create(CultureInfo.InvariantCulture, $"{r.Label} · {r.Confidence * 100:0} %"), new Point(rect.X + 6, rect.Y + 6),
                selected ? StageDrawing.TextBrush : StageDrawing.DimText, 11);
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (Wizard is not { } w)
            return;
        var r = PictureRect();
        var p = e.GetPosition(this);
        if (r.Width <= 0 || !r.Contains(p))
            return;
        w.PickAt((p.X - r.X) / r.Width, (p.Y - r.Y) / r.Height);
        e.Handled = true;
    }
}
