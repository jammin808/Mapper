using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using PlayMap.App.ViewModels;

namespace PlayMap.App.Views;

/// <summary>The desk's window. Keys go to the shortcut table before the focused control sees them (so Space
/// is the transport, not a press of the last-clicked button), except bare keys while a text field types (Esc then reaches the
/// field too). A 4 Hz tick moves the counters between STATE pushes and retires old toasts. A menu or flyout that closes starts the
/// Esc ladder's guard.</summary>
public partial class MainWindow : Window
{
    private readonly DispatcherTimer _tick;

    static MainWindow()
    {
        // A right-click menu or a flyout that closes (by Esc, a click or a choice) holds Esc's PANIC rung off for the guard, as a
        // drawer that closes does: the Esc that closed it and the next one are the operator backing out, not a panic.
        MenuBase.IsOpenProperty.Changed.AddClassHandler<ContextMenu>((menu, e) =>
        {
            if (e.NewValue is false)
                MenuClosed(menu.PlacementTarget ?? (StyledElement)menu);
        });
        Avalonia.Controls.Primitives.FlyoutBase.IsOpenProperty.Changed.AddClassHandler<Avalonia.Controls.Primitives.FlyoutBase>((flyout, e) =>
        {
            if (e.NewValue is false && flyout.Target is { } target)
                MenuClosed(target);
        });
    }

    /// <summary>A menu or flyout owned by something in a desk window closed (its owner is its logical parent, a submenu's its
    /// menu's): that window's Esc ladder starts its guard.</summary>
    private static void MenuClosed(StyledElement owner)
    {
        if (Avalonia.LogicalTree.LogicalExtensions.GetSelfAndLogicalAncestors(owner).OfType<MainWindow>().FirstOrDefault() is { DataContext: MainViewModel vm })
            vm.Escape.Closed();
    }

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

    /// <summary>The bar's width drives its compact forms (the panel itself decides one row or two).</summary>
    private void OnTopBarSized(object? sender, SizeChangedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
            vm.BarWidth = e.NewSize.Width;
    }

    private void OnKeyDownTunnel(object? sender, KeyEventArgs e)
    {
        if (DataContext is not MainViewModel vm)
            return;
        var focused = FocusManager?.GetFocusedElement();
        var typing = focused is TextBox;
        vm.IsTyping = typing;
        var onCanvas = focused is null or TopLevel or Controls.ProjectorCanvas or Controls.MapViewport or Controls.PlanCanvas;
        if (vm.Shortcuts.Handle(e.Key, e.KeyModifiers, typing, onCanvas))
            e.Handled = true;
    }
}
