using System.Windows.Input;
using Avalonia.Input;

namespace PlayMap.App.ViewModels;

/// <summary>One keyboard shortcut: the keys, what they do, where they apply, and the command.
/// Single keys (no modifier, or Shift alone) never fire while the operator types into a field.</summary>
public sealed record Shortcut(string Keys, string Words, string Scope, Key Key, KeyModifiers Modifiers, ICommand Command, Func<bool>? When = null);

/// <summary>The desk's keyboard: one table that both handles the keys and fills the F1 overlay, so the help
/// never lies. Tool letters (Q P W B T) apply in MAP; the transport keys everywhere.</summary>
public sealed class ShortcutMap
{
    private ShortcutMap(IReadOnlyList<Shortcut> entries) => Entries = entries;

    public IReadOnlyList<Shortcut> Entries { get; }

    public IEnumerable<IGrouping<string, Shortcut>> Groups => Entries.GroupBy(e => e.Scope);

    public static ShortcutMap Build(MainViewModel vm)
    {
        ArgumentNullException.ThrowIfNull(vm);
        var map = vm.Map;
        bool InMap() => vm.Workspace == Workspace.Map;
        List<Shortcut> e =
        [
            new("F1", "Show or hide this help", "Desk", Key.F1, KeyModifiers.None, vm.ToggleHelp),
            new("Esc", "Close the help and the drawers; cancel a pen path", "Desk", Key.Escape, KeyModifiers.None,
                new Infrastructure.RelayCommand(() =>
                {
                    if (vm.Workspace == Workspace.Map && map.Pen.IsDrawing)
                        map.Pen.Cancel();
                    else
                        vm.CloseOverlays.Execute(null);
                })),
            new("Ctrl+1", "PLAY workspace", "Desk", Key.D1, KeyModifiers.Control, vm.ShowPlay),
            new("Ctrl+2", "MAP workspace", "Desk", Key.D2, KeyModifiers.Control, vm.ShowMap),
            new("Ctrl+3", "SCAN workspace", "Desk", Key.D3, KeyModifiers.Control, vm.ShowScan),
            new("Ctrl+Shift+O", "Outputs drawer", "Desk", Key.O, KeyModifiers.Control | KeyModifiers.Shift, vm.ToggleOutputs),
            new("Ctrl+Shift+N", "Network drawer", "Desk", Key.N, KeyModifiers.Control | KeyModifiers.Shift, vm.ToggleNetwork),
            new("Ctrl+Z", "Undo (UNDO)", "Desk", Key.Z, KeyModifiers.Control, vm.Undo.Command),
            new("Ctrl+Y", "Redo (REDO)", "Desk", Key.Y, KeyModifiers.Control, vm.Redo.Command),
            new("Ctrl+Shift+Z", "Redo (REDO)", "Desk", Key.Z, KeyModifiers.Control | KeyModifiers.Shift, vm.Redo.Command),
            new("Ctrl+S", "Save the project (PROJECT SAVE)", "Desk", Key.S, KeyModifiers.Control, vm.Save.Command),
            new("Space", "Play or pause the focused playlist", "Transport", Key.Space, KeyModifiers.None, vm.Master.PlayPause.Command),
            new("Shift+Space", "Stop the focused playlist (to the top, held)", "Transport", Key.Space, KeyModifiers.Shift, vm.Master.Stop.Command),
            new("Ctrl+Left", "Previous item", "Transport", Key.Left, KeyModifiers.Control, vm.Master.Prev.Command),
            new("Ctrl+Right", "Next item", "Transport", Key.Right, KeyModifiers.Control, vm.Master.Next.Command),
            new("Ctrl+B", "Blackout on or off (BLACKOUT)", "Transport", Key.B, KeyModifiers.Control, vm.Blackout.Command),
            new("Q", "Select tool", "MAP", Key.Q, KeyModifiers.None, map.SelectTool, InMap),
            new("P", "Pin tool: click a surface in projector view to pin it", "MAP", Key.P, KeyModifiers.None, map.PinTool, InMap),
            new("W", "Warp tool: drag lattice and corner handles", "MAP", Key.W, KeyModifiers.None, map.WarpTool, InMap),
            new("B", "Pen tool: click corners, drag smooth nodes, Alt-click toggles, click the first node to close", "MAP", Key.B, KeyModifiers.None, map.PenTool, InMap),
            new("T", "Test pattern menu", "MAP", Key.T, KeyModifiers.None, map.OpenPatternMenu, InMap),
            new("Tab", "3D view ↔ projector view", "MAP", Key.Tab, KeyModifiers.None, map.ToggleView, InMap),
            new("Enter", "Close the pen path", "MAP", Key.Enter, KeyModifiers.None, map.Pen.CloseCommand, () => InMap() && map.Pen.IsDrawing),
            new("Delete", "Remove the selection (REMOVE)", "MAP", Key.Delete, KeyModifiers.None, map.RemoveSelection, InMap),
        ];
        return new ShortcutMap(e);
    }

    /// <summary>Runs the shortcut for a key press; true when one ran. <paramref name="typing"/> is true
    /// while a text field has the focus (bare keys are then left to the field).</summary>
    public bool Handle(Key key, KeyModifiers modifiers, bool typing)
    {
        foreach (var s in Entries)
        {
            if (s.Key != key || s.Modifiers != modifiers)
                continue;
            if (typing && (modifiers == KeyModifiers.None || modifiers == KeyModifiers.Shift) && key is not (Key.F1 or Key.Escape))
                return false;
            if (s.When is { } when && !when())
                continue;
            if (!s.Command.CanExecute(null))
                return true;
            s.Command.Execute(null);
            return true;
        }
        return false;
    }
}
