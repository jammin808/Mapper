using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using PlayMap.App.ViewModels;

namespace PlayMap.App.Controls;

/// <summary>Where a window's modules put marks on an object's row.</summary>
public interface IBadgeSource
{
    IReadOnlyList<ObjectBadge> BadgesFor(IMenuTarget target);

    /// <summary>Raised on the UI thread when badges may have changed (after a STATE or project push).</summary>
    event Action? BadgesChanged;
}

/// <summary>The badges of the row it sits in (its data is the row's object): a padlock, a chip, a dot, each with its words
/// in the tooltip. It asks the window's modules on every push, so a badge comes and goes with STATE; empty, it takes no
/// room.</summary>
public class BadgeStrip : StackPanel
{
    private IBadgeSource? _source;

    public BadgeStrip()
    {
        Orientation = Orientation.Horizontal;
        Spacing = 3;
        VerticalAlignment = VerticalAlignment.Center;
    }

    /// <summary>The badges shown now.</summary>
    public IReadOnlyList<ObjectBadge> Shown { get; private set; } = [];

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _source = TopLevel.GetTopLevel(this)?.DataContext as IBadgeSource;
        if (_source is not null)
            _source.BadgesChanged += Rebuild;
        Rebuild();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_source is not null)
            _source.BadgesChanged -= Rebuild;
        _source = null;
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        Rebuild();
    }

    private void Rebuild()
    {
        IReadOnlyList<ObjectBadge> badges = _source is not null && DataContext is IMenuTarget t ? _source.BadgesFor(t) : [];
        if (badges.SequenceEqual(Shown))
            return;
        Shown = badges;
        Children.Clear();
        foreach (var b in badges)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
            if (Application.Current?.TryGetResource("Icon." + b.Icon, null, out var g) == true && g is Geometry geometry)
                row.Children.Add(new PathIcon { Data = geometry, Width = 11, Height = 11, Foreground = LightBrushConverter.Brushes.For(b.Light) });
            if (b.Text.Length > 0)
                row.Children.Add(new TextBlock { Text = b.Text, FontSize = 10, Foreground = LightBrushConverter.Brushes.For(b.Light), VerticalAlignment = VerticalAlignment.Center });
            var chip = new Border { Child = row, Padding = new Thickness(3, 1), CornerRadius = new CornerRadius(4), Tag = b };
            ToolTip.SetTip(chip, b.Tip);
            Children.Add(chip);
        }
    }
}
