using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace PlayMap.App.Controls;

/// <summary>A score as a ring: the arc fills to <see cref="Value"/> of <see cref="Maximum"/> in the light's colour, the
/// number in the middle (a calibration's 0–100, a preflight's done of all).</summary>
public class ScoreRing : Control
{
    public static readonly StyledProperty<double> ValueProperty = AvaloniaProperty.Register<ScoreRing, double>(nameof(Value));

    public static readonly StyledProperty<double> MaximumProperty = AvaloniaProperty.Register<ScoreRing, double>(nameof(Maximum), 100);

    public static readonly StyledProperty<string> LightProperty = AvaloniaProperty.Register<ScoreRing, string>(nameof(Light), "grey");

    static ScoreRing()
    {
        AffectsRender<ScoreRing>(ValueProperty, MaximumProperty, LightProperty);
    }

    public double Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public double Maximum
    {
        get => GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    public string Light
    {
        get => GetValue(LightProperty);
        set => SetValue(LightProperty, value);
    }

    /// <summary>The fraction of the ring that is filled, 0–1.</summary>
    public double Fraction => Maximum <= 0 ? 0 : Math.Clamp(Value / Maximum, 0, 1);

    protected override Size MeasureOverride(Size availableSize) => new(36, 36);

    public override void Render(DrawingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var r = Math.Min(Bounds.Width, Bounds.Height) / 2 - 3;
        var c = new Point(Bounds.Width / 2, Bounds.Height / 2);
        context.DrawEllipse(null, new Pen(new SolidColorBrush(Color.FromArgb(60, 255, 255, 255)), 4), c, r, r);
        var brush = LightBrushConverter.Brushes.For(Light);
        DrawArc(context, c, r, Fraction, new Pen(brush, 4));
        var text = new FormattedText(Value.ToString("0", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            Typeface.Default, 11, Brushes.White);
        context.DrawText(text, new Point(c.X - text.Width / 2, c.Y - text.Height / 2));
    }

    /// <summary>Draws the arc of a ring from twelve o'clock, clockwise, over <paramref name="fraction"/> of the turn.</summary>
    public static void DrawArc(DrawingContext context, Point centre, double radius, double fraction, IPen pen)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (fraction <= 0 || radius <= 0)
            return;
        if (fraction >= 0.999)
        {
            context.DrawEllipse(null, pen, centre, radius, radius);
            return;
        }
        var angle = fraction * 2 * Math.PI;
        var start = new Point(centre.X, centre.Y - radius);
        var end = new Point(centre.X + radius * Math.Sin(angle), centre.Y - radius * Math.Cos(angle));
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(start, false);
            g.ArcTo(end, new Size(radius, radius), 0, fraction > 0.5, SweepDirection.Clockwise);
            g.EndFigure(false);
        }
        context.DrawGeometry(null, pen, geometry);
    }
}
