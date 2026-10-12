using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using PlayMap.App.ViewModels;

namespace PlayMap.App.Controls;

/// <summary>Gives a control a right-click menu made from its object's <see cref="DeskAction"/> list — the same
/// actions its toolbar shows, so the menu and the buttons can never disagree. Each item carries its
/// shortcut and, in its tooltip, the verb it sends. The menu is rebuilt each time it opens, so its words
/// follow the latest STATE (Play reads Pause while the clip plays).</summary>
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
                Fill(menu.Items, current);
        };
        control.ContextMenu = menu;
    }

    /// <summary>The menu items for a list of actions (also what the tests compare with the toolbar).</summary>
    public static void Fill(ItemCollection items, IEnumerable<DeskAction> actions)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(actions);
        items.Clear();
        foreach (var a in actions)
        {
            if (a.SeparatorBefore && items.Count > 0)
                items.Add(new Separator());
            items.Add(Build(a));
        }
    }

    private static MenuItem Build(DeskAction a)
    {
        var item = new MenuItem
        {
            Header = a.Label,
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
            ToolTip.SetTip(item, verb);
        if (a.IsDanger)
            item.Classes.Add("danger");
        foreach (var child in a.Children)
            item.Items.Add(Build(child));
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
