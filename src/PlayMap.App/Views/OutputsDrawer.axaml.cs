using Avalonia.Controls;
using Avalonia.Interactivity;
using PlayMap.App.Controls;
using PlayMap.App.ViewModels;

namespace PlayMap.App.Views;

/// <summary>The OUTPUTS drawer.</summary>
public partial class OutputsDrawer : UserControl
{
    public OutputsDrawer()
    {
        InitializeComponent();
    }

    private void OnPatternClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not OutputsDrawerViewModel vm)
            return;
        var menu = new ContextMenu();
        DeskMenu.Fill(menu.Items, vm.Patterns);
        PatternButton.ContextMenu = menu;
        menu.Open(PatternButton);
    }
}
