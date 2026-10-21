using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace PlayMap.App.Views;

/// <summary>The contextual inspector (PLAY, MAP and the Outputs drawer each have one). A typed corner (a quad's x or y) sends when Enter
/// is pressed in it or it loses the keyboard, never on each keystroke.</summary>
public partial class InspectorView : UserControl
{
    public InspectorView()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
    }

    private static void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && e.Source is TextBox { Classes: var classes } box && classes.Contains("corner"))
            BindingOperations.GetBindingExpressionBase(box, TextBox.TextProperty)?.UpdateSource();
    }
}
