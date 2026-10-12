using Avalonia.Controls;
using Avalonia.Interactivity;
using PlayMap.App.Controls;
using PlayMap.App.ViewModels;

namespace PlayMap.App.Views;

/// <summary>MAP. The group "add" buttons and the test-pattern button open their action lists as menus
/// (the same lists the shortcuts and the outliner's right-click menus use).</summary>
public partial class MapView : UserControl
{
    public MapView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is MapViewModel vm)
                vm.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(MapViewModel.IsPatternMenuOpen) && vm.IsPatternMenuOpen)
                        OpenPatterns(vm);
                };
        };
    }

    private void OnAddClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: OutlinerGroup group } button)
            return;
        var menu = new ContextMenu();
        DeskMenu.Fill(menu.Items, group.AddActions);
        button.ContextMenu = menu;
        menu.Open(button);
    }

    private void OnPatternClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MapViewModel vm)
            OpenPatterns(vm);
    }

    private void OpenPatterns(MapViewModel vm)
    {
        var menu = new ContextMenu();
        DeskMenu.Fill(menu.Items, vm.PatternActions);
        menu.Closed += (_, _) => vm.IsPatternMenuOpen = false;
        PatternButton.ContextMenu = menu;
        menu.Open(PatternButton);
    }
}
