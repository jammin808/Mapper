using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace PlayMap.App.Infrastructure;

/// <summary>The desk's view-model base: property change notification and nothing else. A setter that
/// would store the same value raises nothing, so a STATE push that changes one field repaints one field.</summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Stores the value and raises the change; false (and silent) when it is equal to the old one.</summary>
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        Raise(name);
        return true;
    }

    /// <summary>True while anything listens for this object's changes (a binding, a control).</summary>
    protected bool HasListeners => PropertyChanged is not null;

    protected void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    /// <summary>Raises a change for several derived properties at once.</summary>
    protected void Raise(params ReadOnlySpan<string> names)
    {
        foreach (var n in names)
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }
}
