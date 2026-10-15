using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using PlayMap.App.ViewModels;

namespace PlayMap.App.Controls;

/// <summary>What a window offers every right-click menu beyond the object's own actions: the modules' entries for
/// that target (Lock, Add effect ▸, Bind ▸…), the same instances its inspector's toolbar shows.</summary>
public interface IMenuExtras
{
    IReadOnlyList<DeskAction> ExtrasFor(IMenuTarget target);
}

/// <summary>Gives a control a right-click menu made from its object's <see cref="DeskAction"/> list — the same
/// actions its toolbar shows, so the menu and the buttons can never disagree — and, when the control's data is an
/// <see cref="IMenuTarget"/>, the modules' entries for it after a rule. Each item carries its shortcut and, in its
/// tooltip, the verb it sends; an item the gate refuses is greyed with "· armed" and the gate's words. An action whose
/// verb the node does not run is left out. The menu is rebuilt each time it opens, so its words follow the latest
/// STATE (Play reads Pause while the clip plays).</summary>
public static class DeskMenu
{
    public static readonly AttachedProperty<IEnumerable<DeskAction>?> ActionsProperty =
        AvaloniaProperty.RegisterAttached<Control, IEnumerable<DeskAction>?>("Actions", typeof(DeskMenu));

    static DeskMenu()
    {
        ActionsProperty.Changed.AddClassHandler<Control>((control, e) => Attach(control, e.NewValue as IEnumerable<DeskAction>));
    }

    public static IEnumerable<DeskAction>? GetActions(Control control) => control.GetValue(ActionsProperty);

    public static void SetActions(Control control, IEnumerable<DeskAction>? value) => control.SetValue(ActionsProperty, value);

    private static void Attach(Control control, IEnumerable<DeskAction>? actions)
    {
        if (actions is null)
        {
            control.ContextMenu = null;
            return;
        }
        var menu = new ContextMenu();
        Fill(menu.Items, actions);
        menu.Opening += (_, _) =>
        {
            if (GetActions(control) is { } current)
                Fill(menu.Items, current, ExtrasOf(control));
        };
        control.ContextMenu = menu;
    }

    /// <summary>The modules' entries for the control's target (none when the control's data names no object or the
    /// window offers none).</summary>
    public static IReadOnlyList<DeskAction> ExtrasOf(Control control)
    {
        ArgumentNullException.ThrowIfNull(control);
        var target = control.DataContext switch
        {
            IMenuTarget t => t,
            InspectorHost { Current: { } c } => c,
            _ => null,
        };
        return target is not null && TopLevel.GetTopLevel(control)?.DataContext is IMenuExtras extras ? extras.ExtrasFor(target) : [];
    }

    /// <summary>The menu items for a list of actions and the modules' extras after a rule (also what the tests compare
    /// with the toolbar).</summary>
    public static void Fill(ItemCollection items, IEnumerable<DeskAction> actions, IEnumerable<DeskAction>? extras = null)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(actions);
        items.Clear();
        foreach (var a in actions)
        {
            if (!a.IsAvailable)
                continue;
            if (a.SeparatorBefore && items.Count > 0)
                items.Add(new Separator());
            items.Add(Build(a));
        }
        var first = true;
        foreach (var a in extras ?? [])
        {
            if (!a.IsAvailable || (a.HasChildren && !a.Children.Any(c => c.IsAvailable)))
                continue;
            if ((first || a.SeparatorBefore) && items.Count > 0)
                items.Add(new Separator());
            first = false;
            items.Add(Build(a));
        }
    }

    private static MenuItem Build(DeskAction a)
    {
        var item = new MenuItem
        {
            Header = a.MenuLabel,
            Command = a.Children.Count > 0 ? null : a.Command,
            IsEnabled = a.IsEnabled,
            Tag = a,
        };
        if (a.IsToggle)
        {
            item.ToggleType = MenuItemToggleType.CheckBox;
            item.IsChecked = a.IsChecked;
        }
        if (a.Gesture is { } g && TryGesture(g, out var gesture))
            item.InputGesture = gesture;
        if (a.Icon is { } icon && Application.Current?.TryGetResource("Icon." + icon, null, out var geometry) == true && geometry is Avalonia.Media.Geometry geo)
            item.Icon = new PathIcon { Data = geo, Width = 14, Height = 14 };
        var verb = a.Verb;
        if (verb.Length > 0)
            ToolTip.SetTip(item, a.Refusal is { } why ? verb + "\n" + why : verb);
        if (a.IsDanger)
            item.Classes.Add("danger");
        foreach (var child in a.Children)
        {
            if (child.IsAvailable)
                item.Items.Add(Build(child));
        }
        return item;
    }

    /// <summary>"Ctrl+Z" → a gesture (the platform's spelling of keys).</summary>
    public static bool TryGesture(string text, out KeyGesture gesture)
    {
        gesture = new KeyGesture(Key.None);
        try
        {
            gesture = KeyGesture.Parse(text);
            return true;
        }
        catch (ArgumentException)
        {
            // A gesture the platform cannot spell (a single letter tool key shown as a hint) stays a hint.
            return false;
        }
    }
}
