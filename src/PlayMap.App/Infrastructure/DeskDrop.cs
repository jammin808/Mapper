using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;

namespace PlayMap.App.Infrastructure;

/// <summary>A drop on an object's row, as a module handles it: the data format it takes, and the step that turns the
/// dropped data and the row's object into verbs (true when it took the drop).</summary>
public sealed record DropHandler(string Format, Func<object, object?, bool> Handle);

/// <summary>Drop targets without per-view code: a row marks itself with <c>DeskDrop.Target="True"</c>, and a module
/// registers a handler for a data format (a live input, a preset, an effect). The first handler whose format the drag
/// carries and that takes the drop wins; the row's data context is the object dropped on. Handlers send verbs only.</summary>
public static class DeskDrop
{
    public static readonly AttachedProperty<bool> TargetProperty = AvaloniaProperty.RegisterAttached<Control, bool>("Target", typeof(DeskDrop));

    private static readonly List<DropHandler> Handlers = [];
    private static readonly Dictionary<string, DataFormat<string>> Formats = new(StringComparer.Ordinal);
    private static readonly Lock Gate = new();

    static DeskDrop()
    {
        TargetProperty.Changed.AddClassHandler<Control>((control, e) =>
        {
            var on = e.NewValue is true;
            DragDrop.SetAllowDrop(control, on);
            control.RemoveHandler(DragDrop.DropEvent, OnDrop);
            if (on)
                control.AddHandler(DragDrop.DropEvent, OnDrop);
        });
    }

    public static bool GetTarget(Control control) => control.GetValue(TargetProperty);

    public static void SetTarget(Control control, bool value) => control.SetValue(TargetProperty, value);

    public static void Register(DropHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (Gate)
        {
            Handlers.RemoveAll(h => h.Format == handler.Format);
            Handlers.Add(handler);
            if (!Formats.ContainsKey(handler.Format))
                Formats[handler.Format] = DataFormat.CreateInProcessFormat<string>(handler.Format);
        }
    }

    /// <summary>Takes a handler away (a module that leaves, a test).</summary>
    public static bool Unregister(string format)
    {
        lock (Gate)
            return Handlers.RemoveAll(h => h.Format == format) > 0;
    }

    /// <summary>Offers dropped data in a format to the handlers (what a drop does; also the tests' way in).</summary>
    public static bool Offer(string format, object data, object? target)
    {
        ArgumentNullException.ThrowIfNull(data);
        DropHandler[] handlers;
        lock (Gate)
            handlers = [.. Handlers];
        foreach (var h in handlers)
        {
            if (h.Format == format && h.Handle(data, target))
                return true;
        }
        return false;
    }

    private static void OnDrop(object? sender, DragEventArgs e)
    {
        if (sender is not Control control)
            return;
        (DropHandler Handler, DataFormat<string> Format)[] handlers;
        lock (Gate)
            handlers = [.. Handlers.Select(h => (h, Formats[h.Format]))];
        foreach (var (h, format) in handlers)
        {
            if (e.DataTransfer.TryGetValue(format) is { } data && h.Handle(data, control.DataContext))
            {
                e.Handled = true;
                return;
            }
        }
    }
}
