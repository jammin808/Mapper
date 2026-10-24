using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using PlayMap.App.ViewModels;

namespace PlayMap.App.Controls;

/// <summary>Map anything's touch: which <see cref="DeskAction"/> a control runs — an action button's own, a template's (its data), or,
/// for a control bound straight to an action's command (ARM, Solve), the action named by <see cref="ActionProperty"/> — so the window
/// can find, under a press, the control a touch would make learn. Only a control carrying the <c>bindable</c> class (outlined while the
/// mode is on and the gate would let BIND LEARN of its line through) is one.</summary>
public static class MapAnythingTouch
{
    public static readonly AttachedProperty<DeskAction?> ActionProperty =
        AvaloniaProperty.RegisterAttached<Control, DeskAction?>("Action", typeof(MapAnythingTouch));

    public static DeskAction? GetAction(Control control)
    {
        ArgumentNullException.ThrowIfNull(control);
        return control.GetValue(ActionProperty);
    }

    public static void SetAction(Control control, DeskAction? action)
    {
        ArgumentNullException.ThrowIfNull(control);
        control.SetValue(ActionProperty, action);
    }

    /// <summary>The action of the outlined control at or above <paramref name="source"/> whose line a touch learns now, or null.</summary>
    public static DeskAction? At(Visual source)
    {
        ArgumentNullException.ThrowIfNull(source);
        foreach (var v in source.GetSelfAndVisualAncestors())
        {
            if (v is not Control c || !c.Classes.Contains("bindable"))
                continue;
            var action = c is ActionMenuButton button ? button.Action : GetAction(c) ?? c.DataContext as DeskAction;
            if (action is { IsBindable: true })
                return action;
        }
        return null;
    }
}
