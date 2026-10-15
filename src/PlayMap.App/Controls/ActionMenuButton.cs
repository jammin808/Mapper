using Avalonia;
using Avalonia.Controls;
using PlayMap.App.ViewModels;

namespace PlayMap.App.Controls;

/// <summary>A toolbar button for one <see cref="DeskAction"/>: a press runs it, or, when it has sub-actions ("Add effect ▸"),
/// opens them as a menu built by <see cref="DeskMenu"/> — the same items, words and gate as its right-click entry. Hidden
/// when the node does not run its verb (or runs none of its sub-actions').</summary>
public class ActionMenuButton : Button
{
    public static readonly StyledProperty<DeskAction?> ActionProperty = AvaloniaProperty.Register<ActionMenuButton, DeskAction?>(nameof(Action));

    static ActionMenuButton()
    {
        ActionProperty.Changed.AddClassHandler<ActionMenuButton>((b, _) => b.Sync());
    }

    protected override Type StyleKeyOverride => typeof(Button);

    public DeskAction? Action
    {
        get => GetValue(ActionProperty);
        set => SetValue(ActionProperty, value);
    }

    /// <summary>The menu the last press opened (sub-actions only).</summary>
    public MenuFlyout? Opened { get; private set; }

    private void Sync()
    {
        if (Action is not { } a)
            return;
        Content = a.HasChildren ? a.Label + " ▸" : a.Label;
        ToolTip.SetTip(this, a.Tip);
        IsVisible = a.HasChildren ? a.Children.Any(c => c.IsAvailable) : a.IsAvailable;
        IsEnabled = a.HasChildren || a.IsEnabled;
    }

    protected override void OnClick()
    {
        if (Action is not { } a)
            return;
        if (!a.HasChildren)
        {
            if (a.Command.CanExecute(null))
                a.Command.Execute(null);
            return;
        }
        var flyout = new MenuFlyout();
        DeskMenu.Fill(flyout.Items, a.Children);
        Opened = flyout;
        flyout.ShowAt(this);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Sync();
    }
}
