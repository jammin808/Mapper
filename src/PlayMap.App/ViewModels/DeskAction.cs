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
    private Func<string?>? _whyNot;
    private string _labelText;
    private DeskSession? _session;

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

    /// <summary>An action that sends the line <paramref name="verb"/> returns (null = nothing to send now). <paramref name="whyNot"/>
    /// says why its own condition (<paramref name="isEnabled"/>) holds it greyed, in its tooltip. With <paramref name="everyPress"/>
    /// every press is sent at once, never waiting for the reply to the one before (PANIC, BLACKOUT, the master).</summary>
    public static DeskAction Send(DeskSession session, string id, string label, Func<string?> verb, Func<string>? dynamicLabel = null,
        Func<bool>? isChecked = null, Func<bool>? isEnabled = null, Func<string?>? whyNot = null, bool everyPress = false)
    {
        ArgumentNullException.ThrowIfNull(session);
        DeskAction? self = null;
        var command = new AsyncCommand(async ct =>
        {
            var line = verb();
            if (line is not null)
                await session.SendAsync(line, ct);
        }, () => self?.IsEnabled ?? true, ex => session.Notify(ToastKind.Fault, ex.Message), everyPress);
        self = new DeskAction(id, label, verb, command, dynamicLabel, isChecked, isEnabled) { _session = session, _whyNot = whyNot };
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
        self = new DeskAction(id, label, verb ?? (() => null), command, dynamicLabel, isChecked, isEnabled) { _session = session };
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

    /// <summary>Live: its own condition holds and the gate (the node's doors: pairing, the fleet, the show's policy, the locks)
    /// would not refuse its line.</summary>
    public bool IsEnabled => (_isEnabled?.Invoke() ?? true) && Refusal is null;

    private (string Verb, long Stamp, GateRefusal? Refusal, bool Available)? _asked;

    /// <summary>The gate's words when the line this action sends would be refused now ("Not while armed: …"), or null.</summary>
    public string? Refusal => Ask().Refusal?.Words;

    /// <summary>False when the node does not run this action's verb (its package is absent there): the control hides.</summary>
    public bool IsAvailable => Ask().Available;

    /// <summary>The gate and the verb list asked once per line and per move of their facts (every binding reads them).</summary>
    private (string Verb, long Stamp, GateRefusal? Refusal, bool Available) Ask()
    {
        if (_session is not { } s)
            return ("", 0, null, true);
        var verb = Verb;
        var stamp = s.GateStamp;
        if (_asked is { } a && a.Stamp == stamp && string.Equals(a.Verb, verb, StringComparison.Ordinal))
            return a;
        var fresh = (verb, stamp, s.GateRefusalOf(verb), s.Knows(verb));
        _asked = fresh;
        return fresh;
    }

    /// <summary>The menu's words: the label, and the gate's tag while it refuses ("· armed", "· locked", "· mirror", "· fenced",
    /// "· not paired", "· blind draft").</summary>
    public string MenuLabel => Ask().Refusal is { } why ? Label + " · " + why.Tag : Label;

    /// <summary>The line this action sends now, or "" for a desk-only action.</summary>
    public string Verb => _verb() ?? "";

    /// <summary>What the tooltip says: the label, the shortcut and the verb, and why it is greyed (the gate's words, or its own
    /// condition's).</summary>
    public string Tip
    {
        get
        {
            var head = Gesture is null ? Label : $"{Label}  ({Gesture})";
            var verb = Verb;
            var tip = verb.Length == 0 ? head : head + "\n" + verb;
            var why = Refusal ?? (_whyNot is { } w && _isEnabled?.Invoke() == false ? w() : null);
            return why is not null ? tip + "\n" + why : tip;
        }
    }

    public AsyncCommand Command { get; }

    /// <summary>Sub-actions (a submenu in the right-click menu; a flyout on a toolbar).</summary>
    public ObservableCollection<DeskAction> Children { get; } = [];

    public bool HasChildren => Children.Count > 0;

    public void SetLabel(string label)
    {
        if (Set(ref _labelText, label, nameof(Label)))
            Raise(nameof(Tip), nameof(MenuLabel));
    }

    private (string Label, string Verb, string Tip, bool IsChecked, bool IsEnabled, string? Refusal, string MenuLabel, bool IsAvailable)? _shown;

    /// <summary>Re-reads the label, the verb, the checked and enabled state (after a STATE push) and raises what moved: a push
    /// that moves nothing of this action repaints nothing of it, and an action nothing shows reads nothing (whatever binds it
    /// later reads it then).</summary>
    public void Refresh()
    {
        if (!HasListeners)
        {
            _shown = null;
            Command.RaiseCanExecuteChanged();
            return;
        }
        var now = (Label, Verb, Tip, IsChecked, IsEnabled, Refusal, MenuLabel, IsAvailable);
        var was = _shown;
        _shown = now;
        if (was is not { } w)
        {
            Raise(nameof(Label), nameof(Verb), nameof(Tip), nameof(IsChecked), nameof(IsEnabled), nameof(Refusal), nameof(MenuLabel), nameof(IsAvailable));
            Command.RaiseCanExecuteChanged();
            return;
        }
        if (w == now)
            return;
        Span<string> moved = new string[8];
        var n = 0;
        if (!string.Equals(w.Label, now.Label, StringComparison.Ordinal))
            moved[n++] = nameof(Label);
        if (!string.Equals(w.Verb, now.Verb, StringComparison.Ordinal))
            moved[n++] = nameof(Verb);
        if (!string.Equals(w.Tip, now.Tip, StringComparison.Ordinal))
            moved[n++] = nameof(Tip);
        if (w.IsChecked != now.IsChecked)
            moved[n++] = nameof(IsChecked);
        if (w.IsEnabled != now.IsEnabled)
            moved[n++] = nameof(IsEnabled);
        if (!string.Equals(w.Refusal, now.Refusal, StringComparison.Ordinal))
            moved[n++] = nameof(Refusal);
        if (!string.Equals(w.MenuLabel, now.MenuLabel, StringComparison.Ordinal))
            moved[n++] = nameof(MenuLabel);
        if (w.IsAvailable != now.IsAvailable)
            moved[n++] = nameof(IsAvailable);
        Raise(moved[..n]);
        if (w.IsEnabled != now.IsEnabled)
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
            a.Children.RefreshAll();
            a.Refresh();
        }
    }

    /// <summary>The actions a toolbar shows (menu-only ones left out).</summary>
    public static IReadOnlyList<DeskAction> Toolbar(this IEnumerable<DeskAction> actions) => actions.Where(a => !a.MenuOnly).ToList();
}

/// <summary>Converters that turn one action into a list (a row's menu of one item).</summary>
public static class ActionLists
{
    public static readonly Avalonia.Data.Converters.IValueConverter One =
        new Avalonia.Data.Converters.FuncValueConverter<DeskAction?, IEnumerable<DeskAction>?>(a => a is null ? null : [a]);
}
