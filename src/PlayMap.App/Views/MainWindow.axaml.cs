using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using PlayMap.App.ViewModels;

namespace PlayMap.App.Views;

/// <summary>The desk's window. Keys go to the shortcut table before the focused control sees them (so Space
/// is the transport, not a press of the last-clicked button), except bare keys while a text field types. A
/// 4 Hz tick moves the counters between STATE pushes and retires old toasts.</summary>
public partial class MainWindow : Window
{
    private readonly DispatcherTimer _tick;

    public MainWindow()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnKeyDownTunnel, RoutingStrategies.Tunnel);
        _tick = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) =>
        {
            if (DataContext is MainViewModel vm)
            {
                vm.Tick();
                vm.Session.Expire(TimeSpan.FromSeconds(6));
            }
        });
        Opened += (_, _) => _tick.Start();
        Closed += (_, _) => _tick.Stop();
    }

    private void OnKeyDownTunnel(object? sender, KeyEventArgs e)
    {
        if (DataContext is not MainViewModel vm)
            return;
        var typing = FocusManager?.GetFocusedElement() is TextBox;
        if (vm.Shortcuts.Handle(e.Key, e.KeyModifiers, typing))
            e.Handled = true;
    }
}
