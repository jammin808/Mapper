using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using PlayMap.App.Infrastructure;

namespace PlayMap.App.Tests;

/// <summary>The harness's own rules: a click lands on its control or fails naming what covers it.</summary>
public class DeskHarnessTests
{
    [AvaloniaFact]
    public void AClickOnACoveredControlFailsNamingWhatCoversItAndPressesNothing()
    {
        var runs = 0;
        var button = new Button { Content = "Fix", Width = 80, Height = 40, Command = new RelayCommand(_ => runs++) };
        var cover = new Border { Name = "Cover", Background = Brushes.Black, Width = 120, Height = 60 };
        var window = new Window { Content = new Panel { Children = { button, cover } }, Width = 200, Height = 100 };
        window.Show();
        DeskHarness.Pump();
        var covered = Assert.Throws<InvalidOperationException>(() => DeskHarness.Click(window, button));
        Assert.Contains("Border#Cover", covered.Message, StringComparison.Ordinal);
        Assert.Equal(0, runs);

        cover.IsVisible = false;
        DeskHarness.Click(window, button);
        Assert.Equal(1, runs);
        window.Close();
    }
}
