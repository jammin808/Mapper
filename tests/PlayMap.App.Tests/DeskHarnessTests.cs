using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using PlayMap.App.Infrastructure;

namespace PlayMap.App.Tests;

/// <summary>The harness's own rules: no window opens a tooltip on hover, since its show delay runs on the machine's clock and an
/// open tooltip takes the presses aimed at what it covers; a click lands on its control or fails naming what covers it.</summary>
public class DeskHarnessTests
{
    [AvaloniaFact]
    public async Task AHarnessWindowOpensNoTooltipOnHover()
    {
        await using var desk = new DeskHarness();
        var window = MainWindowTests.Open(desk);
        var pill = MainWindowTests.Find<Button>(window, "NetworkPill");
        Assert.NotNull(ToolTip.GetTip(pill));
        // With no show delay a live tooltip service opens the tip as the pointer arrives, whatever the machine's clock reads.
        ToolTip.SetShowDelay(pill, 0);
        HeadlessWindowExtensions.MouseMove(window, DeskHarness.Aim(window, pill), RawInputModifiers.None);
        DeskHarness.Pump();
        Assert.True(pill.IsPointerOver);
        Assert.False(ToolTip.GetIsOpen(pill));
        Assert.Empty(window.GetVisualDescendants().OfType<ToolTip>());
        window.Close();
    }

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
