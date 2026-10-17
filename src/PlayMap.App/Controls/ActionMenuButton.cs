using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using PlayMap.App.ViewModels;

namespace PlayMap.App.Controls;

/// <summary>A toolbar button for one <see cref="DeskAction"/>: a press runs it, or, when it has sub-actions ("Add effect ▸"),
/// opens them as a menu built by <see cref="DeskMenu"/> — the same items, words and gate as its right-click entry; an action
/// held to confirm (<see cref="DeskAction.IsHold"/>) runs only when its press is held to the end of its ring. Hidden when the
/// node does not run its verb (or runs none of its sub-actions').</summary>
public class ActionMenuButton : HoldButton
{
    public static readonly StyledProperty<DeskAction?> ActionProperty = AvaloniaProperty.Register<ActionMenuButton, DeskAction?>(nameof(Action));

    static ActionMenuButton()
    {
        ActionProperty.Changed.AddClassHandler<ActionMenuButton>((b, e) =>
        {
            if (e.OldValue is DeskAction old)
                old.PropertyChanged -= b.OnActionChanged;
            if (e.NewValue is DeskAction now && b.IsAttachedToVisualTree())
                now.PropertyChanged += b.OnActionChanged;
            b.Sync();
        });
    }

    private void OnActionChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => Sync();

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
        ToolTip.SetShowOnDisabled(this, true);
        IsVisible = a.HasChildren ? a.Children.Any(c => c.IsAvailable) : a.IsAvailable;
        IsEnabled = a.HasChildren || a.IsEnabled;
    }

    protected override bool Holds => Action is { IsHold: true, HasChildren: false };

    protected override void OnHeld()
    {
        if (Action is { } a && a.Command.CanExecute(null))
            a.Command.Execute(null);
    }

    protected override void OnClick()
    {
        if (Action is not { } a || Holds)
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
        if (Action is { } a)
            a.PropertyChanged += OnActionChanged;
        Sync();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (Action is { } a)
            a.PropertyChanged -= OnActionChanged;
        base.OnDetachedFromVisualTree(e);
    }
}
