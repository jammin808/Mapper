using System.Windows.Input;
using Avalonia.Input;

namespace PlayMap.App.ViewModels;

/// <summary>One keyboard shortcut: the keys, what they do, where they apply, and the command. While the
/// operator types into a field only the shortcuts marked <see cref="WhileTyping"/> fire (help, escape, save,
/// the workspaces): Ctrl+Left in a label moves the caret, it never skips the show.</summary>
public sealed record Shortcut(string Keys, string Words, string Scope, Key Key, KeyModifiers Modifiers, ICommand Command, Func<bool>? When = null)
{
    public bool WhileTyping { get; init; }

    /// <summary>While a field types, the key runs the shortcut and still reaches the field (Esc: the field's own cancel).</summary>
    public bool FieldKeepsKey { get; init; }
}

/// <summary>The desk's keyboard: one table that both handles the keys and fills the F1 overlay, so the help
/// never lies. Tool letters (S Q P W B T O) apply in MAP; the transport keys everywhere.</summary>
public sealed class ShortcutMap
{
    private ShortcutMap(IReadOnlyList<Shortcut> entries) => Entries = entries;

    public IReadOnlyList<Shortcut> Entries { get; }

    public IEnumerable<IGrouping<string, Shortcut>> Groups => Entries.GroupBy(e => e.Scope);

    /// <summary>A table of the given shortcuts (the desk builds its own with <see cref="Build"/>).</summary>
    public static ShortcutMap For(IReadOnlyList<Shortcut> entries) => new(entries);

