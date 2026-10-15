using Avalonia;
using Avalonia.Controls;

namespace PlayMap.App.Controls;

/// <summary>Where a top-bar child sits in its row.</summary>
public enum TopBarSlot
{
    Left,
    Centre,
    Right,
}

/// <summary>The top bar's layout: one row when everything fits and the bar is at least <see cref="TwoRowBelow"/> wide;
/// otherwise two rows — identity, workspaces and the drawer buttons on the first, the transport, timecode, tempo and the
/// show cluster on the second (children marked <c>TopBarPanel.Second</c>). In a row, Left children run from the left,
/// Right children from the right, and Centre children sit centred in what is left (shrunk to it, never past the Right
/// group). No child ends beyond the bar: one that cannot fit is given the room there is and clips inside itself.</summary>
public class TopBarPanel : Panel
{
    public const double TwoRowBelow = 1366;

    public static readonly AttachedProperty<TopBarSlot> SlotProperty =
        AvaloniaProperty.RegisterAttached<TopBarPanel, Control, TopBarSlot>("Slot");

    public static readonly AttachedProperty<bool> SecondProperty =
        AvaloniaProperty.RegisterAttached<TopBarPanel, Control, bool>("Second");

    public static readonly StyledProperty<double> GapProperty = AvaloniaProperty.Register<TopBarPanel, double>(nameof(Gap), 8);

    private const double RowGap = 6;

    static TopBarPanel()
    {
        AffectsParentMeasure<TopBarPanel>(SlotProperty, SecondProperty);
        AffectsMeasure<TopBarPanel>(GapProperty);
    }

    public static TopBarSlot GetSlot(Control c) => c.GetValue(SlotProperty);

    public static void SetSlot(Control c, TopBarSlot v) => c.SetValue(SlotProperty, v);

    public static bool GetSecond(Control c) => c.GetValue(SecondProperty);

    public static void SetSecond(Control c, bool v) => c.SetValue(SecondProperty, v);

    public double Gap
    {
        get => GetValue(GapProperty);
        set => SetValue(GapProperty, value);
    }

    /// <summary>True when the last layout used two rows.</summary>
    public bool IsTwoRow { get; private set; }

    private IEnumerable<Control> Shown => Children.Where(c => c.IsVisible);

    protected override Size MeasureOverride(Size availableSize)
    {
        foreach (var c in Children)
            c.Measure(new Size(double.PositiveInfinity, availableSize.Height));
        var width = double.IsFinite(availableSize.Width) ? availableSize.Width : RowWidth(Shown);
        IsTwoRow = width < TwoRowBelow || RowWidth(Shown) > width;
        if (!IsTwoRow)
            return new Size(Math.Min(width, RowWidth(Shown)), RowHeight(Shown));
        var first = Shown.Where(c => !GetSecond(c)).ToList();
        var second = Shown.Where(GetSecond).ToList();
        // A row that does not fit gives its Centre children what is left (they clip inside themselves).
        FitCentre(first, width);
        FitCentre(second, width);
        return new Size(Math.Min(width, Math.Max(RowWidth(first), RowWidth(second))), RowHeight(first) + RowGap + RowHeight(second));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (!IsTwoRow)
        {
            ArrangeRow(Shown.ToList(), 0, finalSize.Width, finalSize.Height);
            return finalSize;
        }
        var first = Shown.Where(c => !GetSecond(c)).ToList();
        var second = Shown.Where(GetSecond).ToList();
        var h1 = RowHeight(first);
        ArrangeRow(first, 0, finalSize.Width, h1);
        ArrangeRow(second, h1 + RowGap, finalSize.Width, RowHeight(second));
        return finalSize;
    }

    private void FitCentre(List<Control> row, double width)
    {
        var fixedWidth = row.Where(c => GetSlot(c) != TopBarSlot.Centre).Sum(c => c.DesiredSize.Width) + Gap * Math.Max(0, row.Count - 1);
        var room = Math.Max(0, width - fixedWidth);
        foreach (var c in row.Where(c => GetSlot(c) == TopBarSlot.Centre))
            c.Measure(new Size(room, double.PositiveInfinity));
    }

    private double RowWidth(IEnumerable<Control> row)
    {
        var list = row.ToList();
        return list.Sum(c => c.DesiredSize.Width) + Gap * Math.Max(0, list.Count - 1);
    }

    private static double RowHeight(IEnumerable<Control> row) => row.Select(c => c.DesiredSize.Height).DefaultIfEmpty(0).Max();

    private void ArrangeRow(List<Control> row, double top, double width, double height)
    {
        var x = 0.0;
        foreach (var c in row.Where(c => GetSlot(c) == TopBarSlot.Left))
        {
            var w = Math.Min(c.DesiredSize.Width, Math.Max(0, width - x));
            c.Arrange(new Rect(x, top, w, height));
            x += w + Gap;
        }
        var right = width;
        foreach (var c in row.Where(c => GetSlot(c) == TopBarSlot.Right).Reverse())
        {
            var w = Math.Min(c.DesiredSize.Width, Math.Max(0, right - x));
            right -= w;
            c.Arrange(new Rect(right, top, w, height));
            right -= Gap;
        }
        var centre = row.Where(c => GetSlot(c) == TopBarSlot.Centre).ToList();
        if (centre.Count == 0)
            return;
        var room = Math.Max(0, right - x);
        var want = centre.Sum(c => c.DesiredSize.Width) + Gap * (centre.Count - 1);
        var cx = x + Math.Max(0, (room - want) / 2);
        foreach (var c in centre)
        {
            var w = Math.Min(c.DesiredSize.Width, Math.Max(0, x + room - cx));
            c.Arrange(new Rect(cx, top, w, height));
            cx += w + Gap;
        }
    }
}
