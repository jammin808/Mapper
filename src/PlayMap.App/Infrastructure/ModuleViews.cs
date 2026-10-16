using Avalonia.Controls;
using Avalonia.Controls.Templates;

namespace PlayMap.App.Infrastructure;

/// <summary>The views a desk module brings for its own view models (a top-bar slot, a drawer tab, an inspector
/// section, a banner): registered once where the module is composed, found by the view model's exact type, so a
/// package adds a pair of files and edits no shared template.</summary>
public static class ModuleViews
{
    private static readonly Dictionary<Type, Func<Control>> Factories = [];
    private static readonly Lock Gate = new();

    public static void Register<TViewModel>(Func<Control> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        lock (Gate)
            Factories[typeof(TViewModel)] = factory;
    }

    internal static Func<Control>? For(Type type)
    {
        lock (Gate)
            return Factories.TryGetValue(type, out var f) ? f : null;
    }
}

/// <summary>The application-wide template that hands a module's view model its registered view.</summary>
public sealed class ModuleViewLocator : IDataTemplate
{
    public Control? Build(object? param) => param is null ? null : ModuleViews.For(param.GetType())?.Invoke();

    public bool Match(object? data) => data is not null && ModuleViews.For(data.GetType()) is not null;
}
