using System.Collections.ObjectModel;
using PlayMap.App.Infrastructure;
using PlayMap.App.Link;

namespace PlayMap.App.ViewModels;

/// <summary>One thing the operator can do to an object: the same instance feeds the object's toolbar button,
/// its right-click menu item and its keyboard shortcut, so the three never disagree. <see cref="Verb"/> is
/// the line it sends right now (shown in every tooltip, so the operator — and anyone reading over their
/// shoulder — learns the wire's words); a desk-only action (select, show info) says so instead.</summary>
public sealed class DeskAction : ObservableObject
{
    private readonly Func<string?> _verb;
    private readonly Func<string>? _label;
    private readonly Func<bool>? _isChecked;
    private readonly Func<bool>? _isEnabled;
    private string _labelText;

    private DeskAction(string id, string label, Func<string?> verb, AsyncCommand command, Func<string>? dynamicLabel,
        Func<bool>? isChecked, Func<bool>? isEnabled)
    {
        Id = id;
        _labelText = label;
        _verb = verb;
        Command = command;
        _label = dynamicLabel;
        _isChecked = isChecked;
        _isEnabled = isEnabled;
    }

    /// <summary>An action that sends the line <paramref name="verb"/> returns (null = nothing to send now).</summary>
    public static DeskAction Send(DeskSession session, string id, string label, Func<string?> verb, Func<string>? dynamicLabel = null,
        Func<bool>? isChecked = null, Func<bool>? isEnabled = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        DeskAction? self = null;
        var command = new AsyncCommand(async ct =>
        {
            var line = verb();
            if (line is not null)
                await session.SendAsync(line, ct);
        }, () => self?.IsEnabled ?? true, ex => session.Notify(ToastKind.Fault, ex.Message));
        self = new DeskAction(id, label, verb, command, dynamicLabel, isChecked, isEnabled);
        return self;
    }

    /// <summary>An action that runs on the desk (it may send verbs itself; <paramref name="verb"/> names
    /// them for the tooltip).</summary>
    public static DeskAction Run(DeskSession session, string id, string label, Func<CancellationToken, Task> run, Func<string?>? verb = null,
        Func<string>? dynamicLabel = null, Func<bool>? isChecked = null, Func<bool>? isEnabled = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        DeskAction? self = null;
        var command = new AsyncCommand(run, () => self?.IsEnabled ?? true, ex => session.Notify(ToastKind.Fault, ex.Message));
        self = new DeskAction(id, label, verb ?? (() => null), command, dynamicLabel, isChecked, isEnabled);
        return self;
    }

    /// <summary>A desk-only action: no verb.</summary>
    public static DeskAction Local(string id, string label, Action run, Func<bool>? isChecked = null, Func<bool>? isEnabled = null)
    {
        DeskAction? self = null;
        var command = new AsyncCommand(_ =>
        {
            run();
            return Task.CompletedTask;
        }, () => self?.IsEnabled ?? true);
        self = new DeskAction(id, label, () => null, command, null, isChecked, isEnabled);
        return self;
    }

    public string Id { get; }

    public string Label => _label?.Invoke() ?? _labelText;

    /// <summary>The keyboard shortcut, as shown ("Space", "Ctrl+Z").</summary>
    public string? Gesture { get; private set; }

    /// <summary>A key into the desk's icon set ("play", "pause", "stop"…); null = text only.</summary>
    public string? Icon { get; private set; }

    /// <summary>Air-affecting or hard to undo: drawn in the danger colour.</summary>
    public bool IsDanger { get; private set; }

    /// <summary>A rule is drawn above this item in menus.</summary>
    public bool SeparatorBefore { get; private set; }

    /// <summary>Shown in the object's menu only, not on its toolbar.</summary>
    public bool MenuOnly { get; private set; }

    /// <summary>Sets how the action looks (once, where it is made) and returns it.</summary>
    public DeskAction Look(string? icon = null, string? gesture = null, bool danger = false, bool separator = false, bool menuOnly = false)
    {
        Icon = icon;
        Gesture = gesture;
        IsDanger = danger;
        SeparatorBefore = separator;
        MenuOnly = menuOnly;
        return this;
    }

    public bool IsToggle => _isChecked is not null;

    public bool IsChecked => _isChecked?.Invoke() ?? false;

    public bool IsEnabled => _isEnabled?.Invoke() ?? true;

    /// <summary>The line this action sends now, or "" for a desk-only action.</summary>
    public string Verb => _verb() ?? "";

    /// <summary>What the tooltip says: the label, the shortcut and the verb.</summary>
    public string Tip
    {
        get
        {
            var head = Gesture is null ? Label : $"{Label}  ({Gesture})";
            var verb = Verb;
            return verb.Length == 0 ? head : head + "\n" + verb;
        }
    }

    public AsyncCommand Command { get; }

    /// <summary>Sub-actions (a submenu in the right-click menu; a flyout on a toolbar).</summary>
    public ObservableCollection<DeskAction> Children { get; } = [];

    public bool HasChildren => Children.Count > 0;

    public void SetLabel(string label)
    {
        if (Set(ref _labelText, label, nameof(Label)))
            Raise(nameof(Tip));
    }

    /// <summary>Re-reads the label, the verb, the checked and enabled state (after a STATE push).</summary>
    public void Refresh()
    {
        Raise(nameof(Label), nameof(Verb), nameof(Tip), nameof(IsChecked), nameof(IsEnabled));
        Command.RaiseCanExecuteChanged();
    }

    public override string ToString() => $"{Label} → {Verb}";
}

/// <summary>Helpers for an object's action list.</summary>
public static class DeskActions
{
    public static void RefreshAll(this IEnumerable<DeskAction> actions)
    {
        ArgumentNullException.ThrowIfNull(actions);
        foreach (var a in actions)
        {
            a.Refresh();
            foreach (var c in a.Children)
                c.Refresh();
        }
    }

    /// <summary>The actions a toolbar shows (menu-only ones left out).</summary>
    public static IReadOnlyList<DeskAction> Toolbar(this IEnumerable<DeskAction> actions) => actions.Where(a => !a.MenuOnly).ToList();
}