    /// <summary>The desk's table: its own rows, then the rows the modules append (F1 lists them all).</summary>
    public static ShortcutMap Build(MainViewModel vm, IEnumerable<Shortcut>? modules = null)
    {
        ArgumentNullException.ThrowIfNull(vm);
        var map = vm.Map;
        bool InMap() => vm.Workspace == Workspace.Map;
        List<Shortcut> e =
        [
            new("F1", "Show or hide this help", "Desk", Key.F1, KeyModifiers.None, vm.ToggleHelp) { WhileTyping = true },
            new("Esc", "The Esc ladder: cancel a pattern task, a learn or a draft; close the help and the drawers; PANIC in PLAY when the desk's preference says so; clear the selection. While a field types, Esc is the field's and only closes the help and the drawers", "Desk",
                Key.Escape, KeyModifiers.None, vm.EscapeCommand) { WhileTyping = true, FieldKeepsKey = true },
            new("Ctrl+1", "PLAY workspace", "Desk", Key.D1, KeyModifiers.Control, vm.ShowPlay) { WhileTyping = true },
            new("Ctrl+2", "MAP workspace", "Desk", Key.D2, KeyModifiers.Control, vm.ShowMap) { WhileTyping = true },
            new("Ctrl+3", "SCAN workspace", "Desk", Key.D3, KeyModifiers.Control, vm.ShowScan) { WhileTyping = true },
            new("Ctrl+Shift+O", "Outputs drawer", "Desk", Key.O, KeyModifiers.Control | KeyModifiers.Shift, vm.ToggleOutputs) { WhileTyping = true },
            new("Ctrl+Shift+N", "Network drawer", "Desk", Key.N, KeyModifiers.Control | KeyModifiers.Shift, vm.ToggleNetwork) { WhileTyping = true },
            new("Ctrl+Shift+I", "Inputs drawer", "Desk", Key.I, KeyModifiers.Control | KeyModifiers.Shift, vm.ToggleInputs) { WhileTyping = true },
            new("Ctrl+Shift+H", "Health drawer", "Desk", Key.H, KeyModifiers.Control | KeyModifiers.Shift, vm.ToggleHealth) { WhileTyping = true },
            new("Ctrl+Shift+P", "Simple ⇄ Pro (this desk only)", "Desk", Key.P, KeyModifiers.Control | KeyModifiers.Shift, vm.TogglePro) { WhileTyping = true },
            new("Ctrl+Z", "Undo (UNDO)", "Desk", Key.Z, KeyModifiers.Control, vm.Undo.Command),
            new("Ctrl+Y", "Redo (REDO)", "Desk", Key.Y, KeyModifiers.Control, vm.Redo.Command),
            new("Ctrl+Shift+Z", "Redo (REDO)", "Desk", Key.Z, KeyModifiers.Control | KeyModifiers.Shift, vm.Redo.Command),
            new("Ctrl+S", "Save the project (PROJECT SAVE)", "Desk", Key.S, KeyModifiers.Control, vm.Save.Command) { WhileTyping = true },
            new("Space", "Play or pause the focused playlist", "Transport", Key.Space, KeyModifiers.None, vm.Master.PlayPause.Command),
            new("Shift+Space", "Stop the focused playlist (to the top, held)", "Transport", Key.Space, KeyModifiers.Shift, vm.Master.Stop.Command),
            new("Ctrl+Left", "Previous item", "Transport", Key.Left, KeyModifiers.Control, vm.Master.Prev.Command),
            new("Ctrl+Right", "Next item", "Transport", Key.Right, KeyModifiers.Control, vm.Master.Next.Command),
            new("Ctrl+B", "Blackout on or off (BLACKOUT)", "Transport", Key.B, KeyModifiers.Control, vm.Blackout.Command),
            new("S", "Select tool", "MAP", Key.S, KeyModifiers.None, map.SelectTool, InMap),
            new("Q", "Quad tool: click the four corners of the wall, on the wall; Enter draws it (SURFACE QUAD)", "MAP", Key.Q, KeyModifiers.None, map.QuadTool, InMap),
            new("O", "On the wall: the edit overlay on the projector's output (OVERLAY ON|OFF ON <output>)", "MAP", Key.O, KeyModifiers.None, map.OnWall.Command, InMap),
            new("P", "Pin tool: click a surface in projector view to pin it", "MAP", Key.P, KeyModifiers.None, map.PinTool, InMap),
            new("W", "Warp tool: drag lattice and corner handles", "MAP", Key.W, KeyModifiers.None, map.WarpTool, InMap),
            new("B", "Pen tool: click corners, drag smooth nodes, Alt-click toggles, click the first node to close", "MAP", Key.B, KeyModifiers.None, map.PenTool, InMap),
            new("T", "Test pattern menu", "MAP", Key.T, KeyModifiers.None, map.OpenPatternMenu, InMap),
            new("V", "3D view ↔ projector view", "MAP", Key.V, KeyModifiers.None, map.ToggleView, InMap),
            new("Enter", "Close the pen path", "MAP", Key.Enter, KeyModifiers.None, map.Pen.CloseCommand, () => InMap() && map.Pen.IsDrawing),
            new("Enter", "Draw the quad from its four corners (SURFACE QUAD)", "MAP", Key.Enter, KeyModifiers.None, map.FinishQuad, () => InMap() && map.Quad.IsReady),
            new("Tab", "The selected quad's next corner (HANDLE SELECT)", "MAP", Key.Tab, KeyModifiers.None, map.NextCorner, () => InMap() && map.CanNudge),
            new("Shift+Tab", "The selected quad's previous corner (HANDLE SELECT)", "MAP", Key.Tab, KeyModifiers.Shift, map.PrevCorner, () => InMap() && map.CanNudge),
            new("Delete", "Remove the selection (REMOVE)", "MAP", Key.Delete, KeyModifiers.None, map.RemoveSelection, InMap),
        ];
        // The arrows move the selected quad's corner in wall pixels: 1 px, Shift ×10, Alt ×0.1 (SURFACE CORNER … BY … PX).
        foreach (var (mods, step, words) in new[] { (KeyModifiers.None, 1.0, ""), (KeyModifiers.Shift, 10.0, "Shift+"), (KeyModifiers.Alt, 0.1, "Alt+") })
        {
            foreach (var (key, dx, dy, name) in new[] { (Key.Left, -1.0, 0.0, "Left"), (Key.Right, 1.0, 0.0, "Right"), (Key.Up, 0.0, -1.0, "Up"), (Key.Down, 0.0, 1.0, "Down") })
            {
                var (x, y) = (dx * step, dy * step);
                e.Add(new(words + name, string.Create(System.Globalization.CultureInfo.InvariantCulture,
                    $"Move the selected quad's corner {step:0.#} px {name.ToLowerInvariant()} on the wall (SURFACE CORNER … BY … PX)"),
                    "MAP", key, mods, new Infrastructure.RelayCommand(() => map.Nudge(x, y)), () => InMap() && map.CanNudge));
            }
        }
        if (modules is not null)
            e.AddRange(modules);
        return new ShortcutMap(e);
    }

    /// <summary>Runs the shortcut for a key press; true when one ran and the key is spent (false for a key the focused field keeps
    /// as well). <paramref name="typing"/> is true while a text field has the focus (the field keeps every key but the few marked
    /// WhileTyping).</summary>
    public bool Handle(Key key, KeyModifiers modifiers, bool typing)
    {
        foreach (var s in Entries)
        {
            if (s.Key != key || s.Modifiers != modifiers)
                continue;
            if (typing && !s.WhileTyping)
                return false;
            if (s.When is { } when && !when())
                continue;
            if (!s.Command.CanExecute(null))
                return !(typing && s.FieldKeepsKey);
            s.Command.Execute(null);
            return !(typing && s.FieldKeepsKey);
        }
        return false;
    }
}
